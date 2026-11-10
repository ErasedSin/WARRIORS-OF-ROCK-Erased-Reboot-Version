// Quitting from the main menu.
//
// The game has no way out of its own: on the console the Guide button went to
// the dashboard. In the main menu, B (the red fret) now asks whether to quit:
// A (green) closes the game, B goes back. While the question is up the game
// does not see the controllers.
//
// Whether the main menu is on screen comes from the game's screen elements.
// Its scripts create and destroy them (CreateScreenElement and
// DestroyScreenElement), each with an id, the QBKey of its name; the engine
// finds one by id in a hash table (sub_823CC0B0, the table object at
// [0x829A8854]). The element 0x55074978 exists in the main menu only, and
// 0xCC5FECBC is dialog_box_container, a message box over the menu, which takes
// B itself.

#include "quit_prompt.h"

#include <rex/cvar.h>
#include <rex/input/input.h>
#include <rex/input/input_system.h>
#include <rex/logging.h>
#include <rex/system/kernel_state.h>
#include <rex/system/xmemory.h>
#include <rex/ui/window.h>

#include <imgui.h>

#include <atomic>
#include <cstdint>
#include <cstring>
#include <filesystem>
#include <fstream>
#include <functional>
#include <string>

#ifdef _WIN32
#include <windows.h>
#endif

REXCVAR_DEFINE_BOOL(rewor_quit_prompt, true, "re-wor",
                    "In the main menu, B asks whether to quit the game (A quits, B goes back)");

namespace {

constexpr uint32_t kElementTable = 0x829A8854;
constexpr uint32_t kMainMenuElement = 0x55074978;
constexpr uint32_t kDialogBoxElement = 0xCC5FECBC;  // dialog_box_container
constexpr uint32_t kGuestLow = 0x40000000;

std::atomic<bool> g_main_menu{false};

uint32_t Load32(const uint8_t* base, uint32_t address) {
  uint32_t value;
  std::memcpy(&value, base + address, 4);
  return _byteswap_ulong(value);
}

// One of the table object's two hash tables: { u32 bits, entry* buckets, ...,
// u8 keep_empty at +21 }; an entry is { u32 key, u32 value, entry* next }, and
// the chain starts at buckets[id & ((1 << bits) - 1)]. Like sub_823CC0B0, an
// entry counts when its value is set or the table keeps empty ones.
bool InTable(const uint8_t* base, uint32_t table, uint32_t id) {
  if (table < kGuestLow) return false;
  const uint32_t bits = Load32(base, table);
  const uint32_t buckets = Load32(base, table + 4);
  if (buckets < kGuestLow || bits >= 32) return false;
  const bool keep_empty = base[table + 21] != 0;
  uint32_t entry = buckets + (id & ((1u << bits) - 1)) * 12;
  for (int i = 0; i < 256 && entry >= kGuestLow; ++i) {
    if (Load32(base, entry) == id && (keep_empty || Load32(base, entry + 4) != 0)) return true;
    entry = Load32(base, entry + 8);
  }
  return false;
}

bool ElementExists(const uint8_t* base, uint32_t id) {
  const uint32_t object = Load32(base, kElementTable);
  if (object < kGuestLow) return false;
  return InTable(base, Load32(base, object + 12), id) || InTable(base, Load32(base, object + 16), id);
}

bool UseEnglish() {
#ifdef _WIN32
  wchar_t exe[MAX_PATH] = {};
  GetModuleFileNameW(nullptr, exe, MAX_PATH);
  std::ifstream file(std::filesystem::path(exe).parent_path() / "launcher_language.txt");
  std::string saved;
  if (file >> saved) return saved == "en";
  return PRIMARYLANGID(GetUserDefaultUILanguage()) != LANG_PORTUGUESE;
#else
  return true;
#endif
}

class QuitPromptDialog : public rex::ui::ImGuiDialog {
 public:
  QuitPromptDialog(rex::ui::ImGuiDrawer* drawer, std::function<rex::system::IInputSystem*()> input,
                   rex::ui::Window* window)
      : ImGuiDialog(drawer), get_input_(std::move(input)), window_(window), english_(UseEnglish()) {}

  ~QuitPromptDialog() override {
    if (blocking_ && input_) input_->RemoveUIInputBlocker();
  }

