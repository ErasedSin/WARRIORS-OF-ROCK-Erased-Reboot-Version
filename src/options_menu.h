#pragma once

#include <rex/ui/imgui_dialog.h>

#include <filesystem>
#include <functional>
#include <memory>

// The Erased Reboot Version options, inside the game (F5; the SDK takes F1 for its quick menu): the launcher's
// settings with the game running. Only what is safe to change live applies at
// once; the rest is saved to re_wor.toml and applies the next time the game
// starts, which the menu says next to each one.
std::unique_ptr<rex::ui::ImGuiDialog> CreateOptionsMenu(rex::ui::ImGuiDrawer* drawer,
                                                        std::filesystem::path config_path,
                                                        std::function<void(bool)> set_fps_counter);

// Shows or hides it.
void ToggleOptionsMenu(rex::ui::ImGuiDialog* menu);
