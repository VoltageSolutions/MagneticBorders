// GoogleTest cases for DragController using fake ports.
#include <gtest/gtest.h>

#include <algorithm>
#include <map>
#include <utility>

#include "app/drag_controller.hpp"

using namespace mb;

namespace {

/// Inventory returning a canned Snapshot and settable frames.
struct FakeInventory : WindowInventory {
    Snapshot snap;
    std::map<WindowId, Rect> frames;
    Snapshot snapshot(WindowId) override { return snap; }
    std::optional<Rect> frame_of(WindowId id) override {
        const auto it = frames.find(id);
        return it == frames.end() ? std::nullopt : std::optional{it->second};
    }
};

/// Mover that records batches and echoes requested frames.
struct FakeMover : WindowMover {
    std::vector<std::vector<Move>> batches;
    std::map<WindowId, Rect> force;  // optional override of the applied frame
    std::vector<Move> apply(std::span<const Move> moves) override {
        batches.emplace_back(moves.begin(), moves.end());
        std::vector<Move> out(moves.begin(), moves.end());
        for (auto& m : out)
            if (const auto it = force.find(m.id); it != force.end()) m.frame = it->second;
        return out;
    }
};

/// Manually advanced clock.
struct FakeClock : Clock {
    std::uint64_t t{1000};
    std::uint64_t now_ms() override { return t; }
};

/// Timer that records armed kinds without firing.
struct FakeTimer : Timer {
    std::map<TimerKind, unsigned> armed;
    void arm(TimerKind k, unsigned ms) override { armed[k] = ms; }
    void cancel_all() override { armed.clear(); }
    [[nodiscard]] bool is_armed(TimerKind k) const { return armed.contains(k); }
};

/// Log that discards output.
struct NullLog : Log {
    void info(std::string_view) override {}
    void debug(std::string_view) override {}
};

/// Test fixture: two side-by-side windows and a controller wired to the fakes.
struct Fixture : ::testing::Test {
    FakeInventory inv;
    FakeMover mover;
    FakeClock clock;
    FakeTimer timer;
    NullLog log;
    DragController ctl{inv, mover, clock, timer, log};

    Fixture() {
        inv.snap.work_area = {0, 0, 3440, 1440};
        inv.snap.dpi_scale = 1.0;
        inv.snap.windows = {{1, {0, 0, 1720, 1440}, 1, 1}, {2, {1720, 0, 3440, 1440}, 1, 1}};
        inv.frames[1] = inv.snap.windows[0].frame;
    }
    void drag_a_to(int right) {
        inv.frames[1] = {0, 0, right, 1440};
        ctl.on_location_change(1);
    }
};

}  // namespace

TEST_F(Fixture, ResizeAppliesNeighbourInOneBatch) {
    ctl.on_move_size_start(1);
    drag_a_to(1820);
    ASSERT_EQ(mover.batches.size(), 1u);
    ASSERT_EQ(mover.batches[0].size(), 1u);
    EXPECT_EQ(mover.batches[0][0].frame, (Rect{1820, 0, 3440, 1440}));
}

TEST_F(Fixture, MoveIsIgnored) {
    ctl.on_move_size_start(1);
    inv.frames[1] = {50, 50, 1770, 1490};
    ctl.on_location_change(1);
    ctl.on_move_size_end(1);
    EXPECT_TRUE(mover.batches.empty());
}

TEST_F(Fixture, IneligibleDraggedWindowIsIgnored) {
    inv.snap.windows.erase(inv.snap.windows.begin());
    ctl.on_move_size_start(1);
    drag_a_to(1820);
    EXPECT_TRUE(mover.batches.empty());
}

TEST_F(Fixture, ScreenEdgeResizeIsIgnored) {
    inv.snap.windows = {{1, {0, 0, 1720, 1440}, 1, 1}};
    ctl.on_move_size_start(1);
    inv.frames[1] = {0, 0, 3440, 1440};
    ctl.on_location_change(1);
    EXPECT_TRUE(mover.batches.empty());
}

TEST_F(Fixture, ThrottleSkipsAndEndAppliesLatest) {
    ctl.on_move_size_start(1);
    drag_a_to(1800);
    clock.t += 3;
    drag_a_to(1850);
    EXPECT_EQ(mover.batches.size(), 1u);
    ctl.on_move_size_end(1);
    ASSERT_EQ(mover.batches.size(), 2u);
    EXPECT_EQ(mover.batches[1][0].frame.l, 1850);
}

