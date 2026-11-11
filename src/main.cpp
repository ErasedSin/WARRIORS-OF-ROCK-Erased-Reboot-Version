#include "generated/rewor_init.h"

#include "discord_rpc.h"
#include "options_menu.h"
#include "quit_prompt.h"

#include <rex/cvar.h>
#include <rex/filesystem.h>
#include <rex/filesystem/device.h>
#include <rex/filesystem/devices/host_path_device.h>
#include <rex/filesystem/vfs.h>
#include <rex/hook.h>
#include <rex/rex_app.h>
#include <rex/runtime.h>
#include <rex/ui/imgui_dialog.h>
#include <rex/ui/imgui_drawer.h>
#include <rex/ui/keybinds.h>

#include <imgui.h>

#include <atomic>
#include <chrono>
#include <cstdio>
#include <cstring>
#include <fstream>
#include <iterator>
#include <string>
#include <unordered_set>

#ifdef REWOR_DEV
#include "stub_tracker.h"
#endif

#ifdef _WIN32
#include <windows.h>
#include <dbghelp.h>
#include <dxgi.h>
#pragma comment(lib, "dbghelp.lib")
#pragma comment(lib, "dxgi.lib")
#endif

// Written by the launcher, which applies them by patching the game scripts in
// the mods folder. Declared here so the config loader knows them and the F4
// settings save keeps them.
REXCVAR_DEFINE_BOOL(rewor_motion_blur, true, "re-wor",
                    "Game motion blur (applied by the launcher through the mods folder)")
    .lifecycle(rex::cvar::Lifecycle::kRequiresRestart);
REXCVAR_DEFINE_BOOL(rewor_screen_noise, true, "re-wor",
                    "Game screen noise effect (applied by the launcher through the mods folder)")
    .lifecycle(rex::cvar::Lifecycle::kRequiresRestart);
REXCVAR_DEFINE_BOOL(rewor_dof, true, "re-wor",
                    "Game depth of field (applied by the launcher through the mods folder)")
    .lifecycle(rex::cvar::Lifecycle::kRequiresRestart);
REXCVAR_DEFINE_BOOL(rewor_bloom, true, "re-wor",
                    "Game bloom (applied by the launcher through the mods folder)")
    .lifecycle(rex::cvar::Lifecycle::kRequiresRestart);
REXCVAR_DEFINE_BOOL(rewor_skip_intro, false, "re-wor",
                    "Skip the opening video and go straight to the game (src/intro.cpp)")
    .lifecycle(rex::cvar::Lifecycle::kRequiresRestart);
REXCVAR_DEFINE_BOOL(rewor_show_fps, true, "re-wor", "Show the FPS counter in the top-left corner (F2 toggles it)");
REXCVAR_DEFINE_STRING(rewor_frame_log, "", "re-wor",
                      "CSV file that receives the time of every frame the game draws (empty = off). "
                      "The SDK's perf_log_csv is compiled out of Release.")
    .lifecycle(rex::cvar::Lifecycle::kRequiresRestart);
REXCVAR_DEFINE_STRING(rewor_game_folder, "", "re-wor",
                      "Game files folder picked in the launcher (passed as --game_data_root)")
    .lifecycle(rex::cvar::Lifecycle::kRequiresRestart);

// src/dlc.cpp: installs the content packages of the dlc folder.
void ReWorInstallDlc();
// src/online.cpp: online play over a LAN or a virtual LAN.
void ReWorStartOnline();
void ReWorDumpEarly();
void ReWorApplyDebugMenu();

// src/sampler.cpp: where each thread spends its time, when rewor_sample_file is set.
void rewor_start_sampler();

#ifndef REXGLUE_BUILD_CONFIG
#define REXGLUE_BUILD_CONFIG "unknown"
#endif

#ifdef REWOR_DEV
static std::ofstream& dbg() {
  static std::ofstream log("re_wor_trace.log", std::ios::out | std::ios::trunc);
  return log;
}
#define TRACE(msg) do { dbg() << msg << std::endl; dbg().flush(); } while(0)
#else
#define TRACE(msg) ((void)0)
#endif

