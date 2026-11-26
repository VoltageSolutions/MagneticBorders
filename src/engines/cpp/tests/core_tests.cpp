// GoogleTest cases for build_seam and propagate.
#include <gtest/gtest.h>

#include "core/seam.hpp"

using namespace mb;

namespace {
const Rect kWork{0, 0, 3440, 1440};
std::vector<WinInfo> two_windows(int min_b = 1) {
    return {{1, {0, 0, 1720, 1440}, 1, 1}, {2, {1720, 0, 3440, 1440}, min_b, 1}};
}
}  // namespace

TEST(BuildSeam, GluesTouchingNeighbour) {
    const auto seam = build_seam(two_windows(), 1, Edge::Right, kWork, {12});
    ASSERT_TRUE(seam);
    ASSERT_EQ(seam->members.size(), 1u);
    EXPECT_EQ(seam->members[0].side, Side::Opposite);
    EXPECT_EQ(seam->x, 1720);
}

TEST(BuildSeam, ScreenEdgeIsNeverASeam) {
    EXPECT_FALSE(build_seam(two_windows(), 2, Edge::Right, kWork, {12}));
    EXPECT_FALSE(build_seam(two_windows(), 1, Edge::Left, kWork, {12}));
}

TEST(BuildSeam, GapBeyondToleranceIsNotGlued) {
    std::vector<WinInfo> w{{1, {0, 0, 1700, 1440}, 1, 1}, {2, {1740, 0, 3440, 1440}, 1, 1}};
    EXPECT_FALSE(build_seam(w, 1, Edge::Right, kWork, {12}));
}

TEST(BuildSeam, ClosureAddsSameSideStackedWindow) {
    std::vector<WinInfo> w{{1, {0, 0, 1000, 700}, 1, 1},
                           {2, {0, 700, 1000, 1440}, 1, 1},
                           {3, {1000, 0, 3440, 1440}, 1, 1}};
    const auto seam = build_seam(w, 1, Edge::Right, kWork, {12});
    ASSERT_TRUE(seam);
    ASSERT_EQ(seam->members.size(), 2u);
}

TEST(Propagate, MovesMembersAndKeepsOffset) {
    std::vector<WinInfo> w{{1, {0, 0, 1272, 1440}, 1, 1}, {2, {1280, 0, 2552, 1440}, 1, 1}};
    const auto seam = build_seam(w, 1, Edge::Right, kWork, {12});
    ASSERT_TRUE(seam);
    const Plan p = propagate(*seam, 1372);
    EXPECT_EQ(p.clamped_pos, 1372);
    ASSERT_EQ(p.moves.size(), 1u);
    EXPECT_EQ(p.moves[0].frame, (Rect{1380, 0, 2552, 1440}));
}

TEST(Propagate, ClampsToMemberMinimum) {
    std::vector<WinInfo> w{{1, {0, 0, 1720, 1440}, 1, 1}, {2, {1720, 0, 3440, 1440}, 1600, 1}};
    const auto seam = build_seam(w, 1, Edge::Right, kWork, {12});
    ASSERT_TRUE(seam);
    EXPECT_EQ(propagate(*seam, 2020).clamped_pos, 1840);
}

TEST(Propagate, TopSeamUsesHeights) {
    std::vector<WinInfo> w{{1, {0, 0, 1000, 700}, 1, 1}, {2, {0, 700, 1000, 1440}, 1, 300}};
    const auto seam = build_seam(w, 1, Edge::Bottom, kWork, {12});
    ASSERT_TRUE(seam);
    const Plan p = propagate(*seam, 1400);
    EXPECT_EQ(p.clamped_pos, 1140);
    EXPECT_EQ(p.moves[0].frame.t, 1140);
}

TEST(Geometry, RectExtents) {
    const Rect r{10, 20, 110, 70};
    EXPECT_EQ(r.width(), 100);
    EXPECT_EQ(r.height(), 50);
}

TEST(Geometry, OppositeEdges) {
    EXPECT_EQ(opposite(Edge::Left), Edge::Right);
    EXPECT_EQ(opposite(Edge::Right), Edge::Left);
    EXPECT_EQ(opposite(Edge::Top), Edge::Bottom);
    EXPECT_EQ(opposite(Edge::Bottom), Edge::Top);
}

TEST(Geometry, VerticalLineEdges) {
    EXPECT_TRUE(is_vertical_line(Edge::Left));
    EXPECT_TRUE(is_vertical_line(Edge::Right));
    EXPECT_FALSE(is_vertical_line(Edge::Top));
    EXPECT_FALSE(is_vertical_line(Edge::Bottom));
}

