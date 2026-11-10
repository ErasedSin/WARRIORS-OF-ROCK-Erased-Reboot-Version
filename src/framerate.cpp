// Frame rates above 60.
//
// The game presents one frame per vertical blank of the SDK's emulated video
// (video_mode_refresh_rate, which the launcher's frame rate option sets), and
// its engine clock follows real time, so at 120 or 144 Hz it simply draws more
// frames at the same speed. A few things in it, though, are counted in frames
// and would run faster:
// - clocks counted in vertical blanks, divided by the global 0x82860438 (60);
// - script waits of N game frames ("Wait 1 gameframe" and the like);
// - button presses that scripts poll with ControllerMake, which last one frame;
// - the whammy on a sustain, compared with the previous frame and counted in
//   runs of 5 frames;
// - the loading screen's spinning star and icon, turned by a step per frame;
// - the guitar strings' 62 Hz vibration and its fade per frame.
// The hooks below keep each of them at the console's 60 per second. At 60 Hz
// (or with rewor_frame_rate_fix off) none of them changes anything.
//
// The addresses are for the retail default.xex (CRC32 62201F03).

#include <rex/cvar.h>
#include <rex/logging.h>
#include <rex/ppc/context.h>
#include <rex/system/kernel_state.h>
#include <rex/system/xmemory.h>

#include <algorithm>
#include <atomic>
#include <chrono>
#include <cmath>
#include <cstring>
#include <iterator>
#include <mutex>
#include <unordered_map>

REXCVAR_DEFINE_BOOL(rewor_frame_rate_fix, true, "re-wor",
                    "Above 60 FPS (video_mode_refresh_rate): keep what the game counts in frames (script waits, "
                    "button presses polled by scripts, the whammy, the loading screen) at the console's speed");

namespace {

using Clock = std::chrono::steady_clock;

// The emulated vertical blanks per second: video_mode_refresh_rate with vsync,
// one a millisecond without it. Looked up once a second per thread.
double VblankRate() {
  thread_local double rate = 60.0;
  thread_local Clock::time_point checked;
  const Clock::time_point now = Clock::now();
  if (checked == Clock::time_point{} || now - checked > std::chrono::seconds(1)) {
    checked = now;
    rate = rex::cvar::Query<bool>("vsync")
               ? std::clamp(rex::cvar::Query<double>("video_mode_refresh_rate"), 24.0, 240.0)
               : 1000.0;
  }
  return rate;
}

bool Above60() { return REXCVAR_GET(rewor_frame_rate_fix) && VblankRate() != 60.0; }

// The main thread's frames as measured: the last one and a running average,
// so what is counted in frames keeps its time when the game falls below the
// chosen rate.
std::atomic<double> g_last_frame{0.0};
std::atomic<double> g_mean_frame{0.0};

double MeanFrame() {
  const double mean = g_mean_frame.load(std::memory_order_relaxed);
  return mean > 0.0 ? mean : 1.0 / VblankRate();
}

double LastFrame() {
  const double last = g_last_frame.load(std::memory_order_relaxed);
  return last > 0.0 ? last : 1.0 / VblankRate();
}

uint8_t* GuestBase() {
  auto* kernel = rex::system::kernel_state();
  return kernel && kernel->memory() ? kernel->memory()->virtual_membase() : nullptr;
}

uint32_t ReadU32(const uint8_t* base, uint32_t address) {
  uint32_t value;
  std::memcpy(&value, base + address, 4);
  return __builtin_bswap32(value);
}

void WriteU32(uint8_t* base, uint32_t address, uint32_t value) {
  value = __builtin_bswap32(value);
  std::memcpy(base + address, &value, 4);
}

}  // namespace

// [[midasm_hook]] 0x8242C57C, the end of the main thread's frame (sub_8242C450).
// A gap outside 0.25 to 50 ms (a load) is clamped, not taken as a frame.
void ReWorFrameEndHook() {
  static Clock::time_point previous;  // main thread only
  const Clock::time_point now = Clock::now();
  if (previous != Clock::time_point{}) {
    const double seconds = std::clamp(std::chrono::duration<double>(now - previous).count(), 0.00025, 0.05);
    g_last_frame.store(seconds, std::memory_order_relaxed);
    const double mean = g_mean_frame.load(std::memory_order_relaxed);
    g_mean_frame.store(mean > 0.0 ? mean + (seconds - mean) * 0.1 : seconds, std::memory_order_relaxed);
  }
  previous = now;
}

