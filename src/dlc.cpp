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
// The title id in the package header says which game a package is for. The game
// reads content of its own title and of the alternate titles its executable
// lists (Warriors of Rock lists three, Guitar Hero 5 among them), so a package of
// one of those is installed under its own title, where the game looks for it.
// Any other package is left out instead of being handed to a game that cannot
// read it. Builds before this check filed every package under the running
// game's title, which crashed Warriors of Rock with Guitar Hero 5 packages: what
// they extracted that way is removed.
//
// Content Guitar Hero 5 already installed (its gh5 folder next to this game's)
// is not extracted a second time: its folders are hard linked into this game's
// content folder, the same files under two names, so they take no more space.
//
// What was installed is listed in dlc/installed.txt (name, size, time). A package
// is extracted again when it changes or when its extracted folder is gone (the
// game's user data moved or was cleaned). Two packages with the same file name
// would share one folder, so only the first is installed.

#include <rex/cvar.h>
#include <rex/logging.h>
#include <rex/runtime.h>
#include <rex/system/kernel_state.h>
#include <rex/system/user_module.h>
#include <rex/system/xex_module.h>
#include <rex/system/xam/content_device.h>
#include <rex/system/xam/content_manager.h>

#include <cstdint>
#include <cstdio>
#include <filesystem>
#include <fstream>
#include <map>
#include <set>
#include <sstream>
#include <string>
#include <system_error>
#include <vector>

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

REXCVAR_DEFINE_STRING(rewor_dlc_titles, "", "re-wor",
                      "Title ids (hex, separated by commas) of other games whose content packages are installed "
                      "too, besides Warriors of Rock's own and the alternate titles its executable lists")
    .lifecycle(rex::cvar::Lifecycle::kRequiresRestart);

REXCVAR_DEFINE_STRING(rewor_dlc_shared, "gh5/userdata;../gh5/userdata", "re-wor",
                      "User data folders of other games (separated by ;, relative to the game's folder) whose "
                      "installed content of the accepted titles is hard linked in instead of extracted again")
    .lifecycle(rex::cvar::Lifecycle::kRequiresRestart);

using rex::X_RESULT;