TEST_F(Fixture, SnapBackWhenNeighbourHitsMinimum) {
    inv.snap.windows[1].min_w = 1600;
    ctl.on_move_size_start(1);
    drag_a_to(2020);
    ctl.on_move_size_end(1);
    const auto& last = mover.batches.back();
    ASSERT_EQ(last.size(), 2u);
    EXPECT_EQ(last[0].frame.l, 1840);
    EXPECT_EQ(last[1].id, 1u);
    EXPECT_EQ(last[1].frame, (Rect{0, 0, 1840, 1440}));
}

TEST_F(Fixture, LearnsMinimumFromAppliedFrame) {
    mover.force[2] = {1900, 0, 3440, 1440};  // window refuses to shrink below 1540 wide
    ctl.on_move_size_start(1);
    drag_a_to(2000);
    clock.t += 20;
    drag_a_to(2100);
    ASSERT_EQ(mover.batches.size(), 2u);
    EXPECT_EQ(mover.batches[1][0].frame.l, 1900);  // clamped by the learned minimum
}

TEST_F(Fixture, TrailingFlushAppliesLastFrameWhileMouseIsStill) {
    ctl.on_move_size_start(1);
    drag_a_to(1800);
    clock.t += 3;
    drag_a_to(1850);  // throttled
    ASSERT_EQ(mover.batches.size(), 1u);
    ASSERT_TRUE(timer.is_armed(TimerKind::Flush));
    clock.t += 10;
    ctl.on_timer(TimerKind::Flush);
    ASSERT_EQ(mover.batches.size(), 2u);
    EXPECT_EQ(mover.batches[1][0].frame.l, 1850);
    ctl.on_timer(TimerKind::Flush);  // nothing pending
    EXPECT_EQ(mover.batches.size(), 2u);
}

TEST_F(Fixture, EndCancelsFlushAndSettleAndArmsVerify) {
    ctl.on_move_size_start(1);
    drag_a_to(1800);
    EXPECT_TRUE(timer.is_armed(TimerKind::Settle));
    ctl.on_move_size_end(1);
    EXPECT_FALSE(timer.is_armed(TimerKind::Flush));
    EXPECT_FALSE(timer.is_armed(TimerKind::Settle));
    EXPECT_TRUE(timer.is_armed(TimerKind::Verify));
}

TEST_F(Fixture, SettleLearnsAsynchronouslyEnforcedMinimum) {
    ctl.on_move_size_start(1);
    drag_a_to(2000);
    ASSERT_EQ(mover.batches.size(), 1u);
    EXPECT_EQ(mover.batches[0][0].frame.l, 2000);
    inv.frames[2] = {1900, 0, 3440, 1440};  // the app enforced a 1540 minimum after the fact
    ctl.on_timer(TimerKind::Settle);
    ASSERT_EQ(mover.batches.size(), 2u);
    EXPECT_EQ(mover.batches[1][0].frame.l, 1900);
}

TEST_F(Fixture, VerifyReappliesOverwrittenSnapBack) {
    inv.snap.windows[1].min_w = 1600;
    ctl.on_move_size_start(1);
    drag_a_to(2020);
    ctl.on_move_size_end(1);
    const auto before = mover.batches.size();
    inv.frames[1] = {0, 0, 2020,
                     1440};  // the system's end-of-drag handling overwrote the snap-back
    inv.frames[2] = {1840, 0, 3440, 1440};
    ctl.on_timer(TimerKind::Verify);
    ASSERT_EQ(mover.batches.size(), before + 1);
    const auto& last = mover.batches.back();
    const auto it = std::ranges::find(last, 1u, &Move::id);
    ASSERT_NE(it, last.end());
    EXPECT_EQ(it->frame, (Rect{0, 0, 1840, 1440}));
}

TEST_F(Fixture, CountersTrackResizeAndGluedDrags) {
    ctl.on_move_size_start(1);
    drag_a_to(1820);
    ctl.on_move_size_end(1);
    EXPECT_EQ(ctl.drags(), 1u);
    EXPECT_EQ(ctl.glued_drags(), 1u);
    ctl.on_move_size_start(1);
    inv.frames[1] = {50, 50, 1770, 1490};  // a move: not counted
    ctl.on_location_change(1);
    ctl.on_move_size_end(1);
    EXPECT_EQ(ctl.drags(), 1u);
}

TEST_F(Fixture, InactiveGlueIgnoresDrags) {
    ctl.set_glue_active(false);
    ctl.on_move_size_start(1);
    drag_a_to(1820);
    ctl.on_move_size_end(1);
    EXPECT_TRUE(mover.batches.empty());
    EXPECT_EQ(ctl.drags(), 0u);
    ctl.set_glue_active(true);
    ctl.on_move_size_start(1);
    drag_a_to(1820);
    EXPECT_EQ(mover.batches.size(), 1u);
}

