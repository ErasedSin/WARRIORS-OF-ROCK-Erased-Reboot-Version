// Skipping the opening video.
//
// The boot script plays "initial_boot_movie" (53 seconds) with the game's
// PlayMovie C function (sub_8251DE38, the entry "PlayMovie" of the C function
// table at 0x8280DDA4) and waits for it to end or for a button. With
// rewor_skip_intro, PlayMovie answers "not started" for that one movie, so the
// script goes straight on. Every other movie plays as usual.

#include <rex/cvar.h>
#include <rex/logging.h>
#include <rex/ppc/context.h>
#include <rex/system/kernel_state.h>
#include <rex/system/xmemory.h>

#include <cstdint>
#include <cstring>

REXCVAR_DECLARE(bool, rewor_skip_intro);

namespace {

constexpr uint32_t kMovieKey = 0xE2A10D90;  // QBKey of "movie"
constexpr char kBootMovie[] = "initial_boot_movie";
constexpr uint32_t kGuestLow = 0x40000000;

uint32_t Load32(const uint8_t* base, uint32_t address) {
  uint32_t value;
  std::memcpy(&value, base + address, 4);
  return _byteswap_ulong(value);
}

bool SameName(const uint8_t* text, const char* name) {
  for (; *name; ++text, ++name) {
    const char c = static_cast<char>(*text >= 'A' && *text <= 'Z' ? *text + 32 : *text);
    if (c != *name) return false;
  }
  return *text == 0 || *text == '.';
}

}  // namespace

// Midasm hook at 0x8251DE44 (sub_8251DE38, after its prologue): r3 = the
// script's parameters, a list of components { type at +2, name at +4, value at
// +8, next at +12 }. True jumps to the function's "return 0" at 0x8251DEA0.
bool ReWorPlayMovieHook(PPCRegister& r3) {
  if (!REXCVAR_GET(rewor_skip_intro)) return false;
  auto* kernel = rex::system::kernel_state();
  if (!kernel || !kernel->memory() || r3.u32 < kGuestLow) return false;
  const uint8_t* base = kernel->memory()->virtual_membase();
  uint32_t component = Load32(base, r3.u32 + 4);
  for (int i = 0; i < 32 && component >= kGuestLow; ++i) {
    if (Load32(base, component + 4) == kMovieKey) {
      const uint32_t text = Load32(base, component + 8);
      if (text >= kGuestLow && SameName(base + text, kBootMovie)) {
        REXLOG_WARN("[intro] opening video skipped (rewor_skip_intro)");
        return true;
      }
      return false;
    }
    component = Load32(base, component + 12);
  }
  return false;
}
