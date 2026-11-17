#include "options_menu.h"

#include "quit_prompt.h"

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
    return saved != "pt";
  }
#ifdef _WIN32
  return PRIMARYLANGID(GetUserDefaultUILanguage()) != LANG_PORTUGUESE;
#else
  return true;
#endif
}

// The menu's own fonts (AddOptionsMenuFonts): the SDK's default is a 10 pixel
// bitmap font, too small and too rough for a menu the player reads.
ImFont* g_font_text = nullptr;
ImFont* g_font_bold = nullptr;
ImFont* g_font_title = nullptr;

struct Choice {
  const char* value;
  const char* pt;
  const char* en;
};

enum class When { kNow, kRestart };

// One setting: a cvar shown as a switch (no choices) or a list of choices.
struct Option {
  const char* section;  // "video", "graphics" or "game"
  const char* cvar;
  const char* pt;
  const char* en;
  When when;
  std::vector<Choice> choices;
  bool scripts = false;  // applied by the launcher's patched scripts in mods
  const char* hint_pt = nullptr;
  const char* hint_en = nullptr;
};

// Two settings shown as one: "video_mode" is the pair width x height, "scene_upscaler" the pair dlss_mode / fsr_mode
// ("dlss:quality", "fsr:native_aa" or "off").
const std::vector<Option>& Options() {
  static const std::vector<Option> options = {
      {"video", "video_mode", "Resolução da tela", "Screen resolution", When::kRestart,
       {{"1280x720", "1280x720 (HD)", "1280x720 (HD)"},
        {"848x480", "848x480 (SD widescreen)", "848x480 (SD widescreen)"},
        {"640x480", "640x480 (SD 4:3)", "640x480 (SD 4:3)"},
        {"1920x1080", "1920x1080 (Full HD)", "1920x1080 (Full HD)"},
        {"2560x1440", "2560x1440 (QHD)", "2560x1440 (QHD)"},
        {"3840x2160", "3840x2160 (4K)", "3840x2160 (4K)"},
        {"2560x1080", "2560x1080 (ultrawide)", "2560x1080 (ultrawide)"},
        {"3440x1440", "3440x1440 (ultrawide)", "3440x1440 (ultrawide)"}}},
      {"video", "draw_resolution_percent", "Resolução abaixo de 720p", "Resolution below 720p", When::kNow,
       {{"100", "100%", "100%"},
        {"75", "75% (960x540)", "75% (960x540)"},
        {"50", "50% (640x360)", "50% (640x360)"},
        {"33", "33% (426x240)", "33% (426x240)"}},
       false, "Alivia a placa. Só no DirectX 12, sem DLSS/FSR 3/4.",
       "Lighter on the GPU. DirectX 12 only, without DLSS/FSR 3/4."},
      {"video", "resolution_scale", "Resolução de desenho", "Render resolution", When::kRestart,
       {{"1", "1x (720p)", "1x (720p)"}, {"2", "2x (1440p)", "2x (1440p)"}, {"3", "3x (4K)", "3x (4K)"}},
       false, "Mais nítido, para placa forte.", "Sharper, for a strong GPU."},
      {"video", "present_effect", "Filtro da imagem", "Image filter", When::kNow,
       {{"bilinear", "Bilinear", "Bilinear"}, {"cas", "AMD CAS (nitidez)", "AMD CAS (sharpening)"},
        {"fsr", "AMD FSR 1", "AMD FSR 1"}}},
      {"video", "scene_upscaler", "DLSS / FSR na cena", "DLSS / FSR on the scene", When::kNow,
       {{"off", "Desligado", "Off"},
        {"dlss:dlaa", "NVIDIA DLAA", "NVIDIA DLAA"},
        {"dlss:quality", "NVIDIA DLSS Qualidade", "NVIDIA DLSS Quality"},
        {"fsr:native_aa", "AMD FSR 3/4 anti-serrilhado", "AMD FSR 3/4 anti-aliasing"},
        {"fsr:quality", "AMD FSR 3/4 Qualidade", "AMD FSR 3/4 Quality"}},
       false, "Experimental. Só no DirectX 12.", "Experimental. DirectX 12 only."},
      {"video", "present_vsync", "V-Sync", "V-Sync", When::kNow, {}, false,
       "Desligado: 60 FPS fixos sem esperar o monitor.", "Off: a steady 60 FPS without waiting for the display."},
      {"video", "rewor_show_fps", "Mostrar FPS (F2)", "Show FPS (F2)", When::kNow, {}},
      {"graphics", "gpu", "API gráfica", "Graphics API", When::kRestart,
       {{"any", "Automática", "Automatic"}, {"d3d12", "DirectX 12", "DirectX 12"}, {"vulkan", "Vulkan", "Vulkan"}}},
      {"graphics", "rewor_motion_blur", "Desfoque de movimento", "Motion blur", When::kRestart, {}, true},
      {"graphics", "rewor_screen_noise", "Granulado de tela", "Screen noise", When::kRestart, {}, true},
      {"graphics", "rewor_dof", "Profundidade de campo", "Depth of field", When::kRestart, {}, true},
      {"graphics", "rewor_bloom", "Brilho das luzes (bloom)", "Light bloom", When::kRestart, {}, true},
      {"graphics", "anisotropic_override", "Filtro anisotrópico", "Anisotropic filtering", When::kRestart,
       {{"-1", "Do jogo", "Game default"},
        {"0", "Desligado", "Off"},
        {"2", "2x", "2x"},
        {"3", "4x", "4x"},
        {"4", "8x", "8x"},
        {"5", "16x", "16x"}}},
      {"graphics", "gamma_render_target_as_unorm16", "Cores em 16 bits", "16-bit color", When::kRestart, {}, false,
       "Mais pesado.", "Heavier."},
      {"game", "user_language", "Idioma do jogo", "Game language", When::kRestart,
       {{"1", "English", "English"},
        {"5", "Español", "Español"},
        {"4", "Français", "Français"},
        {"3", "Deutsch", "Deutsch"},
        {"6", "Italiano", "Italiano"}}},
      {"game", "audio_maxqframes", "Buffer de áudio", "Audio buffer", When::kRestart,
       {{"16", "16 (menos atraso)", "16 (less delay)"},
        {"32", "32", "32"},
        {"64", "64 (sem estalos)", "64 (no crackling)"}}},
      {"game", "rewor_debug_menu", "Menu debug do jogo", "Game debug menu", When::kRestart, {}},
  };
  return options;
}