#ifdef _WIN32
// Resolves an address to "module+offset symbol (file:line)" using the PDBs
// next to the binaries. Module+offset stays valid across runs despite ASLR.
// Adapted from TFujiwara's fork of re-wor.
static void LogSymbolizedAddress(std::ofstream& log, uintptr_t addr) {
  log << "0x" << std::hex << addr;

  HMODULE module = nullptr;
  if (GetModuleHandleExA(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS |
                             GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                         reinterpret_cast<LPCSTR>(addr), &module) &&
      module) {
    char module_path[MAX_PATH] = {};
    if (GetModuleFileNameA(module, module_path, MAX_PATH)) {
      const char* name = strrchr(module_path, '\\');
      name = name ? name + 1 : module_path;
      log << "  " << name << "+0x" << std::hex << (addr - reinterpret_cast<uintptr_t>(module));
    }
  }

  alignas(SYMBOL_INFO) char sym_buffer[sizeof(SYMBOL_INFO) + MAX_SYM_NAME] = {};
  auto* symbol = reinterpret_cast<SYMBOL_INFO*>(sym_buffer);
  symbol->SizeOfStruct = sizeof(SYMBOL_INFO);
  symbol->MaxNameLen = MAX_SYM_NAME;
  DWORD64 displacement = 0;
  if (SymFromAddr(GetCurrentProcess(), addr, &displacement, symbol)) {
    log << "  " << symbol->Name << "+0x" << std::hex << displacement;
    IMAGEHLP_LINE64 line = {};
    line.SizeOfStruct = sizeof(IMAGEHLP_LINE64);
    DWORD line_displacement = 0;
    if (SymGetLineFromAddr64(GetCurrentProcess(), addr, &line_displacement, &line)) {
      log << "  (" << line.FileName << ":" << std::dec << line.LineNumber << ")";
    }
  }
  log << "\n";
}

static LONG WINAPI CrashHandler(EXCEPTION_POINTERS* ep) {
  std::ofstream log("re_wor_crash_detail.log", std::ios::out | std::ios::trunc);
  log << "Exception: 0x" << std::hex << ep->ExceptionRecord->ExceptionCode << "\n";
  log << "Module base: 0x" << std::hex << reinterpret_cast<uintptr_t>(GetModuleHandleA(NULL)) << "\n";
  if (ep->ExceptionRecord->ExceptionCode == EXCEPTION_ACCESS_VIOLATION) {
    log << "Access: " << (ep->ExceptionRecord->ExceptionInformation[0] == 0 ? "READ" :
                          ep->ExceptionRecord->ExceptionInformation[0] == 1 ? "WRITE" : "EXEC") << "\n";
    uintptr_t target = ep->ExceptionRecord->ExceptionInformation[1];
    log << "Target: 0x" << std::hex << target << "\n";
    // Guest memory starts at 0x100000000, so a fault inside that window is a
    // bad guest pointer and the offset is the guest address.
    log << "Guest address (if inside the guest window): 0x" << std::hex << (target - 0x100000000ull)
        << "\n";
  }

  SymSetOptions(SYMOPT_LOAD_LINES | SYMOPT_UNDNAME | SYMOPT_DEFERRED_LOADS);
  BOOL sym_ready = SymInitialize(GetCurrentProcess(), NULL, TRUE);

  log << "\nRIP: ";
  LogSymbolizedAddress(log, ep->ContextRecord->Rip);

  void* stack[64];
  USHORT frames = CaptureStackBackTrace(0, 64, stack, NULL);
  log << "\nStack (" << std::dec << frames << " frames):\n";
  for (USHORT i = 0; i < frames; i++) {
    log << "  [" << std::dec << i << "] ";
    LogSymbolizedAddress(log, reinterpret_cast<uintptr_t>(stack[i]));
  }

  if (sym_ready) {
    SymCleanup(GetCurrentProcess());
  }
  log.flush();
  return EXCEPTION_CONTINUE_SEARCH;
}
#endif