 protected:
  void OnDraw(ImGuiIO& io) override {
    // The runtime makes its input system after the dialogs.
    if (!input_ && get_input_) input_ = static_cast<rex::input::InputSystem*>(get_input_());
    if (!input_ || !REXCVAR_GET(rewor_quit_prompt)) return;

    uint16_t held = 0;
    for (uint32_t user = 0; user < 4; ++user) {
      rex::input::X_INPUT_STATE state{};
      if (input_->GetStateForUI(user, &state) == 0) held |= state.gamepad.buttons;
    }
    const uint16_t pressed = held & ~last_held_;
    last_held_ = held;

    if (!open_) {
      // Closed: the game gets the controllers back once nothing is held, so
      // the B that closed the question does not reach the menu.
      if (blocking_ && held == 0) {
        input_->RemoveUIInputBlocker();
        blocking_ = false;
      }
      if ((pressed & rex::input::X_INPUT_GAMEPAD_B) && g_main_menu.load(std::memory_order_relaxed)) {
        open_ = true;
        if (!blocking_) {
          input_->AddUIInputBlocker();
          blocking_ = true;
        }
      }
      return;
    }

    bool quit = (pressed & rex::input::X_INPUT_GAMEPAD_A) != 0;
    bool back = (pressed & rex::input::X_INPUT_GAMEPAD_B) != 0;

    const ImVec2 size(360.0f, 0.0f);
    ImGui::SetNextWindowPos(ImVec2(io.DisplaySize.x * 0.5f, io.DisplaySize.y * 0.5f), ImGuiCond_Always,
                            ImVec2(0.5f, 0.5f));
    ImGui::SetNextWindowSize(size, ImGuiCond_Always);
    ImGui::PushStyleColor(ImGuiCol_WindowBg, ImVec4(0.04f, 0.04f, 0.04f, 0.97f));
    ImGui::PushStyleColor(ImGuiCol_Border, ImVec4(0.78f, 0.06f, 0.18f, 1.0f));
    ImGui::PushStyleColor(ImGuiCol_Button, ImVec4(0.43f, 0.03f, 0.09f, 1.0f));
    ImGui::PushStyleColor(ImGuiCol_ButtonHovered, ImVec4(0.78f, 0.06f, 0.18f, 1.0f));
    ImGui::PushStyleColor(ImGuiCol_ButtonActive, ImVec4(0.91f, 0.14f, 0.25f, 1.0f));
    ImGui::PushStyleVar(ImGuiStyleVar_WindowBorderSize, 2.0f);
    ImGui::PushStyleVar(ImGuiStyleVar_WindowPadding, ImVec2(20.0f, 16.0f));
    const ImGuiWindowFlags flags = ImGuiWindowFlags_NoDecoration | ImGuiWindowFlags_NoSavedSettings |
                                   ImGuiWindowFlags_NoMove | ImGuiWindowFlags_AlwaysAutoResize;
    if (ImGui::Begin("##rewor_quit", nullptr, flags)) {
      ImGui::SetWindowFontScale(1.3f);
      ImGui::TextUnformatted(english_ ? "Quit the game?" : "Sair do jogo?");
      ImGui::SetWindowFontScale(1.0f);
      ImGui::Spacing();
      ImGui::TextDisabled("%s", english_ ? "A (green): quit     B (red): back"
                                         : "A (verde): sair     B (vermelho): voltar");
      ImGui::Spacing();
      const float button = (size.x - 40.0f - 8.0f) * 0.5f;
      if (ImGui::Button(english_ ? "Quit" : "Sair", ImVec2(button, 32.0f))) quit = true;
      ImGui::SameLine(0.0f, 8.0f);
      if (ImGui::Button(english_ ? "Back" : "Voltar", ImVec2(button, 32.0f))) back = true;
    }
    ImGui::End();
    ImGui::PopStyleVar(2);
    ImGui::PopStyleColor(5);

    if (quit) {
      REXLOG_INFO("[quit] quitting from the main menu");
      open_ = false;
      if (window_) window_->RequestClose();
    } else if (back) {
      open_ = false;
    }
  }

 private:
  std::function<rex::system::IInputSystem*()> get_input_;
  rex::input::InputSystem* input_ = nullptr;
  rex::ui::Window* window_;
  bool english_;
  bool open_ = false;
  bool blocking_ = false;
  uint16_t last_held_ = 0;
};

}  // namespace

// Midasm hook at 0x8242C510 (sub_8242C450, the main thread's frame, before it
// waits for the previous one): once per frame, on the thread that runs the
// scripts, so the table is not being changed while it is read.
void ReWorMainMenuHook() {
  auto* kernel = rex::system::kernel_state();
  if (!kernel || !kernel->memory()) return;
  const uint8_t* base = kernel->memory()->virtual_membase();
  const bool menu = ElementExists(base, kMainMenuElement) && !ElementExists(base, kDialogBoxElement);
  if (menu != g_main_menu.exchange(menu, std::memory_order_relaxed)) {
    REXLOG_INFO("[quit] main menu {}", menu ? "shown" : "left");
  }
}

std::unique_ptr<rex::ui::ImGuiDialog> CreateQuitPrompt(rex::ui::ImGuiDrawer* drawer,
                                                       std::function<rex::system::IInputSystem*()> input,
                                                       rex::ui::Window* window) {
  return std::make_unique<QuitPromptDialog>(drawer, std::move(input), window);
}
