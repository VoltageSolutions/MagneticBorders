// Win32Mover, the WindowMover adapter.
#pragma once
#include "app/ports.hpp"
#include "win_common.hpp"

namespace mb::win32 {

/// Applies moves with one BeginDeferWindowPos/DeferWindowPos/EndDeferWindowPos batch, falling back to SetWindowPos
/// per window (banning windows that fail). Converts visual frames to window rects with the registry offsets.
/// UI thread. Borrows the registry.
class Win32Mover final : public WindowMover {
public:
    /// Creates the adapter over a shared registry.
    explicit Win32Mover(WindowRegistry& registry) : registry_(registry) {}
    /// Applies the moves and returns the visual frames read back; skips hung, banned or never-inventoried windows.
    std::vector<Move> apply(std::span<const Move> moves) override;

private:
    WindowRegistry& registry_;
};

}  // namespace mb::win32