// [[midasm_hook]] 0x82629360 and 0x82629588 (sub_826291F8): r30 = the game's
// vertical blanks per second, which turns a clock counted in vertical blanks
// into seconds and back. Scaled to the emulated rate.
void ReWorVblankClockHook(PPCRegister& r30) {
  if (!Above60()) {
    return;
  }
  static std::atomic<bool> told{false};
  if (!told.exchange(true)) {
    REXLOG_WARN("[frame rate] {:.0f} Hz: vblank clocks scaled from {}", VblankRate(), int32_t(r30.u32));
  }
  r30.u64 = uint64_t(int64_t(std::lround(double(int32_t(r30.u32)) * VblankRate() / 60.0)));
}

// Script waits in game frames. Each one is given an end time at 60 frames a
// second; its count is held above zero until the frame nearest that end. A wait
// set again right as the previous one on the same script ended (a loop) starts
// at that end, so loops keep the console's pace.
namespace {

// Scripts that add the frame's own length each pass (star power's drain,
// button_checker, timers on GetFrameLength / GetDeltaTime) must run every frame.
constexpr uint32_t kTimedByFrameLength[] = {
    0x74B936AB, 0x716BADDC, 0xC8580B17, 0xD1260990, 0x473164C7, 0x06913019, 0xCF931794,
    0x17D365CC, 0xEE68B834, 0x3D11F8E9, 0xA947584A, 0x75DBE425, 0xEC23DF7E, 0xE66D1DA1,
    0x4E850682, 0xE20DF41E, 0x6742F67A, 0xE5C4CFDD, 0x3E4FDDAF,
};

// Scripts that line up with the band's and the stage's alternate frames
// (IsPrimaryFrame) and the scripts that call them: also every frame.
constexpr uint32_t kAlignedToFrames[] = {
    0xCAD261DF, 0x6506F6C8, 0x9A8F399F, 0xC09883B8, 0xF1C14221, 0xF93E0168, 0x0050D48F, 0x01DE43A7,
    0x094DD2E1, 0x0DA14A2E, 0x309DB495, 0x3B948900, 0x44DDB52D, 0x45233C61, 0x4F4822E4, 0x57EA03DA,
    0x679800B9, 0x80E45206, 0x883F7786, 0x9AFA7534, 0x9FFB805D, 0xBF51BD3C, 0xC1CA119A, 0xE4DE9864,
};

template <size_t N>
bool Contains(const uint32_t (&list)[N], uint32_t value) {
  return std::find(std::begin(list), std::end(list), value) != std::end(list);
}

struct ScriptWait {
  Clock::time_point start;
  Clock::time_point end;
};

std::mutex g_waits_mutex;
std::unordered_map<uint32_t, ScriptWait> g_waits;  // by the wait's guest address

}  // namespace

// [[midasm_hook]] entry of sub_8256B2A0 and sub_8256B250, a wait of N game
// frames being set: r3 = the wait, r4 = N. The script's name is at +60.
void ReWorScriptWaitSetHook(PPCRegister& r3, PPCRegister& r4) {
  if (!Above60()) {
    return;
  }
  const uint8_t* base = GuestBase();
  const uint32_t script = base ? ReadU32(base, r3.u32 + 60) : 0;
  std::lock_guard lock(g_waits_mutex);
  if (Contains(kTimedByFrameLength, script) || Contains(kAlignedToFrames, script)) {
    g_waits.erase(r3.u32);
    return;
  }
  const Clock::time_point now = Clock::now();
  const auto frame = std::chrono::duration<double>(MeanFrame());
  ScriptWait& wait = g_waits[r3.u32];
  const bool loop = wait.end != Clock::time_point{} &&
                    std::chrono::abs(std::chrono::duration<double>(now - wait.end)) <= frame * 1.5;
  wait.start = loop ? wait.end : now;
  wait.end = wait.start + std::chrono::duration_cast<Clock::duration>(
                              std::chrono::duration<double>(double(int32_t(r4.u32)) / 60.0));
}

// [[midasm_hook]] 0x8256B5E4 (sub_8256B5A0), before the count of a game-frame
// wait goes down by one: r3 = the wait, r11 = its count. 1 makes it end next
// frame, 2 keeps it waiting.
void ReWorScriptWaitCountHook(PPCRegister& r3, PPCRegister& r11) {
  if (!Above60()) {
    return;
  }
  Clock::time_point end;
  {
    std::lock_guard lock(g_waits_mutex);
    auto it = g_waits.find(r3.u32);
    if (it == g_waits.end()) {
      return;
    }
    end = it->second.end;
  }
  static std::atomic<bool> told{false};
  if (!told.exchange(true)) {
    REXLOG_WARN("[frame rate] {:.0f} Hz: script frame waits keep the console's 60 per second", VblankRate());
  }
  const double left = std::chrono::duration<double>(end - Clock::now()).count();
  r11.u64 = left < MeanFrame() * 1.5 ? 1 : 2;
}

