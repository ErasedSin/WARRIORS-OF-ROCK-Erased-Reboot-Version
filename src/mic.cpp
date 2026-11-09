// The PC's microphone as the game's USB microphone.
//
// The game finds a microphone through XamUserGetDeviceContext(user, 4, &ctx):
// ctx = (family << 28) | port, family 6 meaning the kernel's MicDeviceRequest.
// It then talks to it with requests { u16 type, u16 port, ... } sent to
// MicDeviceRequest, which the SDK leaves as a stub. Here the player chosen in
// rewor_mic_player gets a microphone on port 1, and the requests are answered
// with sound captured from Windows (WASAPI, shared mode, 48 kHz mono 16-bit).
//
// Adapted from Piscacho's Guitar Hero: Warriors of Rock PC port
// (https://github.com/Piscacho/Guitar-Hero-Warriors-of-Rock-PC, runtime/xam.cpp
// and runtime/mic_capture.cpp), MIT License, Copyright (c) 2026 Piscacho.

#include "generated/rewor_init.h"

#include <rex/cvar.h>
#include <rex/hook.h>
#include <rex/logging.h>
#include <rex/ppc/context.h>
#include <rex/ppc/function.h>
#include <rex/system/function_dispatcher.h>
#include <rex/system/thread_state.h>
#include <rex/system/kernel_state.h>
#include <rex/system/xmemory.h>
#include <rex/system/xthread.h>

#include <algorithm>
#include <atomic>
#include <chrono>
#include <cstdint>
#include <cstdlib>
#include <cstring>
#include <cwchar>
#include <deque>
#include <mutex>
#include <string>
#include <thread>
#include <vector>

#ifdef _WIN32
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>
#include <audioclient.h>
#include <mmdeviceapi.h>
#include <functiondiscoverykeys_devpkey.h>
#include <timeapi.h>
#pragma comment(lib, "ole32.lib")
#pragma comment(lib, "winmm.lib")
#endif

REXCVAR_DEFINE_INT32(rewor_mic_player, 0, "re-wor",
                     "Player who sings with the PC's microphone: 1-4, 0 = no microphone. Applies when the game "
                     "starts.")
    .lifecycle(rex::cvar::Lifecycle::kRequiresRestart);
REXCVAR_DEFINE_STRING(rewor_mic_device, "", "re-wor",
                      "Windows microphone: its name as the launcher lists it (the start of the Windows name is "
                      "enough) or its WASAPI id; empty = the Windows default recording device")
    .lifecycle(rex::cvar::Lifecycle::kRequiresRestart);
REXCVAR_DEFINE_INT32(rewor_mic_volume, 100, "re-wor", "Microphone volume, in percent (100 = as captured)");
REXCVAR_DEFINE_INT32(rewor_mic_gate, 0, "re-wor",
                     "Microphone sensitivity: sound quieter than this percent of full scale (after the volume) is "
                     "silenced, so the game does not take noise for singing; 0 = off");