TEST_F(Fixture, ToleranceChangeAppliesOnNextDrag) {
    inv.snap.windows[1].frame = {1740, 0, 3440, 1440};  // 20 px gap
    ctl.set_glue_tolerance(12);
    ctl.on_move_size_start(1);
    drag_a_to(1820);
    ctl.on_move_size_end(1);
    EXPECT_EQ(ctl.glued_drags(), 0u);
    ctl.set_glue_tolerance(24);
    ctl.on_move_size_start(1);
    drag_a_to(1820);
    EXPECT_EQ(ctl.glued_drags(), 1u);
}

namespace {

/// Log that keeps every line.
struct RecordingLog : Log {
    std::vector<std::string> lines;
    void info(std::string_view msg) override { lines.emplace_back(msg); }
    void debug(std::string_view msg) override { lines.emplace_back(msg); }
    [[nodiscard]] bool contains(std::string_view needle) const {
        return std::ranges::any_of(
            lines, [&](const std::string& l) { return l.find(needle) != std::string::npos; });
    }
};

/// Four equal quadrants on a 1440x1440 work area.
struct QuadFixture : ::testing::Test {
    FakeInventory inv;
    FakeMover mover;
    FakeClock clock;
    FakeTimer timer;
    RecordingLog log;
    DragController ctl{inv, mover, clock, timer, log, ControllerOptions{12, "Test", true}};

    QuadFixture() {
        inv.snap.work_area = {0, 0, 1440, 1440};
        inv.snap.windows = {{1, {0, 0, 720, 720}, 1, 1},
                            {2, {720, 0, 1440, 720}, 1, 1},
                            {3, {0, 720, 720, 1440}, 1, 1},
                            {4, {720, 720, 1440, 1440}, 1, 1}};
        inv.frames[4] = inv.snap.windows[3].frame;
    }
};

}  // namespace

TEST_F(QuadFixture, CornerDragBuildsTwoSeamsAndMergesMovesPerWindow) {
    ctl.on_move_size_start(4);
    inv.frames[4] = {820, 820, 1440, 1440};
    ctl.on_location_change(4);
    ASSERT_EQ(mover.batches.size(), 1u);
    ASSERT_EQ(mover.batches[0].size(), 3u);
    for (const auto& m : mover.batches[0]) {
        if (m.id == 1) {
            EXPECT_EQ(m.frame, (Rect{0, 0, 820, 820}));
        }
        if (m.id == 2) {
            EXPECT_EQ(m.frame, (Rect{820, 0, 1440, 820}));
        }
        if (m.id == 3) {
            EXPECT_EQ(m.frame, (Rect{0, 820, 820, 1440}));
        }
    }
    EXPECT_EQ(ctl.drags(), 1u);
    EXPECT_EQ(ctl.glued_drags(), 1u);
}

TEST_F(QuadFixture, VerboseLogsStartAndApply) {
    ctl.on_move_size_start(4);
    inv.frames[4] = {820, 720, 1440, 1440};
    ctl.on_location_change(4);
    EXPECT_TRUE(log.contains("[Test +"));
    EXPECT_TRUE(log.contains("resize start id=4 left->["));
    EXPECT_TRUE(log.contains("apply pos=820 clamp=820 members=3"));
}

TEST_F(QuadFixture, SettleLogsClampAndGap) {
    ctl.on_move_size_start(4);
    inv.frames[4] = {820, 720, 1440, 1440};
    ctl.on_location_change(4);
    ctl.on_timer(TimerKind::Settle);
    EXPECT_TRUE(log.contains("settled +"));
    EXPECT_TRUE(log.contains("left=820 clamp=820 gap=0"));
}

TEST_F(QuadFixture, EndWithoutMismatchVerifiesOk) {
    ctl.on_move_size_start(4);
    inv.frames[4] = {820, 720, 1440, 1440};
    ctl.on_location_change(4);
    ctl.on_move_size_end(4);
    EXPECT_TRUE(log.contains("resize end id=4 ok"));
    for (const auto& m : mover.batches.back()) inv.frames[m.id] = m.frame;
    ctl.on_timer(TimerKind::Verify);
    EXPECT_TRUE(log.contains("ok"));
    const auto before = mover.batches.size();
    ctl.on_timer(TimerKind::Verify);  // already verified
    EXPECT_EQ(mover.batches.size(), before);
}

TEST_F(QuadFixture, TopEdgeDragMovesWindowsAboveAndAcross) {
    ctl.on_move_size_start(4);
    inv.frames[4] = {720, 620, 1440, 1440};
    ctl.on_location_change(4);
    ASSERT_EQ(mover.batches.size(), 1u);
    for (const auto& m : mover.batches[0]) {
        if (m.id == 1 || m.id == 2) {
            EXPECT_EQ(m.frame.b, 620);
        }
        if (m.id == 3) {
            EXPECT_EQ(m.frame.t, 620);
        }
    }
}

