// GoogleTest cases for Attraction and screen_side_targets (SNAP rules without the OS).
#include <gtest/gtest.h>

#include <algorithm>
#include <utility>

#include "core/attraction.hpp"

using namespace mb;

namespace {

const Rect kWork{0, 0, 1000, 800};

/// A 100x100 window with its top-left at (l, t).
Rect box(int l, int t) { return {l, t, l + 100, t + 100}; }

/// An Attraction on kWork plus a clock that advances 100 ms per step, so steps never count as fast.
struct Rig {
    Attraction a;
    std::uint64_t t{0};
    explicit Rig(std::vector<Rect> others = {}, const AttractionConfig& cfg = {},
                 const SideTargets& sides = {})
        : a(cfg, std::move(others), kWork, sides) {}
    Rect step(const Rect& free) {
        t += 100;
        return a.update(free, t);
    }
};

/// Config with only the given switches on.
AttractionConfig only(bool screen, bool windows) {
    AttractionConfig c;
    c.screen_edges = screen;
    c.windows = windows;
    return c;
}

/// A window at x 300..500, y 300..500.
const Rect kW{300, 300, 500, 500};

}  // namespace

TEST(Attraction,
     PullsAnEdgeWithinThePullInDistanceOntoTheScreenEdge) {  // SNAP-010, SNAP-030, SNAP-040
    Rig r;
    EXPECT_EQ(r.step(box(10, 400)), (Rect{0, 400, 100, 500}));
    EXPECT_TRUE(r.a.held());
    EXPECT_EQ(r.a.snaps(), 1u);
    Rig right;
    EXPECT_EQ(right.step(box(890, 400)), (Rect{900, 400, 1000, 500}));
}

TEST(Attraction, PullInDistanceIsInclusive) {  // SNAP-010
    Rig in;
    EXPECT_EQ(in.step(box(16, 400)), box(0, 400));
    Rig out;
    EXPECT_EQ(out.step(box(17, 400)), box(17, 400));
    EXPECT_FALSE(out.a.held());
}

TEST(Attraction, SnapsTopAndBottomToTheWorkArea) {  // SNAP-030
    Rig top;
    EXPECT_EQ(top.step(box(400, 12)), box(400, 0));
    Rig bottom;
    EXPECT_EQ(bottom.step(box(400, 690)), box(400, 700));
}

TEST(Attraction, TheSizeNeverChanges) {
    Rig r;
    const Rect out = r.step({10, 12, 410, 312});
    EXPECT_EQ(out.width(), 400);
    EXPECT_EQ(out.height(), 300);
}

TEST(Attraction, ZeroDistanceHoldsWithoutMoving) {
    Rig r;
    EXPECT_EQ(r.step(box(0, 400)), box(0, 400));
    EXPECT_TRUE(r.a.held());
}

TEST(Attraction, ANegativePullInDistanceIsTreatedAsZero) {
    AttractionConfig c;
    c.pull_in = -5;
    Rig on(/*others=*/{}, c);
    EXPECT_EQ(on.step(box(0, 400)), box(0, 400));
    EXPECT_TRUE(on.a.held());
    Rig off(/*others=*/{}, c);
    EXPECT_EQ(off.step(box(1, 400)), box(1, 400));
    EXPECT_FALSE(off.a.held());
}

TEST(Attraction, AFastMoveDoesNotStartASnap) {  // SNAP-012, SNAP-040
    Attraction a({}, {}, kWork, {});
    (void)a.update(box(500, 400), 0);
    const Rect out = a.update(box(10, 400), 10);  // 49 000 px/s
    EXPECT_EQ(out, box(10, 400));
    EXPECT_FALSE(a.held());
    EXPECT_GT(a.speed(), 600.0);
}

TEST(Attraction, TheSnappingSpeedIsInclusive) {  // SNAP-012
    Attraction at({}, {}, kWork, {});
    (void)at.update(box(40, 400), 0);
    EXPECT_EQ(at.update(box(10, 400), 50), box(0, 400));  // exactly 600 px/s
    Attraction over({}, {}, kWork, {});
    (void)over.update(box(41, 400), 0);
    EXPECT_EQ(over.update(box(10, 400), 50), box(10, 400));  // 620 px/s
}

TEST(Attraction, TheSpeedLimitScalesWithTheConfig) {  // SNAP-012
    AttractionConfig c;
    c.max_speed = 1000.0;
    Attraction a(c, {}, kWork, {});
    (void)a.update(box(50, 400), 0);
    EXPECT_EQ(a.update(box(10, 400), 50), box(0, 400));  // 800 px/s is within 1000
}