// Calls one guest CRT static constructor, using SEH so a single crashing ctor
// is skipped instead of taking the whole game down.
static void safe_call_ctor(PPCContext& ctx, uint8_t* base, uint32_t ppc_addr,
                           const char* source, int& called, int& skipped, int& crashed) {
  auto* fn = REX_LOOKUP_FUNC(base, ppc_addr);
  if (!fn) {
    skipped++;
    return;
  }
#ifdef _WIN32
  __try {
    fn(ctx, base);
    called++;
  } __except(EXCEPTION_EXECUTE_HANDLER) {
    TRACE("[ctor] CRASH in 0x" << std::hex << ppc_addr << " (" << source
          << ") exception=0x" << GetExceptionCode() << " - skipping");
    crashed++;
  }
#else
  fn(ctx, base);
  called++;
#endif
}

// Replaces sub_822CCB60, the guest CRT static initializer iterator, with a
// version that survives individual constructors crashing.
REX_HOOK_RAW(sub_822CCB60) {
  TRACE("[sub_822CCB60] Safe CRT init started");
  int called = 0, skipped = 0, crashed = 0;

  uint32_t fn_addr = 0;
  bool stage1_readable = false;
#ifdef _WIN32
  __try {
    fn_addr = REX_LOAD_U32(0x82064074);
    stage1_readable = true;
  } __except(EXCEPTION_EXECUTE_HANDLER) {
    TRACE("[sub_822CCB60] Cannot read stage1 addr 0x82064074 (exc=0x"
          << std::hex << GetExceptionCode() << ") - skipping stage1");
  }
#else
  fn_addr = REX_LOAD_U32(0x82064074);
  stage1_readable = true;
#endif
  if (stage1_readable && fn_addr != 0) {
    safe_call_ctor(ctx, base, fn_addr, "stage1", called, skipped, crashed);
  }

  TRACE("[sub_822CCB60] Stage 2: table1 (0x827D2720-0x827D272C)");
  for (uint32_t ea = 0x827D2720; ea < 0x827D272C; ea += 4) {
    fn_addr = REX_LOAD_U32(ea);
    if (fn_addr != 0) {
      safe_call_ctor(ctx, base, fn_addr, "table1", called, skipped, crashed);
    }
  }

  TRACE("[sub_822CCB60] Stage 3: table2 (0x827D0414-0x827D271C)");
  for (uint32_t ea = 0x827D0414; ea < 0x827D271C; ea += 4) {
    fn_addr = REX_LOAD_U32(ea);
    if (fn_addr != 0 && fn_addr != 0xFFFFFFFF) {
      safe_call_ctor(ctx, base, fn_addr, "table2", called, skipped, crashed);
    }
  }

  TRACE("[sub_822CCB60] Done: called=" << std::dec << called
        << " skipped=" << skipped << " crashed=" << crashed);
}

#ifdef REWOR_DEV
static std::unordered_set<uint32_t> g_logged_unmapped;
#endif

// Target for dispatch table slots the recompiler never filled, so an indirect
// call into an undiscovered function returns 0 instead of jumping to null.
static REX_FUNC(unmapped_func_stub) {
  (void)base;
#ifdef REWOR_DEV
  uint32_t target = ctx.ctr.u32;
  uint32_t caller = static_cast<uint32_t>(ctx.lr);
  if (g_logged_unmapped.insert(target).second) {
    TRACE("[UNMAPPED] PPC call to 0x" << std::hex << target
          << " from 0x" << caller << " - stubbed (total unmapped: "
          << std::dec << g_logged_unmapped.size() << ")");
  }
  StubTracker::instance().record_unmapped(target, caller);
#endif
  ctx.r3.u64 = 0;
}