TEST_F(QuadFixture, BottomEdgeDragUsesHeights) {
    ctl.on_move_size_start(1);
    inv.frames[1] = {0, 0, 720, 800};
    ctl.on_location_change(1);
    ASSERT_EQ(mover.batches.size(), 1u);
    for (const auto& m : mover.batches[0]) {
        if (m.id == 3 || m.id == 4) {
            EXPECT_EQ(m.frame.t, 800);
        }
    }
}

TEST_F(QuadFixture, HighDpiScalesTolerance) {
    inv.snap.dpi_scale = 2.0;
    inv.snap.windows[3].frame = {744, 720, 1440, 1440};  // 24 px gap to window 3
    inv.snap.windows[1].frame = {744, 0, 1440, 720};
    ctl.on_move_size_start(3);
    inv.frames[3] = {0, 720, 820, 1440};
    ctl.on_location_change(3);
    EXPECT_EQ(ctl.glued_drags(), 1u);
}

TEST_F(Fixture, UnknownDraggedFrameIsIgnored) {
    ctl.on_move_size_start(1);
    inv.frames.erase(1);
    ctl.on_location_change(1);
    ctl.on_move_size_end(1);
    EXPECT_TRUE(mover.batches.empty());
}

TEST_F(Fixture, EventsForOtherWindowsAreIgnored) {
    ctl.on_move_size_start(1);
    inv.frames[2] = {1800, 0, 3440, 1440};
    ctl.on_location_change(2);
    ctl.on_move_size_end(2);
    EXPECT_TRUE(mover.batches.empty());
    EXPECT_EQ(ctl.drags(), 0u);
}

TEST_F(Fixture, LocationChangeBeforeStartIsIgnored) {
    drag_a_to(1820);
    EXPECT_TRUE(mover.batches.empty());
}

TEST_F(Fixture, TimersWithoutDragDoNothing) {
    ctl.on_timer(TimerKind::Flush);
    ctl.on_timer(TimerKind::Settle);
    ctl.on_timer(TimerKind::Verify);
    EXPECT_TRUE(mover.batches.empty());
}

TEST_F(Fixture, EndAfterIgnoredDragClearsState) {
    ctl.set_glue_active(false);
    ctl.on_move_size_start(1);
    ctl.on_move_size_end(1);
    EXPECT_FALSE(timer.is_armed(TimerKind::Verify));
}

TEST_F(Fixture, LearnedMinimumPersistsIntoNextDrag) {
    mover.force[2] = {1900, 0, 3440, 1440};
    ctl.on_move_size_start(1);
    drag_a_to(2000);
    ctl.on_move_size_end(1);
    mover.force.clear();
    mover.batches.clear();
    inv.snap.windows[0].frame = {0, 0, 1900, 1440};
    inv.snap.windows[1].frame = {1900, 0, 3440, 1440};
    inv.frames[1] = inv.snap.windows[0].frame;
    ctl.on_move_size_start(1);
    drag_a_to(2500);
    ASSERT_EQ(mover.batches.size(), 1u);
    EXPECT_EQ(mover.batches[0][0].frame.l, 1900);  // the neighbour keeps its learned width
}

TEST_F(Fixture, VerifyLearnsFromMismatchedMember) {
    ctl.on_move_size_start(1);
    drag_a_to(2000);
    ctl.on_move_size_end(1);
    inv.frames[1] = {0, 0, 2000, 1440};
    inv.frames[2] = {2100, 0, 3440, 1440};  // the app refused to shrink below 1340
    const auto before = mover.batches.size();
    ctl.on_timer(TimerKind::Verify);
    EXPECT_EQ(mover.batches.size(), before + 1);
}

TEST_F(Fixture, UnreadableMemberFrameIsSkippedWhenVerifying) {
    ctl.on_move_size_start(1);
    drag_a_to(2000);
    ctl.on_move_size_end(1);
    inv.frames[1] = {0, 0, 2000, 1440};
    ctl.on_timer(TimerKind::Verify);  // member 2 has no readable frame
    SUCCEED();
}

TEST_F(Fixture, SettleWithoutMismatchDoesNotReapply) {
    ctl.on_move_size_start(1);
    drag_a_to(2000);
    inv.frames[1] = {0, 0, 2000, 1440};
    inv.frames[2] = {2000, 0, 3440, 1440};
    ctl.on_timer(TimerKind::Settle);
    EXPECT_EQ(mover.batches.size(), 1u);
}

TEST_F(Fixture, FlushOutsideResizeIsIgnored) {
    ctl.on_move_size_start(1);
    ctl.on_timer(TimerKind::Flush);
    EXPECT_TRUE(mover.batches.empty());
}
