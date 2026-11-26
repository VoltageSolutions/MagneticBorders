// EngineApp (the composition root and message handler) and run_engine.
#define NOMINMAX
#define WIN32_LEAN_AND_MEAN
#include <windows.h>

#include <atomic>
#include <chrono>
#include <condition_variable>
#include <exception>
#include <memory>
#include <mutex>

#include "app/drag_controller.hpp"
#include "app/pipe_protocol.hpp"
#include "app/settings.hpp"
#include "engine_app.hpp"
#include "mb_version.hpp"
#include "settings.hpp"
#include "win32/file_log.hpp"
#include "win32/message_window.hpp"
#include "win32/paths.hpp"
#include "win32/pipe_server.hpp"
#include "win32/run_key.hpp"
#include "win32/settings_watcher.hpp"
#include "win32/system_adapters.hpp"
#include "win32/tray_icon.hpp"
#include "win32/win32_event_source.hpp"
#include "win32/win32_inventory.hpp"
#include "win32/win32_mover.hpp"

namespace mb::host {
namespace {

/// Posted by SettingsWatcher when the data folder changes.
constexpr UINT kMsgSettingsChanged = WM_APP + 1;
/// Posted by the pipe thread; lParam carries a heap-allocated std::shared_ptr<PipeJob>.
constexpr UINT kMsgPipeRequest = WM_APP + 2;
/// Tray icon callback message.
constexpr UINT kMsgTray = WM_APP + 3;
/// Id of the settings reload debounce timer on the message window.
constexpr UINT_PTR kTimerReload = 1;
/// Debounce delay before reloading settings.
constexpr UINT kReloadDebounceMs = 100;
/// How long a pipe thread waits for the UI thread to answer.
constexpr DWORD kPipeTimeoutMs = 500;

/// Command ids of the tray context menu.
enum MenuId : UINT { kMenuHeader = 1, kMenuEnabled, kMenuSettings, kMenuExit };

/// A pipe request travelling to the UI thread. The pipe thread waits on `cv` until the UI thread sets `done`.
/// Shared between both threads through a shared_ptr.
struct PipeJob {
    std::string request;
    std::string response;
    bool done{false};
    std::mutex mu;
    std::condition_variable cv;
};

/// Thread id of the UI thread; read by the console control handler.
std::atomic<DWORD> g_ui_thread{0};

/// Console control handler (Ctrl+C, Ctrl+Break, close): posts WM_QUIT to the UI thread.
BOOL WINAPI on_console_event(DWORD type) {
    if (type == CTRL_C_EVENT || type == CTRL_BREAK_EVENT || type == CTRL_CLOSE_EVENT) {
        PostThreadMessageW(g_ui_thread.load(), WM_QUIT, 0, 0);
        return TRUE;
    }
    return FALSE;
}

/// Converts UTF-8 strings to UTF-16.
std::vector<std::wstring> widen_all(const std::vector<std::string>& in) {
    std::vector<std::wstring> out;
    out.reserve(in.size());
    for (const auto& s : in) out.push_back(win32::utf8_to_wide(s));
    return out;
}

/// Owns every adapter and the DragController, in dependency order, and handles messages on the UI thread:
/// settings reload, pipe requests, tray events and TaskbarCreated. Only serve() runs on a pipe thread.
class EngineApp final : public win32::MessageHandler {
public:
    /// Constructs the adapters and the controller and connects the timer to it.
    explicit EngineApp(const EngineOptions& opts)
        : log_(win32::log_path(kEngineId), opts.console, opts.verbose), registry_({}),
          inventory_(registry_), mover_(registry_),
          controller_(inventory_, mover_, clock_, timer_, log_,
                      ControllerOptions{12, "MagneticBorders.Cpp", opts.verbose}),
          started_(std::chrono::steady_clock::now()) {
        timer_.set_sink(controller_);
    }

    /// Creates the window, tray icon, hooks, watcher and pipe server, then runs the message loop until WM_QUIT.
    int run() {
        log_.info(std::string("Magnetic Borders (C++) ") + std::string(kVersion) +
                  " starting, pid " + std::to_string(GetCurrentProcessId()));
        BOOL full_drag = TRUE;
        SystemParametersInfoW(SPI_GETDRAGFULLWINDOWS, 0, &full_drag, 0);
        if (!full_drag)
            log_.warn("'Show window contents while dragging' is off; live magnetic coupling will "
                      "not work.");

        taskbar_created_ = RegisterWindowMessageW(L"TaskbarCreated");
        win32::MessageWindow window{*this};
        hwnd_ = window.hwnd();
        win32::TrayIcon tray{hwnd_, kMsgTray};
        tray_ = &tray;
        tray.add(tooltip());

        reload_settings(true);
        win32::WinEventSource events{controller_};
        win32::SettingsWatcher watcher{win32::data_dir(), hwnd_, kMsgSettingsChanged};
        std::unique_ptr<win32::PipeServer> pipe;
        pipe = std::make_unique<win32::PipeServer>(
            [this](const std::string& line) { return serve(line); });

        g_ui_thread = GetCurrentThreadId();
        MSG msg;
        while (GetMessageW(&msg, nullptr, 0, 0) > 0) {
            TranslateMessage(&msg);
            DispatchMessageW(&msg);
        }
        alive_ = false;
        pipe.reset();
        tray.remove();
        tray_ = nullptr;
        log_.info("exiting");
        return 0;
    }

