// The player's own picture as the profile's gamer picture.
//
// The game shows the profile's gamer picture (in the song list, for one) by
// asking the system to draw it into a texture: XamReadTileToTexture(type,
// title id, tile id, user, buffer, stride, height, overlapped). The SDK fills
// that texture with white. Here it gets the picture chosen in the launcher,
// which saves it next to the game as profile_picture.bin: 64 x 64 pixels,
// 4 bytes each in the order alpha, red, green, blue.

#include "generated/rewor_init.h"

#include <rex/filesystem.h>
#include <rex/hook.h>
#include <rex/logging.h>
#include <rex/ppc/context.h>
#include <rex/system/kernel_state.h>

#include <cstdint>
#include <cstring>
#include <fstream>
#include <vector>

namespace {

constexpr uint32_t kSide = 64;
constexpr uint32_t kInvalidParameter = 87;
constexpr uint32_t kIoPending = 997;

// Empty when the player chose no picture. Read once.
const std::vector<uint8_t>& Picture() {
  static const std::vector<uint8_t> picture = [] {
    std::vector<uint8_t> data;
    std::ifstream file(rex::filesystem::GetExecutableFolder() / "profile_picture.bin",
                       std::ios::binary);
    if (file) {
      data.assign(std::istreambuf_iterator<char>(file), std::istreambuf_iterator<char>());
      if (data.size() != kSide * kSide * 4) data.clear();
    }
    return data;
  }();
  return picture;
}

}  // namespace

REX_HOOK_RAW(__imp__XamReadTileToTexture) {
  const uint64_t tile = ctx.r5.u64;
  const uint32_t buffer = ctx.r7.u32, stride = ctx.r8.u32, height = ctx.r9.u32;
  const uint32_t overlapped = ctx.r10.u32;
  if (!tile || !buffer) {
    ctx.r3.u64 = kInvalidParameter;
    return;
  }
  REXLOG_WARN("[picture] gamer picture asked for: tile {:016X}, {} x {}", tile, stride / 4, height);
  uint8_t* out = base + buffer;
  const std::vector<uint8_t>& picture = Picture();
  const uint32_t width = stride / 4;
  if (picture.empty() || !width || !height) {
    std::memset(out, 0xFF, size_t(stride) * height);
  } else {
    // Nearest pixel, for a texture of another size than the picture's.
    for (uint32_t y = 0; y < height; ++y) {
      const uint8_t* row = picture.data() + size_t(y * kSide / height) * kSide * 4;
      uint8_t* line = out + size_t(y) * stride;
      for (uint32_t x = 0; x < width; ++x) {
        std::memcpy(line + size_t(x) * 4, row + size_t(x * kSide / width) * 4, 4);
      }
    }
  }
  if (overlapped) {
    REX_KERNEL_STATE()->CompleteOverlappedImmediate(overlapped, 0);
    ctx.r3.u64 = kIoPending;
    return;
  }
  ctx.r3.u64 = 0;
}
