// Win32Inventory, the WindowInventory adapter.
#pragma once
#include "app/ports.hpp"
#include "win_common.hpp"

namespace mb::win32 {

/// Enumerates eligible top-level windows on the dragged window's monitor (visible, resizable, not minimized,
/// maximized, cloaked, owned, tool, excluded, or banned) and records their frame offsets in the WindowRegistry.
/// UI thread. Borrows the registry.
class Win32Inventory final : public WindowInventory {
public:
    /// Creates the adapter over a shared registry.
    explicit Win32Inventory(WindowRegistry& registry) : registry_(registry) {}
    /// Builds the Snapshot for the monitor of the dragged window.
    Snapshot snapshot(WindowId dragged) override;
    /// Returns the visual frame of a window.
    std::optional<Rect> frame_of(WindowId id) override;

private:
    WindowRegistry& registry_;
};

}  // namespace mb::win32