namespace {

constexpr const char* kLogPrefix = "re-wor";
constexpr const char* kCommonContent = "0000000000000000";
constexpr const char* kMarketplace = "00000002";

std::filesystem::path ExeDir() {
#ifdef _WIN32
  wchar_t path[MAX_PATH] = {};
  GetModuleFileNameW(nullptr, path, MAX_PATH);
  return std::filesystem::path(path).parent_path();
#else
  return std::filesystem::current_path();
#endif
}

std::string TitleDir(uint32_t title) {
  char text[9];
  std::snprintf(text, sizeof(text), "%08X", title);
  return text;
}

// The title id of an STFS package (its metadata, offset 0x360, big endian), or 0
// when the file is not a CON, LIVE or PIRS package.
uint32_t PackageTitleId(const std::filesystem::path& file) {
  std::ifstream in(file, std::ios::binary);
  char magic[4] = {};
  if (!in.read(magic, 4)) return 0;
  const std::string m(magic, 4);
  if (m != "CON " && m != "LIVE" && m != "PIRS") return 0;
  unsigned char id[4] = {};
  in.seekg(0x360);
  if (!in.read(reinterpret_cast<char*>(id), 4)) return 0;
  const uint32_t title = (uint32_t(id[0]) << 24) | (uint32_t(id[1]) << 16) | (uint32_t(id[2]) << 8) | id[3];
  return title ? title : 0xFFFFFFFFu;  // a package without a title id still counts as a package
}

// This game's title, the alternate titles its executable lists, and the ones in
// rewor_dlc_titles.
std::set<uint32_t> AcceptedTitles(rex::system::KernelState* kernel) {
  std::set<uint32_t> titles{kernel->title_id()};
  auto module = kernel->GetExecutableModule();
  if (module && module->xex_module()) {
    for (uint32_t id : module->xex_module()->opt_alternate_title_ids()) titles.insert(id);
  }
  std::stringstream list(REXCVAR_GET(rewor_dlc_titles));
  for (std::string item; std::getline(list, item, ',');) {
    try {
      size_t used = 0;
      const unsigned long value = std::stoul(item, &used, 16);
      if (used) titles.insert(static_cast<uint32_t>(value));
    } catch (...) {
    }
  }
  return titles;
}

// "name|size|time": a package installed before and unchanged since.
std::string Stamp(const std::filesystem::path& file) {
  std::error_code ec;
  const auto size = std::filesystem::file_size(file, ec);
  const auto time = std::filesystem::last_write_time(file, ec).time_since_epoch().count();
  return file.filename().string() + "|" + std::to_string(size) + "|" + std::to_string(time);
}

// The content InstallContent makes of a package with this file name.
rex::system::xam::XCONTENT_AGGREGATE_DATA InstalledData(const std::string& name, uint32_t title) {
  rex::system::xam::XCONTENT_AGGREGATE_DATA data;
  data.device_id = static_cast<uint32_t>(rex::system::xam::DummyDeviceId::HDD);
  data.content_type = rex::system::XContentType::kMarketplaceContent;
  data.title_id = title;
  data.xuid = 0;
  data.set_file_name(name);
  return data;
}

std::filesystem::path HeaderPath(const std::filesystem::path& root, uint32_t title, const std::string& name) {
  return root / kCommonContent / TitleDir(title) / "Headers" / kMarketplace / (name + ".header");
}

// Hard links one installed package (its folder and header) of another game's
// user data into this game's. False when a link cannot be made (another drive):
// what was made is taken out again.
bool LinkPackage(const std::filesystem::path& from_root, const std::filesystem::path& to_root, uint32_t title,
                 const std::string& name) {
  const auto from = from_root / kCommonContent / TitleDir(title) / kMarketplace / name;
  const auto to = to_root / kCommonContent / TitleDir(title) / kMarketplace / name;
  const auto to_header = HeaderPath(to_root, title, name);
  std::error_code ec;
  for (auto it = std::filesystem::recursive_directory_iterator(from, ec);
       !ec && it != std::filesystem::recursive_directory_iterator(); it.increment(ec)) {
    const auto target = to / std::filesystem::relative(it->path(), from, ec);
    if (it->is_directory(ec)) {
      std::filesystem::create_directories(target, ec);
    } else {
      std::filesystem::create_directories(target.parent_path(), ec);
      if (!ec) std::filesystem::create_hard_link(it->path(), target, ec);
    }
    if (ec) break;
  }
  if (!ec) {
    std::filesystem::create_directories(to_header.parent_path(), ec);
    if (!ec) std::filesystem::create_hard_link(HeaderPath(from_root, title, name), to_header, ec);
  }
  if (ec) {
    std::error_code ignored;
    std::filesystem::remove_all(to, ignored);
    std::filesystem::remove(to_header, ignored);
    return false;
  }
  return true;
}

// Links in what the other games in rewor_dlc_shared installed of the accepted
// titles (not this game's own: their packages are for those games).
int LinkSharedContent(const std::filesystem::path& own_root, const std::set<uint32_t>& titles, uint32_t own_title) {
  int linked = 0;
  std::stringstream list(REXCVAR_GET(rewor_dlc_shared));
  for (std::string item; std::getline(list, item, ';');) {
    if (item.empty()) continue;
    std::filesystem::path root = item;
    if (root.is_relative()) root = ExeDir() / root;
    std::error_code ec;
    root = std::filesystem::weakly_canonical(root, ec);
    if (ec || !std::filesystem::is_directory(root / kCommonContent, ec) ||
        std::filesystem::equivalent(root, own_root, ec)) {
      continue;
    }
    for (uint32_t title : titles) {
      if (title == own_title) continue;
      const auto packages = root / kCommonContent / TitleDir(title) / kMarketplace;
      for (auto it = std::filesystem::directory_iterator(packages, ec);
           !ec && it != std::filesystem::directory_iterator(); it.increment(ec)) {
        const std::string name = it->path().filename().string();
        if (!it->is_directory(ec) || !std::filesystem::exists(HeaderPath(root, title, name), ec) ||
            std::filesystem::exists(own_root / kCommonContent / TitleDir(title) / kMarketplace / name, ec)) {
          continue;
        }
        if (LinkPackage(root, own_root, title, name)) {
          ++linked;
        } else {
          REXLOG_ERROR("{}: could not link {} from {} (another drive?)", kLogPrefix, name, root.string());
          return linked;
        }
      }
    }
  }
  return linked;
}

}  // namespace