    /// Window procedure body (UI thread).
    LRESULT on_message(HWND hwnd, UINT msg, WPARAM wp, LPARAM lp) override {
        if (taskbar_created_ && msg == taskbar_created_) {
            if (tray_) tray_->add(tooltip());
            return 0;
        }
        switch (msg) {
            case kMsgSettingsChanged:
                SetTimer(hwnd, kTimerReload, kReloadDebounceMs, nullptr);
                return 0;
            case WM_TIMER:
                if (wp == kTimerReload) {
                    KillTimer(hwnd, kTimerReload);
                    reload_settings(false);
                    return 0;
                }
                break;
            case kMsgPipeRequest: {
                std::unique_ptr<std::shared_ptr<PipeJob>> holder{
                    reinterpret_cast<std::shared_ptr<PipeJob>*>(lp)};
                PipeJob& job = **holder;
                const std::string reply = handle_pipe(job.request);
                {
                    std::scoped_lock lock(job.mu);
                    job.response = reply;
                    job.done = true;
                }
                job.cv.notify_all();
                return 0;
            }
            case kMsgTray: on_tray(LOWORD(lp)); return 0;
            case WM_DESTROY: return 0;
            default: break;
        }
        return DefWindowProcW(hwnd, msg, wp, lp);
    }

private:
    /// Tray tooltip text; marked disabled when the master switch is off.
    [[nodiscard]] std::wstring tooltip() const {
        return settings_.enabled ? L"Magnetic Borders" : L"Magnetic Borders – disabled";
    }

    /// Pushes magnetic coupling activity and tolerance from settings into the controller.
    void refresh_glue() {
        controller_.set_glue_active(settings_.glue_active());
        controller_.set_glue_tolerance(settings_.glue_tolerance_px);
    }

    /// Pushes magnetic attraction activity and tunables from settings into the controller.
    void refresh_snap() {
        controller_.set_snap_active(settings_.snap_active());
        controller_.set_snap_options({settings_.snap_distance_px, settings_.snap_breakaway_px,
                                      settings_.snap_max_speed, settings_.snap_screen_edges,
                                      settings_.snap_windows});
    }

    /// Flips the saved master switch (`enabled`) in settings.json, keeping every other key. The settings watcher
    /// then applies the change like any other edit.
    void toggle_enabled() {
        const auto text = read_settings_file();
        const bool current = text ? parse_settings(*text).value_or(Settings{}).enabled : true;
        const auto updated = with_enabled(text.value_or(std::string{}), !current);
        if (!updated || !write_settings_file(*updated)) {
            log_.warn("could not update settings.json; the Enabled switch was not changed");
            return;
        }
        log_.info(std::string("Enabled switched ") + (!current ? "on" : "off") +
                  " from the tray menu");
    }

    /// Applies new settings: exclusions, controller, start-at-login and logging.
    void apply_settings(const Settings& s, bool initial) {
        const Settings prev = settings_;
        settings_ = s;
        registry_.set_excluded(widen_all(s.excluded_apps));
        refresh_glue();
        refresh_snap();
        if (tray_) tray_->set_tooltip(tooltip());
        if (initial || s.start_at_login != prev.start_at_login) {
            win32::set_start_at_login(s.start_at_login, win32::exe_path());
            log_.info(std::string("start_at_login=") + (s.start_at_login ? "true" : "false"));
        }
        if (initial || s != prev) {
            log_.info(std::string("settings: enabled=") + (s.enabled ? "true" : "false") +
                      " magnetic_coupling=" + (s.glue_enabled ? "on" : "off") +
                      " tolerance_px=" + std::to_string(s.glue_tolerance_px) +
                      " magnetic_attraction=" + (s.snap_enabled ? "on" : "off") +
                      " snap_distance_px=" + std::to_string(s.snap_distance_px) +
                      " snap_breakaway_px=" + std::to_string(s.snap_breakaway_px) +
                      " snap_max_speed=" + std::to_string(s.snap_max_speed) +
                      " snap_screen_edges=" + (s.snap_screen_edges ? "on" : "off") +
                      " snap_windows=" + (s.snap_windows ? "on" : "off") +
                      " excluded_apps=" + std::to_string(s.excluded_apps.size()));
        }
    }

    /// Reads and parses settings.json; a missing file gives defaults, an unparsable file keeps the previous settings.
    void reload_settings(bool initial) {
        const auto text = read_settings_file();
        if (!text) {
            apply_settings(Settings{}, initial);  // missing file -> defaults
            return;
        }
        if (const auto parsed = parse_settings(*text)) {
            apply_settings(*parsed, initial);
        } else {
            log_.warn("settings.json could not be parsed; keeping previous settings");
            if (initial) apply_settings(Settings{}, true);
        }
    }

