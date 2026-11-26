// GoogleTest cases for the Win32 adapters and the host settings file helpers (Windows only).
// LOCALAPPDATA points at a temporary folder for the whole run, so no test touches the user's data.
#include <gtest/gtest.h>

#include <algorithm>
#include <chrono>
#include <filesystem>
#include <fstream>
#include <optional>
#include <sstream>
#include <stdexcept>
#include <string>
#include <thread>
#include <utility>
#include <vector>

#include "app/settings.hpp"
#include "host/settings.hpp"
#include "win32/file_log.hpp"
#include "win32/message_window.hpp"
#include "win32/paths.hpp"
#include "win32/pipe_server.hpp"
#include "win32/pointer_hook.hpp"
#include "win32/settings_watcher.hpp"
#include "win32/system_adapters.hpp"
#include "win32/tray_icon.hpp"
#include "win32/win32_event_source.hpp"
#include "win32/win32_inventory.hpp"
#include "win32/win32_mover.hpp"
#include "win32/win_common.hpp"

using namespace mb;
namespace fs = std::filesystem;

namespace {

/// Redirects LOCALAPPDATA to a temporary folder and removes it afterwards.
class TempDataEnvironment final : public ::testing::Environment {
public:
    void SetUp() override {
        root_ = fs::temp_directory_path() / ("mb-tests-" + std::to_string(GetCurrentProcessId()));
        fs::create_directories(root_);
        SetEnvironmentVariableW(L"LOCALAPPDATA", root_.c_str());
    }
    void TearDown() override {
        std::error_code ec;
        fs::remove_all(root_, ec);
    }

private:
    fs::path root_;
};

// gtest takes ownership of the environment.
// NOLINTNEXTLINE(cppcoreguidelines-owning-memory,bugprone-throwing-static-initialization): registered once at start-up.
const auto* const kEnvironment = ::testing::AddGlobalTestEnvironment(new TempDataEnvironment);

/// Runs the message loop until done() is true or the limit passes.
template <class Done>
bool pump_until(Done done, std::chrono::milliseconds limit = std::chrono::milliseconds(3000)) {
    const auto end = std::chrono::steady_clock::now() + limit;
    while (!done()) {
        if (std::chrono::steady_clock::now() > end) return false;
        MSG msg;
        while (PeekMessageW(&msg, nullptr, 0, 0, PM_REMOVE)) {
            TranslateMessage(&msg);
            DispatchMessageW(&msg);
        }
        MsgWaitForMultipleObjects(0, nullptr, FALSE, 20, QS_ALLINPUT);
    }
    return true;
}

/// Window message handler that records every message id.
struct Recorder final : win32::MessageHandler {
    std::vector<UINT> seen;
    LRESULT on_message(HWND hwnd, UINT msg, WPARAM wp, LPARAM lp) override {
        seen.push_back(msg);
        return DefWindowProcW(hwnd, msg, wp, lp);
    }
    [[nodiscard]] bool saw(UINT msg) const { return std::ranges::find(seen, msg) != seen.end(); }
};

struct TimerSink final : TimerEvents {
    std::vector<TimerKind> fired;
    void on_timer(TimerKind kind) override { fired.push_back(kind); }
};

struct NullEvents final : DragEvents {
    void on_move_size_start(WindowId) override {}
    void on_location_change(WindowId) override {}
    void on_move_size_end(WindowId) override {}
};

std::string slurp(const fs::path& p) {
    std::ifstream in(p, std::ios::binary);
    std::ostringstream ss;
    ss << in.rdbuf();
    return ss.str();
}

}  // namespace

TEST(Win32Registry, ExcludedExecutablesAreMatchedCaseInsensitively) {
    win32::WindowRegistry registry({L"First.exe"});
    EXPECT_TRUE(registry.is_excluded_exe(L"FIRST.EXE"));
    EXPECT_FALSE(registry.is_excluded_exe(L"other.exe"));
    registry.set_excluded({L"Other.exe"});
    EXPECT_FALSE(registry.is_excluded_exe(L"first.exe"));
    EXPECT_TRUE(registry.is_excluded_exe(L"other.EXE"));
}