TEST(Attraction, ASpeedLimitDoesNotReleaseAHeldEdge) {  // SNAP-014
    Rig r;
    EXPECT_EQ(r.step(box(10, 400)), box(0, 400));
    const Rect out = r.a.update(box(5, 450), r.t + 10);  // 5 000 px/s
    EXPECT_EQ(out, box(0, 450));
    EXPECT_TRUE(r.a.held());
}

TEST(Attraction, TheSpeedIsZeroAtTheStartAndMeasuredOverTheLast80Ms) {  // SNAP-013
    Attraction a({}, {}, kWork, {});
    EXPECT_EQ(a.speed(), 0.0);
    (void)a.update(box(500, 400), 1000);
    EXPECT_EQ(a.speed(), 0.0);  // a single position
    (void)a.update(box(510, 400), 1010);
    EXPECT_DOUBLE_EQ(a.speed(), 1000.0);
    (void)a.update(box(520, 430), 1020);  // from (500,400) over 20 ms: hypot(20,30) / 0.02
    EXPECT_NEAR(a.speed(), 1802.775, 0.01);
}

TEST(Attraction, WithFewerThanTwoPositionsTheLastSpeedIsKept) {  // SNAP-013
    Attraction a({}, {}, kWork, {});
    (void)a.update(box(500, 400), 1000);
    (void)a.update(box(510, 400), 1010);
    EXPECT_DOUBLE_EQ(a.speed(), 1000.0);
    (void)a.update(box(512, 400), 1500);  // the earlier positions are older than 80 ms
    EXPECT_DOUBLE_EQ(a.speed(), 1000.0);
    (void)a.update(box(512, 400), 1500);  // same millisecond: no time has passed
    EXPECT_DOUBLE_EQ(a.speed(), 1000.0);
}

TEST(Attraction, AHeldEdgeSlidesAlongTheLine) {  // SNAP-022
    Rig r;
    EXPECT_EQ(r.step(box(10, 100)), box(0, 100));
    EXPECT_EQ(r.step(box(5, 200)), box(0, 200));
    EXPECT_EQ(r.step(box(8, 300)), box(0, 300));
}

TEST(Attraction, BreakawayReleasesPastTheBreakawayDistanceAndJumpsBack) {  // SNAP-041
    Rig r;
    EXPECT_EQ(r.step(box(10, 400)), box(0, 400));
    EXPECT_EQ(r.step(box(-24, 400)), box(0, 400));    // exactly 24 away: still held
    EXPECT_EQ(r.step(box(-25, 400)), box(-25, 400));  // the window is back under the mouse
    EXPECT_FALSE(r.a.held());
    const auto events = r.a.take_events();
    ASSERT_EQ(events.size(), 2u);
    EXPECT_FALSE(events[1].engaged);
    EXPECT_EQ(events[1].edge, Edge::Left);
    EXPECT_EQ(events[1].line, 0);
    EXPECT_EQ(events[1].reason, ReleaseReason::Breakaway);
}

TEST(Attraction, ABreakawayBelowThePullInDistanceUsesThePullInDistance) {  // SNAP-043
    AttractionConfig c;
    c.breakaway = 5;
    Rig r({}, c);
    EXPECT_EQ(r.step(box(10, 400)), box(0, 400));
    EXPECT_EQ(r.step(box(-16, 400)), box(0, 400));  // 16 is the pull-in distance
    EXPECT_EQ(r.step(box(-17, 400)), box(-17, 400));
}

TEST(Attraction, AReleasedEdgeCanBeHeldAgainAfterLeavingTheZone) {  // SNAP-042
    Rig r;
    (void)r.step(box(10, 400));
    EXPECT_EQ(r.step(box(-30, 400)), box(-30, 400));
    EXPECT_EQ(r.step(box(12, 400)), box(0, 400));
    EXPECT_EQ(r.a.snaps(), 2u);
}

TEST(Attraction, ASlideOffTheEndOfAWindowReleasesAndBlocksTheLineUntilFarAway) {
    // SNAP-035, SNAP-042
    Rig r({kW}, only(false, true));
    EXPECT_EQ(r.step(box(505, 320)), box(500, 320));  // left edge on the right side of kW
    EXPECT_EQ(r.step(box(505, 330)), box(500, 330));  // sliding along the line
    // Slide until the vertical gap to kW exceeds the pull-in distance.
    EXPECT_EQ(r.step(box(505, 530)), box(505, 530));
    auto events = r.a.take_events();
    ASSERT_FALSE(events.empty());
    EXPECT_FALSE(events.back().engaged);
    EXPECT_EQ(events.back().reason, ReleaseReason::TargetGone);
    // Back in range with the edge still near the line: the line stays blocked.
    const Rect again = r.step(box(505, 510));
    EXPECT_EQ(again.l, 505);
    // Farther than the pull-in distance clears the block.
    EXPECT_EQ(r.step(box(520, 510)).l, 520);
    EXPECT_EQ(r.step(box(505, 510)).l, 500);
}