bool FlagExists(const char* name) {
  static const std::vector<std::string> flags = rex::cvar::ListFlags();
  return std::find(flags.begin(), flags.end(), name) != flags.end();
}

bool Available(const Option& option) {
  const std::string cvar = option.cvar;
  if (cvar == "video_mode") return true;
  if (cvar == "scene_upscaler") return FlagExists("dlss_mode") && FlagExists("fsr_mode");
  return FlagExists(option.cvar);
}

std::string Read(const Option& option) {
  const std::string cvar = option.cvar;
  if (cvar == "video_mode") {
    return rex::cvar::GetFlagByName("video_mode_width") + "x" + rex::cvar::GetFlagByName("video_mode_height");
  }
  if (cvar == "scene_upscaler") {
    const std::string dlss = rex::cvar::GetFlagByName("dlss_mode");
    const std::string fsr = rex::cvar::GetFlagByName("fsr_mode");
    if (!dlss.empty() && dlss != "off") return "dlss:" + dlss;
    if (!fsr.empty() && fsr != "off") return "fsr:" + fsr;
    return "off";
  }
  return rex::cvar::GetFlagByName(option.cvar);
}

bool Write(const Option& option, const std::string& value) {
  const std::string cvar = option.cvar;
  if (cvar == "video_mode") {
    size_t x = value.find('x');
    return rex::cvar::SetFlagByName("video_mode_width", value.substr(0, x)) &&
           rex::cvar::SetFlagByName("video_mode_height", value.substr(x + 1));
  }
  if (cvar == "scene_upscaler") {
    const bool dlss = value.rfind("dlss:", 0) == 0;
    const bool fsr = value.rfind("fsr:", 0) == 0;
    return rex::cvar::SetFlagByName("dlss_mode", dlss ? value.substr(5) : "off") &&
           rex::cvar::SetFlagByName("fsr_mode", fsr ? value.substr(4) : "off");
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

// The launcher's colors.
ImU32 Rgb(int r, int g, int b, int a = 255) { return IM_COL32(r, g, b, a); }
const ImVec4 kRed(200 / 255.0f, 16 / 255.0f, 46 / 255.0f, 1.0f);
const ImVec4 kRedBright(232 / 255.0f, 36 / 255.0f, 64 / 255.0f, 1.0f);
const ImVec4 kText(0.93f, 0.93f, 0.93f, 1.0f);
const ImVec4 kText2(0.56f, 0.56f, 0.58f, 1.0f);
const ImVec4 kField(0.11f, 0.11f, 0.12f, 1.0f);

constexpr float kWidth = 820.0f;
constexpr float kHeight = 540.0f;
constexpr float kSide = 210.0f;
constexpr float kFooter = 64.0f;

void PushFontOf(ImFont* font) { ImGui::PushFont(font, font ? font->LegacySize : 0.0f); }

// An on / off switch, like a console game's menu.
bool Switch(const char* id, bool* value) {
  const float height = ImGui::GetFrameHeight();
  const float width = height * 1.9f;
  const ImVec2 pos = ImGui::GetCursorScreenPos();
  const bool clicked = ImGui::InvisibleButton(id, ImVec2(width, height));
  if (clicked) *value = !*value;
  ImDrawList* draw = ImGui::GetWindowDrawList();
  const bool hovered = ImGui::IsItemHovered();
  const ImU32 track = *value ? (hovered ? Rgb(232, 36, 64) : Rgb(200, 16, 46)) : (hovered ? Rgb(64, 64, 68) : Rgb(44, 44, 48));
  draw->AddRectFilled(pos, ImVec2(pos.x + width, pos.y + height), track, height * 0.5f);
  const float radius = height * 0.5f - 3.0f;
  const float knob_x = *value ? pos.x + width - radius - 3.0f : pos.x + radius + 3.0f;
  draw->AddCircleFilled(ImVec2(knob_x, pos.y + height * 0.5f), radius, Rgb(240, 240, 240));
  return clicked;
}

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
      for (const Option& option : Options()) values_.push_back(Available(option) ? Read(option) : std::string());
      status_.clear();
      focus_ = true;
    }
  }

 protected:
  void OnDraw(ImGuiIO& io) override {
    if (!open_) return;
    if (ImGui::IsKeyPressed(ImGuiKey_Escape)) {
      open_ = false;
      return;
    }

    ImGui::PushStyleVar(ImGuiStyleVar_WindowRounding, 0.0f);
    ImGui::PushStyleVar(ImGuiStyleVar_WindowPadding, ImVec2(0, 0));
    ImGui::PushStyleVar(ImGuiStyleVar_WindowBorderSize, 0.0f);
    ImGui::PushStyleVar(ImGuiStyleVar_FrameRounding, 2.0f);
    ImGui::PushStyleVar(ImGuiStyleVar_FramePadding, ImVec2(10, 7));
    ImGui::PushStyleVar(ImGuiStyleVar_ItemSpacing, ImVec2(8, 6));
    ImGui::PushStyleVar(ImGuiStyleVar_PopupRounding, 2.0f);
    ImGui::PushStyleVar(ImGuiStyleVar_ScrollbarSize, 8.0f);
    ImGui::PushStyleColor(ImGuiCol_WindowBg, ImVec4(0.035f, 0.035f, 0.04f, 0.98f));
    ImGui::PushStyleColor(ImGuiCol_ChildBg, ImVec4(0, 0, 0, 0));
    ImGui::PushStyleColor(ImGuiCol_PopupBg, ImVec4(0.08f, 0.08f, 0.09f, 1.0f));
    ImGui::PushStyleColor(ImGuiCol_Text, kText);
    ImGui::PushStyleColor(ImGuiCol_FrameBg, kField);
    ImGui::PushStyleColor(ImGuiCol_FrameBgHovered, ImVec4(0.17f, 0.17f, 0.18f, 1.0f));
    ImGui::PushStyleColor(ImGuiCol_Button, kField);
    ImGui::PushStyleColor(ImGuiCol_ButtonHovered, ImVec4(0.17f, 0.17f, 0.18f, 1.0f));
    ImGui::PushStyleColor(ImGuiCol_ButtonActive, kRed);
    ImGui::PushStyleColor(ImGuiCol_Header, kRed);
    ImGui::PushStyleColor(ImGuiCol_HeaderHovered, ImVec4(kRed.x, kRed.y, kRed.z, 0.55f));
    ImGui::PushStyleColor(ImGuiCol_HeaderActive, kRedBright);
    ImGui::PushStyleColor(ImGuiCol_Border, ImVec4(0.2f, 0.2f, 0.22f, 1.0f));
    ImGui::PushStyleColor(ImGuiCol_ScrollbarBg, ImVec4(0, 0, 0, 0));
    ImGui::PushStyleColor(ImGuiCol_ScrollbarGrab, ImVec4(0.25f, 0.25f, 0.27f, 1.0f));
    ImGui::PushStyleColor(ImGuiCol_NavCursor, kRedBright);
    PushFontOf(g_font_text);

    const ImVec2 size(std::min(kWidth, io.DisplaySize.x - 20.0f), std::min(kHeight, io.DisplaySize.y - 20.0f));
    ImGui::SetNextWindowSize(size, ImGuiCond_Always);
    ImGui::SetNextWindowPos(ImVec2(io.DisplaySize.x * 0.5f, io.DisplaySize.y * 0.5f), ImGuiCond_Always,
                            ImVec2(0.5f, 0.5f));
    if (focus_) {
      ImGui::SetNextWindowFocus();
      focus_ = false;
    }
    if (ImGui::Begin("##rewor_options", nullptr,
                     ImGuiWindowFlags_NoTitleBar | ImGuiWindowFlags_NoResize | ImGuiWindowFlags_NoMove |
                         ImGuiWindowFlags_NoCollapse | ImGuiWindowFlags_NoSavedSettings |
                         ImGuiWindowFlags_NoScrollbar)) {
      DrawFrame(size);
      DrawSidebar(size);
      DrawPage(size);
      DrawFooter(size);
    }
    ImGui::End();

    ImGui::PopFont();
    ImGui::PopStyleColor(16);
    ImGui::PopStyleVar(8);
  }

 private:
  const char* Text(const char* pt, const char* en) const { return english_ ? en : pt; }

  void DrawFrame(const ImVec2& size) {
    ImDrawList* draw = ImGui::GetWindowDrawList();
    const ImVec2 p = ImGui::GetWindowPos();
    draw->AddRectFilled(p, ImVec2(p.x + kSide, p.y + size.y), Rgb(14, 14, 16));
    draw->AddRectFilled(p, ImVec2(p.x + size.x, p.y + 3.0f), Rgb(200, 16, 46));
    draw->AddLine(ImVec2(p.x + kSide, p.y + 3.0f), ImVec2(p.x + kSide, p.y + size.y), Rgb(200, 16, 46, 120));
    draw->AddRect(p, ImVec2(p.x + size.x, p.y + size.y), Rgb(60, 12, 20));
  }

  void DrawSidebar(const ImVec2& size) {
    ImGui::SetCursorPos(ImVec2(24, 26));
    PushFontOf(g_font_title);
    ImGui::TextUnformatted("ERASED");
    ImGui::PopFont();
    ImGui::SetCursorPos(ImVec2(25, ImGui::GetCursorPosY() - 4));
    PushFontOf(g_font_bold);
    ImGui::TextColored(kRedBright, "REBOOT VERSION");
    ImGui::PopFont();

    struct Page {
      const char* id;
      const char* pt;
      const char* en;
    };
    static const Page pages[] = {{"video", "VÍDEO", "VIDEO"}, {"graphics", "GRÁFICOS", "GRAPHICS"}, {"game", "JOGO", "GAME"}};
    float y = 110.0f;
    ImDrawList* draw = ImGui::GetWindowDrawList();
    const ImVec2 origin = ImGui::GetWindowPos();
    PushFontOf(g_font_bold);
    for (int i = 0; i < 3; ++i) {
      const bool active = page_ == i;
      ImGui::SetCursorPos(ImVec2(0, y));
      ImGui::PushID(i);
      if (ImGui::InvisibleButton("##page", ImVec2(kSide, 42))) page_ = i;
      const bool hovered = ImGui::IsItemHovered();
      ImGui::PopID();
      const ImVec2 a(origin.x, origin.y + y);
      const ImVec2 b(origin.x + kSide, origin.y + y + 42);
      if (active) {
        draw->AddRectFilled(a, b, Rgb(36, 8, 14));
        draw->AddRectFilled(a, ImVec2(a.x + 4, b.y), Rgb(232, 36, 64));
      } else if (hovered) {
        draw->AddRectFilled(a, b, Rgb(24, 24, 27));
      }
      const char* label = english_ ? pages[i].en : pages[i].pt;
      const ImVec2 text_size = ImGui::CalcTextSize(label);
      draw->AddText(ImVec2(a.x + 26, a.y + (42 - text_size.y) * 0.5f),
                    active ? Rgb(255, 255, 255) : Rgb(150, 150, 155), label);
      y += 44.0f;
    }
    ImGui::PopFont();

    ImGui::SetCursorPos(ImVec2(24, size.y - 58));
    ImGui::PushTextWrapPos(kSide - 18);
    ImGui::TextColored(kText2, "%s", Text("F5 abre e fecha  ·  Esc fecha", "F5 opens and closes  ·  Esc closes"));
    ImGui::PopTextWrapPos();

    // Started from the launcher, which waits hidden: going back to it closes the game.
    if (LauncherWaiting()) {
      PushFontOf(g_font_bold);
      ImGui::SetCursorPos(ImVec2(18, size.y - 112));
      ImGui::PushStyleColor(ImGuiCol_Button, Rgb(30, 30, 34));
      ImGui::PushStyleColor(ImGuiCol_ButtonHovered, kRed);
      if (ImGui::Button(Text("VOLTAR AO LAUNCHER", "BACK TO LAUNCHER"), ImVec2(kSide - 36, 38))) {
        open_ = false;
        QuitGame(true);
      }
      ImGui::PopStyleColor(2);
      ImGui::PopFont();
    }
  }

  void DrawPage(const ImVec2& size) {
    static const char* const sections[] = {"video", "graphics", "game"};
    static const char* const titles_pt[] = {"Vídeo", "Gráficos", "Jogo"};
    static const char* const titles_en[] = {"Video", "Graphics", "Game"};
    const float left = kSide + 32.0f;
    const float width = size.x - kSide - 64.0f;

    ImGui::SetCursorPos(ImVec2(left, 24));
    PushFontOf(g_font_title);
    ImGui::TextUnformatted(english_ ? titles_en[page_] : titles_pt[page_]);
    ImGui::PopFont();

    ImGui::SetCursorPos(ImVec2(left - 10, 74));
    ImGui::BeginChild("##rows", ImVec2(width + 20, size.y - 74 - kFooter - 8), ImGuiChildFlags_None);
    const auto& options = Options();
    const float control_w = std::min(270.0f, width * 0.46f);
    for (size_t i = 0; i < options.size(); ++i) {
      const Option& option = options[i];
      if (std::string(option.section) != sections[page_] || !Available(option)) continue;
      ImGui::PushID(static_cast<int>(i));
      const bool has_hint = english_ ? option.hint_en != nullptr : option.hint_pt != nullptr;
      const float row_h = has_hint ? 64.0f : 46.0f;
      const ImVec2 row = ImGui::GetCursorScreenPos();
      const float row_w = ImGui::GetContentRegionAvail().x;
      const bool hovered = ImGui::IsMouseHoveringRect(row, ImVec2(row.x + row_w, row.y + row_h));
      ImDrawList* draw = ImGui::GetWindowDrawList();
      if (hovered) draw->AddRectFilled(row, ImVec2(row.x + row_w, row.y + row_h), Rgb(22, 22, 25));
      draw->AddLine(ImVec2(row.x + 10, row.y + row_h), ImVec2(row.x + row_w - 10, row.y + row_h), Rgb(30, 30, 34));

      std::string label = english_ ? option.en : option.pt;
      if (option.when == When::kRestart) label += "  *";
      const float text_h = ImGui::GetTextLineHeight();
      const float label_y = has_hint ? 10.0f : (row_h - text_h) * 0.5f;
      draw->AddText(ImVec2(row.x + 12, row.y + label_y), Rgb(236, 236, 236), label.c_str());
      if (has_hint) {
        draw->AddText(ImGui::GetFont(), ImGui::GetFontSize() * 0.88f, ImVec2(row.x + 12, row.y + label_y + text_h + 3),
                      Rgb(140, 140, 146), english_ ? option.hint_en : option.hint_pt, nullptr,
                      row_w - control_w - 48);
      }

      const float frame_h = ImGui::GetFrameHeight();
      const float control_y = (row_h - frame_h) * 0.5f;
      if (option.choices.empty()) {
        ImGui::SetCursorScreenPos(ImVec2(row.x + row_w - 12 - frame_h * 1.9f, row.y + control_y));
        bool checked = IsTrue(values_[i]);
        if (Switch("##switch", &checked)) values_[i] = checked ? "true" : "false";
      } else {
        const Choice* current = nullptr;
        for (const Choice& c : option.choices) {
          if (values_[i] == c.value) current = &c;
        }
        std::string preview = current ? (english_ ? current->en : current->pt) : values_[i];
        ImGui::SetCursorScreenPos(ImVec2(row.x + row_w - 12 - control_w, row.y + control_y));
        ImGui::SetNextItemWidth(control_w);
        if (ImGui::BeginCombo("##choice", preview.c_str(), ImGuiComboFlags_HeightLarge)) {
          for (const Choice& c : option.choices) {
            if (ImGui::Selectable(english_ ? c.en : c.pt, current == &c, 0, ImVec2(0, 26))) values_[i] = c.value;
          }
          ImGui::EndCombo();
        }
      }
      ImGui::SetCursorScreenPos(ImVec2(row.x, row.y + row_h + 1));
      ImGui::Dummy(ImVec2(row_w, 0));
      ImGui::PopID();
    }
    ImGui::EndChild();
  }

  void DrawFooter(const ImVec2& size) {
    const float left = kSide + 32.0f;
    const float y = size.y - kFooter + 12.0f;
    ImGui::SetCursorPos(ImVec2(left, y + 4));
    ImGui::PushTextWrapPos(size.x - 280.0f);
    if (!status_.empty()) {
      ImGui::TextColored(kRedBright, "%s", status_.c_str());
    } else {
      ImGui::TextColored(kText2, "%s",
                         Text("* vale na próxima vez que abrir o jogo", "* applies the next time the game starts"));
    }
    ImGui::PopTextWrapPos();

    PushFontOf(g_font_bold);
    ImGui::SetCursorPos(ImVec2(size.x - 32 - 110 - 10 - 110, y));
    if (ImGui::Button(Text("FECHAR", "CLOSE"), ImVec2(110, 38))) open_ = false;
    ImGui::SetCursorPos(ImVec2(size.x - 32 - 110, y));
    ImGui::PushStyleColor(ImGuiCol_Button, kRed);
    ImGui::PushStyleColor(ImGuiCol_ButtonHovered, kRedBright);
    if (ImGui::Button(Text("SALVAR", "SAVE"), ImVec2(110, 38))) Save();
    ImGui::PopStyleColor(2);
    ImGui::PopFont();
  }

  void Save() {
    const auto& options = Options();
    bool restart = false;
    bool scripts = false;
    bool failed = false;
    for (size_t i = 0; i < options.size(); ++i) {
      const Option& option = options[i];
      if (!Available(option) || values_[i] == Read(option)) continue;
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
      status_ = Text("Salvo, mas algumas opções não foram aplicadas. Use o launcher.",
                     "Saved, but some options could not be applied. Use the launcher.");
    } else if (restart) {
      status_ = Text("Salvo. Abra o jogo de novo para as opções com *.",
                     "Saved. Open the game again for the options with *.");
    } else {
      status_ = Text("Salvo.", "Saved.");
    }
  }

  std::filesystem::path config_path_;
  std::function<void(bool)> set_fps_counter_;
  bool open_ = false;
  bool english_ = false;
  int page_ = 0;
  bool focus_ = false;
  std::vector<std::string> values_;
  std::string status_;
};

}  // namespace

