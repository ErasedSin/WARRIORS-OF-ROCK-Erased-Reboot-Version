// Diagnostic sampler: finds where the game's threads spend their time, to spot
// the guest loops that wait by checking a condition again and again (on the
// console each costs a hardware thread; here each one keeps a host core busy).
//
// With rewor_sample_file set, a background thread waits rewor_sample_delay
// seconds (time to start a song), then, once a millisecond for
// rewor_sample_seconds, suspends each other thread of the process, reads where
// it is, walks a few frames up its stack and resumes it. It writes, per thread,
// the share of samples and the addresses seen most, as "module+offset". The exe's
// offsets are turned into guest functions (sub_XXXXXXXX) with the linker map,
// rewor.map, which the build writes next to the build folder.
//
// Off by default; it only exists to measure.

#include <rex/cvar.h>

#include <algorithm>
#include <chrono>
#include <cstdio>
#include <cstring>
#include <map>
#include <string>
#include <thread>
#include <vector>

#ifdef _WIN32
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>
#include <tlhelp32.h>
#endif

REXCVAR_DEFINE_STRING(rewor_sample_file, "", "re-wor",
                      "Diagnostic: file that receives where each thread spent its time (empty = off)");
REXCVAR_DEFINE_INT32(rewor_sample_delay, 60, "re-wor",
                     "Diagnostic: seconds after start before sampling (time to start a song)");
REXCVAR_DEFINE_INT32(rewor_sample_seconds, 20, "re-wor", "Diagnostic: how long to sample, in seconds");

