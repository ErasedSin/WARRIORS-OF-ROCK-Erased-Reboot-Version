// re-wor - NativeXRenderer for Guitar Hero: Warriors of Rock: the game's D3D entry points and its D3D device and
// object layouts.
//
// From GHWoRRecomp's src/native_x_renderer_ghwor.cpp (https://github.com/ElGuslml/GHWoRRecomp), MIT License,
// Copyright (c) 2026 Gustavo Garcia; see LICENSE-GHWoRRecomp.txt. Same default.xex (CRC 62201F03), so the addresses
// and offsets are his. Its evidence is in GHWoRRecomp's docs/native-x-renderer-ghwor.md.
//
// The XDK Direct3D library is linked into default.xex (XDK 2.0.11164) at 0x822D02A0-0x822EFCD0, the draws included
// (0x822D18A0-0x822D2170), with XGraphics-style helpers inside it (0x822D63D0-0x822D7EF0). It is GH5's (XDK 2.0.8276)
// library recompiled: each of the hooked functions found by a function matcher over both executables' instruction
// indexes has GH5's instruction count (Resolve and Present a few more), and only device offsets differ. The device
// layout is GH5's up to the vertex declaration (the dirty masks, the constants, the register shadow, the fence and the
// flags at the same offsets, each used as often in both libraries); from it on every field is 168 bytes later, the
// draw state at the end 176 to 212. The resource objects are GH5's. Every address and offset here, with its evidence,
// is in docs/native-x-renderer-ghwor.md. The hooks only run the original function unless a NativeXRenderer feature
// is on (--native_x_renderer_trace, --native_x_renderer_verify, --native_x_renderer).

#include "native_x_renderer_wor.h"

#include <native_x_renderer/hooks.h>

// Draws. DrawVerticesUP (sub_822D1D38) commits its packets with an inlined EndVertices ([dev+48] = [dev+13652]); the
// engine's three BeginVertices / EndVertices pairs (sub_822C3F78, sub_822C53A0, sub_822C9948) call EndVertices
// (sub_822D1890), which is that same store. EndVertices is not hooked: inline_draw_end commits all of them. GH5's
// DrawIndexedVerticesUP and BeginIndexedVertices, which only it called, are not in this executable (the only other
// reader of [dev+13652] is DrawVerticesUP).
NATIVE_X_RENDERER_HOOK(822D2170, DrawIndexedVertices)
NATIVE_X_RENDERER_HOOK(822D1D80, DrawVertices)
NATIVE_X_RENDERER_HOOK(822D1D38, DrawVerticesUP)  // calls BeginVertices, inlines the end
NATIVE_X_RENDERER_HOOK(822D18A0, BeginVertices)

// Render targets, clears, resolves, tiling. The engine sets targets through sub_822D5C48, an alias that branches to
// SetRenderTarget. EndTiling resolves the tiles itself.
NATIVE_X_RENDERER_HOOK(822D5620, SetRenderTarget)
NATIVE_X_RENDERER_HOOK(822D5970, SetDepthStencilSurface)
NATIVE_X_RENDERER_HOOK(822D9900, Clear)  // calls ClearF
NATIVE_X_RENDERER_HOOK(822D97D8, ClearF)
NATIVE_X_RENDERER_HOOK(822E4538, Resolve)
NATIVE_X_RENDERER_HOOK(822E6708, BeginTiling)
NATIVE_X_RENDERER_HOOK(822E6A78, EndTiling)
NATIVE_X_RENDERER_HOOK(822E6598, SetPredication)
NATIVE_X_RENDERER_HOOK(822D54F0, SetViewport)  // D3DVIEWPORT9 (integers), stored as floats at +12824
NATIVE_X_RENDERER_HOOK(822D4B78, SetScissorRect)

// Shaders, vertex input, textures, constants. As in GH5 the engine also writes constants straight into the device and
// patches sampler states in the fetch constants, without a call. SetTexture, SetStreamSource and the constant setters
// take the dirty bit or mask from their caller.
NATIVE_X_RENDERER_HOOK(822D0F80, SetVertexShader)
NATIVE_X_RENDERER_HOOK(822D0DC0, SetPixelShader)
NATIVE_X_RENDERER_HOOK(822D1150, SetVertexDeclaration)
NATIVE_X_RENDERER_HOOK(822D4CC0, SetStreamSource)
NATIVE_X_RENDERER_HOOK(822D4E68, SetIndices)
NATIVE_X_RENDERER_HOOK(822D8500, SetTexture)
NATIVE_X_RENDERER_HOOK(822D0B30, SetVertexShaderConstantF)
NATIVE_X_RENDERER_HOOK(822D0C08, SetPixelShaderConstantF)

// Resources: typed thunks over the common Lock (sub_822DA9F8) and Unlock (sub_822D9AB0), and the cache flush over a
// range. The locks return the pointer (no data argument) and ignore the offset and size.
NATIVE_X_RENDERER_HOOK(822DADC0, VertexBufferLock)
NATIVE_X_RENDERER_HOOK(822D9EC0, VertexBufferUnlock)
NATIVE_X_RENDERER_HOOK(822DAE10, IndexBufferLock)
NATIVE_X_RENDERER_HOOK(822D9F80, IndexBufferUnlock)
NATIVE_X_RENDERER_HOOK(822D6518, TextureUnlock)
NATIVE_X_RENDERER_HOOK(822E5550, FlushCpuRange)