// Button presses polled by scripts. A press shows in a controller's state (+52)
// for one frame; a script held to 60 passes a second would miss presses in the
// frames it skips. Each script's ControllerMake is given, once, the presses of
// the updates since its previous pass.
namespace {

constexpr int kHistory = 8;

struct PadHistory {
  uint32_t state = 0;
  uint32_t updates = 0;
  uint32_t pressed[kHistory] = {};
  Clock::time_point when[kHistory] = {};
};

struct ScriptPass {
  uint32_t current = 0;
  uint32_t previous = 0;
  Clock::time_point last;
};

struct Restore {
  uint32_t state, written, original;
};

std::mutex g_pads_mutex;
PadHistory g_pads[4];
std::unordered_map<uint64_t, ScriptPass> g_passes;  // script << 32 | state
thread_local Restore t_restore[4];
thread_local int t_restore_count = 0;

}  // namespace

// [[midasm_hook]] 0x824CD5B0 (sub_824CD3D8), a controller's state updated:
// r30 = the state, r9 = the buttons pressed this frame.
void ReWorPadUpdateHook(PPCRegister& r30, PPCRegister& r9) {
  if (!Above60()) {
    return;
  }
  std::lock_guard lock(g_pads_mutex);
  for (PadHistory& pad : g_pads) {
    if (pad.state == 0 || pad.state == r30.u32) {
      pad.state = r30.u32;
      const int slot = int(++pad.updates % kHistory);
      pad.pressed[slot] = r9.u32;
      pad.when[slot] = Clock::now();
      return;
    }
  }
}

// [[midasm_hook]] entry of sub_824D58C8 (ControllerMake): r4 = the script.
void ReWorPadPollHook(PPCRegister& r4) {
  t_restore_count = 0;
  uint8_t* base = GuestBase();
  if (!base || !Above60()) {
    return;
  }
  const Clock::time_point now = Clock::now();
  const auto window = std::chrono::duration<double>(1.0 / 60.0 + MeanFrame());
  std::lock_guard lock(g_pads_mutex);
  if (g_passes.size() > 256) {
    for (auto it = g_passes.begin(); it != g_passes.end();) {
      it = now - it->second.last > std::chrono::seconds(1) ? g_passes.erase(it) : std::next(it);
    }
  }
  for (const PadHistory& pad : g_pads) {
    if (pad.state == 0) {
      continue;
    }
    ScriptPass& pass = g_passes[uint64_t(r4.u32) << 32 | pad.state];
    if (pass.last == Clock::time_point{} || now - pass.last > window * 4) {
      pass.current = pass.previous = pad.updates;
    } else if (pass.current != pad.updates) {
      pass.previous = pass.current;
      pass.current = pad.updates;
    }
    pass.last = now;
    uint32_t missed = 0;
    const uint32_t oldest = pass.current >= kHistory ? pass.current - kHistory + 1 : 1;
    for (uint32_t update = std::max(pass.previous + 1, oldest); update < pass.current; ++update) {
      const int slot = int(update % kHistory);
      if (now - pad.when[slot] <= window) {
        missed |= pad.pressed[slot];
      }
    }
    const uint32_t original = ReadU32(base, pad.state + 52);
    if ((missed & ~original) && t_restore_count < 4) {
      WriteU32(base, pad.state + 52, original | missed);
      t_restore[t_restore_count++] = {pad.state, original | missed, original};
    }
  }
}

// [[midasm_hook]] 0x824D5988, ControllerMake's exit: the pressed bits put back.
void ReWorPadPollEndHook() {
  uint8_t* base = GuestBase();
  for (int i = 0; base && i < t_restore_count; ++i) {
    if (ReadU32(base, t_restore[i].state + 52) == t_restore[i].written) {
      WriteU32(base, t_restore[i].state + 52, t_restore[i].original);
    }
  }
  t_restore_count = 0;
}

// The whammy on a sustain: the bar's movement is compared with its position a
// 60th of a second ago (not the previous frame's), and its runs of 5 frames are
// 5 / 60 s in frames of the measured length.
namespace {

struct WhammyHistory {
  Clock::time_point when[8] = {};
  float position[8] = {};
  int next = 0;
};

struct WhammyRun {
  int32_t last = 0;
  int frames = 0;
};

std::mutex g_whammy_mutex;
std::unordered_map<uint64_t, WhammyHistory> g_whammy;  // object << 1 | axis
std::unordered_map<uint32_t, WhammyRun> g_whammy_runs;  // by the count's address

int SixtiethsAsFrames(int sixtieths) { return std::max(1, int(std::lround(sixtieths / 60.0 / MeanFrame()))); }

void CompareOverASixtieth(int axis, uint32_t object, PPCRegister& position, PPCRegister& previous) {
  if (!Above60()) {
    return;
  }
  const Clock::time_point now = Clock::now();
  const double frame = MeanFrame();
  std::lock_guard lock(g_whammy_mutex);
  WhammyHistory& history = g_whammy[uint64_t(object) << 1 | uint64_t(axis)];
  for (int i = 1; i <= 8; ++i) {
    const int slot = (history.next - i) & 7;
    const double age = std::chrono::duration<double>(now - history.when[slot]).count();
    if (age >= 1.0 / 60.0 - frame * 0.5) {
      if (age <= 1.0 / 60.0 + frame * 1.5) {
        previous.f64 = history.position[slot];
      }
      break;
    }
  }
  history.when[history.next] = now;
  history.position[history.next] = float(position.f64);
  history.next = (history.next + 1) & 7;
}

}  // namespace