static void fill_dispatch_table(uint8_t* base) {
  auto* table_base = reinterpret_cast<PPCFunc**>(base + REX_IMAGE_BASE + REX_IMAGE_SIZE);
  size_t num_entries = REX_CODE_SIZE / 4;
  size_t filled = 0;
  for (size_t i = 0; i < num_entries; i++) {
    if (table_base[i] == nullptr) {
      table_base[i] = &unmapped_func_stub;
      filled++;
    }
  }
  TRACE("[dispatch] Filled " << std::dec << filled << " null entries out of "
        << num_entries << " total (" << (num_entries - filled) << " mapped)");
  (void)filled;
}

// XamGetLanguage is still a stub in the SDK, so the game always fell back to
// English. Answer it from the SDK's user_language setting, which the launcher
// writes. Same idea as the SDK fix in TFujiwara's fork, done on this side.
static uint32_t rewor_xam_get_language() {
  uint32_t language = rex::cvar::Query<uint32_t>("user_language");
  return (language >= 1 && language <= 12) ? language : 1;
}
REX_HOOK(__imp__XamGetLanguage, rewor_xam_get_language)

// Serves files from the mods folder when present and falls back to the game
// data otherwise, so the launcher can replace single game files (patched
// scripts, for instance) without touching the user's original copies.
class ModOverlayDevice : public rex::filesystem::Device {
 public:
  ModOverlayDevice(std::string_view mount_path, const std::filesystem::path& mods_root,
                   const std::filesystem::path& game_root)
      : Device(mount_path),
        mods_(std::make_unique<rex::filesystem::HostPathDevice>(mount_path, mods_root, true)),
        game_(std::make_unique<rex::filesystem::HostPathDevice>(mount_path, game_root, true)) {}

  bool Initialize() override { return mods_->Initialize() && game_->Initialize(); }

  void Dump(rex::string::StringBuffer* string_buffer) override { game_->Dump(string_buffer); }

  rex::filesystem::Entry* ResolvePath(const std::string_view path) override {
    // Only files are overridden. Directories always come from the game data,
    // otherwise a partial folder in mods would hide the rest of its contents.
    if (!path.empty()) {
      auto* entry = mods_->ResolvePath(path);
      if (entry && !(entry->attributes() & rex::filesystem::kFileAttributeDirectory)) {
        return entry;
      }
    }
    return game_->ResolvePath(path);
  }

  bool is_read_only() const override { return true; }
  const std::string& name() const override { return game_->name(); }
  uint32_t attributes() const override { return game_->attributes(); }
  uint32_t component_name_max_length() const override {
    return game_->component_name_max_length();
  }
  uint32_t total_allocation_units() const override { return game_->total_allocation_units(); }
  uint32_t available_allocation_units() const override {
    return game_->available_allocation_units();
  }
  uint32_t sectors_per_allocation_unit() const override {
    return game_->sectors_per_allocation_unit();
  }
  uint32_t bytes_per_sector() const override { return game_->bytes_per_sector(); }

 private:
  std::unique_ptr<rex::filesystem::HostPathDevice> mods_;
  std::unique_ptr<rex::filesystem::HostPathDevice> game_;
};

// Points game:\ and d:\ at the overlay. The original game device stays
// registered because the already loaded XEX may still hold entries from it.
static void mount_mod_overlay(rex::filesystem::VirtualFileSystem* vfs,
                              const std::filesystem::path& game_root) {
  auto mods_root = rex::filesystem::GetExecutableFolder() / "mods";
  std::error_code ec;
  if (!vfs || game_root.empty() || !std::filesystem::is_directory(mods_root, ec)) {
    return;
  }
  constexpr std::string_view kMount = "\\Device\\ReWorModOverlay";
  auto device = std::make_unique<ModOverlayDevice>(kMount, mods_root,
                                                   std::filesystem::absolute(game_root, ec));
  if (!device->Initialize() || !vfs->RegisterDevice(std::move(device))) {
    REXLOG_ERROR("re-wor: failed to mount mods folder {}", mods_root.string());
    return;
  }
  for (std::string_view link : {"game:", "d:"}) {
    vfs->UnregisterSymbolicLink(link);
    vfs->RegisterSymbolicLink(link, kMount);
  }
  REXLOG_INFO("re-wor: mods folder mounted over game data: {}", mods_root.string());
}

