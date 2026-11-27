// Ports (pure virtual interfaces) between DragController and the operating system, plus the Snapshot value type.
#pragma once
#include <optional>
#include <span>
#include <string_view>
#include <utility>
#include <vector>

#include "core/geometry.hpp"

namespace mb {

/// Window state captured once at drag start by WindowInventory::snapshot.
struct Snapshot {
    /// The dragged window (if eligible) plus every eligible window on the same monitor.
    std::vector<WinInfo> windows;  // dragged window (if eligible) + eligible others
    /// Work area of the dragged window's monitor.
    Rect work_area{};
    /// Work areas of every monitor, the dragged window's one included; tells which screen sides touch another monitor.
    std::vector<Rect> work_areas;
    /// Every visible, uncloaked, not minimized top-level window that hides what is below it, in z-order from the top,
    /// on any monitor. Magnetic attraction uses it to find which parts of other windows can be seen (SNAP-038).
    std::vector<std::pair<WindowId, Rect>> stack;
    /// DPI of the dragged window divided by 96; scales the glue tolerance and the snap distances.
    double dpi_scale{1.0};
};

/// Port: reads window state. Implemented by win32::Win32Inventory; called on the UI thread.
class WindowInventory {
public:
    virtual ~WindowInventory() = default;
    /// Returns eligible windows, the work area, and the DPI scale for the monitor of `dragged`.
    [[nodiscard]] virtual Snapshot snapshot(WindowId dragged) = 0;
    /// Returns the current visual frame of a window, or nullopt if it cannot be read.
    [[nodiscard]] virtual std::optional<Rect> frame_of(WindowId id) = 0;
};

/// Port: moves and resizes windows. Implemented by win32::Win32Mover; called on the UI thread.
class WindowMover {
public:
    virtual ~WindowMover() = default;
    /// Applies all moves as one batch and returns the frames read back afterwards (empty for skipped windows).
    virtual std::vector<Move> apply(std::span<const Move> moves) = 0;
};

/// Port: monotonic millisecond clock. Implemented by win32::SteadyClock.
class Clock {
public:
    virtual ~Clock() = default;
    /// Milliseconds since an arbitrary fixed point.
    [[nodiscard]] virtual std::uint64_t now_ms() = 0;
};

/// The three controller timers: Flush (trailing throttled apply), Settle (re-check during a drag),
/// Verify (re-check after the drag ends).
enum class TimerKind { Flush, Settle, Verify };

/// Port: one-shot timers on the UI thread; re-arming a kind replaces it. Implemented by win32::Win32Timer.
class Timer {
public:
    virtual ~Timer() = default;
    /// Starts (or restarts) the timer of this kind.
    virtual void arm(TimerKind kind, unsigned delay_ms) = 0;
    /// Cancels every pending timer.
    virtual void cancel_all() = 0;
};

/// Sink for timer expiry. Implemented by DragController; the timer adapter calls it on the UI thread.
class TimerEvents {
public:
    virtual ~TimerEvents() = default;
    /// Called once when the timer of this kind fires.
    virtual void on_timer(TimerKind kind) = 0;
};

/// Port: line logger. Implemented by win32::FileLog and win32::ConsoleLog.
class Log {
public:
    virtual ~Log() = default;
    /// Writes one line.
    virtual void info(std::string_view msg) = 0;
    /// Writes one line only when debug output is enabled.
    virtual void debug(std::string_view msg) = 0;
};

/// Sink for mouse movement during a move. Implemented by DragController; win32::PointerHook calls it on the UI
/// thread for every mouse movement while it runs.
class PointerEvents {
public:
    virtual ~PointerEvents() = default;
    /// Windows is about to put the cursor at `proposed`. Returns the position to put it at instead, or nullopt to
    /// let the movement through unchanged.
    [[nodiscard]] virtual std::optional<Point> on_pointer_move(const Point& proposed) = 0;
    /// A mouse button was released. The move is over even if its end event has not arrived yet.
    virtual void on_pointer_release() = 0;
};

/// Port: watches the mouse while a window is moved (SNAP-070). Implemented by win32::PointerHook with a low-level
/// mouse hook that is installed only between start and stop. Called on the UI thread.
class PointerHook {
public:
    virtual ~PointerHook() = default;
    /// Starts sending mouse movement to `sink`. Returns false when the hook cannot be installed.
    virtual bool start(PointerEvents& sink) = 0;
    /// Stops sending mouse movement. Safe to call when not started.
    virtual void stop() = 0;
    /// The cursor position now, or nullopt if it cannot be read.
    [[nodiscard]] virtual std::optional<Point> cursor() = 0;
};

/// Sink for drag lifecycle events. Implemented by DragController; win32::WinEventSource calls it on the UI thread.
class DragEvents {
public:
    virtual ~DragEvents() = default;
    /// A window started a move or resize.
    virtual void on_move_size_start(WindowId id) = 0;
    /// The dragged window changed position or size.
    virtual void on_location_change(WindowId id) = 0;
    /// The move or resize ended.
    virtual void on_move_size_end(WindowId id) = 0;
};

}  // namespace mb
