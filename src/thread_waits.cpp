// The game's threads wait without spinning.
//
// Some loops of the game wait by checking a condition again and again. On the
// console that costs a hardware thread and nothing else; here each one kept a
// host core busy, which a PC with few cores needs for the render thread, the
// GPU emulation and the audio. Found with rewor_sample_file (src/sampler.cpp)
// in the song list, where they took most of their threads' time:
// - the main thread waiting for the render thread to finish a frame
//   (sub_8242ABE8: while (frames[24] == frames[20]) {}), about 75 % of its time;
// - the render thread waiting for the vertical blank counter (sub_825840F0:
//   while (sub_8239FDC0() < next) {}), once per frame;
// - the D3D library's waits for the GPU, which call sub_822D5D60 for every check.
//
// The hooks in rewor_functions.toml leave each loop as it is and make its checks
// cheap: a wait first spins briefly, so short waits are unchanged, and then the
// thread sleeps in short slices between checks.
//
// Adapted from GHVanHRecomp's src/thread_waits.cpp (MIT License, Copyright (c)
// 2026 Gustavo Garcia), which found the same loops in Guitar Hero: Van Halen,
// on the same engine family.

#include <rex/cvar.h>
#include <rex/ppc/context.h>

#include <immintrin.h>

#include <algorithm>
#include <chrono>

#ifdef _WIN32
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>
#endif

REXCVAR_DEFINE_INT32(rewor_wait_sleep_us, 200, "re-wor",
                     "Game threads waiting for each other: sleep this many microseconds per check once the wait is "
                     "longer than rewor_wait_spin_us (0 = busy wait, as on the console)");
REXCVAR_DEFINE_INT32(rewor_wait_spin_us, 100, "re-wor",
                     "Game threads waiting for each other: busy-wait this long before sleeping");

namespace {

using Clock = std::chrono::steady_clock;

// A check that comes more than this long after the previous one on the same
// thread starts a new wait: the loop did other work in between.
constexpr auto kNewWaitGap = std::chrono::milliseconds(1);

struct WaitState {
  Clock::time_point start;
  Clock::time_point last;
};

thread_local WaitState t_render_wait;
thread_local WaitState t_vblank_wait;
thread_local WaitState t_gpu_wait;
thread_local WaitState t_frame_wait;
thread_local WaitState t_vblank_wait2;

// Sleeps about this long. A plain Sleep has the system timer's steps (up to
// 15.6 ms), far too coarse for a frame of 16.7 ms, so it uses a high resolution
// waitable timer, one per thread.
void ShortSleep(std::chrono::microseconds duration) {
#ifdef _WIN32
  struct Timer {
    HANDLE handle = CreateWaitableTimerExW(nullptr, nullptr, CREATE_WAITABLE_TIMER_HIGH_RESOLUTION, TIMER_ALL_ACCESS);
    ~Timer() {
      if (handle) {
        CloseHandle(handle);
      }
    }
  };
  thread_local Timer timer;
  if (timer.handle) {
    LARGE_INTEGER due;
    due.QuadPart = -static_cast<LONGLONG>(duration.count()) * 10;
    if (SetWaitableTimerEx(timer.handle, &due, 0, nullptr, nullptr, nullptr, 0)) {
      WaitForSingleObject(timer.handle, INFINITE);
      return;
    }
  }
  Sleep(0);
#endif
}

// One check of a wait whose condition is still false.
void SpinOrSleep(WaitState& state) {
  const int32_t sleep_us = REXCVAR_GET(rewor_wait_sleep_us);
  if (sleep_us <= 0) {
    return;
  }
  const Clock::time_point now = Clock::now();
  if (now - state.last > kNewWaitGap) {
    state.start = now;
  }
  state.last = now;
  const int32_t spin_us = std::max(0, REXCVAR_GET(rewor_wait_spin_us));
  if (now - state.start < std::chrono::microseconds(spin_us)) {
    _mm_pause();
    return;
  }
  ShortSleep(std::chrono::microseconds(sleep_us));
  state.last = Clock::now();
}

}  // namespace

// [[midasm_hook]] 0x8242AC1C (sub_8242ABE8): before "cmplw cr6,r11,r10", r11 and
// r10 = the frame counters (the main thread goes on once they differ).
void ReWorRenderWaitHook(PPCRegister& r11, PPCRegister& r10) {
  if (r11.u32 == r10.u32) {
    SpinOrSleep(t_render_wait);
  }
}

// [[midasm_hook]] 0x825841B0 (sub_825840F0): before "cmpld cr6,r3,r11", r3 = the
// vertical blank counter just read and r11 the value the render thread waits for.
void ReWorVblankWaitHook(PPCRegister& r3, PPCRegister& r11) {
  if (r3.u64 < r11.u64) {
    SpinOrSleep(t_vblank_wait);
  }
}

// [[midasm_hook]] 0x825842F8 (sub_825840F0): before "cmpld cr6,r3,r30", the same
// wait further down the function: r3 = the counter just read (sub_8239FDD0)
// and r30 the value the render thread waits for.
void ReWorVblankWait2Hook(PPCRegister& r3, PPCRegister& r30) {
  if (r3.u64 < r30.u64) {
    SpinOrSleep(t_vblank_wait2);
  }
}

// [[midasm_hook]] 0x8242C51C (sub_8242C450, the main thread's frame): before
// "cmplwi cr6,r3,0", r3 = what sub_825528E0 returned; the loop calls it again
// until it returns 0.
void ReWorFrameWaitHook(PPCRegister& r3) {
  if (r3.u32 != 0) {
    SpinOrSleep(t_frame_wait);
  }
}

// [[midasm_hook]] at the entry of sub_822D5D60: a check of a D3D wait for the GPU
// whose condition is still false.
void ReWorGpuWaitHook() { SpinOrSleep(t_gpu_wait); }