// Frames the game has drawn. The window repaints on its own (about 70 times a
// second here) whatever the game does, so its rate says nothing about the
// game's; this counts the render thread's frames instead.
static std::atomic<uint64_t> g_guest_frames{0};

// [[midasm_hook]] at the entry of sub_825840F0, the render thread's frame (it
// waits for the next vertical blank), once per frame the game draws. Also
// writes each frame's time to rewor_frame_log when it is set.
// src/notcd.cpp: where the C function NotCD is registered (rewor_debug_scan).
void ReWorScanForNotCD();

void ReWorGuestFrameHook() {
  if (g_guest_frames.fetch_add(1, std::memory_order_relaxed) == 1500) {
    ReWorScanForNotCD();
  }
  static std::ofstream log;
  static bool opened = false;
  static std::chrono::steady_clock::time_point start, last;
  if (!opened) {
    opened = true;
    const std::string path = REXCVAR_GET(rewor_frame_log);
    if (!path.empty()) {
      log.open(path, std::ios::out | std::ios::app);
      if (log) log << "seconds,frame_ms\n";
    }
    start = last = std::chrono::steady_clock::now();
    return;
  }
  const auto now = std::chrono::steady_clock::now();
  if (log) {
    char line[48];
    std::snprintf(line, sizeof(line), "%.3f,%.2f\n", std::chrono::duration<double>(now - start).count(),
                  std::chrono::duration<double, std::milli>(now - last).count());
    log << line;
  }
  last = now;
}

// Small FPS readout in the top-left corner, with no window chrome and no
// input capture. It shows the game's frames per second, refreshed twice a
// second so it stays readable.
class FpsCounterDialog : public rex::ui::ImGuiDialog {
 public:
  explicit FpsCounterDialog(rex::ui::ImGuiDrawer* drawer) : ImGuiDialog(drawer) {}

 protected:
  void OnDraw(ImGuiIO& io) override {
    elapsed_ += io.DeltaTime;
    if (elapsed_ >= 0.5f) {
      const uint64_t frames = g_guest_frames.load(std::memory_order_relaxed);
      shown_fps_ = static_cast<float>(frames - last_frames_) / elapsed_;
      last_frames_ = frames;
      elapsed_ = 0.0f;
    }

    ImVec4 color = shown_fps_ >= 55.0f   ? ImVec4(0.45f, 1.0f, 0.45f, 1.0f)
                   : shown_fps_ >= 30.0f ? ImVec4(1.0f, 0.85f, 0.3f, 1.0f)
                                         : ImVec4(1.0f, 0.35f, 0.35f, 1.0f);

    ImGui::SetNextWindowPos(ImVec2(8.0f, 6.0f), ImGuiCond_Always);
    ImGui::SetNextWindowBgAlpha(0.35f);
    const ImGuiWindowFlags flags = ImGuiWindowFlags_NoDecoration | ImGuiWindowFlags_NoInputs |
                                   ImGuiWindowFlags_AlwaysAutoResize |
                                   ImGuiWindowFlags_NoSavedSettings |
                                   ImGuiWindowFlags_NoFocusOnAppearing | ImGuiWindowFlags_NoNav;
    if (ImGui::Begin("##rewor_fps", nullptr, flags)) {
      ImGui::TextColored(color, "%.0f FPS", shown_fps_);
    }
    ImGui::End();
  }

 private:
  float elapsed_ = 0.0f;
  float shown_fps_ = 0.0f;
  uint64_t last_frames_ = g_guest_frames.load(std::memory_order_relaxed);
};

class ReWorApp : public rex::ReXApp {
 public:
  using rex::ReXApp::ReXApp;

  ~ReWorApp() override { discord_shutdown(); }