TEST(Attraction, ACornerHoldsTwoAxesAndEachReleasesOnItsOwn) {  // SNAP-021
    Rig r;
    EXPECT_EQ(r.step(box(10, 12)), box(0, 0));
    EXPECT_EQ(r.a.snaps(), 2u);
    EXPECT_EQ(r.step(box(10, -30)), box(0, -30));  // only y breaks away
    EXPECT_TRUE(r.a.held());
    EXPECT_EQ(r.step(box(-30, -30)), box(-30, -30));
    EXPECT_FALSE(r.a.held());
}

TEST(Attraction, EdgeToEdgeLeftOfAWindow) {  // SNAP-033
    Rig r({kW}, only(false, true));
    EXPECT_EQ(r.step(box(205, 350)), box(200, 350));
    const auto events = r.a.take_events();
    ASSERT_EQ(events.size(), 1u);
    EXPECT_EQ(events[0].edge, Edge::Right);
    EXPECT_EQ(events[0].line, 300);
    EXPECT_EQ(events[0].kind, TargetKind::EdgeToEdge);
}

TEST(Attraction, EdgeToEdgeRightOfAboveAndBelowAWindow) {  // SNAP-033
    Rig right({kW}, only(false, true));
    EXPECT_EQ(right.step(box(505, 350)), box(500, 350));
    Rig above({kW}, only(false, true));
    EXPECT_EQ(above.step(box(350, 195)), box(350, 200));
    Rig below({kW}, only(false, true));
    EXPECT_EQ(below.step(box(350, 505)), box(350, 500));
}

TEST(Attraction, EdgeToEdgeNeedsOverlapOrBeingWithinPullInOfOverlapping) {  // SNAP-033
    Rig near({kW}, only(false, true));
    // 10 px below kW: within pull-in of overlapping, so its right side is a target for the left edge.
    EXPECT_EQ(near.step(box(505, 510)), box(500, 500));
    Rig far({kW}, only(false, true));
    EXPECT_EQ(far.step(box(505, 530)), box(505, 530));  // 30 px below: no target at all
    EXPECT_FALSE(far.a.held());
}

TEST(Attraction, AlignedNeedsStackedWindows) {  // SNAP-034
    Rig stacked({kW}, only(false, true));
    EXPECT_EQ(stacked.step(box(305, 510)), box(300, 500));
    const auto events = stacked.a.take_events();
    ASSERT_FALSE(events.empty());
    EXPECT_EQ(events[0].edge, Edge::Left);
    EXPECT_EQ(events[0].kind, TargetKind::Aligned);
    Rig apart({kW}, only(false, true));
    EXPECT_EQ(apart.step(box(305, 530)), box(305, 530));
    Rig right({kW}, only(false, true));
    EXPECT_EQ(right.step(box(405, 510)), box(400, 500));  // right to right: 505 -> 500
}

TEST(Attraction, OverlappingWindowsHaveNoInnerTargets) {  // SNAP-036
    Rig r({kW}, only(false, true));
    EXPECT_EQ(r.step(box(305, 350)), box(305, 350));
    EXPECT_FALSE(r.a.held());
    EXPECT_EQ(r.step(box(395, 350)), box(395, 350));  // right 495 against kW.r 500 as well
    EXPECT_FALSE(r.a.held());
}

TEST(Attraction, SlidingOffTheEndOfAnAlignedTargetReleases) {  // SNAP-035
    Rig r({kW}, only(false, true));
    EXPECT_EQ(r.step(box(305, 510)), box(300, 500));
    EXPECT_EQ(r.step(box(305, 560)), box(305, 560));  // the gap grew to 60
    const auto events = r.a.take_events();
    const bool gone = std::ranges::any_of(events, [](const auto& e) {
        return !e.engaged && e.edge == Edge::Left && e.reason == ReleaseReason::TargetGone;
    });
    EXPECT_TRUE(gone);
}

TEST(Attraction, AWindowWinsATieAgainstAScreenEdge) {  // SNAP-037
    const Rect w{-90, 100, 10, 300};  // its right side is at x = 10, the screen edge at x = 0
    Rig r({w}, AttractionConfig{});
    EXPECT_EQ(r.step(box(5, 150)), box(10, 150));
    const auto events = r.a.take_events();
    ASSERT_FALSE(events.empty());
    EXPECT_EQ(events[0].kind, TargetKind::EdgeToEdge);
}

TEST(Attraction, TheNearestTargetWins) {  // SNAP-037
    const Rect w{-90, 100, 10, 300};
    Rig screen({w}, AttractionConfig{});
    EXPECT_EQ(screen.step(box(4, 150)), box(0, 150));  // the screen edge is 4 away, the window 6
    const Rect w3{-90, 350, 14, 500};
    Rig windows({w, w3}, only(false, true));
    EXPECT_EQ(windows.step(box(8, 280)), box(10, 280));
}