// [[midasm_hook]] 0x82187120, 0x82187244 (sub_82186FF8) and 0x82186F94
// (sub_82186E08), before the position minus the previous frame's: r31 = the
// note hold object, then the position and the previous one.
void ReWorWhammyHook(PPCRegister& object, PPCRegister& position, PPCRegister& previous) {
  CompareOverASixtieth(0, object.u32, position, previous);
}

// [[midasm_hook]] 0x82187258 (sub_82186FF8), a controller's second axis.
void ReWorWhammyAxis2Hook(PPCRegister& object, PPCRegister& position, PPCRegister& previous) {
  CompareOverASixtieth(1, object.u32, position, previous);
}

// [[midasm_hook]] 0x82187140, 0x82187278 (r29) and 0x82186FC0 (r9): the
// whammy's countdown of 5 frames before it is stored.
void ReWorWhammyCountdownHook(PPCRegister& frames) {
  if (Above60()) {
    frames.u64 = uint64_t(SixtiethsAsFrames(int32_t(frames.u32)));
  }
}

// [[midasm_hook]] 0x8218EB24 and 0x8218EC10 (sub_8218E9A0), before the frames
// in a row of whammying (or not) are compared with 5: r30 / r10 = the count's
// address, r11 = the count. A run keeps the length it began with.
void ReWorWhammyRunHook(PPCRegister& count_address, PPCRegister& r11) {
  if (!Above60()) {
    return;
  }
  const int32_t count = int32_t(r11.u32);
  int frames;
  {
    std::lock_guard lock(g_whammy_mutex);
    WhammyRun& run = g_whammy_runs[count_address.u32];
    if (run.frames == 0 || count <= run.last) {
      run.frames = SixtiethsAsFrames(5);
    }
    run.last = count;
    frames = run.frames;
  }
  r11.u64 = count == frames ? 5 : 0;
}

// The loading screen, drawn by the render thread, turns its star and its icon
// by a fixed step per frame: scaled by the frame's length in 60ths of a second.
namespace {

double LoadingStepScale(int which) {
  static Clock::time_point last[2];  // render thread only
  const Clock::time_point now = Clock::now();
  double seconds = 1.0 / VblankRate();
  if (last[which] != Clock::time_point{}) {
    const double since = std::chrono::duration<double>(now - last[which]).count();
    if (since <= 0.1) {
      seconds = std::min(since, 0.05);
    }
  }
  last[which] = now;
  return seconds * 60.0;
}

}  // namespace

// [[midasm_hook]] 0x825F9E30 (sub_825F9D10): f31 = the star's turn this frame.
void ReWorLoadingStarHook(PPCRegister& f31) {
  if (Above60()) {
    f31.f64 *= LoadingStepScale(0);
  }
}

// [[midasm_hook]] 0x8243190C (sub_824318C0): r10 = the icon's step in whole
// degrees; the fraction is carried to the next frame.
void ReWorLoadingIconHook(PPCRegister& r10) {
  if (!Above60()) {
    return;
  }
  static double carry = 0.0;  // render thread only
  const double step = double(int32_t(r10.u32)) * LoadingStepScale(1) + carry;
  const double whole = std::floor(step);
  carry = step - whole;
  r10.u64 = uint64_t(int64_t(whole));
}

// The guitar strings vibrate at 62 Hz sampled once a frame, which at 60 FPS
// shows as a slow 2 Hz wobble, and fade by 0.99 a frame. Above 60 the wobble
// keeps the frequency 60 FPS shows and the fade its rate per 60th of a second.

// [[midasm_hook]] 0x82100950 (sub_82100910): f12 = the fade factor per frame.
void ReWorStringFadeHook(PPCRegister& f12) {
  if (Above60() && f12.f64 > 0.0) {
    f12.f64 = double(float(std::pow(f12.f64, LastFrame() * 60.0)));
  }
}

// [[midasm_hook]] 0x821008E8 (sub_821005F0): f1 = the strings' frequency in Hz.
void ReWorStringFrequencyHook(PPCRegister& f1) {
  if (Above60()) {
    f1.f64 -= 60.0 * std::round(f1.f64 / 60.0);
  }
}