  // Shader and pipeline caches written by another GPU or another build type
  // made the game exit silently while loading them, so each pair gets its
  // own subfolder. The first hardware DXGI adapter is the one the SDK picks,
  // and it follows the Windows graphics preference for this exe.
  static std::string cache_flavor() {
    std::string flavor = REXGLUE_BUILD_CONFIG;
#ifdef _WIN32
    IDXGIFactory1* factory = nullptr;
    if (SUCCEEDED(CreateDXGIFactory1(__uuidof(IDXGIFactory1), reinterpret_cast<void**>(&factory)))) {
      IDXGIAdapter1* adapter = nullptr;
      for (UINT i = 0; factory->EnumAdapters1(i, &adapter) != DXGI_ERROR_NOT_FOUND; ++i) {
        DXGI_ADAPTER_DESC1 desc{};
        adapter->GetDesc1(&desc);
        adapter->Release();
        if (desc.Flags & DXGI_ADAPTER_FLAG_SOFTWARE) {
          continue;
        }
        char id[32];
        std::snprintf(id, sizeof(id), "-%04x-%04x", desc.VendorId, desc.DeviceId);
        flavor += id;
        break;
      }
      factory->Release();
    }
#endif
    return flavor;
  }

  // The launcher writes the chosen game folder to game_folder.txt as UTF-8.
  // A file instead of a command line argument keeps non-ASCII characters in
  // the path intact (the CLI goes through the ANSI code page on Windows).
  // Saves, the shader cache and installed DLC (the SDK's user data) go in the
  // userdata folder next to the game, so the game and its data can live on
  // another drive, as Guitar Hero 5's do. Documents\re_wor, where earlier
  // versions kept them, stays in use while there is no userdata folder, so
  // nobody loses a save; moving Documents\re_wor there as userdata moves them.
  // A user_data_root given on the command line or in re_wor.toml wins.
  static void UseUserDataNextToGame(rex::PathConfig& paths) {
    if (!rex::cvar::Query<std::string>("user_data_root").empty()) {
      return;
    }
    const auto portable = rex::filesystem::GetExecutableFolder() / "userdata";
    std::error_code ec;
    if (!std::filesystem::is_directory(portable, ec)) {
      if (!paths.user_data_root.empty() && std::filesystem::is_directory(paths.user_data_root, ec)) {
        return;
      }
      std::filesystem::create_directories(portable, ec);
      const auto probe = portable / ".write_test";
      {
        std::ofstream f(probe, std::ios::binary);
        if (!f) {
          std::filesystem::remove(portable, ec);
          return;  // a folder the game cannot write to: Documents, as before
        }
      }
      std::filesystem::remove(probe, ec);
    }
    const auto old_root = paths.user_data_root;
    paths.user_data_root = portable;
    if (paths.cache_root.empty() || paths.cache_root == old_root / "cache") {
      paths.cache_root = portable / "cache";
    }
  }

  void OnConfigurePaths(rex::PathConfig& paths) override {
    config_path_ = paths.config_path;
    UseUserDataNextToGame(paths);
    if (!paths.cache_root.empty()) {
      paths.cache_root /= cache_flavor();
    }
    if (!paths.game_data_root.empty()) {
      return;
    }
    auto exe_dir = rex::filesystem::GetExecutableFolder();
    std::ifstream file(exe_dir / "game_folder.txt", std::ios::binary);
    std::string folder((std::istreambuf_iterator<char>(file)), std::istreambuf_iterator<char>());
    if (folder.size() >= 3 && static_cast<unsigned char>(folder[0]) == 0xEF) {
      folder.erase(0, 3);
    }
    while (!folder.empty() && (folder.back() == '\n' || folder.back() == '\r' || folder.back() == ' ')) {
      folder.pop_back();
    }
    std::error_code ec;
    if (!folder.empty()) {
      std::filesystem::path chosen(std::u8string(folder.begin(), folder.end()));
      if (std::filesystem::is_directory(chosen, ec)) {
        paths.game_data_root = chosen;
        return;
      }
      REXLOG_WARN("re-wor: game folder from game_folder.txt not found: {}", folder);
    }
    paths.game_data_root = exe_dir / "assets";
  }