#ifdef _WIN32
namespace {

constexpr int kFrames = 6;

struct Sample {
  DWORD tid;
  int window;  // which 5 s slice of the sampling it was taken in
  DWORD64 pc[kFrames];
  int depth;
};

// Walks up to kFrames frames from a suspended thread's context with the x64
// unwind data (present in every module, no symbols needed).
int Walk(CONTEXT ctx, DWORD64* out) {
  int depth = 0;
  for (; depth < kFrames && ctx.Rip; ++depth) {
    out[depth] = ctx.Rip;
    DWORD64 image_base = 0;
    PRUNTIME_FUNCTION entry = RtlLookupFunctionEntry(ctx.Rip, &image_base, nullptr);
    if (!entry) {
      // A leaf function: the return address is at the top of the stack.
      ctx.Rip = *reinterpret_cast<DWORD64*>(ctx.Rsp);
      ctx.Rsp += 8;
      continue;
    }
    PVOID handler_data = nullptr;
    DWORD64 establisher = 0;
    RtlVirtualUnwind(UNW_FLAG_NHANDLER, image_base, ctx.Rip, entry, &ctx, &handler_data, &establisher, nullptr);
  }
  return depth;
}

std::string Describe(DWORD64 pc) {
  HMODULE module = nullptr;
  if (!GetModuleHandleExA(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                          reinterpret_cast<LPCSTR>(pc), &module) ||
      !module) {
    char buf[32];
    std::snprintf(buf, sizeof(buf), "?+0x%llx", static_cast<unsigned long long>(pc));
    return buf;
  }
  char path[MAX_PATH] = {};
  GetModuleFileNameA(module, path, MAX_PATH);
  const char* name = std::strrchr(path, '\\');
  name = name ? name + 1 : path;
  char buf[MAX_PATH + 32];
  std::snprintf(buf, sizeof(buf), "%s+0x%llx", name,
                static_cast<unsigned long long>(pc - reinterpret_cast<DWORD64>(module)));
  return buf;
}

void Run() {
  std::this_thread::sleep_for(std::chrono::seconds(2));
  const std::string path = REXCVAR_GET(rewor_sample_file);
  if (path.empty()) {
    return;
  }
  std::this_thread::sleep_for(std::chrono::seconds(std::max(0, REXCVAR_GET(rewor_sample_delay))));

  const DWORD self = GetCurrentThreadId();
  const DWORD pid = GetCurrentProcessId();
  const auto begin = std::chrono::steady_clock::now();
  const auto end = begin + std::chrono::seconds(std::max(1, REXCVAR_GET(rewor_sample_seconds)));

  // Filled while threads are suspended, so nothing here may allocate then (a
  // suspended thread may hold the heap's lock): room is reserved up front.
  std::vector<Sample> samples;
  samples.reserve(400000);
  std::vector<DWORD> tids;
  tids.reserve(256);
  int rounds = 0;

  while (std::chrono::steady_clock::now() < end && samples.size() + 256 < samples.capacity()) {
    tids.clear();
    HANDLE snap = CreateToolhelp32Snapshot(TH32CS_SNAPTHREAD, 0);
    if (snap != INVALID_HANDLE_VALUE) {
      THREADENTRY32 te{};
      te.dwSize = sizeof(te);
      for (BOOL ok = Thread32First(snap, &te); ok; ok = Thread32Next(snap, &te)) {
        if (te.th32OwnerProcessID == pid && te.th32ThreadID != self && tids.size() < tids.capacity()) {
          tids.push_back(te.th32ThreadID);
        }
      }
      CloseHandle(snap);
    }
    for (DWORD tid : tids) {
      HANDLE thread = OpenThread(THREAD_SUSPEND_RESUME | THREAD_GET_CONTEXT | THREAD_QUERY_INFORMATION, FALSE, tid);
      if (!thread) {
        continue;
      }
      if (SuspendThread(thread) != static_cast<DWORD>(-1)) {
        CONTEXT ctx{};
        ctx.ContextFlags = CONTEXT_FULL;
        if (GetThreadContext(thread, &ctx)) {
          Sample s{};
          s.tid = tid;
          s.window = static_cast<int>(std::chrono::duration_cast<std::chrono::seconds>(std::chrono::steady_clock::now() - begin).count() / 5);
          s.depth = Walk(ctx, s.pc);
          samples.push_back(s);
        }
        ResumeThread(thread);
      }
      CloseHandle(thread);
    }
    ++rounds;
    std::this_thread::sleep_for(std::chrono::milliseconds(1));
  }

  // Per thread: the share of rounds it was seen in each place. The deepest
  // frame inside the exe is the guest function the time belongs to.
  std::map<DWORD, std::map<std::string, int>> leaf;
  std::map<DWORD, std::map<std::string, int>> in_exe;
  std::map<DWORD, int> per_thread;
  const auto exe = reinterpret_cast<DWORD64>(GetModuleHandleA(nullptr));
  for (const Sample& s : samples) {
    ++per_thread[s.tid];
    leaf[s.tid][Describe(s.pc[0])]++;
    for (int i = 0; i < s.depth; ++i) {
      HMODULE module = nullptr;
      if (GetModuleHandleExA(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                             reinterpret_cast<LPCSTR>(s.pc[i]), &module) &&
          reinterpret_cast<DWORD64>(module) == exe) {
        std::string chain = Describe(s.pc[i]);
        for (int j = i + 1; j < s.depth && j < i + 3; ++j) {
          chain += " <- " + Describe(s.pc[j]);
        }
        in_exe[s.tid][chain]++;
        break;
      }
    }
  }

  FILE* out = std::fopen(path.c_str(), "w");
  if (!out) {
    return;
  }
  std::fprintf(out, "rounds %d, samples %zu\n", rounds, samples.size());
  for (const auto& [tid, count] : per_thread) {
    std::fprintf(out, "\n== thread %lu (%d samples)\n", static_cast<unsigned long>(tid), count);
    for (auto* table : {&leaf[tid], &in_exe[tid]}) {
      std::fprintf(out, table == &leaf[tid] ? "  leaf:\n" : "  exe frame:\n");
      std::vector<std::pair<int, std::string>> rows;
      for (const auto& [where, n] : *table) {
        rows.emplace_back(n, where);
      }
      std::sort(rows.rbegin(), rows.rend());
      for (size_t i = 0; i < rows.size() && i < 12; ++i) {
        std::fprintf(out, "    %5.1f%%  %s\n", 100.0 * rows[i].first / std::max(1, rounds), rows[i].second.c_str());
      }
    }
  }
  // Per 5 s slice, all threads together: the guest function each sample was
  // in (its deepest frame inside the exe, without the offset). Comparing the
  // slices of the menus with those of a song shows the functions only a song
  // runs.
  std::map<int, std::map<std::string, int>> per_window;
  for (const Sample& s : samples) {
    for (int i = 0; i < s.depth; ++i) {
      HMODULE module = nullptr;
      if (GetModuleHandleExA(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                             reinterpret_cast<LPCSTR>(s.pc[i]), &module) &&
          reinterpret_cast<DWORD64>(module) == exe) {
        per_window[s.window][Describe(s.pc[i])]++;
        break;
      }
    }
  }
  for (const auto& [window, table] : per_window) {
    std::fprintf(out, "\n## window %d (%d-%d s)\n", window, window * 5, window * 5 + 5);
    for (const auto& [where, n] : table) {
      std::fprintf(out, "    %d  %s\n", n, where.c_str());
    }
  }
  std::fclose(out);
}

}  // namespace

void rewor_start_sampler() { std::thread(Run).detach(); }
#else
void rewor_start_sampler() {}
#endif
