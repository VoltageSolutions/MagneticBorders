// WinEventSource implementation (thread_local instance slot routing the C callback).
#include "win32_event_source.hpp"

#include <stdexcept>

namespace mb::win32 {
namespace {
// The single slot routing the C callback to the instance (UI thread only).
thread_local WinEventSource* t_instance = nullptr;
constexpr DWORD kHookFlags = WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS;
}  // namespace

WinEventSource::WinEventSource(DragEvents& sink) : sink_(sink) {
    if (t_instance) throw std::logic_error("only one WinEventSource per thread");
    t_instance = this;
    session_hook_ = UniqueWinEventHook{
        SetWinEventHook(EVENT_SYSTEM_MOVESIZESTART, EVENT_SYSTEM_MOVESIZEEND, nullptr,
                        &WinEventSource::on_win_event, 0, 0, kHookFlags)};
    if (!session_hook_) {
        t_instance = nullptr;
        throw std::runtime_error("SetWinEventHook failed");
    }
}

WinEventSource::~WinEventSource() {
    location_hook_.reset();
    session_hook_.reset();
    t_instance = nullptr;
}

void CALLBACK WinEventSource::on_win_event(HWINEVENTHOOK, DWORD event, HWND hwnd, LONG id_object,
                                           LONG id_child, DWORD, DWORD) {
    if (t_instance) t_instance->handle(event, hwnd, id_object, id_child);
}

void WinEventSource::handle(DWORD event, HWND hwnd, LONG id_object, LONG id_child) {
    switch (event) {
        case EVENT_SYSTEM_MOVESIZESTART: {
            if (id_object != OBJID_WINDOW || !hwnd) return;
            DWORD pid = 0;
            GetWindowThreadProcessId(hwnd, &pid);
            dragged_ = hwnd;
            location_hook_ = UniqueWinEventHook{
                SetWinEventHook(EVENT_OBJECT_LOCATIONCHANGE, EVENT_OBJECT_LOCATIONCHANGE, nullptr,
                                &WinEventSource::on_win_event, pid, 0, kHookFlags)};
            sink_.on_move_size_start(to_id(hwnd));
            break;
        }
        case EVENT_OBJECT_LOCATIONCHANGE:
            if (hwnd && hwnd == dragged_ && id_object == OBJID_WINDOW && id_child == CHILDID_SELF)
                sink_.on_location_change(to_id(hwnd));
            break;
        case EVENT_SYSTEM_MOVESIZEEND:
            if (hwnd && hwnd == dragged_) {
                location_hook_.reset();
                dragged_ = nullptr;
                sink_.on_move_size_end(to_id(hwnd));
            }
            break;
        default: break;
    }
}

}  // namespace mb::win32
