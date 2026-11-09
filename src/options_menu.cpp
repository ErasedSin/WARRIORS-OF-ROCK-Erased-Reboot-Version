#include "options_menu.h"

#include <rex/cvar.h>

#include <imgui.h>

#include <algorithm>
#include <fstream>
#include <string>
#include <vector>

#ifdef _WIN32
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>
#endif

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

// The launcher's language choice (launcher_language.txt next to the game), or
// the Windows language.
bool UseEnglish() {
  std::ifstream file(ExeDir() / "launcher_language.txt");
  std::string saved;
  if (file >> saved) {
    if (saved == "en") return true;
    if (saved == "pt") return false;
  }
#ifdef _WIN32
  return PRIMARYLANGID(GetUserDefaultUILanguage()) != LANG_PORTUGUESE;
#else
  return true;
#endif
}

struct Choice {
  const char* value;
  const char* pt;
  const char* en;
};

enum class When { kNow, kRestart };

// One setting: a cvar shown as a checkbox (no choices) or a list of choices.
struct Option {
  const char* section;  // "video", "graphics" or "game"
  const char* cvar;
  const char* pt;
  const char* en;
  When when;
  std::vector<Choice> choices;
  bool scripts = false;  // applied by the launcher's patched scripts in mods
};

const std::vector<Option>& Options() {
  static const std::vector<Option> options = {
      {"video", "video_mode", "Resolução do jogo", "Game resolution", When::kRestart,
       {{"1280x720", "1280x720 (HD, padrão)", "1280x720 (HD, default)"},
        {"848x480", "848x480 (SD widescreen)", "848x480 (SD widescreen)"},
        {"640x480", "640x480 (SD 4:3)", "640x480 (SD 4:3)"},
        {"1920x1080", "1920x1080 (Full HD)", "1920x1080 (Full HD)"},
        {"2560x1080", "2560x1080 (ultrawide, experimental)", "2560x1080 (ultrawide, experimental)"},
        {"3440x1440", "3440x1440 (ultrawide, experimental)", "3440x1440 (ultrawide, experimental)"}}},
      {"video", "draw_resolution_percent", "Resolução interna (abaixo de 720p)", "Internal resolution (below 720p)",
       When::kRestart,
       {{"100", "100% (normal)", "100% (normal)"},
        {"75", "75% (960x540, mais leve)", "75% (960x540, lighter)"},
        {"50", "50% (640x360, bem mais leve)", "50% (640x360, much lighter)"},
        {"33", "33% (426x240, o mais leve)", "33% (426x240, lightest)"}}},
      {"video", "present_effect", "Ajuste da imagem na tela", "Image scaling", When::kRestart,
       {{"bilinear", "Bilinear (mais leve)", "Bilinear (lightest)"},
        {"cas", "AMD CAS (nitidez)", "AMD CAS (sharpening)"},
        {"fsr", "AMD FSR", "AMD FSR"}}},
      {"video", "present_vsync", "V-Sync (desligado = 60 FPS fixos sem esperar o monitor)",
       "V-Sync (off = a steady 60 FPS without waiting for the display)", When::kNow, {}},
      {"video", "rewor_show_fps", "Mostrar FPS (F2)", "Show FPS (F2)", When::kNow, {}},
      {"graphics", "rewor_motion_blur", "Desfoque de movimento", "Motion blur", When::kRestart, {}, true},
      {"graphics", "rewor_screen_noise", "Granulado de tela", "Screen noise", When::kRestart, {}, true},
      {"graphics", "rewor_dof", "Profundidade de campo (DOF)", "Depth of field (DOF)", When::kRestart, {}, true},
      {"graphics", "rewor_bloom", "Brilho das luzes (bloom)", "Light bloom", When::kRestart, {}, true},
      {"graphics", "anisotropic_override", "Filtro anisotrópico", "Anisotropic filtering", When::kRestart,
       {{"-1", "Do jogo", "Game default"},
        {"0", "Desligado (mais leve)", "Off (lightest)"},
        {"2", "2x", "2x"},
        {"3", "4x", "4x"},
        {"4", "8x", "8x"},
        {"5", "16x", "16x"}}},
      {"graphics", "gamma_render_target_as_unorm16", "Cores em 16 bits (mais pesado)", "16-bit color (heavier)",
       When::kRestart, {}},
      {"game", "rewor_debug_menu", "Menu debug do jogo", "Game debug menu",
       When::kRestart, {}},
      {"game", "user_language", "Idioma do jogo", "Game language", When::kRestart,
       {{"1", "English", "English"},
        {"5", "Español", "Español"},
        {"4", "Français", "Français"},
        {"3", "Deutsch", "Deutsch"},
        {"6", "Italiano", "Italiano"}}},
      {"game", "audio_maxqframes", "Buffer de áudio", "Audio buffer", When::kRestart,
       {{"16", "16 (menos atraso)", "16 (less delay)"},
        {"32", "32", "32"},
        {"64", "64 (sem estalos em PC fraco)", "64 (no crackling on weak PCs)"}}},
  };
  return options;
}

