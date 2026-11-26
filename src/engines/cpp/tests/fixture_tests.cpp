// GoogleTest cases for the fixture parser and runner, and a run of every shared .layout file.
#include <gtest/gtest.h>

#include <filesystem>
#include <fstream>
#include <sstream>
#include <stdexcept>

#include "fixture.hpp"

using namespace mb;
namespace fs = std::filesystem;

namespace {
constexpr const char* kTwoWindows = "workarea 0,0 3440x1440\n"
                                    "win A 0,0 1720x1440\n"
                                    "win B 1720,0 1720x1440 min 1600x200\n";
}  // namespace

TEST(FixtureParse, ReadsEveryDirective) {
    const auto f = fixture::parse(std::string("# comment\ntolerance 20\n") + kTwoWindows +
                                  "drag A right +300\nexpect A 0,0 1840x1440\nexpect clamp 1840\n");
    EXPECT_EQ(f.tolerance, 20);
    EXPECT_EQ(f.work_area, (Rect{0, 0, 3440, 1440}));
    ASSERT_EQ(f.windows.size(), 2u);
    EXPECT_EQ(f.windows[1].info.id, 2u);
    EXPECT_EQ(f.windows[1].info.min_w, 1600);
    EXPECT_EQ(f.windows[1].info.min_h, 200);
    EXPECT_EQ(f.drag_name, "A");
    EXPECT_EQ(f.drag_edge, Edge::Right);
    EXPECT_EQ(f.drag_delta, 300);
    ASSERT_EQ(f.expects.size(), 1u);
    EXPECT_EQ(f.expect_clamp, 1840);
}

TEST(FixtureParse, ParsesAllEdgeNames) {
    for (const auto& [name, edge] :
         {std::pair{"left", Edge::Left}, std::pair{"right", Edge::Right},
          std::pair{"top", Edge::Top}, std::pair{"bottom", Edge::Bottom}}) {
        const auto f = fixture::parse(std::string("win A 0,0 10x10\ndrag A ") + name + " -5\n");
        EXPECT_EQ(f.drag_edge, edge);
        EXPECT_EQ(f.drag_delta, -5);
    }
}

TEST(FixtureParse, RejectsBadLines) {
    EXPECT_THROW(fixture::parse("nonsense here"), std::runtime_error);
    EXPECT_THROW(fixture::parse("win A 0;0 10x10"), std::runtime_error);
    EXPECT_THROW(fixture::parse("win A 0,0 10y10"), std::runtime_error);
    EXPECT_THROW(fixture::parse("win A 0,0 10x10 max 5x5"), std::runtime_error);
    EXPECT_THROW(fixture::parse("win A 0,0 10x10\ndrag A middle 5"), std::runtime_error);
}

TEST(FixtureRun, ClampsAndSnapsDraggedWindowBack) {
    const auto f = fixture::parse(std::string(kTwoWindows) + "drag A right +300\n");
    const auto out = fixture::run(f);
    ASSERT_TRUE(out.clamp);
    EXPECT_EQ(*out.clamp, 1840);
    EXPECT_EQ(fixture::format(out), "A 0,0 1840x1440\nB 1840,0 1600x1440\n");
}

TEST(FixtureRun, UnknownDragWindowThrows) {
    const auto f = fixture::parse(std::string(kTwoWindows) + "drag Z right +1\n");
    EXPECT_THROW(fixture::run(f), std::runtime_error);
}

TEST(FixtureRun, NoSeamMovesOnlyTheDraggedWindow) {
    const auto f = fixture::parse("win A 0,0 100x100\nwin B 500,0 100x100\ndrag A right +10\n");
    const auto out = fixture::run(f);
    EXPECT_FALSE(out.clamp);
    EXPECT_EQ(fixture::format(out), "A 0,0 110x100\nB 500,0 100x100\n");
}

TEST(FixtureCheck, ReportsEveryKindOfMismatch) {
    auto f =
        fixture::parse(std::string(kTwoWindows) + "drag A right +300\nexpect A 0,0 1840x1440\n"
                                                  "expect B 1840,0 1600x1440\nexpect clamp 1840\n");
    EXPECT_EQ(fixture::check(f, fixture::run(f)), "");

    f.expects[0].second.r += 1;
    EXPECT_NE(fixture::check(f, fixture::run(f)).find("A: expected"), std::string::npos);

    f = fixture::parse(std::string(kTwoWindows) + "drag A right +300\nexpect Q 0,0 1x1\n");
    EXPECT_NE(fixture::check(f, fixture::run(f)).find("unknown window Q"), std::string::npos);

    f = fixture::parse(std::string(kTwoWindows) + "drag A right +300\nexpect A 0,0 1840x1440\n");
    EXPECT_NE(fixture::check(f, fixture::run(f)).find("B: expected unchanged"), std::string::npos);

    f = fixture::parse(std::string(kTwoWindows) + "drag A right +300\nexpect A 0,0 1840x1440\n"
                                                  "expect B 1840,0 1600x1440\nexpect clamp 7\n");
    EXPECT_NE(fixture::check(f, fixture::run(f)).find("clamp: expected 7 got 1840"),
              std::string::npos);

    f = fixture::parse(
        "win A 0,0 100x100\ndrag A right +1\nexpect A 0,0 101x100\nexpect clamp 5\n");
    EXPECT_NE(fixture::check(f, fixture::run(f)).find("got none"), std::string::npos);
}

TEST(Fixtures, EverySharedLayoutPasses) {
    std::size_t count = 0;
    for (const auto& e : fs::directory_iterator(MB_FIXTURES_DIR)) {
        if (e.path().extension() != ".layout") continue;
        std::ifstream in(e.path(), std::ios::binary);
        std::ostringstream text;
        text << in.rdbuf();
        const auto f = fixture::parse(text.str());
        EXPECT_EQ(fixture::check(f, fixture::run(f)), "") << e.path().string();
        ++count;
    }
    EXPECT_GT(count, 0u);
}
