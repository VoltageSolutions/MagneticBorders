// Win32Mover, the WindowMover adapter.
#pragma once
#include "app/ports.hpp"
#include "win_common.hpp"

namespace mb::win32 {

/// Applies moves with one BeginDeferWindowPos/DeferWindowPos/EndDeferWindowPos batch, falling back to SetWindowPos
/// per window (banning windows that fail). Converts visual frames to window rects with the registry offsets.
/// With a diagnostics log it also writes the `move:` lines described in docs/spec/engine.md.
/// UI thread. Borrows the registry and the log.
class Win32Mover final : public WindowMover {
public:
    /// Creates the adapter over a shared registry. `diagnostics` is the log that receives the verbose `move:` lines,
    /// or null for none (the normal case).
    explicit Win32Mover(WindowRegistry& registry, Log* diagnostics = nullptr)
        : registry_(registry), diagnostics_(diagnostics) {}
    /// Applies the moves and returns the visual frames read back; skips hung, banned or never-inventoried windows.
    std::vector<Move> apply(std::span<const Move> moves) override;

private:
    WindowRegistry& registry_;
    Log* diagnostics_;
};

}  // namespace mb::win32