bool FlagExists(const char* name) {
  static const std::vector<std::string> flags = rex::cvar::ListFlags();
  return std::find(flags.begin(), flags.end(), name) != flags.end();
}

// The value shown for an option. "video_mode" is the pair width x height.
std::string Read(const Option& option) {
  if (std::string(option.cvar) == "video_mode") {
    return rex::cvar::GetFlagByName("video_mode_width") + "x" + rex::cvar::GetFlagByName("video_mode_height");
  }
  return rex::cvar::GetFlagByName(option.cvar);
}

bool Write(const Option& option, const std::string& value) {
  if (std::string(option.cvar) == "video_mode") {
    size_t x = value.find('x');
    return rex::cvar::SetFlagByName("video_mode_width", value.substr(0, x)) &&
           rex::cvar::SetFlagByName("video_mode_height", value.substr(x + 1));
  }
  return rex::cvar::SetFlagByName(option.cvar, value);
}

bool IsTrue(const std::string& value) { return value == "true" || value == "1"; }

// Runs the launcher with --apply-scripts, which rewrites the patched game
// scripts in mods from re_wor.toml (the effects are read when the game starts).
bool ApplyScripts() {
#ifdef _WIN32
  const std::filesystem::path dir = ExeDir();
  const std::filesystem::path candidates[] = {
      dir / "re-wor launcher.exe",
      dir.parent_path() / "Erased Reboot Version.exe",
      dir.parent_path() / "re-wor launcher.exe",
  };
  for (const auto& launcher : candidates) {
    if (!std::filesystem::exists(launcher)) continue;
    std::wstring command = L"\"" + launcher.wstring() + L"\" --apply-scripts";
    STARTUPINFOW si{};
    si.cb = sizeof(si);
    PROCESS_INFORMATION pi{};
    if (CreateProcessW(nullptr, command.data(), nullptr, nullptr, FALSE, CREATE_NO_WINDOW, nullptr,
                       launcher.parent_path().c_str(), &si, &pi)) {
      CloseHandle(pi.hThread);
      CloseHandle(pi.hProcess);
      return true;
    }
  }
#endif
  return false;
}

const ImVec4 kRed(0.78f, 0.06f, 0.18f, 1.0f);
const ImVec4 kRedBright(0.91f, 0.14f, 0.25f, 1.0f);
const ImVec4 kRedDark(0.43f, 0.03f, 0.09f, 1.0f);
const ImVec4 kGray(0.6f, 0.6f, 0.6f, 1.0f);

class OptionsMenu : public rex::ui::ImGuiDialog {
 public:
  OptionsMenu(rex::ui::ImGuiDrawer* drawer, std::filesystem::path config_path,
              std::function<void(bool)> set_fps_counter)
      : ImGuiDialog(drawer), config_path_(std::move(config_path)), set_fps_counter_(std::move(set_fps_counter)) {}

  void Toggle() {
    open_ = !open_;
    if (open_) {
      english_ = UseEnglish();
      values_.clear();
      for (const Option& option : Options()) values_.push_back(Read(option));
      status_.clear();
    }
  }

 protected:
  void OnDraw(ImGuiIO& io) override {
    if (!open_) return;
    const char* title = english_ ? "ERASED REBOOT VERSION  -  Options###rewor_options"
                                 : "ERASED REBOOT VERSION  -  Opções###rewor_options";

    ImGui::PushStyleColor(ImGuiCol_TitleBg, kRedDark);
    ImGui::PushStyleColor(ImGuiCol_TitleBgActive, kRed);
    ImGui::PushStyleColor(ImGuiCol_WindowBg, ImVec4(0.05f, 0.05f, 0.05f, 0.96f));
    ImGui::PushStyleColor(ImGuiCol_Border, kRed);
    ImGui::PushStyleColor(ImGuiCol_FrameBg, ImVec4(0.13f, 0.13f, 0.13f, 1.0f));
    ImGui::PushStyleColor(ImGuiCol_FrameBgHovered, kRedDark);
    ImGui::PushStyleColor(ImGuiCol_CheckMark, kRedBright);
    ImGui::PushStyleColor(ImGuiCol_Button, kRedDark);
    ImGui::PushStyleColor(ImGuiCol_ButtonHovered, kRed);
    ImGui::PushStyleColor(ImGuiCol_ButtonActive, kRedBright);
    ImGui::PushStyleColor(ImGuiCol_Header, kRedDark);
    ImGui::PushStyleColor(ImGuiCol_HeaderHovered, kRed);
    ImGui::PushStyleColor(ImGuiCol_Tab, kRedDark);
    ImGui::PushStyleColor(ImGuiCol_TabHovered, kRed);
    ImGui::PushStyleColor(ImGuiCol_TabActive, kRed);

    ImGui::SetNextWindowSize(ImVec2(520, 0), ImGuiCond_Always);
    ImGui::SetNextWindowPos(ImVec2(io.DisplaySize.x * 0.5f, io.DisplaySize.y * 0.5f), ImGuiCond_Appearing,
                            ImVec2(0.5f, 0.5f));
    bool keep_open = true;
    if (ImGui::Begin(title, &keep_open, ImGuiWindowFlags_NoCollapse | ImGuiWindowFlags_NoSavedSettings)) {
      if (ImGui::BeginTabBar("##rewor_options")) {
        DrawTab("video", english_ ? "VIDEO" : "VÍDEO");
        DrawTab("graphics", english_ ? "GRAPHICS" : "GRÁFICOS");
        DrawTab("game", english_ ? "GAME" : "JOGO");
        ImGui::EndTabBar();
      }
      ImGui::Separator();
      ImGui::TextColored(kGray, "%s",
                         english_ ? "* applies the next time the game starts." : "* vale na próxima vez que abrir o jogo.");
      if (ImGui::Button(english_ ? "SAVE" : "SALVAR", ImVec2(110, 30))) Save();
      ImGui::SameLine();
      if (ImGui::Button(english_ ? "CLOSE" : "FECHAR", ImVec2(110, 30))) keep_open = false;
      if (!status_.empty()) {
        ImGui::PushTextWrapPos(0.0f);
        ImGui::TextColored(kRedBright, "%s", status_.c_str());
        ImGui::PopTextWrapPos();
      }
    }
    ImGui::End();
    ImGui::PopStyleColor(15);
    if (!keep_open) open_ = false;
  }

