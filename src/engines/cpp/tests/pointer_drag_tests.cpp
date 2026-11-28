// GoogleTest cases for PointerDrag (SNAP-070 to SNAP-076 without the OS).
#include <gtest/gtest.h>

#include <vector>

#include "core/pointer_drag.hpp"

using namespace mb;

namespace {

const Rect kWork{0, 0, 1000, 800};
/// The dragged window: 100x100 at (100, 400), grabbed with the cursor at (150, 410).
const Rect kFrame{100, 400, 200, 500};
const Point kGrab{150, 410};

/// A PointerDrag with only screen edges as targets and a clock that advances 100 ms per move (never fast).
/// Windows reports each mouse movement as the cursor's position plus that movement, so nudge() does the same.
struct Rig {
    PointerDrag d;
    Point cursor{kGrab};
    std::uint64_t t{0};
    explicit Rig(const AttractionConfig& cfg = {})
        : d(Attraction(cfg, std::vector<Rect>{}, kWork, {}), kFrame, kGrab) {}
    Point nudge(int dx, int dy) {
        t += 100;
        cursor = d.move({cursor.x + dx, cursor.y + dy}, t);
        return cursor;
    }
};

}  // namespace

TEST(PointerDrag, TheCursorGoesWhereTheMouseSendsItWhenNothingIsHeld) {  // SNAP-072
    Rig r;
    EXPECT_EQ(r.nudge(10, 10), (Point{160, 420}));
    EXPECT_EQ(r.d.window(), (Rect{110, 410, 210, 510}));
    EXPECT_FALSE(r.d.attraction().held());
}

TEST(PointerDrag, CalibrationMovesTheReferenceAndReturnsTheHeldCursor) {  // SNAP-077
    Rig r;
    (void)r.nudge(-90, 0);  // held at x = 0, cursor (50, 410)
    EXPECT_EQ(r.d.calibrate(true, 5), (Point{45, 410}));
    EXPECT_EQ(r.d.window(), (Rect{0, 400, 100, 500}));
    EXPECT_EQ(r.d.cursor(), (Point{45, 410}));
    EXPECT_EQ(r.d.calibrate(true, 0), std::nullopt);
    EXPECT_EQ(r.d.calibrate(false, 3), std::nullopt);  // y is not held
}

TEST(PointerDrag, PullInMovesTheCursorSoTheWindowLandsOnTheLine) {  // SNAP-040, SNAP-072
    Rig r;
    // The window's left edge would be at 10: pulled to 0, so the cursor lands 10 px further left.
    EXPECT_EQ(r.nudge(-90, 0), (Point{50, 410}));
    EXPECT_EQ(r.d.window().l, 0);
    EXPECT_TRUE(r.d.attraction().held_x());
}

TEST(PointerDrag, AHeldAxisKeepsTheCursorStillWhileTheOtherAxisSlides) {  // SNAP-022, SNAP-071
    Rig r;
    (void)r.nudge(-90, 0);                         // held at x = 0, cursor x 50
    EXPECT_EQ(r.nudge(-5, 40), (Point{50, 450}));  // pushing left and down: only y moves
    EXPECT_EQ(r.nudge(8, 50), (Point{50, 500}));   // a small wiggle right: x stays
    EXPECT_EQ(r.d.window(), (Rect{0, 490, 100, 590}));
}

TEST(PointerDrag, BreakawayLetsGoWithoutAJump) {  // SNAP-041, SNAP-073
    Rig r;
    (void)r.nudge(-90, 0);  // free left edge 10, held at 0, cursor x 50
    // Push left: the free position builds up (0, then -20) while the cursor stays.
    EXPECT_EQ(r.nudge(-10, 0), (Point{50, 410}));
    EXPECT_EQ(r.nudge(-20, 0), (Point{50, 410}));
    // The next push takes the free position to -25, past the 24 px breakaway: the cursor moves by this push only.
    EXPECT_EQ(r.nudge(-5, 0), (Point{45, 410}));
    EXPECT_FALSE(r.d.attraction().held_x());
    EXPECT_EQ(r.d.window().l, -5);
}

TEST(PointerDrag, AfterBreakawayTheLineWaitsUntilTheWindowLeavesTheZone) {  // SNAP-042, SNAP-074
    Rig r;
    (void)r.nudge(-90, 0);
    (void)r.nudge(-30, 0);
    (void)r.nudge(-5, 0);  // let go, window left edge at -5
    EXPECT_FALSE(r.d.attraction().held_x());
    // Back toward the line: still blocked, the cursor moves freely.
    EXPECT_EQ(r.nudge(10, 0), (Point{55, 410}));
    EXPECT_FALSE(r.d.attraction().held_x());
    // Out of the zone (left edge 25, more than 16 px away), then back in: held again.
    EXPECT_EQ(r.nudge(20, 0), (Point{75, 410}));
    EXPECT_EQ(r.nudge(-10, 0), (Point{50, 410}));
    EXPECT_TRUE(r.d.attraction().held_x());
}

TEST(PointerDrag, ACornerHoldsTheCursorOnBothAxes) {  // SNAP-021
    Rig r;
    // Left edge 10 from 0 and bottom edge 795 from 800: both pulled.
    EXPECT_EQ(r.nudge(-90, 295), (Point{50, 710}));
    EXPECT_EQ(r.nudge(-3, 3), (Point{50, 710}));
    EXPECT_EQ(r.d.window(), (Rect{0, 700, 100, 800}));
}

TEST(PointerDrag, AFastMovePassesThroughTheLine) {  // SNAP-012
    PointerDrag d(Attraction({}, std::vector<Rect>{}, kWork, {}), kFrame, kGrab);
    (void)d.move({150, 410}, 0);  // the first move gives the speed its first sample
    // 90 px in 10 ms is 9000 px/s: nothing is held.
    EXPECT_EQ(d.move({60, 410}, 10), (Point{60, 410}));
    EXPECT_FALSE(d.attraction().held());
}
