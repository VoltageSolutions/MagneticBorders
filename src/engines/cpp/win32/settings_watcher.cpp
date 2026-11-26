// SettingsWatcher implementation (FindFirstChangeNotification loop).
#include "settings_watcher.hpp"

namespace mb::win32 {

SettingsWatcher::SettingsWatcher(const std::filesystem::path& dir, HWND target, UINT message)
    : stop_(CreateEventW(nullptr, TRUE, FALSE, nullptr)) {
    const HANDLE stop = stop_.get();
    const std::wstring d = dir.wstring();
    thread_ = std::thread([d, target, message, stop] {
        const HANDLE change = FindFirstChangeNotificationW(
            d.c_str(), FALSE,
            FILE_NOTIFY_CHANGE_FILE_NAME | FILE_NOTIFY_CHANGE_LAST_WRITE | FILE_NOTIFY_CHANGE_SIZE);
        if (change == INVALID_HANDLE_VALUE) return;
        const HANDLE handles[2] = {stop, change};
        while (WaitForMultipleObjects(2, handles, FALSE, INFINITE) == WAIT_OBJECT_0 + 1) {
            PostMessageW(target, message, 0, 0);
            if (!FindNextChangeNotification(change)) break;
        }
        FindCloseChangeNotification(change);
    });
}

SettingsWatcher::~SettingsWatcher() {
    SetEvent(stop_.get());
    if (thread_.joinable()) thread_.join();
}

}  // namespace mb::win32