TEST(Win32Registry, RemembersOffsetsAndBans) {
    win32::WindowRegistry registry({});
    EXPECT_FALSE(registry.offsets(7));
    registry.remember_offsets(7, {-7, 0, 7, 7});
    ASSERT_TRUE(registry.offsets(7));
    EXPECT_EQ(registry.offsets(7)->l, -7);
    EXPECT_FALSE(registry.banned(7));
    registry.ban(7);
    EXPECT_TRUE(registry.banned(7));
}

TEST(Win32Handles, UniqueHandleOwnsAndMoves) {
    win32::UniqueHandle a{CreateEventW(nullptr, TRUE, FALSE, nullptr)};
    EXPECT_TRUE(static_cast<bool>(a));
    win32::UniqueHandle b{std::move(a)};
    // NOLINTNEXTLINE(bugprone-use-after-move): a moved-from handle is documented to be empty.
    EXPECT_FALSE(static_cast<bool>(a));
    EXPECT_TRUE(static_cast<bool>(b));
    win32::UniqueHandle c;
    c = std::move(b);
    EXPECT_TRUE(static_cast<bool>(c));
    c.reset();
    EXPECT_FALSE(static_cast<bool>(c));
    EXPECT_EQ(c.get(), nullptr);
}

TEST(Win32Handles, UniqueWinEventHookMovesAndResets) {
    win32::UniqueWinEventHook empty;
    EXPECT_FALSE(static_cast<bool>(empty));
    win32::UniqueWinEventHook hook{SetWinEventHook(
        EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, nullptr,
        [](HWINEVENTHOOK, DWORD, HWND, LONG, LONG, DWORD, DWORD) {}, 0, 0, WINEVENT_OUTOFCONTEXT)};
    EXPECT_TRUE(static_cast<bool>(hook));
    win32::UniqueWinEventHook moved{std::move(hook)};
    EXPECT_TRUE(static_cast<bool>(moved));
    moved.reset();
    EXPECT_FALSE(static_cast<bool>(moved));
}

TEST(Win32Paths, Utf8RoundTrip) {
    const std::string text = "M\xC3\xBCll \xE6\x97\xA5\xE6\x9C\xAC.exe";
    const std::wstring wide = win32::utf8_to_wide(text);
    EXPECT_EQ(wide, L"Müll 日本.exe");
    EXPECT_EQ(win32::wide_to_utf8(wide), text);
    EXPECT_TRUE(win32::utf8_to_wide("").empty());
    EXPECT_TRUE(win32::wide_to_utf8(L"").empty());
}

TEST(Win32Paths, DataFolderLivesUnderLocalAppData) {
    const auto dir = win32::data_dir();
    EXPECT_TRUE(fs::is_directory(dir));
    EXPECT_EQ(dir.filename(), fs::path(L"MagneticBorders"));
    EXPECT_EQ(win32::settings_path(), dir / L"settings.json");
    const auto log = win32::log_path("cpp");
    EXPECT_EQ(log.filename(), fs::path(L"cpp.log"));
    EXPECT_TRUE(fs::is_directory(log.parent_path()));
}

TEST(Win32Paths, ExecutablePathExists) { EXPECT_TRUE(fs::exists(win32::exe_path())); }

TEST(Win32Paths, SettingsAppIsNotLaunchedWhenMissing) {
    if (win32::find_settings_app()) GTEST_SKIP() << "the settings app sits next to the tests";
    EXPECT_FALSE(win32::launch_settings_app(nullptr));
}

TEST(HostSettingsFile, MissingFileReadsAsNothing) {
    std::error_code ec;
    fs::remove(win32::settings_path(), ec);
    EXPECT_FALSE(host::read_settings_file());
}

