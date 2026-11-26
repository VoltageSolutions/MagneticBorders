// TrayIcon implementation.
#include "tray_icon.hpp"

#include <shellapi.h>

namespace mb::win32 {

TrayIcon::TrayIcon(HWND owner, UINT callback_message)
    : owner_(owner), msg_(callback_message),
      icon_(static_cast<HICON>(LoadImageW(GetModuleHandleW(nullptr), MAKEINTRESOURCEW(1),
                                          IMAGE_ICON, GetSystemMetrics(SM_CXSMICON),
                                          GetSystemMetrics(SM_CYSMICON), LR_SHARED))) {}

TrayIcon::~TrayIcon() { remove(); }

NOTIFYICONDATAW TrayIcon::data(const std::wstring& tooltip) const {
    NOTIFYICONDATAW nid{};
    nid.cbSize = sizeof(nid);
    nid.hWnd = owner_;
    nid.uID = 1;
    nid.uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_SHOWTIP;
    nid.uCallbackMessage = msg_;
    nid.hIcon = icon_ ? icon_ : LoadIconW(nullptr, MAKEINTRESOURCEW(32512));
    wcsncpy_s(nid.szTip, tooltip.c_str(), _TRUNCATE);
    return nid;
}

bool TrayIcon::add(const std::wstring& tooltip) {
    NOTIFYICONDATAW nid = data(tooltip);
    Shell_NotifyIconW(NIM_DELETE, &nid);  // clear a stale entry (e.g. after explorer restart)
    added_ = Shell_NotifyIconW(NIM_ADD, &nid) != FALSE;
    if (added_) {
        nid.uVersion = NOTIFYICON_VERSION_4;
        Shell_NotifyIconW(NIM_SETVERSION, &nid);
    }
    return added_;
}

void TrayIcon::set_tooltip(const std::wstring& tooltip) {
    if (!added_) return;
    NOTIFYICONDATAW nid = data(tooltip);
    Shell_NotifyIconW(NIM_MODIFY, &nid);
}

void TrayIcon::remove() {
    if (!added_) return;
    NOTIFYICONDATAW nid{};
    nid.cbSize = sizeof(nid);
    nid.hWnd = owner_;
    nid.uID = 1;
    Shell_NotifyIconW(NIM_DELETE, &nid);
    added_ = false;
}

}  // namespace mb::win32
