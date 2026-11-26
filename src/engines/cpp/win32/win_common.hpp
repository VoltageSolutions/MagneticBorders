// Shared Win32 helpers: HWND/WindowId conversion, visual_frame, RAII handles and the WindowRegistry.
#pragma once
#ifndef NOMINMAX
#define NOMINMAX
#endif
#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#include <windows.h>

#include <algorithm>
#include <cwctype>
#include <optional>
#include <string>
#include <unordered_map>
#include <unordered_set>
#include <utility>
#include <vector>

#include "core/geometry.hpp"

namespace mb::win32 {

/// Converts a domain WindowId back to an HWND.
[[nodiscard]] inline HWND to_hwnd(WindowId id) {
    return reinterpret_cast<HWND>(static_cast<UINT_PTR>(id));
}
/// Converts an HWND to a domain WindowId.
[[nodiscard]] inline WindowId to_id(HWND h) {
    return static_cast<WindowId>(reinterpret_cast<UINT_PTR>(h));
}

/// Returns the visual frame (DWMWA_EXTENDED_FRAME_BOUNDS) of a window, or nullopt on failure.
[[nodiscard]] std::optional<Rect> visual_frame(HWND hwnd);

/// Move-only RAII owner of a WinEvent hook; unhooks on destruction or reset().
class UniqueWinEventHook {
public:
    UniqueWinEventHook() = default;
    /// Takes ownership of hook h (may be null).
    explicit UniqueWinEventHook(HWINEVENTHOOK h) : h_(h) {}
    UniqueWinEventHook(const UniqueWinEventHook&) = delete;
    UniqueWinEventHook& operator=(const UniqueWinEventHook&) = delete;
    UniqueWinEventHook(UniqueWinEventHook&& o) noexcept : h_(std::exchange(o.h_, nullptr)) {}
    UniqueWinEventHook& operator=(UniqueWinEventHook&& o) noexcept {
        if (this != &o) {
            reset();
            h_ = std::exchange(o.h_, nullptr);
        }
        return *this;
    }
    ~UniqueWinEventHook() { reset(); }
    /// Unhooks and clears the handle.
    void reset() noexcept {
        if (h_) {
            UnhookWinEvent(h_);
            h_ = nullptr;
        }
    }
    /// True when a hook is held.
    explicit operator bool() const noexcept { return h_ != nullptr; }

private:
    HWINEVENTHOOK h_{};
};

/// Move-only RAII owner of a kernel handle; closes it on destruction or reset().
class UniqueHandle {
public:
    UniqueHandle() = default;
    /// Takes ownership of handle h (may be null).
    explicit UniqueHandle(HANDLE h) : h_(h) {}
    UniqueHandle(const UniqueHandle&) = delete;
    UniqueHandle& operator=(const UniqueHandle&) = delete;
    UniqueHandle(UniqueHandle&& o) noexcept : h_(std::exchange(o.h_, nullptr)) {}
    UniqueHandle& operator=(UniqueHandle&& o) noexcept {
        if (this != &o) {
            reset();
            h_ = std::exchange(o.h_, nullptr);
        }
        return *this;
    }
    ~UniqueHandle() { reset(); }
    /// Closes and clears the handle.
    void reset() noexcept {
        if (h_) {
            CloseHandle(h_);
            h_ = nullptr;
        }
    }
    /// Returns the raw handle without releasing it.
    [[nodiscard]] HANDLE get() const noexcept { return h_; }
    /// True when a handle is held.
    explicit operator bool() const noexcept { return h_ != nullptr; }

private:
    HANDLE h_{};
};

/// Difference between GetWindowRect and the visual frame for one window.
/// The inventory records it; the mover adds it back to turn visual frames into SetWindowPos rectangles.
struct FrameOffsets {
    /// GetWindowRect minus visual frame, per side.
    int l{}, t{}, r{}, b{};
};

/// Per-session state shared by Win32Inventory and Win32Mover: excluded executables, frame offsets and banned windows.
/// UI thread only.
class WindowRegistry {
public:
    /// Creates the registry with excluded executable names (matched case-insensitively).
    explicit WindowRegistry(std::vector<std::wstring> excluded_exes) {
        for (auto& e : excluded_exes) excluded_.insert(lower(std::move(e)));
    }
    /// Replaces the excluded executable names.
    void set_excluded(std::vector<std::wstring> excluded_exes) {
        excluded_.clear();
        for (auto& e : excluded_exes) excluded_.insert(lower(std::move(e)));
    }
    /// True when exe (a file name such as `app.exe`) is excluded.
    [[nodiscard]] bool is_excluded_exe(std::wstring exe) const {
        return excluded_.contains(lower(std::move(exe)));
    }
    /// Stores the frame offsets of a window (written by the inventory).
    void remember_offsets(WindowId id, FrameOffsets o) { offsets_[id] = o; }
    /// Returns the stored offsets, or nullopt if the window was never inventoried.
    [[nodiscard]] std::optional<FrameOffsets> offsets(WindowId id) const {
        const auto it = offsets_.find(id);
        return it == offsets_.end() ? std::nullopt : std::optional{it->second};
    }
    /// Marks a window as unmovable (for example, an elevated window that denies SetWindowPos).
    void ban(WindowId id) { banned_.insert(id); }
    /// True when the window was banned; banned windows are skipped by inventory and mover.
    [[nodiscard]] bool banned(WindowId id) const { return banned_.contains(id); }

private:
    static std::wstring lower(std::wstring s) {
        std::ranges::transform(s, s.begin(),
                               [](wchar_t c) { return static_cast<wchar_t>(std::towlower(c)); });
        return s;
    }
    std::unordered_set<std::wstring> excluded_;
    std::unordered_map<WindowId, FrameOffsets> offsets_;
    std::unordered_set<WindowId> banned_;
};

}  // namespace mb::win32