    /// Builds the status snapshot for the pipe.
    [[nodiscard]] EngineStatus status() const {
        EngineStatus st;
        st.engine = kEngineId;
        st.version = kVersion;
        st.pid = GetCurrentProcessId();
        st.settings = settings_;
        st.drags = controller_.drags();
        st.glued_drags = controller_.glued_drags();
        st.uptime_s = static_cast<std::uint64_t>(std::chrono::duration_cast<std::chrono::seconds>(
                                                     std::chrono::steady_clock::now() - started_)
                                                     .count());
        return st;
    }

    /// Handles one pipe request on the UI thread and performs the requested action.
    std::string handle_pipe(const std::string& line) {
        const PipeReply reply = handle_pipe_request(line, status());
        switch (reply.action) {
            case PipeAction::Reload: reload_settings(false); break;
            case PipeAction::Quit:
                log_.info("quit requested over pipe");
                PostQuitMessage(0);
                break;
            case PipeAction::None: break;
        }
        return reply.line;
    }

    /// Pipe handler. Runs on a pipe thread: posts the request to the UI thread and waits up to 500 ms
    /// (returns a timeout error reply otherwise).
    std::string serve(const std::string& line) {
        static const std::string kTimeout = R"({"ok":false,"error":"timeout"})";
        if (!alive_) return kTimeout;
        auto job = std::make_shared<PipeJob>();
        job->request = line;
        auto holder = std::make_unique<std::shared_ptr<PipeJob>>(job);
        if (!PostMessageW(hwnd_, kMsgPipeRequest, 0, reinterpret_cast<LPARAM>(holder.get())))
            return kTimeout;
        // NOLINTNEXTLINE(bugprone-unused-return-value): the UI thread now owns the job.
        holder.release();
        std::unique_lock lock(job->mu);
        if (!job->cv.wait_for(lock, std::chrono::milliseconds(kPipeTimeoutMs),
                              [&] { return job->done; }))
            return kTimeout;
        return job->response;
    }

    /// Left click opens the settings flyout (falls back to the menu); the context menu event shows the menu.
    void on_tray(UINT event) {
        if (event == WM_LBUTTONUP || event == NIN_KEYSELECT) {
            if (!win32::launch_settings_app(L"--flyout")) show_menu();
        } else if (event == WM_CONTEXTMENU) {
            show_menu();
        }
    }

    /// Shows the tray context menu and runs the chosen command.
    void show_menu() {
        const HMENU menu = CreatePopupMenu();
        const std::wstring header =
            std::wstring(L"Magnetic Borders ") + std::wstring(kVersion.begin(), kVersion.end());
        AppendMenuW(menu, MF_STRING | MF_GRAYED, kMenuHeader, header.c_str());
        AppendMenuW(menu, settings_.enabled ? (MF_STRING | MF_CHECKED) : MF_STRING, kMenuEnabled,
                    L"Enabled");
        AppendMenuW(menu, MF_STRING, kMenuSettings, L"Settings…");
        AppendMenuW(menu, MF_SEPARATOR, 0, nullptr);
        AppendMenuW(menu, MF_STRING, kMenuExit, L"Exit");
        POINT pt{};
        GetCursorPos(&pt);
        SetForegroundWindow(hwnd_);
        const UINT cmd = static_cast<UINT>(TrackPopupMenu(
            menu, TPM_RETURNCMD | TPM_RIGHTBUTTON | TPM_NONOTIFY, pt.x, pt.y, 0, hwnd_, nullptr));
        PostMessageW(hwnd_, WM_NULL, 0, 0);
        DestroyMenu(menu);
        switch (cmd) {
            case kMenuEnabled: toggle_enabled(); break;
            case kMenuSettings:
                if (!win32::launch_settings_app(nullptr)) log_.warn("settings app not found");
                break;
            case kMenuExit: PostQuitMessage(0); break;
            default: break;
        }
    }

    win32::FileLog log_;
    win32::SteadyClock clock_;
    win32::Win32Timer timer_;
    win32::WindowRegistry registry_;
    win32::Win32Inventory inventory_;
    win32::Win32Mover mover_;
    DragController controller_;
    Settings settings_;
    std::atomic<bool> alive_{true};
    std::chrono::steady_clock::time_point started_;
    HWND hwnd_{};
    UINT taskbar_created_{0};
    win32::TrayIcon* tray_{nullptr};
};

}  // namespace

/// Runs the engine until quit; see engine_app.hpp.
int run_engine(const EngineOptions& options) {
    try {
        EngineApp app{options};
        int rc = 0;
        if (options.console) SetConsoleCtrlHandler(on_console_event, TRUE);
        rc = app.run();
        if (options.console) SetConsoleCtrlHandler(on_console_event, FALSE);
        return rc;
    } catch (const std::exception& e) {
        MessageBoxA(nullptr, e.what(), "Magnetic Borders - fatal error", MB_OK | MB_ICONERROR);
        return 2;
    }
}

}  // namespace mb::host
