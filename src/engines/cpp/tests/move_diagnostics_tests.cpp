// GoogleTest cases for the diagnostic line formatting of the Win32 move adapter (no Windows headers needed).
#include <gtest/gtest.h>

#include "win32/move_diagnostics.hpp"

using namespace mb;
namespace diag = mb::win32::diag;

TEST(MoveDiagnostics, RectAndDeltaText) {
    EXPECT_EQ(diag::rect_text({1, -2, 30, 40}), "1,-2,30,40");
    EXPECT_EQ(diag::delta({0, 0, 100, 100}, {1, 0, 98, 101}), (Rect{1, 0, -2, 1}));
}

TEST(MoveDiagnostics, HexAndFlagsText) {
    EXPECT_EQ(diag::hex_id(0x1a2b), "0x1a2b");
    EXPECT_EQ(diag::flags_text(0x214), "0x0214");
}

TEST(MoveDiagnostics, BatchLine) {
    EXPECT_EQ(diag::batch_line(3, "deferred", 0x214, 812, 1),
              "move: batch windows=3 mode=deferred flags=0x0214 elapsed_us=812 mismatches=1");
}

TEST(MoveDiagnostics, WindowLineMatchesOmitsDeltas) {
    const Rect r{10, 10, 210, 110};
    EXPECT_EQ(diag::window_line(0xabc, r, r, {}),
              "move: hwnd=0xabc requested=10,10,210,110 actual=10,10,210,110");
}

TEST(MoveDiagnostics, WindowLineReportsRefusedSize) {
    const Rect requested{0, 0, 100, 100};
    const Rect actual{0, 0, 120, 100};
    EXPECT_EQ(diag::window_line(1, requested, actual, {0, 0, 20, 0}),
              "move: hwnd=0x1 requested=0,0,100,100 actual=0,0,120,100 delta=0,0,20,0 "
              "window_delta=0,0,20,0");
}

TEST(MoveDiagnostics, WindowLineWithoutReadBack) {
    EXPECT_EQ(diag::window_line(2, {0, 0, 1, 1}, std::nullopt, {}),
              "move: hwnd=0x2 requested=0,0,1,1 actual=unavailable");
}

TEST(MoveDiagnostics, FailureAndSkipLines) {
    EXPECT_EQ(diag::deferred_failure_line("defer", 0x10, 5),
              "move: deferred batch failed step=defer hwnd=0x10 error=5, falling back to "
              "SetWindowPos");
    EXPECT_EQ(diag::set_window_pos_failure_line(0x10, 5),
              "move: SetWindowPos failed hwnd=0x10 error=5");
    EXPECT_EQ(diag::skip_line(0x10, "hung"), "move: skipped hwnd=0x10 reason=hung");
}

TEST(MoveDiagnostics, CandidateLine) {
    EXPECT_EQ(diag::border({990, 100, 1910, 900}, {1000, 100, 1900, 890}), (Rect{-10, 0, 10, 10}));
    EXPECT_EQ(diag::candidate_line(12, {1000, 100, 1900, 890}, {990, 100, 1910, 900}, 120,
                                   "ConsoleWindowClass", "WindowsTerminal.exe", true),
              "diag: window id=12 frame=1000,100,1900,890 rect=990,100,1910,900 "
              "border=-10,0,10,10 dpi=120 class=ConsoleWindowClass exe=WindowsTerminal.exe "
              "dragged=yes");
    EXPECT_EQ(diag::candidate_line(3, {0, 0, 10, 10}, {0, 0, 10, 10}, 96, "", "", false),
              "diag: window id=3 frame=0,0,10,10 rect=0,0,10,10 border=0,0,0,0 dpi=96 "
              "class=unknown exe=unknown dragged=no");
}

TEST(MoveDiagnostics, MoveResultLine) {
    EXPECT_EQ(diag::move_result_line(12, Rect{1, 2, 3, 4}, Rect{0, 2, 4, 5}, Rect{1, 2, 3, 4}),
              "diag: move result id=12 frame=1,2,3,4 rect=0,2,4,5 held=1,2,3,4");
    EXPECT_EQ(diag::move_result_line(12, std::nullopt, std::nullopt, std::nullopt),
              "diag: move result id=12 frame=unavailable rect=unavailable held=none");
}

TEST(MoveDiagnostics, SystemLines) {
    EXPECT_EQ(diag::system_line("22631.3880", "per-monitor-v2", true, 2),
              "diag: system os_build=22631.3880 dpi_awareness=per-monitor-v2 dwm_composition=on "
              "monitors=2");
    EXPECT_EQ(diag::monitor_line(0, {0, 0, 1920, 1200}, {0, 0, 1920, 1160}, 144, 144, true),
              "diag: monitor 0 rect=0,0,1920,1200 work=0,0,1920,1160 dpi=144x144 primary=yes");
}