  void OnPreSetup(rex::RuntimeConfig& config) override {
    TRACE("[OnPreSetup] entered");
    if (config.gpu_plugin.empty()) {
      config.gpu_plugin = "xenos";
    }
  }

  // Content packages from the dlc folder (src/dlc.cpp), before the game looks
  // for them: the SDK knows the title by now, and the game has not started.
  void OnPreLaunchModule() override {
    ReWorDumpEarly();
    ReWorApplyDebugMenu();
    ReWorInstallDlc();
    ReWorStartOnline();
  }

  void OnPostSetup() override {
    TRACE("[OnPostSetup] entered - XEX loaded, runtime ready");
    fill_dispatch_table(runtime()->virtual_membase());
    mount_mod_overlay(runtime()->file_system(), game_data_root());

    discord_init();
    discord_update();

    rex::ui::RegisterBind("fullscreen_toggle", "F11", "Toggle fullscreen", [this]() {
      window()->SetFullscreen(!window()->IsFullscreen());
    });
#ifdef REWOR_DEV
    StubTracker::instance().start_reporter(5);
#endif
  }

  void OnCreateDialogs(rex::ui::ImGuiDrawer* drawer) override {
    if (REXCVAR_GET(rewor_show_fps)) {
      fps_counter_ = std::make_unique<FpsCounterDialog>(drawer);
    }
    rex::ui::RegisterBind("fps_counter_toggle", "F2", "Toggle FPS counter", [this, drawer]() {
      if (fps_counter_) {
        fps_counter_.reset();
      } else {
        fps_counter_ = std::make_unique<FpsCounterDialog>(drawer);
      }
    });

    // The SDK fills in the config path itself when the app leaves it empty: it
    // is re_wor.toml next to the exe.
    std::filesystem::path config = config_path_;
    if (config.empty()) {
      wchar_t exe[MAX_PATH] = {};
      GetModuleFileNameW(nullptr, exe, MAX_PATH);
      config = std::filesystem::path(exe).parent_path() / "re_wor.toml";
    }
    options_menu_ = CreateOptionsMenu(drawer, config, [this, drawer](bool show) {
      if (show && !fps_counter_) {
        fps_counter_ = std::make_unique<FpsCounterDialog>(drawer);
      } else if (!show) {
        fps_counter_.reset();
      }
    });
    rex::ui::RegisterBind("rewor_options", "F5", "Erased Reboot Version options",
                          [this]() { ToggleOptionsMenu(options_menu_.get()); });
    quit_prompt_ = CreateQuitPrompt(
        drawer, [this]() { return runtime() ? runtime()->input_system() : nullptr; }, window());
    window()->SetTitle("Guitar Hero: Warriors of Rock");
#ifdef _WIN32
    HWND hwnd = static_cast<HWND>(window()->GetNativeWindowHandle());
    if (hwnd) {
      HICON icon = LoadIconA(GetModuleHandleA(NULL), "IDI_ICON1");
      if (icon) {
        SendMessageA(hwnd, WM_SETICON, ICON_BIG, (LPARAM)icon);
        SendMessageA(hwnd, WM_SETICON, ICON_SMALL, (LPARAM)icon);
      }
    }
#endif
  }

  static std::unique_ptr<rex::ui::WindowedApp> Create(rex::ui::WindowedAppContext& ctx) {
#ifdef _WIN32
    SetUnhandledExceptionFilter(CrashHandler);
#endif
    rewor_start_sampler();
    // The name doubles as the config file and user folder name, so it must be
    // filesystem safe. The window title is set separately in OnCreateDialogs.
    return std::unique_ptr<ReWorApp>(new ReWorApp(ctx, "re_wor", PPCImageConfig));
  }

 private:
  std::unique_ptr<FpsCounterDialog> fps_counter_;
  std::unique_ptr<rex::ui::ImGuiDialog> options_menu_;
  std::unique_ptr<rex::ui::ImGuiDialog> quit_prompt_;
  std::filesystem::path config_path_;
};

REX_DEFINE_APP(re_wor, ReWorApp::Create)
