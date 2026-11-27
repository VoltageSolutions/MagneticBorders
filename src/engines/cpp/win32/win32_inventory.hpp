// Win32Inventory, the WindowInventory adapter.
#pragma once
#include "app/ports.hpp"
#include "win_common.hpp"

namespace mb::win32 {

/// Enumerates eligible top-level windows on the dragged window's monitor (visible, resizable, not minimized,
/// maximized, cloaked, owned, tool, excluded, or banned) and records their frame offsets in the WindowRegistry.
/// With a diagnostics log it also writes the `diag: window` and `diag: move result` lines described in
/// docs/spec/engine.md.
/// UI thread. Borrows the registry.
class Win32Inventory final : public WindowInventory {
public:
    /// Creates the adapter over a shared registry.
    /// `diagnostics` is the log that receives the verbose `diag:` lines, or null for none (the normal case).
    explicit Win32Inventory(WindowRegistry& registry, Log* diagnostics = nullptr)
        : registry_(registry), diagnostics_(diagnostics) {}
    /// Builds the Snapshot for the monitor of the dragged window.
    Snapshot snapshot(WindowId dragged) override;
    /// Returns the visual frame of a window.
    std::optional<Rect> frame_of(WindowId id) override;
    /// Writes the `diag: move result` line when a diagnostics log is set.
    void report_move_result(WindowId id, const std::optional<Rect>& held) override;

private:
    WindowRegistry& registry_;
    Log* diagnostics_;
};

}  // namespace mb::win32
