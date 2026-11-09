// The game's debug menu.
//
// The main menu script adds DEBUG MENU in a debug build. The debug executable
// that circulates differs from the retail one in its table of script C
// functions (DebugBuild and RetailBuild swapped); rewor_debug_menu makes the
// same swap in memory (ReWorApplyDebugMenu).
//
// Several scripts also run developer-only parts inside "if NotCD": NotCD is a C
// function that is true when the game is read from a PC and false on a disc;
// rewor_debug_notcd makes it true (the hooks at the end of this file).
//
// rewor_debug_scan lists the C function table; rewor_debug_dump and
// rewor_debug_dump_early write the image, to compare with a modified executable.
#include <rex/cvar.h>
#include <rex/ppc/context.h>
#include <rex/system/kernel_state.h>
#include <rex/system/xmemory.h>

#include <algorithm>
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
                    "The game's debug menu: DEBUG MENU under OPTIONS in the main menu (the script functions "
                    "DebugBuild and RetailBuild swap answers, as in the debug executable). Applies when the game "
                    "starts.")
    .lifecycle(rex::cvar::Lifecycle::kRequiresRestart);
REXCVAR_DEFINE_BOOL(rewor_debug_notcd, false, "re-wor",
                    "Experimental: the scripts' developer-only parts (NotCD true, the \"running from a disc\" "
                    "byte 0). Not needed for the debug menu. Applies when the game starts.")
    .lifecycle(rex::cvar::Lifecycle::kRequiresRestart);
REXCVAR_DEFINE_STRING(rewor_debug_dump, "", "re-wor",
                      "Diagnostic: file that receives the game's image as loaded (guest 0x82000000 on, the size "
                      "of rewor_debug_dump_size), to compare with a modified executable (empty = off)");
REXCVAR_DEFINE_INT32(rewor_debug_dump_size, 0x988000, "re-wor", "Diagnostic: bytes of the image rewor_debug_dump writes");
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

// Writes the game's image as loaded: the decrypted executable laid out as the
// console maps it, from 0x82000000. Pages not mapped are written as zeros.
static void DumpImage(const uint8_t* base) {
  const std::string path = REXCVAR_GET(rewor_debug_dump);
  if (path.empty()) return;
  const uint32_t begin = 0x82000000u;
  const uint32_t size = uint32_t(std::max(0, REXCVAR_GET(rewor_debug_dump_size)));
  std::vector<uint8_t> image(size, 0);
  for (const auto& [p, length] : ReadableRanges(base, begin, begin + size)) {
    std::memcpy(image.data() + (p - base - begin), p, length);
  }
  if (FILE* out = std::fopen(path.c_str(), "wb")) {
    std::fwrite(image.data(), 1, image.size(), out);
    std::fclose(out);
  }
}

REXCVAR_DEFINE_STRING(rewor_debug_dump_early, "", "re-wor",
                      "Diagnostic: like rewor_debug_dump, but written before the game runs (empty = off)");

// Called before the game starts (src/main.cpp): the image as the executable has it.
void ReWorDumpEarly() {
  const std::string path = REXCVAR_GET(rewor_debug_dump_early);
  if (path.empty()) return;
  auto* kernel = rex::system::kernel_state();
  if (!kernel || !kernel->memory()) return;
  const uint8_t* base = kernel->memory()->virtual_membase();
  const uint32_t begin = 0x82000000u;
  const uint32_t size = uint32_t(std::max(0, REXCVAR_GET(rewor_debug_dump_size)));
  std::vector<uint8_t> image(size, 0);
  for (const auto& [p, length] : ReadableRanges(base, begin, begin + size)) {
    std::memcpy(image.data() + (p - base - begin), p, length);
  }
  if (FILE* out = std::fopen(path.c_str(), "wb")) {
    std::fwrite(image.data(), 1, image.size(), out);
    std::fclose(out);
  }
}

// Called once, from the first frames of the game (src/main.cpp).
void ReWorScanForNotCD() {
  {
    auto* kernel = rex::system::kernel_state();
    if (kernel && kernel->memory()) DumpImage(kernel->memory()->virtual_membase());
  }
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

// The debug executable differs from the retail one in its table of script C
// functions: DebugBuild points at the function that answers 1 and RetailBuild at
// the one that answers 0, the other way round from retail. The main menu script
// adds DEBUG MENU when the build is a debug one. The same swap, in memory, before
// the game runs; only when the table is the expected one (this exact executable).
void ReWorApplyDebugMenu() {
  if (!REXCVAR_GET(rewor_debug_menu)) return;
  auto* kernel = rex::system::kernel_state();
  if (!kernel || !kernel->memory()) return;
  uint8_t* base = kernel->memory()->virtual_membase();
  const uint32_t debug_entry = 0x82810CCC, retail_entry = 0x82810CD8;
  const uint32_t answers_false = 0x8259EBA8, answers_true = 0x8245CF10;
  auto name_is = [&](uint32_t entry, const char* name) {
    const uint32_t p = LoadBE(base + entry);
    return p >= 0x82000000u && p < 0x82C00000u && std::strcmp(reinterpret_cast<const char*>(base + p), name) == 0;
  };
  if (!name_is(debug_entry, "DebugBuild") || !name_is(retail_entry, "RetailBuild")) return;
  if (LoadBE(base + debug_entry + 4) != answers_false || LoadBE(base + retail_entry + 4) != answers_true) return;
  auto store = [&](uint32_t address, uint32_t value) {
    for (int i = 0; i < 4; ++i) base[address + i] = uint8_t(value >> (24 - 8 * i));
  };
  store(debug_entry + 4, answers_true);
  store(retail_entry + 4, answers_false);
}

// [[midasm_hook]] 0x824FFCD8: the return of NotCD (sub_824FFCC8), r3 = its result:
// lis r11,-32102; lbz r10,30970(r11); cntlzw r9,r10; rlwinm r3,r9,27,31,31.
void ReWorNotCDHook(PPCRegister& r3) {
  if (REXCVAR_GET(rewor_debug_notcd)) {
    r3.u64 = 1;
  }
}

// The debug executable's patch (the hooks next to these in rewor_functions.toml):
// the "running from a disc" byte at 0x829A78FA stays 0, so CD is false and NotCD
// true everywhere, the C code that reads the byte directly included.

// [[midasm_hook]] 0x824030F8 (sub_82403098): before "cmplwi cr6,r10,0"; the
// debug executable turns the "beq" after it into a nop.
void ReWorDiscFlagBranchHook(PPCRegister& r10) {
  if (REXCVAR_GET(rewor_debug_notcd)) {
    r10.u64 = 1;
  }
}

// [[midasm_hook]] at the three "stb r11,30970(rN)" (0x82403104, 0x824AD6B4,
// 0x824FFBA0): the value stored in the byte.
void ReWorDiscFlagStoreHook(PPCRegister& r11) {
  if (REXCVAR_GET(rewor_debug_notcd)) {
    r11.u64 = 0;
  }
}
