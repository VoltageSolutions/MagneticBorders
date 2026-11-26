// MessageWindow, the hidden top-level window that receives the engine's messages, and its handler interface.
#pragma once
#include "win_common.hpp"

namespace mb::win32 {

/// Receives window messages from MessageWindow. Implemented by host EngineApp.
class MessageHandler {
public:
    virtual ~MessageHandler() = default;
    /// Handles one message on the UI thread; return the LRESULT for the window procedure.
    virtual LRESULT on_message(HWND hwnd, UINT msg, WPARAM wp, LPARAM lp) = 0;
};

/// Hidden top-level window (not message-only: it must receive TaskbarCreated broadcasts) on the calling thread.
/// Owns the HWND and destroys it on destruction. The handler is borrowed and must outlive the window.
class MessageWindow {
public:
    /// Registers the class and creates the window; throws std::runtime_error on failure.
    explicit MessageWindow(MessageHandler& handler);
    /// Destroys the window.
    ~MessageWindow();
    MessageWindow(const MessageWindow&) = delete;
    MessageWindow& operator=(const MessageWindow&) = delete;
    /// The window handle; used as the owner of the tray icon and the target of posted messages.
    [[nodiscard]] HWND hwnd() const { return hwnd_; }

private:
    static LRESULT CALLBACK wnd_proc(HWND, UINT, WPARAM, LPARAM);
    HWND hwnd_{};
};

}  // namespace mb::win32
