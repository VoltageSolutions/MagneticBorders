// TrayIcon, the notification-area icon wrapper.
#pragma once
#include <string>

#include "win_common.hpp"

#include <shellapi.h>

namespace mb::win32 {

/// Shell_NotifyIcon wrapper (NOTIFYICON_VERSION_4). Icon = resource id 1 of this exe. UI thread.
/// Removes the icon on destruction. Mouse and keyboard events arrive at `owner` as `callback_message`.
class TrayIcon {
public:
    /// Loads the icon; does not show it until add().
    TrayIcon(HWND owner, UINT callback_message);
    /// Removes the icon.
    ~TrayIcon();
    TrayIcon(const TrayIcon&) = delete;
    TrayIcon& operator=(const TrayIcon&) = delete;

    /// Adds the icon (replacing any stale entry); also used to re-add it after TaskbarCreated. Returns success.
    bool add(const std::wstring& tooltip);  // also used to re-add after TaskbarCreated
    /// Updates the tooltip if the icon was added.
    void set_tooltip(const std::wstring& tooltip);
    /// Removes the icon if it was added.
    void remove();

private:
    [[nodiscard]] NOTIFYICONDATAW data(const std::wstring& tooltip) const;
    HWND owner_;
    UINT msg_;
    HICON icon_{};
    bool added_{false};
};

}  // namespace mb::win32