 private:
  void DrawTab(const char* section, const char* label) {
    if (!ImGui::BeginTabItem(label)) return;
    ImGui::Spacing();
    const auto& options = Options();
    for (size_t i = 0; i < options.size(); ++i) {
      const Option& option = options[i];
      if (std::string(option.section) != section) continue;
      if (std::string(option.cvar) != "video_mode" && !FlagExists(option.cvar)) continue;
      ImGui::PushID(static_cast<int>(i));
      std::string label_text = english_ ? option.en : option.pt;
      if (option.when == When::kRestart) label_text += " *";
      if (option.choices.empty()) {
        bool checked = IsTrue(values_[i]);
        if (ImGui::Checkbox(label_text.c_str(), &checked)) values_[i] = checked ? "true" : "false";
      } else {
        const Choice* current = nullptr;
        for (const Choice& c : option.choices) {
          if (values_[i] == c.value) current = &c;
        }
        std::string preview = current ? (english_ ? current->en : current->pt) : values_[i];
        ImGui::TextUnformatted(label_text.c_str());
        ImGui::SetNextItemWidth(-1);
        if (ImGui::BeginCombo("##choice", preview.c_str())) {
          for (const Choice& c : option.choices) {
            if (ImGui::Selectable(english_ ? c.en : c.pt, current == &c)) values_[i] = c.value;
          }
          ImGui::EndCombo();
        }
      }
      ImGui::Spacing();
      ImGui::PopID();
    }
    ImGui::EndTabItem();
  }

  void Save() {
    const auto& options = Options();
    bool restart = false;
    bool scripts = false;
    bool failed = false;
    for (size_t i = 0; i < options.size(); ++i) {
      const Option& option = options[i];
      if (values_[i] == Read(option)) continue;
      if (!Write(option, values_[i])) {
        failed = true;
        continue;
      }
      if (option.when == When::kRestart) restart = true;
      if (option.scripts) scripts = true;
      if (std::string(option.cvar) == "rewor_show_fps" && set_fps_counter_) set_fps_counter_(IsTrue(values_[i]));
    }
    rex::cvar::SaveConfig(config_path_);
    bool scripts_ok = !scripts || ApplyScripts();
    if (failed || !scripts_ok) {
      status_ = english_ ? "Saved, but some options could not be applied. Use the launcher to apply the effects."
                         : "Salvo, mas algumas opções não foram aplicadas. Use o launcher para aplicar os efeitos.";
    } else if (restart) {
      status_ = english_ ? "Saved. Close and open the game again to apply the options marked with *."
                         : "Salvo. Feche e abra o jogo de novo para aplicar as opções marcadas com *.";
    } else {
      status_ = english_ ? "Saved." : "Salvo.";
    }
  }

  std::filesystem::path config_path_;
  std::function<void(bool)> set_fps_counter_;
  bool open_ = false;
  bool english_ = false;
  std::vector<std::string> values_;
  std::string status_;
};

}  // namespace

std::unique_ptr<rex::ui::ImGuiDialog> CreateOptionsMenu(rex::ui::ImGuiDrawer* drawer,
                                                        std::filesystem::path config_path,
                                                        std::function<void(bool)> set_fps_counter) {
  return std::make_unique<OptionsMenu>(drawer, std::move(config_path), std::move(set_fps_counter));
}

void ToggleOptionsMenu(rex::ui::ImGuiDialog* menu) {
  if (menu) static_cast<OptionsMenu*>(menu)->Toggle();
}