namespace {

constexpr uint32_t kRate = 48000;
constexpr size_t kRing = kRate;                    // 1 s
constexpr size_t kMaxLatency = kRate * 60 / 1000;  // older sound than 60 ms is dropped

std::mutex g_ring_mutex;
std::vector<int16_t> g_ring(kRing);
size_t g_ring_read = 0, g_ring_count = 0;
std::atomic<bool> g_capture_started{false};

uint32_t Load32(const uint8_t* base, uint32_t a) {
  return uint32_t(base[a]) << 24 | uint32_t(base[a + 1]) << 16 | uint32_t(base[a + 2]) << 8 | base[a + 3];
}
uint16_t Load16(const uint8_t* base, uint32_t a) { return uint16_t(base[a] << 8 | base[a + 1]); }
void Store32(uint8_t* base, uint32_t a, uint32_t v) {
  for (int i = 0; i < 4; ++i) base[a + i] = uint8_t(v >> (24 - 8 * i));
}
void Store16(uint8_t* base, uint32_t a, uint16_t v) {
  base[a] = uint8_t(v >> 8);
  base[a + 1] = uint8_t(v);
}

bool MicOn() { return REXCVAR_GET(rewor_mic_player) >= 1 && REXCVAR_GET(rewor_mic_player) <= 4; }
uint32_t MicUser() { return uint32_t(REXCVAR_GET(rewor_mic_player) - 1); }

void Push(const int16_t* samples, size_t n) {
  std::lock_guard lock(g_ring_mutex);
  for (size_t i = 0; i < n; ++i) {
    g_ring[(g_ring_read + g_ring_count) % kRing] = samples[i];
    if (g_ring_count < kRing) {
      ++g_ring_count;
    } else {
      g_ring_read = (g_ring_read + 1) % kRing;
    }
  }
  if (g_ring_count > kMaxLatency) {
    const size_t drop = g_ring_count - kMaxLatency;
    g_ring_read = (g_ring_read + drop) % kRing;
    g_ring_count -= drop;
  }
}

// n samples (48 kHz mono s16); silence where the microphone has not caught up.
void ReadSamples(int16_t* out, size_t n) {
  std::lock_guard lock(g_ring_mutex);
  const size_t k = std::min(n, g_ring_count);
  for (size_t i = 0; i < k; ++i) out[i] = g_ring[(g_ring_read + i) % kRing];
  g_ring_read = (g_ring_read + k) % kRing;
  g_ring_count -= k;
  if (k < n) std::memset(out + k, 0, (n - k) * sizeof(int16_t));
}

#ifdef _WIN32
// The recording device whose Windows name starts with name (the launcher lists
// them through winmm, which cuts names at 31 characters).
IMMDevice* FindByName(IMMDeviceEnumerator* enumerator, const wchar_t* name) {
  IMMDeviceCollection* devices = nullptr;
  if (FAILED(enumerator->EnumAudioEndpoints(eCapture, DEVICE_STATE_ACTIVE, &devices))) return nullptr;
  IMMDevice* found = nullptr;
  UINT count = 0;
  devices->GetCount(&count);
  const size_t length = wcslen(name);
  for (UINT i = 0; i < count && !found; ++i) {
    IMMDevice* device = nullptr;
    if (FAILED(devices->Item(i, &device))) continue;
    IPropertyStore* store = nullptr;
    PROPVARIANT value;
    PropVariantInit(&value);
    if (SUCCEEDED(device->OpenPropertyStore(STGM_READ, &store)) &&
        SUCCEEDED(store->GetValue(PKEY_Device_FriendlyName, &value)) && value.vt == VT_LPWSTR &&
        wcsncmp(value.pwszVal, name, length) == 0) {
      found = device;
      device = nullptr;
    }
    PropVariantClear(&value);
    if (store) store->Release();
    if (device) device->Release();
  }
  devices->Release();
  return found;
}

void CaptureThread() {
  CoInitializeEx(nullptr, COINIT_MULTITHREADED);
  IMMDeviceEnumerator* enumerator = nullptr;
  IMMDevice* device = nullptr;
  IAudioClient* client = nullptr;
  IAudioCaptureClient* capture = nullptr;
  if (FAILED(CoCreateInstance(__uuidof(MMDeviceEnumerator), nullptr, CLSCTX_ALL, __uuidof(IMMDeviceEnumerator),
                              reinterpret_cast<void**>(&enumerator)))) {
    REXLOG_WARN("re-wor mic: no audio devices");
    return;
  }
  const std::string id = REXCVAR_GET(rewor_mic_device);
  if (!id.empty()) {
    wchar_t wide[512] = {};
    MultiByteToWideChar(CP_UTF8, 0, id.c_str(), -1, wide, 512);
    if (FAILED(enumerator->GetDevice(wide, &device))) {
      device = FindByName(enumerator, wide);
    }
    if (!device) REXLOG_WARN("re-wor mic: the chosen microphone is not connected, using the Windows default");
  }
  if (!device && FAILED(enumerator->GetDefaultAudioEndpoint(eCapture, eConsole, &device))) {
    REXLOG_WARN("re-wor mic: Windows has no recording device");
    return;
  }
  if (FAILED(device->Activate(__uuidof(IAudioClient), CLSCTX_ALL, nullptr, reinterpret_cast<void**>(&client)))) {
    REXLOG_WARN("re-wor mic: could not open the microphone");
    return;
  }
  WAVEFORMATEX format{};
  format.wFormatTag = WAVE_FORMAT_PCM;
  format.nChannels = 1;
  format.nSamplesPerSec = kRate;
  format.wBitsPerSample = 16;
  format.nBlockAlign = 2;
  format.nAvgBytesPerSec = kRate * 2;
  HRESULT hr = client->Initialize(AUDCLNT_SHAREMODE_SHARED,
                                  AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM | AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY,
                                  200000 /* 20 ms */, 0, &format, nullptr);
  if (FAILED(hr) || FAILED(client->GetService(__uuidof(IAudioCaptureClient), reinterpret_cast<void**>(&capture)))) {
    REXLOG_WARN("re-wor mic: could not start capturing ({:08X})", uint32_t(hr));
    return;
  }
  client->Start();
  REXLOG_INFO("re-wor mic: capturing from Windows, 48 kHz mono");
  std::vector<int16_t> temp;
  int gate_hold = 0;  // samples the gate stays open after a loud one
  for (;;) {
    Sleep(2);
    const float gain = std::clamp(REXCVAR_GET(rewor_mic_volume), 0, 400) / 100.0f;
    const int gate = std::clamp(REXCVAR_GET(rewor_mic_gate), 0, 100) * 32767 / 100;
    UINT32 packet = 0;
    while (SUCCEEDED(capture->GetNextPacketSize(&packet)) && packet) {
      BYTE* data = nullptr;
      UINT32 frames = 0;
      DWORD flags = 0;
      if (FAILED(capture->GetBuffer(&data, &frames, &flags, nullptr, nullptr))) break;
      temp.resize(frames);
      if (flags & AUDCLNT_BUFFERFLAGS_SILENT) {
        std::fill(temp.begin(), temp.end(), int16_t(0));
      } else {
        const auto* samples = reinterpret_cast<const int16_t*>(data);
        for (UINT32 i = 0; i < frames; ++i) {
          temp[i] = int16_t(std::clamp(int(samples[i] * gain), -32768, 32767));
        }
      }
      capture->ReleaseBuffer(frames);
      if (gate > 0) {
        // Open for 250 ms after any sample above the threshold, so the ends of
        // notes are not cut.
        for (auto& sample : temp) {
          if (std::abs(int(sample)) >= gate) gate_hold = int(kRate / 4);
          if (gate_hold > 0) {
            --gate_hold;
          } else {
            sample = 0;
          }
        }
      }
      Push(temp.data(), frames);
    }
  }
}
#endif

void StartCapture() {
#ifdef _WIN32
  bool expected = false;
  if (g_capture_started.compare_exchange_strong(expected, true)) std::thread(CaptureThread).detach();
#endif
}

// Finishes a request at once: its callback (request + 16) gets (request, status),
// and the game is told the request is pending, as when hardware answers later.
// The callback may send the next request (the game's set-up runs as a chain).
void Complete(PPCContext& ctx, uint8_t* base, uint32_t request, uint32_t status) {
  const uint32_t callback = Load32(base, request + 16);
  PPCFunc* fn = callback ? REX_LOOKUP_FUNC(base, callback) : nullptr;
  if (fn) {
    PPCContext call = ctx;
    call.r1.u64 = ctx.r1.u64 - 512;
    call.r3.u64 = request;
    call.r4.u64 = status;
    fn(call, base);
  }
  ctx.r3.u64 = fn ? 0x103 /* STATUS_PENDING */ : status;
}

// Reads (type 7) take n packets of 1 ms each. A thread of our own answers them at
// the real rate, calling the request's callback with (request, status,
// u32 statuses[n], u16 lengths[n]).
std::atomic<bool> g_open{false};
std::mutex g_reads_mutex;
std::deque<uint32_t> g_reads;
std::atomic<bool> g_reader_started{false};
rex::system::object_ref<rex::system::XHostThread> g_reader;

int ReaderMain() {
  auto* kernel = rex::system::kernel_state();
  uint8_t* base = kernel->memory()->virtual_membase();
  const uint32_t scratch = kernel->memory()->SystemHeapAlloc(128);  // statuses u32[16], lengths u16[16]
  std::vector<int16_t> pcm;
  using Clock = std::chrono::steady_clock;
  auto next_due = Clock::now();
  timeBeginPeriod(1);
  for (;;) {
    uint32_t request = 0;
    {
      std::lock_guard lock(g_reads_mutex);
      if (!g_reads.empty()) request = g_reads.front();
    }
    const auto now = Clock::now();
    if (!request || now < next_due) {
      Sleep(1);
      continue;
    }
    {
      std::lock_guard lock(g_reads_mutex);
      g_reads.pop_front();
    }
    const uint32_t packets = std::clamp<uint32_t>(Load32(base, request + 24), 1, 16);
    const uint32_t data = Load32(base, request + 20);
    const uint32_t context = Load32(base, request + 12);
    const uint32_t record = context ? Load32(base, context + 40) : 0;
    uint32_t bytes_per_packet = record ? Load32(base, record + 8) : 96;
    if (bytes_per_packet == 0 || bytes_per_packet > 1024) bytes_per_packet = 96;
    pcm.resize(size_t(packets) * bytes_per_packet / 2);
    ReadSamples(pcm.data(), pcm.size());
    // Little-endian PCM, as it comes over USB.
    if (data) std::memcpy(base + data, pcm.data(), pcm.size() * sizeof(int16_t));
    for (uint32_t i = 0; i < packets; ++i) {
      Store32(base, scratch + i * 4, 0);
      Store16(base, scratch + 64 + i * 2, uint16_t(bytes_per_packet));
    }
    if (const uint32_t callback = Load32(base, request + 16)) {
      uint64_t args[] = {request, 0, scratch, scratch + 64};
      kernel->function_dispatcher()->Execute(g_reader->thread_state(), callback, args, 4);
    }
    // n packets of 1 ms; never more than 50 ms behind.
    next_due = std::max(next_due, now - std::chrono::milliseconds(50)) + std::chrono::milliseconds(packets);
  }
}

void QueueRead(uint32_t request) {
  {
    std::lock_guard lock(g_reads_mutex);
    g_reads.push_back(request);
  }
  bool expected = false;
  if (g_reader_started.compare_exchange_strong(expected, true)) {
    g_reader = rex::system::object_ref<rex::system::XHostThread>(
        new rex::system::XHostThread(rex::system::kernel_state(), 64 * 1024, 0, [] { return ReaderMain(); }));
    g_reader->set_name("re-wor microphone");
    g_reader->Create();
  }
}

}  // namespace