// Called before the game starts (ReWorApp::OnPreLaunchModule, src/main.cpp).
void ReWorInstallDlc() {
  auto* kernel = rex::system::kernel_state();
  auto* content = kernel ? kernel->content_manager() : nullptr;
  if (!content) return;
  const uint32_t own_title = kernel->title_id();
  const std::set<uint32_t> accepted = AcceptedTitles(kernel);

  std::error_code ec;
  std::filesystem::path own_root = kernel->emulator()->user_data_root();
  if (!own_root.empty()) own_root = std::filesystem::weakly_canonical(std::filesystem::absolute(own_root), ec);
  int shared = 0;
  if (!own_root.empty()) shared = LinkSharedContent(own_root, accepted, own_title);
  if (shared) REXLOG_ERROR("{}: content packages linked from other games: {}", kLogPrefix, shared);

  std::filesystem::path folder = REXCVAR_GET(rewor_dlc_folder);
  if (folder.empty()) return;
  if (folder.is_relative()) folder = ExeDir() / folder;
  if (!std::filesystem::is_directory(folder, ec)) {
    std::filesystem::create_directories(folder, ec);  // so the player sees where packages go
    return;
  }

  const std::filesystem::path record = folder / "installed.txt";
  std::set<std::string> installed;
  {
    std::ifstream in(record);
    for (std::string line; std::getline(in, line);) {
      if (!line.empty()) installed.insert(line);
    }
  }

  std::set<std::string> keep;                                  // the stamps still true after this run
  std::map<std::string, std::filesystem::path> by_name;         // file name -> the package that has it
  std::map<std::string, uint32_t> foreign;                      // packages filed under another title
  int added = 0, failed = 0, other_game = 0, same_name = 0;
  for (auto it = std::filesystem::recursive_directory_iterator(folder, ec);
       !ec && it != std::filesystem::recursive_directory_iterator(); it.increment(ec)) {
    if (!it->is_regular_file(ec) || it->path() == record) continue;
    const uint32_t title = PackageTitleId(it->path());
    if (!title) continue;
    const std::string name = it->path().filename().string();
    if (!accepted.count(title)) {
      ++other_game;
      foreign.emplace(name, title);
      REXLOG_ERROR("{}: {} is content of another game (title {:08X}), not installed", kLogPrefix,
                   it->path().string(), title);
      continue;
    }

    auto [first, inserted] = by_name.emplace(name, it->path());
    if (!inserted) {
      ++same_name;
      REXLOG_ERROR("{}: {} has the same file name as {}, not installed (rename one of them)", kLogPrefix,
                   it->path().string(), first->second.string());
      continue;
    }
    if (title != own_title) foreign.emplace(name, title);

    const std::string stamp = Stamp(it->path());
    if (content->ContentExists(0, InstalledData(name, title))) {
      // Unchanged, or linked from the other game's install (its header has another name).
      const bool linked = !own_root.empty() &&
                          std::filesystem::hard_link_count(HeaderPath(own_root, title, name), ec) > 1 && !ec;
      if (installed.count(stamp) || linked) {
        keep.insert(stamp);
        continue;
      }
    }
    const X_RESULT result = content->InstallContent(it->path(), title);
    if (result == X_ERROR_SUCCESS) {
      keep.insert(stamp);
      ++added;
    } else {
      ++failed;
      REXLOG_ERROR("{}: could not install the content package {} ({:08X})", kLogPrefix, it->path().string(),
                   result);
    }
  }

  // Builds before the title check filed packages of other games under this
  // game's title: take that out, unless a package of this game has the name.
  for (const auto& [name, title] : foreign) {
    auto own = by_name.find(name);
    if (own != by_name.end() && PackageTitleId(own->second) == own_title) continue;
    if (!content->ContentExists(0, InstalledData(name, own_title))) continue;
    const X_RESULT removed = content->DeleteContent(0, InstalledData(name, own_title));
    REXLOG_ERROR("{}: removed what an older build had installed of {} as this game's ({:08X})", kLogPrefix, name,
                 removed);
  }

  if (keep != installed) {
    std::ofstream out(record, std::ios::trunc);
    for (const auto& line : keep) out << line << "\n";
  }
  if (added || failed || other_game || same_name) {
    REXLOG_ERROR("{}: content packages installed: {}, failed: {}, of another game: {}, same name: {}", kLogPrefix,
                 added, failed, other_game, same_name);
  }
}
