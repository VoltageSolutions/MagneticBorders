// MessageWindow implementation: window class, window procedure trampoline to MessageHandler.
#include "message_window.hpp"

#include <stdexcept>

namespace mb::win32 {
namespace {
constexpr wchar_t kClass[] = L"MagneticBorders.MessageWindow";
}

LRESULT CALLBACK MessageWindow::wnd_proc(HWND h, UINT m, WPARAM wp, LPARAM lp) {
    if (m == WM_NCCREATE) {
        const auto* cs = reinterpret_cast<CREATESTRUCTW*>(lp);
        SetWindowLongPtrW(h, GWLP_USERDATA, reinterpret_cast<LONG_PTR>(cs->lpCreateParams));
    }
    auto* handler = reinterpret_cast<MessageHandler*>(GetWindowLongPtrW(h, GWLP_USERDATA));
    if (handler && m != WM_NCCREATE) return handler->on_message(h, m, wp, lp);
    return DefWindowProcW(h, m, wp, lp);
}

MessageWindow::MessageWindow(MessageHandler& handler) {
    WNDCLASSEXW wc{};
    wc.cbSize = sizeof(wc);
    wc.lpfnWndProc = &MessageWindow::wnd_proc;
    wc.hInstance = GetModuleHandleW(nullptr);
    wc.lpszClassName = kClass;
    RegisterClassExW(&wc);
    hwnd_ = CreateWindowExW(WS_EX_TOOLWINDOW, kClass, L"Magnetic Borders", WS_POPUP, 0, 0, 0, 0,
                            nullptr, nullptr, wc.hInstance, &handler);
    if (!hwnd_) throw std::runtime_error("CreateWindowEx failed");
}

MessageWindow::~MessageWindow() {
    if (hwnd_) {
        SetWindowLongPtrW(hwnd_, GWLP_USERDATA, 0);
        DestroyWindow(hwnd_);
    }
}

}  // namespace mb::win32
