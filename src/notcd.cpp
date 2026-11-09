// The game's debug menu.
//
// The menu scripts add the debug menu's entry when the globals 2AF92804 and
// enable_debug_menus are 1 (both are, in qb.pab), and several scripts run
// developer-only parts inside "if NotCD": NotCD is a C function of the exe that
// is true on a development machine (the game read from a PC, not a disc) and
// false on a retail disc.
//
// rewor_debug_scan finds NotCD in the running game: the exe registers its C
// functions in a table of { name, function } entries, so the name "NotCD" is a
// string in the image and one entry points at it. The report lists each
// entry, to turn into a hook that makes NotCD true.

#include <rex/cvar.h>
#include <rex/ppc/context.h>
#include <rex/system/kernel_state.h>
#include <rex/system/xmemory.h>

#include <cstdint>
#include <cstdio>
#include <cstring>
#include <string>
#include <vector>

#ifdef _WIN32
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>
#endif

REXCVAR_DEFINE_BOOL(rewor_debug_menu, false, "re-wor",
                    "Experimental: the game's developer-only parts, its debug menu among them (NotCD answers "
                    "true). Applies when the game starts.")
    .lifecycle(rex::cvar::Lifecycle::kRequiresRestart);
REXCVAR_DEFINE_STRING(rewor_debug_scan, "", "re-wor",
                      "Diagnostic: file that receives where the C function NotCD is registered (empty = off)");

namespace {

uint32_t LoadBE(const uint8_t* p) { return uint32_t(p[0]) << 24 | uint32_t(p[1]) << 16 | uint32_t(p[2]) << 8 | p[3]; }

// The readable ranges of guest memory in [begin, end), as host pointers.
std::vector<std::pair<const uint8_t*, size_t>> ReadableRanges(const uint8_t* base, uint32_t begin, uint32_t end) {
  std::vector<std::pair<const uint8_t*, size_t>> ranges;
#ifdef _WIN32
  const uint8_t* p = base + begin;
  const uint8_t* stop = base + end;
  while (p < stop) {
    MEMORY_BASIC_INFORMATION info{};
    if (!VirtualQuery(p, &info, sizeof(info))) break;
    const uint8_t* region_end = static_cast<const uint8_t*>(info.BaseAddress) + info.RegionSize;
    if (region_end > stop) region_end = stop;
    const DWORD readable = PAGE_READONLY | PAGE_READWRITE | PAGE_EXECUTE_READ | PAGE_EXECUTE_READWRITE | PAGE_WRITECOPY;
    if (info.State == MEM_COMMIT && (info.Protect & readable) && !(info.Protect & PAGE_GUARD)) {
      ranges.emplace_back(p, size_t(region_end - p));
    }
    p = region_end;
  }
#endif
  return ranges;
}

}  // namespace

// Called once, from the first frames of the game (src/main.cpp).
void ReWorScanForNotCD() {
  const std::string path = REXCVAR_GET(rewor_debug_scan);
  if (path.empty()) return;
  auto* kernel = rex::system::kernel_state();
  if (!kernel || !kernel->memory()) return;
  const uint8_t* base = kernel->memory()->virtual_membase();
  FILE* out = std::fopen(path.c_str(), "w");
  if (!out) return;

  const auto ranges = ReadableRanges(base, 0x82000000u, 0x84000000u);
  static const char kName[] = "NotCD";
  std::vector<uint32_t> strings;
  for (const auto& [p, size] : ranges) {
    for (size_t i = 0; i + sizeof(kName) <= size; ++i) {
      if (std::memcmp(p + i, kName, sizeof(kName)) == 0) {
        strings.push_back(uint32_t(p + i - base));
      }
    }
  }
  std::fprintf(out, "\"NotCD\" strings: %zu\n", strings.size());
  for (uint32_t s : strings) {
    std::fprintf(out, "string at %08X\n", s);
    for (const auto& [p, size] : ranges) {
      for (size_t i = 0; i + 4 <= size; i += 4) {
        if (LoadBE(p + i) != s) continue;
        const uint32_t at = uint32_t(p + i - base);
        std::fprintf(out, "  pointed at from %08X:", at);
        for (int k = -4; k <= 4; ++k) {
          const int64_t o = int64_t(i) + k * 4;
          if (o >= 0 && size_t(o) + 4 <= size) std::fprintf(out, " %08X", LoadBE(p + o));
        }
        std::fprintf(out, "\n");
      }
    }
  }
  // Every C function the scripts can call: the exe's tables of { name, function,
  // 0 } entries (the one with NotCD among them). With the names, the scripts'
  // QBKeys (CRC32 of the lowercase name) can be read.
  std::fprintf(out, "\ncfuncs:\n");
  int cfuncs = 0;
  auto is_name = [&](uint32_t address) {
    if (address < 0x82000000u || address >= 0x83000000u) return false;
    const char* s = reinterpret_cast<const char*>(base + address);
    size_t n = 0;
    for (; n < 64 && s[n]; ++n) {
      const char c = s[n];
      if (!((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '_')) return false;
    }
    return n >= 2 && n < 64;
  };
  for (const auto& [p, size] : ranges) {
    for (size_t i = 0; i + 12 <= size; i += 4) {
      const uint32_t name = LoadBE(p + i), func = LoadBE(p + i + 4), zero = LoadBE(p + i + 8);
      if (zero != 0 || func < 0x82000000u || func >= 0x83000000u || (func & 3) || !is_name(name)) continue;
      std::fprintf(out, "%08X %s\n", func, reinterpret_cast<const char*>(base + name));
      ++cfuncs;
    }
  }
  std::fprintf(out, "cfuncs found: %d\n", cfuncs);

  // The two globals the main menu checks before its debug entry, as the game
  // holds them in memory (its symbol table): where each key is, and the words
  // around it, which hold the value.
  for (const uint32_t key : {0x2AF92804u, 0x69E6470Eu, 0xF117A744u, 0x0BC49B56u}) {
    std::fprintf(out, "\nkey %08X\n", key);
    int shown = 0;
    for (const auto& [p, size] : ReadableRanges(base, 0x40000000u, 0xFFFF0000u)) {
      for (size_t i = 0; i + 4 <= size && shown < 40; i += 4) {
        if (LoadBE(p + i) != key) continue;
        ++shown;
        std::fprintf(out, "  at %08X:", uint32_t(p + i - base));
        for (int k = -8; k <= 8; ++k) {
          const int64_t o = int64_t(i) + k * 4;
          if (o >= 0 && size_t(o) + 4 <= size) std::fprintf(out, " %08X", LoadBE(p + o));
        }
        std::fprintf(out, "\n");
      }
    }
  }
  std::fclose(out);
}

// [[midasm_hook]] 0x824FFCD8: the return of NotCD (sub_824FFCC8), r3 = its result:
// lis r11,-32102; lbz r10,30970(r11); cntlzw r9,r10; rlwinm r3,r9,27,31,31.
void ReWorNotCDHook(PPCRegister& r3) {
  if (REXCVAR_GET(rewor_debug_menu)) {
    r3.u64 = 1;
  }
}