ImFont* MenuFont(int kind) { return kind == 2 ? g_font_title : kind == 1 ? g_font_bold : g_font_text; }

void AddOptionsMenuFonts(ImFontAtlas* atlas) {
#ifdef _WIN32
  static const ImWchar kRanges[] = {0x0020, 0x00FF, 0x2000, 0x206F, 0};
  ImFontConfig config;
  config.OversampleH = 2;
  config.OversampleV = 1;
  const char* regular = "C:\\Windows\\Fonts\\segoeui.ttf";
  const char* semibold = "C:\\Windows\\Fonts\\seguisb.ttf";
  const char* bold = "C:\\Windows\\Fonts\\segoeuib.ttf";
  if (std::filesystem::exists(regular)) g_font_text = atlas->AddFontFromFileTTF(regular, 17.0f, &config, kRanges);
  const char* strong = std::filesystem::exists(semibold) ? semibold : bold;
  if (std::filesystem::exists(strong)) g_font_bold = atlas->AddFontFromFileTTF(strong, 17.0f, &config, kRanges);
  if (std::filesystem::exists(bold)) g_font_title = atlas->AddFontFromFileTTF(bold, 30.0f, &config, kRanges);
#else
  (void)atlas;
#endif
}

std::unique_ptr<rex::ui::ImGuiDialog> CreateOptionsMenu(rex::ui::ImGuiDrawer* drawer,
                                                        std::filesystem::path config_path,
                                                        std::function<void(bool)> set_fps_counter) {
  return std::make_unique<OptionsMenu>(drawer, std::move(config_path), std::move(set_fps_counter));
}

void ToggleOptionsMenu(rex::ui::ImGuiDialog* menu) {
  if (menu) static_cast<OptionsMenu*>(menu)->Toggle();
}