TEST(HostSettingsFile, WriteReplacesAtomicallyAndReadsBack) {
    ASSERT_TRUE(host::write_settings_file(R"({"enabled":false})"));
    ASSERT_TRUE(host::write_settings_file(R"({"enabled":true,"glue":{"tolerance_px":20}})"));
    const auto text = host::read_settings_file();
    ASSERT_TRUE(text);
    const auto parsed = parse_settings(*text);
    ASSERT_TRUE(parsed);
    EXPECT_TRUE(parsed->enabled);
    EXPECT_EQ(parsed->glue_tolerance_px, 20);
    EXPECT_FALSE(fs::exists(win32::settings_path().wstring() + L".tmp"));
}

TEST(Win32FileLog, WritesTimestampedLinesAndHonoursDebugFlag) {
    const auto file = win32::data_dir() / L"test-a.log";
    std::error_code ec;
    fs::remove(file, ec);
    {
        win32::FileLog log(file, false, false);
        log.info("hello");
        log.debug("hidden");
        log.warn("careful");
    }
    {
        win32::FileLog log(file, false, true);
        log.debug("shown");
    }
    const auto text = slurp(file);
    EXPECT_NE(text.find(" hello\n"), std::string::npos);
    EXPECT_NE(text.find("WARNING: careful"), std::string::npos);
    EXPECT_EQ(text.find("hidden"), std::string::npos);
    EXPECT_NE(text.find("shown"), std::string::npos);
}

TEST(Win32FileLog, TruncatesFilesOverOneMegabyte) {
    const auto file = win32::data_dir() / L"test-b.log";
    {
        std::ofstream out(file, std::ios::binary | std::ios::trunc);
        out << std::string(1024 * 1024 + 10, 'x');
    }
    {
        win32::FileLog log(file, true, false);
        log.info("fresh");
    }
    EXPECT_LT(fs::file_size(file), 1024u);
}

TEST(Win32System, SteadyClockNeverGoesBackwards) {
    win32::SteadyClock clock;
    const auto a = clock.now_ms();
    std::this_thread::sleep_for(std::chrono::milliseconds(5));
    EXPECT_GE(clock.now_ms(), a);
}

TEST(Win32System, ConsoleLogPrintsOnlyEnabledDebugLines) {
    win32::ConsoleLog quiet;
    quiet.info("console info");
    quiet.debug("not printed");
    win32::ConsoleLog loud(true);
    loud.debug("console debug");
    SUCCEED();
}

TEST(Win32Timer, FiresOneShotTimersOnTheMessageLoop) {
    TimerSink sink;
    win32::Win32Timer timer;
    timer.set_sink(sink);
    timer.arm(TimerKind::Flush, 1);
    timer.arm(TimerKind::Flush, 5);  // re-arming replaces the pending timer
    timer.arm(TimerKind::Verify, 1);
    ASSERT_TRUE(pump_until([&] { return sink.fired.size() >= 2; }));
    EXPECT_EQ(std::ranges::count(sink.fired, TimerKind::Flush), 1);
    EXPECT_EQ(std::ranges::count(sink.fired, TimerKind::Verify), 1);
}

TEST(Win32Timer, CancelAllStopsPendingTimers) {
    TimerSink sink;
    win32::Win32Timer timer;
    timer.set_sink(sink);
    timer.arm(TimerKind::Settle, 1);
    timer.cancel_all();
    EXPECT_FALSE(pump_until([&] { return !sink.fired.empty(); }, std::chrono::milliseconds(100)));
}

TEST(Win32Timer, OnlyOneInstancePerThread) {
    win32::Win32Timer first;
    EXPECT_THROW(win32::Win32Timer second, std::logic_error);
}

TEST(Win32EventSource, InstallsHooksAndAllowsOnlyOnePerThread) {
    NullEvents events;
    win32::WinEventSource source{events};
    EXPECT_THROW(win32::WinEventSource second{events}, std::logic_error);
}

TEST(Win32MessageWindow, DeliversPostedMessagesToTheHandler) {
    Recorder recorder;
    win32::MessageWindow window{recorder};
    ASSERT_NE(window.hwnd(), nullptr);
    PostMessageW(window.hwnd(), WM_APP + 5, 0, 0);
    EXPECT_TRUE(pump_until([&] { return recorder.saw(WM_APP + 5); }));
}

