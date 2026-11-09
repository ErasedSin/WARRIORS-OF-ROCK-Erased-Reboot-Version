// Downloadable content and custom songs from a folder.
//
// The player drops Xbox 360 content packages (STFS: CON, LIVE or PIRS files,
// subfolders allowed) into the dlc folder next to the game. Before the game
// starts, each package not installed yet goes through the SDK's
// ContentManager::InstallContent, which extracts it into the content folder the
// game enumerates (0000000000000000/<title id>/00000002/<file name>/) with its
// license header, as if bought on the console. Custom songs made with Onyx are
// such packages too, together with the song cache package Onyx builds for them
// (Other tools > GH:WoR cache, over all the packages, the official ones too).
//
// What was installed is listed in dlc/installed.txt (name, size, time), so a
// package is only extracted again when it changes.

#include <rex/cvar.h>
#include <rex/logging.h>
#include <rex/system/kernel_state.h>
#include <rex/system/xam/content_manager.h>

#include <cstdio>
#include <filesystem>
#include <fstream>
#include <set>
#include <string>
#include <system_error>

#ifdef _WIN32
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>
#endif

REXCVAR_DEFINE_STRING(rewor_dlc_folder, "dlc", "re-wor",
                      "Folder with Xbox 360 content packages (DLC and custom songs: CON, LIVE or PIRS files), "
                      "relative to the game's folder; installed when the game starts")
    .lifecycle(rex::cvar::Lifecycle::kRequiresRestart);

using rex::X_RESULT;

namespace {

std::filesystem::path ExeDir() {
#ifdef _WIN32
  wchar_t path[MAX_PATH] = {};
  GetModuleFileNameW(nullptr, path, MAX_PATH);
  return std::filesystem::path(path).parent_path();
#else
  return std::filesystem::current_path();
#endif
}

bool IsContentPackage(const std::filesystem::path& file) {
  std::ifstream in(file, std::ios::binary);
  char magic[4] = {};
  if (!in.read(magic, 4)) return false;
  const std::string m(magic, 4);
  return m == "CON " || m == "LIVE" || m == "PIRS";
}

// "name|size|time": a package installed before and unchanged since.
std::string Stamp(const std::filesystem::path& file) {
  std::error_code ec;
  const auto size = std::filesystem::file_size(file, ec);
  const auto time = std::filesystem::last_write_time(file, ec).time_since_epoch().count();
  return file.filename().string() + "|" + std::to_string(size) + "|" + std::to_string(time);
}

}  // namespace

// Called before the game starts (ReWorApp::OnPreLaunchModule, src/main.cpp).
void ReWorInstallDlc() {
  std::filesystem::path folder = REXCVAR_GET(rewor_dlc_folder);
  if (folder.empty()) return;
  if (folder.is_relative()) folder = ExeDir() / folder;
  std::error_code ec;
  if (!std::filesystem::is_directory(folder, ec)) {
    std::filesystem::create_directories(folder, ec);  // so the player sees where packages go
    return;
  }

  auto* kernel = rex::system::kernel_state();
  auto* content = kernel ? kernel->content_manager() : nullptr;
  if (!content) return;

  const std::filesystem::path record = folder / "installed.txt";
  std::set<std::string> installed;
  {
    std::ifstream in(record);
    for (std::string line; std::getline(in, line);) {
      if (!line.empty()) installed.insert(line);
    }
  }

  int added = 0, failed = 0;
  for (auto it = std::filesystem::recursive_directory_iterator(folder, ec);
       !ec && it != std::filesystem::recursive_directory_iterator(); it.increment(ec)) {
    if (!it->is_regular_file(ec) || it->path() == record || !IsContentPackage(it->path())) continue;
    const std::string stamp = Stamp(it->path());
    if (installed.count(stamp)) continue;
    const X_RESULT result = content->InstallContent(it->path());
    if (result == X_ERROR_SUCCESS) {
      installed.insert(stamp);
      ++added;
    } else {
      ++failed;
      REXLOG_ERROR("re-wor: could not install the content package {} ({:08X})", it->path().string(), result);
    }
  }

  if (added) {
    std::ofstream out(record, std::ios::trunc);
    for (const auto& line : installed) out << line << "\n";
  }
  if (added || failed) {
    REXLOG_ERROR("re-wor: content packages installed: {}, failed: {}", added, failed);
  }
}
