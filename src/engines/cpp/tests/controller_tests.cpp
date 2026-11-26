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

TEST_F(Fixture, MoveIsIgnoredWithAttractionOff) {  // SNAP-001
    ctl.set_snap_active(false);
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

namespace {

/// A dragged window (id 1, 200x200) with one other window (id 2) to its right on a 1000x800 work area.
struct MoveFixture : ::testing::Test {
    FakeInventory inv;
    FakeMover mover;
    FakeClock clock;
    FakeTimer timer;
    RecordingLog log;
    DragController ctl{inv, mover, clock, timer, log, ControllerOptions{12, "Test", false, {}}};

    MoveFixture() {
        inv.snap.work_area = {0, 0, 1000, 800};
        inv.snap.work_areas = {inv.snap.work_area};
        inv.snap.windows = {{1, {200, 250, 400, 450}, 1, 1}, {2, {500, 200, 700, 400}, 1, 1}};
        inv.frames[1] = inv.snap.windows[0].frame;
    }
    /// Reports the dragged window at `frame` and delivers a location change.
    void move_to(const Rect& frame) {
        inv.frames[1] = frame;
        clock.t += 100;
        ctl.on_location_change(1);
    }
    /// The held position for a free position whose right edge is 5 px short of window 2.
    static constexpr Rect kNear{295, 250, 495, 450};
    static constexpr Rect kHeld{300, 250, 500, 450};
};

}  // namespace

TEST_F(MoveFixture, AMoveWithAttractionAppliesTheHeldPosition) {  // SNAP-020, SNAP-023, SNAP-040
    ctl.on_move_size_start(1);
    move_to(kNear);  // the first location change classifies the move and is handled too
    ASSERT_EQ(mover.batches.size(), 1u);
    ASSERT_EQ(mover.batches[0].size(), 1u);
    EXPECT_EQ(mover.batches[0][0].id, 1u);
    EXPECT_EQ(mover.batches[0][0].frame, kHeld);
    EXPECT_EQ(ctl.move_drags(), 1u);
    EXPECT_EQ(ctl.snaps(), 1u);
    EXPECT_EQ(ctl.drags(), 0u);
    EXPECT_TRUE(log.contains("snap hold right=500 target=edge-to-edge"));
}

TEST_F(MoveFixture, AFreePositionIsNotMoved) {  // SNAP-023
    ctl.on_move_size_start(1);
    move_to({100, 250, 300, 450});
    EXPECT_TRUE(mover.batches.empty());
}

TEST_F(MoveFixture, TheOwnMoveEchoIsIgnoredAndTheNextFreePositionIsHandled) {  // SNAP-024, SNAP-025
    ctl.on_move_size_start(1);
    move_to(kNear);
    ASSERT_EQ(mover.batches.size(), 1u);
    move_to(kHeld);  // Windows reports the position we just set
    EXPECT_EQ(mover.batches.size(), 1u);
    clock.t += 1;  // no throttling of the dragged window
    move_to({296, 250, 496, 450});
    ASSERT_EQ(mover.batches.size(), 2u);
    EXPECT_EQ(mover.batches[1][0].frame, kHeld);
}

TEST_F(MoveFixture, TheEchoAsReadBackFromTheMoverIsIgnoredToo) {  // SNAP-024
    mover.force[1] = {301, 250, 501, 450};
    ctl.on_move_size_start(1);
    move_to(kNear);
    ASSERT_EQ(mover.batches.size(), 1u);
    move_to({301, 250, 501, 450});
    EXPECT_EQ(mover.batches.size(), 1u);
}

TEST_F(MoveFixture, SlidingAndBreakaway) {  // SNAP-022, SNAP-041
    ctl.on_move_size_start(1);
    move_to(kNear);
    move_to({292, 300, 492, 500});  // the window slides down while held
    ASSERT_EQ(mover.batches.size(), 2u);
    EXPECT_EQ(mover.batches[1][0].frame, (Rect{300, 300, 500, 500}));
    move_to({270, 300, 470,
             500});  // 30 px from the line: released, the window is already at the free position
    EXPECT_EQ(mover.batches.size(), 2u);
    EXPECT_TRUE(log.contains("snap release right=500 reason=breakaway"));
}

TEST_F(MoveFixture, AFastMoveDoesNotSnap) {  // SNAP-012
    ctl.on_move_size_start(1);
    move_to({100, 250, 300, 450});
    clock.t += 5;
    inv.frames[1] = {295, 250, 495, 450};
    ctl.on_location_change(1);  // 195 px in 5 ms
    EXPECT_TRUE(mover.batches.empty());
    EXPECT_EQ(ctl.snaps(), 0u);
}

TEST_F(MoveFixture, TheEndAppliesTheHeldPositionOnceWhenWindowsPutTheWindowBackAtTheFreePosition) {
    // SNAP-050, SNAP-052
    ctl.on_move_size_start(1);
    move_to(kNear);
    ASSERT_EQ(mover.batches.size(), 1u);
    inv.frames[1] = kNear;
    ctl.on_move_size_end(1);
    ASSERT_EQ(mover.batches.size(), 2u);
    EXPECT_EQ(mover.batches[1][0].frame, kHeld);
    EXPECT_FALSE(timer.is_armed(TimerKind::Verify));
    EXPECT_TRUE(log.contains("move end id=1 snaps=1 applied"));
    ctl.on_timer(TimerKind::Verify);
    ctl.on_timer(TimerKind::Settle);
    ctl.on_timer(TimerKind::Flush);
    EXPECT_EQ(mover.batches.size(), 2u);
}

TEST_F(MoveFixture, TheEndDoesNothingWhenTheWindowIsAlreadyHeld) {  // SNAP-050
    ctl.on_move_size_start(1);
    move_to(kNear);
    inv.frames[1] = kHeld;
    ctl.on_move_size_end(1);
    EXPECT_EQ(mover.batches.size(), 1u);
}

TEST_F(MoveFixture, TheEndDoesNothingWhenAnotherToolPlacedTheWindow) {  // SNAP-051
    ctl.on_move_size_start(1);
    move_to(kNear);
    inv.frames[1] = {0, 0, 500, 800};  // FancyZones or Windows Snap
    ctl.on_move_size_end(1);
    EXPECT_EQ(mover.batches.size(), 1u);
}

TEST_F(MoveFixture, TheEndDoesNothingWhenTheSizeChanged) {  // SNAP-051
    ctl.on_move_size_start(1);
    move_to(kNear);
    move_to({296, 250, 496, 500});  // another tool resized the window during the drag
    const auto before = mover.batches.size();
    ctl.on_move_size_end(1);
    EXPECT_EQ(mover.batches.size(), before);
}

TEST_F(MoveFixture, TheEndDoesNothingWhenNothingIsHeldOrTheFrameIsUnreadable) {  // SNAP-050
    ctl.on_move_size_start(1);
    move_to({100, 250, 300, 450});
    ctl.on_move_size_end(1);
    EXPECT_TRUE(mover.batches.empty());
    EXPECT_TRUE(log.contains("move end id=1 snaps=0"));
    ctl.on_move_size_start(1);
    move_to(kNear);
    inv.frames.erase(1);
    ctl.on_move_size_end(1);
    EXPECT_EQ(mover.batches.size(), 1u);
}

TEST_F(MoveFixture, AttractionOffIgnoresMoves) {  // SNAP-001
    ctl.set_snap_active(false);
    ctl.on_move_size_start(1);
    move_to(kNear);
    ctl.on_move_size_end(1);
    EXPECT_TRUE(mover.batches.empty());
    EXPECT_EQ(ctl.move_drags(), 0u);
}

TEST_F(MoveFixture, AttractionStillHandlesMovesWhenCouplingIsOff) {
    ctl.set_glue_active(false);
    ctl.on_move_size_start(1);
    move_to(kNear);
    ASSERT_EQ(mover.batches.size(), 1u);
    EXPECT_EQ(mover.batches[0][0].frame, kHeld);
    ctl.on_move_size_end(1);
    const auto before = mover.batches.size();
    ctl.on_move_size_start(1);
    inv.frames[1] = {200, 250, 450, 450};  // a resize is ignored without coupling
    ctl.on_location_change(1);
    ctl.on_move_size_end(1);
    EXPECT_EQ(ctl.drags(), 0u);
    EXPECT_EQ(mover.batches.size(), before);
}

TEST_F(MoveFixture, ResizesStayGluedWhenAttractionIsOff) {
    ctl.set_snap_active(false);
    inv.snap.windows = {{1, {0, 0, 500, 800}, 1, 1}, {2, {500, 0, 1000, 800}, 1, 1}};
    inv.frames[1] = {0, 0, 500, 800};
    ctl.on_move_size_start(1);
    inv.frames[1] = {0, 0, 600, 800};
    ctl.on_location_change(1);
    ASSERT_EQ(mover.batches.size(), 1u);
    EXPECT_EQ(mover.batches[0][0].frame, (Rect{600, 0, 1000, 800}));
}

TEST_F(MoveFixture, AnIneligibleDraggedWindowIsIgnored) {
    inv.snap.windows.erase(inv.snap.windows.begin());
    ctl.on_move_size_start(1);
    move_to(kNear);
    EXPECT_TRUE(mover.batches.empty());
}

TEST_F(MoveFixture, DistancesAreScaledByTheDpi) {  // SNAP-010
    const Rect free{280, 250, 480, 450};           // 20 px from window 2
    ctl.on_move_size_start(1);
    move_to(free);
    EXPECT_TRUE(mover.batches.empty());
    inv.snap.dpi_scale = 2.0;  // 16 px become 32 px
    ctl.on_move_size_end(1);
    ctl.on_move_size_start(1);
    move_to(free);
    ASSERT_EQ(mover.batches.size(), 1u);
    EXPECT_EQ(mover.batches[0][0].frame, kHeld);
}

TEST_F(MoveFixture, OptionsApplyOnTheNextDrag) {  // SNAP-032, SNAP-043
    SnapOptions o;
    o.windows = false;
    ctl.set_snap_options(o);
    ctl.on_move_size_start(1);
    move_to(kNear);
    EXPECT_TRUE(mover.batches.empty());
    ctl.on_move_size_end(1);
    o.windows = true;
    o.distance_px = 0;
    ctl.set_snap_options(o);
    ctl.on_move_size_start(1);
    move_to(kNear);
    EXPECT_TRUE(mover.batches.empty());  // 5 px is out of a 0 px pull-in
}

TEST_F(MoveFixture, ASideOnAnotherMonitorIsNotATarget) {  // SNAP-031
    inv.snap.windows = {{1, {700, 250, 900, 450}, 1, 1}};
    inv.snap.work_areas = {inv.snap.work_area, {1000, 0, 2000, 800}};
    inv.frames[1] = {700, 250, 900, 450};
    ctl.on_move_size_start(1);
    move_to({795, 250, 995, 450});
    EXPECT_TRUE(mover.batches.empty());
    move_to({600, 250, 800, 450});  // nothing near
    EXPECT_TRUE(mover.batches.empty());
}

TEST_F(MoveFixture, AMoveOnAScreenEdgeIsLogged) {  // SNAP-060
    inv.snap.windows = {{1, {200, 250, 400, 450}, 1, 1}};
    ctl.on_move_size_start(1);
    move_to({10, 250, 210, 450});
    ASSERT_EQ(mover.batches.size(), 1u);
    EXPECT_EQ(mover.batches[0][0].frame, (Rect{0, 250, 200, 450}));
    EXPECT_TRUE(log.contains("snap hold left=0 target=screen"));
    EXPECT_TRUE(log.contains("move start id=1"));
    move_to({-30, 250, 170, 450});
    EXPECT_TRUE(log.contains("snap release left=0 reason=breakaway"));
    ctl.on_move_size_end(1);
    EXPECT_TRUE(log.contains("move end id=1 snaps=1"));
}

TEST_F(MoveFixture, ASlideOffAWindowIsLoggedAsTargetGone) {  // SNAP-035, SNAP-060
    ctl.on_move_size_start(1);
    move_to(kNear);
    move_to({295, 600, 495, 800});  // far below window 2: it is no longer a target
    EXPECT_TRUE(log.contains("reason=target-gone"));
}

TEST_F(MoveFixture, AnAlignedHoldIsLogged) {  // SNAP-034, SNAP-060
    inv.snap.windows = {{1, {200, 250, 400, 450}, 1, 1}, {2, {500, 200, 700, 400}, 1, 1}};
    ctl.on_move_size_start(1);
    move_to({505, 410, 705, 610});  // below window 2, left edge 5 px from its left edge
    EXPECT_TRUE(log.contains("target=aligned"));
}

TEST_F(MoveFixture, AWindowHiddenBehindAnotherDoesNotAttract) {  // SNAP-038
    // Window 3 sits above window 2 and covers it completely.
    inv.snap.stack = {
        {1, inv.snap.windows[0].frame}, {3, {450, 150, 750, 450}}, {2, inv.snap.windows[1].frame}};
    ctl.on_move_size_start(1);
    move_to(kNear);
    EXPECT_TRUE(mover.batches.empty());
}

TEST_F(MoveFixture, TheDraggedWindowNeverCoversATarget) {  // SNAP-038
    // The dragged window is on top and overlaps window 2's left edge at drag start.
    inv.snap.stack = {{1, {450, 200, 650, 400}}, {2, inv.snap.windows[1].frame}};
    ctl.on_move_size_start(1);
    move_to(kNear);
    ASSERT_EQ(mover.batches.size(), 1u);
    EXPECT_EQ(mover.batches[0][0].frame, kHeld);
}
