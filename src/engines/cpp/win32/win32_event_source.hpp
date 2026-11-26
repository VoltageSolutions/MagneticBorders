// WinEventSource, the WinEvent hook adapter that turns move/size events into DragEvents.
#pragma once
#include "app/ports.hpp"
#include "win_common.hpp"

namespace mb::win32 {

/// Installs the permanent MOVESIZESTART/END hook and, per drag, a LOCATIONCHANGE hook scoped to the dragged window's
/// process. Forwards events to the sink on the installing thread (out-of-context hooks need its message loop).
/// Owns both hooks and unhooks on destruction. At most one instance per thread.
class WinEventSource {
public:
    /// Installs the hook; throws std::logic_error if one exists on this thread or std::runtime_error on failure.
    explicit WinEventSource(DragEvents& sink);
    /// Unhooks both hooks.
    ~WinEventSource();
    WinEventSource(const WinEventSource&) = delete;
    WinEventSource& operator=(const WinEventSource&) = delete;

private:
    static void CALLBACK on_win_event(HWINEVENTHOOK, DWORD event, HWND hwnd, LONG id_object,
                                      LONG id_child, DWORD, DWORD);
    void handle(DWORD event, HWND hwnd, LONG id_object, LONG id_child);

    DragEvents& sink_;
    UniqueWinEventHook session_hook_;
    UniqueWinEventHook location_hook_;
    HWND dragged_{};
};

}  // namespace mb::win32