// Command buffer, queries, frame. The kick submits the segment and continues in the same memory; NewSegment moves the
// write pointer to a newly allocated segment (from the kick, the reservation sub_822DC938 and Present). The engine
// calls Synchronize (sub_822E5E08, Swap with no front buffer), then Present, which calls VdSwap with the front buffer
// in r4; the library's worker thread sub_822E8378 can present again.
NATIVE_X_RENDERER_HOOK(822DC700, KickSegment)
NATIVE_X_RENDERER_HOOK(822DBC20, NewSegment)
NATIVE_X_RENDERER_HOOK(822D12D8, SetShaderGPRAllocation)
NATIVE_X_RENDERER_HOOK(822E0680, QueryIssue)
NATIVE_X_RENDERER_HOOK(822E0908, QueryGetData)
NATIVE_X_RENDERER_HOOK(822E59E0, Swap)
NATIVE_X_RENDERER_HOOK(822E5E10, Present)

namespace rewor {

const native_x_renderer::Profile& NativeXRendererProfile() {
  static const native_x_renderer::Profile profile = [] {
    native_x_renderer::Profile p;
    p.game = "Guitar Hero: Warriors of Rock (41560883)";
    p.xdk_version = "2.0.11164";
    p.output_width = 1280;  // the presentation parameters come from a video-mode table at run time
    p.output_height = 720;
    // The finished scene's copy, before the screen effects' composite, the note highway and the HUD, for FXAA and SMAA.
    // Every resolve goes through the render targets' helper sub_8255AC80 (its color resolve returns to 0x8255AD1C), as
    // GH5's did, and the game alternates two kinds of frame, both shown, that copy the scene from different places into
    // the same 1280x720 texture the composite then samples: one through the scene renderer sub_8255A3C8 after the
    // scene's draws (0x8255A990), the other from inside the composite sub_82641110 after the bloom's passes (0x82641554),
    // with an earlier copy of the stage and the crowd before (0x8264C1B4). Antialiasing one of them only (the outer
    // caller) left every other frame's edges as they were; so every 1280x720 copy through the helper is antialiased (1.5
    // a frame), the HUD and the highway drawn over the composite stay sharp. The same helper's smaller copies (the
    // screen effects' quarter sizes, the band lobby's 400x400 portraits) are not the scene: NativeXRenderer takes only
    // copies of the output's size.
    p.scene_resolve_caller = 0x8255AD1C;
    // GHWoRRecomp widens the cameras for ultrawide (its src/widescreen.cpp); this build has no such hooks, so the
    // image keeps 16:9.
    p.widens_cameras = false;
    // The screen effects' reduced copies of the screen (640x360, 320x180, 160x90) at the game's resolution (D127). Their
    // blurs step by constants for 320x180 texels, so at the render scale the taps skipped host texels: the background the
    // depth of field blurs showed a grid at 3440x1440 (Gustavo, the rocker creator's character screen; its blurred copy of
    // the scene is the 320x180 0x8436000). GH5, whose bloom showed nothing, was checked on its title screen only.
    p.unscaled_screen_fractions = true;
    // The pipeline list (D129): every shader the game has is in data/compressed/FXFILES/MaterialLibrary.bin.xen, as in GH Metallica (NativeXRenderer's D129, D133).
    static const char* const kShaderFiles[] = {"data/compressed/FXFILES/MaterialLibrary.bin.xen"};
    p.title_id = 0x41560883;
    p.shader_files = kShaderFiles;
    p.shader_file_count = 1;
    native_x_renderer::DeviceLayout& d = p.device;
    // Six dirty masks from +0: VS constants, PS constants, register groups and the program, fetch slots (low half,
    // slot 0 in bit 31) and the 0x2280 group, the 0x2300/0x2380 groups with the clip planes and bool/loop constants,
    // the tile-dependent state. GH5's up to the vertex declaration.
    d.vs_constants_dirty = 0;
    d.ps_constants_dirty = 8;
    d.fetch_constants_dirty = 24;
    d.fetch_dirty_slot0_bit = 31;
    d.command_buffer_write = 48;
    d.fetch_constants = 1152;
    d.vs_float_constants = 1920;
    d.ps_float_constants = 6016;
    d.bool_loop_constants = 10112;
    d.register_groups = {{
        {10368, 0x2000, 16},
        {10444, 0x2100, 21},
        {10528, 0x2180, 5},
        {10548, 0x2200, 12},
        {10596, 0x2280, 21},
        {10680, 0x2300, 38},
        {10832, 0x2380, 8},
    }};
    d.fence_counter = 10908;
    d.write_back_block = 10896;
    d.flags = 10940;
    // From here GH5's + 168 (SetVertexDeclaration's store, SetIndices', SetRenderTarget's targets, SetStreamSource's
    // and SetTexture's slots, SetViewport's floats, the shaders, BeginTiling's count).
    d.vertex_declaration = 11992;
    d.index_buffer = 12612;
    d.render_targets = 12616;
    d.depth_stencil = 12632;
    d.vertex_buffers = 12636;  // one pointer per stream
    d.textures = 12720;
    d.viewport = 12824;  // x, y, width, height, min z, max z (floats)
    d.pixel_shader = 12868;
    d.vertex_shader = 12872;
    d.predication_mask = 12884;
    d.tile_count = 12924;
    d.tile_rects = 12928;
    // DrawVerticesUP and EndVertices commit BeginVertices' packets with [dev+48] = [dev+13652] (GH5's 13444).
    d.inline_draw_end = 13652;
    native_x_renderer::ObjectLayout& o = p.objects;
    o.texture_fetch = 28;
    o.surface_info = 24;
    o.surface_color_depth_info = 28;
    o.surface_size = 36;
    o.surface_width_minus_one = {18, 14};
    o.surface_height_minus_one = {3, 15};
    o.buffer_address = 24;
    o.buffer_size = 28;
    return p;
  }();
  return profile;
}

}  // namespace rewor