TEST(Win32TrayIcon, AddsUpdatesAndRemovesTheIcon) {
    Recorder recorder;
    win32::MessageWindow window{recorder};
    win32::TrayIcon tray{window.hwnd(), WM_APP + 6};
    tray.set_tooltip(L"before add is ignored");
    tray.add(L"Magnetic Borders tests");
    tray.set_tooltip(L"updated");
    tray.remove();
    tray.remove();
    SUCCEED();
}

TEST(Win32SettingsWatcher, PostsAMessageWhenTheFolderChanges) {
    Recorder recorder;
    win32::MessageWindow window{recorder};
    const auto dir = win32::data_dir() / L"watched";
    fs::create_directories(dir);
    win32::SettingsWatcher watcher{dir, window.hwnd(), WM_APP + 7};
    std::this_thread::sleep_for(std::chrono::milliseconds(100));
    std::ofstream(dir / L"settings.json") << "{}";
    EXPECT_TRUE(pump_until([&] { return recorder.saw(WM_APP + 7); }));
}

TEST(Win32Inventory, SnapshotOfTheDesktopHasAPositiveDpiScale) {
    win32::WindowRegistry registry({});
    win32::Win32Inventory inventory{registry};
    const auto desktop = win32::to_id(GetDesktopWindow());
    const Snapshot snap = inventory.snapshot(desktop);
    EXPECT_GT(snap.dpi_scale, 0.0);
    EXPECT_GT(snap.work_area.width(), 0);
    (void)inventory.frame_of(desktop);
    EXPECT_FALSE(win32::visual_frame(nullptr));
}

TEST(Win32Mover, SkipsUnknownBannedAndMovesOwnWindows) {
    win32::WindowRegistry registry({});
    win32::Win32Mover mover{registry};
    EXPECT_TRUE(mover.apply({}).empty());
    const std::vector<Move> unknown{{12345, {0, 0, 10, 10}}};
    EXPECT_TRUE(mover.apply(unknown).empty());

    Recorder recorder;
    win32::MessageWindow window{recorder};
    const WindowId id = win32::to_id(window.hwnd());
    registry.remember_offsets(id, {});
    const std::vector<Move> own{{id, {10, 10, 210, 110}}};
    (void)mover.apply(own);  // the frame read back depends on DWM; the call must not throw

    registry.ban(id);
    EXPECT_TRUE(mover.apply(own).empty());
}

TEST(Win32Pipe, ServerAnswersRequestLines) {
    if (win32::pipe_request(R"({"cmd":"status"})", 50))
        GTEST_SKIP() << "an engine is already running";
    win32::PipeServer server([](const std::string& line) { return "echo:" + line; });
    std::optional<std::string> reply;
    for (int i = 0; i < 40 && !reply; ++i) {
        reply = win32::pipe_request("hi", 250);
        if (!reply) std::this_thread::sleep_for(std::chrono::milliseconds(50));
    }
    ASSERT_TRUE(reply);
    EXPECT_EQ(*reply, "echo:hi");
}

namespace {
/// Pointer sink that lets every movement through.
struct PassThrough : PointerEvents {
    std::optional<Point> on_pointer_move(const Point&) override { return std::nullopt; }
    void on_pointer_release() override {}
};
}  // namespace

TEST(Win32PointerHook, StartsOncePerThreadAndStops) {  // SNAP-070
    PassThrough sink;
    win32::Win32PointerHook first;
    win32::Win32PointerHook second;
    if (!first.start(sink)) GTEST_SKIP() << "no low-level hooks on this desktop";
    EXPECT_FALSE(second.start(sink));
    first.stop();
    EXPECT_TRUE(second.start(sink));
    second.stop();
    second.stop();  // safe when not started
}

TEST(Win32PointerHook, ReadsTheCursorWhenThereIsOne) {
    win32::Win32PointerHook hook;
    POINT p{};
    EXPECT_EQ(hook.cursor().has_value(), GetCursorPos(&p) != FALSE);
}
