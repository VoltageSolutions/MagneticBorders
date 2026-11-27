// Win32PointerHook implementation.
#include "pointer_hook.hpp"

namespace mb::win32 {
namespace {
// The single slot routing the hook callback to the instance (UI thread only).
thread_local Win32PointerHook* t_pointer_hook = nullptr;

/// True for the release of any mouse button.
bool is_button_up(WPARAM wp) {
    return wp == WM_LBUTTONUP || wp == WM_RBUTTONUP || wp == WM_MBUTTONUP || wp == WM_XBUTTONUP;
}
}  // namespace

Win32PointerHook::~Win32PointerHook() { stop(); }

bool Win32PointerHook::start(PointerEvents& sink) {
    if (hook_ != nullptr) stop();
    if (t_pointer_hook != nullptr) return false;
    hook_ =
        SetWindowsHookExW(WH_MOUSE_LL, &Win32PointerHook::on_mouse, GetModuleHandleW(nullptr), 0);
    if (hook_ == nullptr) return false;
    sink_ = &sink;
    t_pointer_hook = this;
    return true;
}

void Win32PointerHook::stop() {
    if (hook_ != nullptr) UnhookWindowsHookEx(hook_);
    hook_ = nullptr;
    sink_ = nullptr;
    if (t_pointer_hook == this) t_pointer_hook = nullptr;
}

std::optional<Point> Win32PointerHook::cursor() {
    POINT p{};
    if (!GetCursorPos(&p)) return std::nullopt;
    return Point{p.x, p.y};
}

LRESULT CALLBACK Win32PointerHook::on_mouse(int code, WPARAM wp, LPARAM lp) {
    Win32PointerHook* self = t_pointer_hook;
    if (code == HC_ACTION && self != nullptr && self->sink_ != nullptr) {
        const auto* m = reinterpret_cast<const MSLLHOOKSTRUCT*>(lp);
        if (wp == WM_MOUSEMOVE) {
            if (const auto to = self->sink_->on_pointer_move({m->pt.x, m->pt.y})) {
                SetCursorPos(to->x, to->y);
                return 1;  // the original movement is replaced by the held cursor position
            }
        } else if (is_button_up(wp)) {
            self->sink_->on_pointer_release({m->pt.x, m->pt.y});  // may stop (unhook) this hook
        }
    }
    return CallNextHookEx(nullptr, code, wp, lp);
}

}  // namespace mb::win32