TEST(Geometry, EdgeCoordAndWithEdge) {
    const Rect r{1, 2, 3, 4};
    EXPECT_EQ(edge_coord(r, Edge::Left), 1);
    EXPECT_EQ(edge_coord(r, Edge::Top), 2);
    EXPECT_EQ(edge_coord(r, Edge::Right), 3);
    EXPECT_EQ(edge_coord(r, Edge::Bottom), 4);
    EXPECT_EQ(with_edge(r, Edge::Left, 9), (Rect{9, 2, 3, 4}));
    EXPECT_EQ(with_edge(r, Edge::Top, 9), (Rect{1, 9, 3, 4}));
    EXPECT_EQ(with_edge(r, Edge::Right, 9), (Rect{1, 2, 9, 4}));
    EXPECT_EQ(with_edge(r, Edge::Bottom, 9), (Rect{1, 2, 3, 9}));
}

TEST(BuildSeam, UnknownDraggedWindowGivesNothing) {
    EXPECT_FALSE(build_seam(two_windows(), 99, Edge::Right, kWork, {12}));
}

TEST(BuildSeam, LeftEdgeGluesWindowOnTheLeft) {
    const auto seam = build_seam(two_windows(), 2, Edge::Left, kWork, {12});
    ASSERT_TRUE(seam);
    ASSERT_EQ(seam->members.size(), 1u);
    EXPECT_EQ(seam->members[0].id, 1u);
    EXPECT_EQ(moving_edge(*seam, seam->members[0]), Edge::Right);
}

TEST(BuildSeam, TopEdgeGluesWindowAbove) {
    std::vector<WinInfo> w{{1, {0, 0, 1000, 700}, 1, 1}, {2, {0, 700, 1000, 1440}, 1, 1}};
    const auto seam = build_seam(w, 2, Edge::Top, kWork, {12});
    ASSERT_TRUE(seam);
    ASSERT_EQ(seam->members.size(), 1u);
    EXPECT_EQ(seam->members[0].id, 1u);
}

TEST(BuildSeam, SameEdgeWindowIsGluedAsSameSide) {
    std::vector<WinInfo> w{{1, {0, 0, 1000, 700}, 1, 1}, {2, {0, 700, 1005, 1440}, 1, 1}};
    const auto seam = build_seam(w, 1, Edge::Right, kWork, {12});
    ASSERT_TRUE(seam);
    ASSERT_EQ(seam->members.size(), 1u);
    EXPECT_EQ(seam->members[0].side, Side::Same);
    EXPECT_EQ(seam->members[0].d, 5);
    EXPECT_EQ(moving_edge(*seam, seam->members[0]), Edge::Right);
}

TEST(BuildSeam, WindowOutsideThePerpendicularSpanIsNotGlued) {
    std::vector<WinInfo> w{{1, {0, 0, 1000, 500}, 1, 1}, {2, {1000, 800, 2000, 1440}, 1, 1}};
    EXPECT_FALSE(build_seam(w, 1, Edge::Right, kWork, {12}));
}

TEST(BuildSeam, ClosureReachesWindowsThroughOtherMembers) {
    std::vector<WinInfo> w{{1, {0, 0, 1000, 400}, 1, 1},
                           {4, {1000, 800, 2000, 1200}, 1, 1},
                           {2, {1000, 400, 2000, 800}, 1, 1},
                           {3, {1000, 0, 2000, 400}, 1, 1}};
    const auto seam = build_seam(w, 1, Edge::Right, kWork, {12});
    ASSERT_TRUE(seam);
    EXPECT_EQ(seam->members.size(), 3u);
}

TEST(Propagate, ConflictingLimitsKeepTheSeamWhereItIs) {
    std::vector<WinInfo> w{{1, {0, 0, 1000, 700}, 1, 1},
                           {2, {0, 700, 1000, 1440}, 1200, 1},
                           {3, {1000, 0, 1500, 1440}, 400, 1}};
    const auto seam = build_seam(w, 1, Edge::Right, kWork, {12});
    ASSERT_TRUE(seam);
    ASSERT_EQ(seam->members.size(), 2u);
    const Plan p = propagate(*seam, 1300);
    EXPECT_EQ(p.clamped_pos, 1000);
}

TEST(Propagate, LeftDragClampsAtTheSameSideLimit) {
    std::vector<WinInfo> w{{1, {1000, 0, 2000, 1440}, 1, 1}, {2, {0, 0, 1000, 1440}, 600, 1}};
    const auto seam = build_seam(w, 1, Edge::Left, kWork, {12});
    ASSERT_TRUE(seam);
    EXPECT_EQ(propagate(*seam, 100).clamped_pos, 600);
    EXPECT_EQ(propagate(*seam, 1500).clamped_pos, 1500);
}

TEST(Propagate, TopDragClampsToMemberHeight) {
    std::vector<WinInfo> w{{1, {0, 700, 1000, 1440}, 1, 1}, {2, {0, 0, 1000, 700}, 1, 300}};
    const auto seam = build_seam(w, 1, Edge::Top, kWork, {12});
    ASSERT_TRUE(seam);
    EXPECT_EQ(propagate(*seam, 50).clamped_pos, 300);
}
