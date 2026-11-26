// GoogleTest cases for parse_settings and handle_pipe_request.
#include <gtest/gtest.h>

#include <nlohmann/json.hpp>

#include "app/pipe_protocol.hpp"
#include "app/settings.hpp"

using namespace mb;

TEST(Settings, EmptyObjectGivesDefaults) {
    const auto s = parse_settings("{}");
    ASSERT_TRUE(s);
    EXPECT_EQ(*s, Settings{});
    EXPECT_EQ(s->glue_tolerance_px, 12);
    EXPECT_EQ(s->snap_distance_px, 16);
    EXPECT_TRUE(s->enabled);
    EXPECT_FALSE(s->start_at_login);
}

TEST(Settings, FullFile) {
    const auto s =
        parse_settings(R"({"schema":1,"enabled":false,"glue":{"enabled":false,"tolerance_px":0},
        "snap":{"enabled":false,"distance_px":40},"excluded_apps":["b.exe","a.exe"],"start_at_login":true,"extra":1})");
    ASSERT_TRUE(s);
    EXPECT_FALSE(s->enabled);
    EXPECT_FALSE(s->glue_enabled);
    EXPECT_EQ(s->glue_tolerance_px, 0);
    EXPECT_FALSE(s->snap_enabled);
    EXPECT_EQ(s->snap_distance_px, 40);
    EXPECT_EQ(s->excluded_apps, (std::vector<std::string>{"b.exe", "a.exe"}));
    EXPECT_TRUE(s->start_at_login);
}

TEST(Settings, PartialFileKeepsDefaultsForMissingKeys) {
    const auto s = parse_settings(R"({"glue":{"tolerance_px":30}})");
    ASSERT_TRUE(s);
    EXPECT_TRUE(s->glue_enabled);
    EXPECT_EQ(s->glue_tolerance_px, 30);
    EXPECT_TRUE(s->enabled);
}

TEST(Settings, ClampsAndIgnoresWrongTypes) {
    const auto s = parse_settings(
        R"({"glue":{"tolerance_px":500},"snap":{"distance_px":-5},"enabled":"yes","excluded_apps":[1,"",  "x.exe"]})");
    ASSERT_TRUE(s);
    EXPECT_EQ(s->glue_tolerance_px, 64);
    EXPECT_EQ(s->snap_distance_px, 0);
    EXPECT_TRUE(s->enabled);
    EXPECT_EQ(s->excluded_apps, (std::vector<std::string>{"x.exe"}));
}

TEST(Settings, BadJsonIsRejectedSoCallerKeepsPrevious) {
    EXPECT_FALSE(parse_settings(""));
    EXPECT_FALSE(parse_settings("{ not json"));
    EXPECT_FALSE(parse_settings("[1,2]"));
}

TEST(Settings, BomIsTolerated) { EXPECT_TRUE(parse_settings("\xEF\xBB\xBF{}")); }

TEST(Settings, GlueActiveRule) {
    Settings s;
    EXPECT_TRUE(s.glue_active());
    s.glue_enabled = false;
    EXPECT_FALSE(s.glue_active());
    s.glue_enabled = true;
    s.enabled = false;
    EXPECT_FALSE(s.glue_active());
}

TEST(Settings, WithEnabledPreservesUnknownKeys) {
    const std::string in =
        R"({"schema":1,"enabled":true,"future":{"a":[1,2,{"b":null}]},"glue":{"enabled":true,"tolerance_px":12,"x":"y"},"zzz":"last"})";
    const auto out = with_enabled(in, false);
    ASSERT_TRUE(out);
    const auto j = nlohmann::json::parse(*out);
    auto expected = nlohmann::json::parse(in);
    expected["enabled"] = false;
    EXPECT_EQ(j, expected);
    const auto back = with_enabled(*out, true);
    ASSERT_TRUE(back);
    EXPECT_EQ(nlohmann::json::parse(*back), nlohmann::json::parse(in));
}

TEST(Settings, WithEnabledAddsKeyAndRejectsNonObjects) {
    const auto added = with_enabled(R"({"keep":1})", false);
    ASSERT_TRUE(added);
    EXPECT_EQ(nlohmann::json::parse(*added),
              nlohmann::json::parse(R"({"keep":1,"enabled":false})"));
    const auto fresh = with_enabled("  ", false);
    ASSERT_TRUE(fresh);
    EXPECT_EQ(nlohmann::json::parse(*fresh), nlohmann::json::parse(R"({"enabled":false})"));
    EXPECT_FALSE(with_enabled("[1]", true));
    EXPECT_FALSE(with_enabled("{ broken", true));
}

namespace {
EngineStatus sample() {
    EngineStatus st;
    st.pid = 1234;
    st.drags = 17;
    st.glued_drags = 9;
    st.uptime_s = 360;
    return st;
}
}  // namespace

TEST(Pipe, StatusExactShape) {
    const auto r = handle_pipe_request(R"({"cmd":"status"})", sample());
    EXPECT_EQ(r.action, PipeAction::None);
    EXPECT_EQ(
        r.line,
        std::string(R"({"ok":true,"engine":"cpp","version":")") + std::string(kVersion) +
            R"(","pid":1234,"enabled":true,)"
            R"("glue":{"enabled":true,"tolerance_px":12},"drags":17,"glued_drags":9,"uptime_s":360})");
}

TEST(Pipe, Commands) {
    const auto st = sample();
    auto r = handle_pipe_request(R"({"cmd":"reload"})", st);
    EXPECT_EQ(r.line, R"({"ok":true})");
    EXPECT_EQ(r.action, PipeAction::Reload);
    r = handle_pipe_request(R"({"cmd":"quit"})", st);
    EXPECT_EQ(r.line, R"({"ok":true})");
    EXPECT_EQ(r.action, PipeAction::Quit);
}

TEST(Pipe, UnknownAndMalformed) {
    for (const char* req : {R"({"cmd":"dance"})", R"({"cmd":"pause"})", R"({"cmd":"resume"})",
                            "garbage", "{}", R"({"cmd":5})", "[]"}) {
        const auto r = handle_pipe_request(req, sample());
        EXPECT_EQ(r.line, R"({"ok":false,"error":"unknown command"})") << req;
        EXPECT_EQ(r.action, PipeAction::None);
    }
}

TEST(Pipe, StatusReportsDisabledAndTolerance) {
    auto st = sample();
    st.settings.enabled = false;
    st.settings.glue_tolerance_px = 20;
    const auto r = handle_pipe_request(R"({"cmd":"status"})", st);
    const auto j = nlohmann::json::parse(r.line);
    EXPECT_TRUE(j["ok"].get<bool>());
    EXPECT_FALSE(j["enabled"].get<bool>());
    EXPECT_EQ(j["glue"]["tolerance_px"].get<int>(), 20);
}

TEST(Settings, NonNumericPixelValuesFallBackToDefaults) {
    const auto s = parse_settings(R"({"glue":{"tolerance_px":"wide"},"snap":[1]})");
    ASSERT_TRUE(s);
    EXPECT_EQ(s->glue_tolerance_px, 12);
    EXPECT_EQ(s->snap_distance_px, 16);
}