// XamUserGetDeviceContext(user, type, &context): type 4 asks for the user's
// microphone. Otherwise as the SDK answers it.
REX_HOOK_RAW(__imp__XamUserGetDeviceContext) {
  const uint32_t user = ctx.r3.u32 & 0xFF, type = ctx.r4.u32, out = ctx.r5.u32;
  uint32_t value = 0;
  if (MicOn() && type == 4 && user == MicUser()) value = 0x60000001;  // USB microphone on port 1
  if (out) Store32(base, out, value);
  ctx.r3.u64 = (value || user == 0 || user == 0xFF) ? 0 : 0x48F /* ERROR_DEVICE_NOT_CONNECTED */;
}

// MicDeviceRequest(request): +0 u16 type, +2 u16 port, +4 u32 result, +12 context,
// +16 callback(request, status), +20 buffer, +24 u16 length, +28 USB setup packet
// (bmRequestType, bRequest, wValue, wIndex, wLength).
REX_HOOK_RAW(__imp__MicDeviceRequest) {
  const uint32_t request = ctx.r3.u32;
  const uint16_t type = Load16(base, request), port = Load16(base, request + 2);
  if (!MicOn() || port != 1) {
    ctx.r3.u64 = 0xC000009D;  // STATUS_DEVICE_NOT_CONNECTED
    return;
  }
  switch (type) {
    case 0:  // state: 3 = connected and configured, 5 = open and streaming
      Store32(base, request + 4, g_open ? 5 : 3);
      ctx.r3.u64 = 0;
      return;
    case 8:  // sample rates: 32 kHz (0x40) and 48 kHz (0x100)
      Store32(base, request + 20, 0x140);
      Complete(ctx, base, request, 0);
      return;
    case 1:  // open at the chosen rate
      g_open = true;
      StartCapture();
      REXLOG_INFO("re-wor mic: the game opened the microphone");
      Complete(ctx, base, request, 0);
      return;
    case 7:  // read n packets of 1 ms: answered by the reader thread
      QueueRead(request);
      ctx.r3.u64 = 0x103;  // STATUS_PENDING
      return;
    case 3:
    case 4:
    case 5:
    case 6:
    case 9:
      Complete(ctx, base, request, 0);
      return;
    case 2: {  // USB audio class control transfer: the microphone's volume
      const uint8_t request_type = base[request + 28], code = base[request + 29];
      const uint32_t buffer = Load32(base, request + 20);
      const uint16_t length = Load16(base, request + 24);
      if ((request_type & 0x80) && buffer && length >= 2) {
        // GET_CUR 0x81 / GET_MIN 0x82 / GET_MAX 0x83 / GET_RES 0x84, in 1/256 dB, little endian.
        const int16_t value = code == 0x82   ? int16_t(-0x0C00)
                              : code == 0x83 ? int16_t(0x0C00)
                              : code == 0x84 ? int16_t(0x0100)
                                             : int16_t(0);
        base[buffer] = uint8_t(value & 0xFF);
        base[buffer + 1] = uint8_t(uint16_t(value) >> 8);
      }
      Complete(ctx, base, request, 0);
      return;
    }
    default:
      ctx.r3.u64 = 0xC0000001;  // STATUS_UNSUCCESSFUL
      return;
  }
}