TEST(Attraction, ASideOnAnotherMonitorIsNotATarget) {  // SNAP-031
    const Rect primary{0, 0, 1920, 1080};
    const std::vector<Rect> all{primary, {1920, 0, 3840, 1080}};
    const SideTargets s = screen_side_targets(primary, all);
    EXPECT_TRUE(s.left);
    EXPECT_TRUE(s.top);
    EXPECT_FALSE(s.right);
    EXPECT_TRUE(s.bottom);
    EXPECT_FALSE(s.allows(Edge::Right));
    EXPECT_TRUE(s.allows(Edge::Left));
    EXPECT_TRUE(s.allows(Edge::Top));
    EXPECT_TRUE(s.allows(Edge::Bottom));
}

TEST(Attraction, EverySideCanTouchAnotherMonitor) {  // SNAP-031
    const Rect work{1000, 1000, 2000, 2000};
    const std::vector<Rect> all{work,
                                {0, 1000, 1000, 2000},
                                {2000, 1000, 3000, 2000},
                                {1000, 0, 2000, 1000},
                                {1000, 2000, 2000, 3000}};
    const SideTargets s = screen_side_targets(work, all);
    EXPECT_FALSE(s.left);
    EXPECT_FALSE(s.right);
    EXPECT_FALSE(s.top);
    EXPECT_FALSE(s.bottom);
}

TEST(Attraction, ASideIsATargetWhenTheOtherMonitorOnlyTouchesAtACornerOrIsOffset) {  // SNAP-031
    const Rect primary{0, 0, 1920, 1080};
    const std::vector<Rect> all{primary, {1920, 1080, 3840, 2160}, {2500, 0, 3000, 1080}};
    const SideTargets s = screen_side_targets(primary, all);
    EXPECT_TRUE(s.left);
    EXPECT_TRUE(s.top);
    EXPECT_TRUE(s.right);
    EXPECT_TRUE(s.bottom);
    const SideTargets alone = screen_side_targets(primary, std::vector<Rect>{primary});
    EXPECT_TRUE(alone.right);
}

TEST(Attraction, AnUntargetedSideDoesNotHold) {  // SNAP-031
    SideTargets sides;
    sides.right = false;
    Rig r({}, AttractionConfig{}, sides);
    EXPECT_EQ(r.step(box(895, 400)), box(895, 400));
    EXPECT_FALSE(r.a.held());
    EXPECT_EQ(r.step(box(10, 400)), box(0, 400));
}

TEST(Attraction, ScreenEdgesOffRemovesTheScreenTargets) {  // SNAP-030
    Rig r({}, only(false, true));
    EXPECT_EQ(r.step(box(10, 400)), box(10, 400));
    EXPECT_FALSE(r.a.held());
}

TEST(Attraction, WindowsOffRemovesTheWindowTargets) {  // SNAP-032
    Rig r({kW}, only(true, false));
    EXPECT_EQ(r.step(box(205, 350)), box(205, 350));
    EXPECT_FALSE(r.a.held());
    EXPECT_EQ(r.step(box(10, 350)), box(0, 350));
}

TEST(Attraction, EventsCarryEdgeLineKindAndSpeedAndAreCleared) {  // SNAP-060
    Rig r;
    (void)r.step(box(10, 400));
    const auto events = r.a.take_events();
    ASSERT_EQ(events.size(), 1u);
    EXPECT_TRUE(events[0].engaged);
    EXPECT_EQ(events[0].edge, Edge::Left);
    EXPECT_EQ(events[0].line, 0);
    EXPECT_EQ(events[0].kind, TargetKind::Screen);
    EXPECT_EQ(events[0].speed, 0.0);
    EXPECT_TRUE(r.a.take_events().empty());
}

TEST(Attraction, CurrentIsTheLastReturnedPosition) {
    Rig r;
    EXPECT_EQ(r.a.current(), Rect{});
    const Rect out = r.step(box(10, 400));
    EXPECT_EQ(r.a.current(), out);
}

TEST(Attraction, OnlyOneEdgePerAxisIsHeldAndItStaysUntilReleased) {  // SNAP-021, SNAP-040
    const Rect w{109, 300, 209, 500};
    Rig r({w}, AttractionConfig{});
    EXPECT_EQ(r.step(box(4, 350)),
              box(0, 350));  // left to the screen edge (4) beats right to w.l (5)
    EXPECT_EQ(r.a.snaps(), 1u);
    EXPECT_EQ(r.step(box(6, 350)),
              box(0, 350));  // right edge is now 3 from w.l, but left stays held
    EXPECT_EQ(r.a.snaps(), 1u);
}
