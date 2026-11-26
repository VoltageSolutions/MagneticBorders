// Win32Timer implementation (thread_local instance slot routing the TIMERPROC).
#include <stdexcept>

#include "system_adapters.hpp"

namespace mb::win32 {
namespace {
// The single slot routing the TIMERPROC to the instance (UI thread only).
thread_local Win32Timer* t_timer = nullptr;
constexpr std::size_t kKinds = 3;
}  // namespace

Win32Timer::Win32Timer() {
    if (t_timer) throw std::logic_error("only one Win32Timer per thread");
    t_timer = this;
}

Win32Timer::~Win32Timer() {
    cancel_all();
    t_timer = nullptr;
}

void Win32Timer::arm(TimerKind kind, unsigned delay_ms) {
    auto& id = ids_[static_cast<std::size_t>(kind)];
    if (id) KillTimer(nullptr, id);
    id = SetTimer(nullptr, 0, delay_ms, &Win32Timer::on_timer);
}

void Win32Timer::cancel_all() {
    for (auto& id : ids_) {
        if (id) KillTimer(nullptr, id);
        id = 0;
    }
}

void CALLBACK Win32Timer::on_timer(HWND, UINT, UINT_PTR id, DWORD) {
    if (!t_timer) return;
    for (std::size_t i = 0; i < kKinds; ++i) {
        if (t_timer->ids_[i] != id) continue;
        KillTimer(nullptr, id);  // one-shot
        t_timer->ids_[i] = 0;
        if (t_timer->sink_) t_timer->sink_->on_timer(static_cast<TimerKind>(i));
        return;
    }
}

}  // namespace mb::win32
