// Win32PointerHook, the low-level mouse hook adapter that lets the controller hold the cursor during a move.
#pragma once
#include "app/ports.hpp"
#include "win_common.hpp"

namespace mb::win32 {

/// Installs a WH_MOUSE_LL hook between start and stop (only while a window is moved, SNAP-070). For each mouse
/// movement it asks the sink where the cursor may go; when that differs, it blocks the movement and puts the cursor
/// there with SetCursorPos, so Windows moves the window to the held position itself. A button release is passed
/// on to the sink. The hook runs on the installing thread, which must pump messages. At most one per thread.
class Win32PointerHook final : public PointerHook {
public:
    Win32PointerHook() = default;
    /// Removes the hook if it is installed.
    ~Win32PointerHook() override;
    Win32PointerHook(const Win32PointerHook&) = delete;
    Win32PointerHook& operator=(const Win32PointerHook&) = delete;

    /// Installs the hook. Returns false when SetWindowsHookExW fails or another hook runs on this thread.
    bool start(PointerEvents& sink) override;
    /// Removes the hook.
    void stop() override;
    /// GetCursorPos, or nullopt when it fails.
    [[nodiscard]] std::optional<Point> cursor() override;

private:
    static LRESULT CALLBACK on_mouse(int code, WPARAM wp, LPARAM lp);

    HHOOK hook_{nullptr};
    PointerEvents* sink_{nullptr};
};

}  // namespace mb::win32
