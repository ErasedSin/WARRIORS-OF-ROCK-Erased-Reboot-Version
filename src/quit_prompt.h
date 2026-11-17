#pragma once

#include <rex/system/interfaces/input.h>
#include <rex/ui/imgui_dialog.h>

#include <functional>
#include <memory>

namespace rex::ui {
class ImGuiDrawer;
class Window;
}  // namespace rex::ui

// The "Quit the game?" question for the main menu (src/quit_prompt.cpp).
std::unique_ptr<rex::ui::ImGuiDialog> CreateQuitPrompt(rex::ui::ImGuiDrawer* drawer,
                                                       std::function<rex::system::IInputSystem*()> input,
                                                       rex::ui::Window* window);

// True when the Erased Reboot Version launcher started the game and waits, hidden, for it to close.
bool LauncherWaiting();

// Closes the game; with to_launcher the waiting launcher comes back instead of closing too.
void QuitGame(bool to_launcher);
