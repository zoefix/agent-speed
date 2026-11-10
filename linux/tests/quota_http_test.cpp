#include <gtest/gtest.h>

#include "agentspeed/quota/http.hpp"
#include "agentspeed/quota/sources.hpp"
#include "agentspeed/quota/window.hpp"

namespace http = agentspeed::quota::http;
using agentspeed::quota::Kind;
using agentspeed::quota::makeWindow;

TEST(QuotaHttp, OnlyNetworkNineHostsOverHttps) {
    http::Request r;
    r.url = "https://api.kimi.com/coding/v1/usages";
    EXPECT_TRUE(http::permitted(r));
    r.url = "http://api.kimi.com/coding/v1/usages";
    EXPECT_FALSE(http::permitted(r));
    r.url = "https://api.kimi.com:8443/coding/v1/usages";
    EXPECT_FALSE(http::permitted(r));
    r.url = "https://api.anthropic.com/api/oauth/usage";
    EXPECT_FALSE(http::permitted(r));
    r.url = "https://claude.ai/api/organizations/x/usage";
    EXPECT_FALSE(http::permitted(r));
    r.url = "https://user@api.kimi.com/x";
    EXPECT_FALSE(http::permitted(r));
    r.url = "https://www.workbuddy.cn/billing/meter/get-user-resource-summary";
    EXPECT_TRUE(http::permitted(r));
    r.url = "https://qoder.com.cn/api/v2/me/usages/big_model_credits";
    EXPECT_TRUE(http::permitted(r));
    r.url = "https://evil.qoder.com/api";
    EXPECT_FALSE(http::permitted(r));
}

TEST(QuotaHttp, LoopbackOnlyToLocalhostWithAPort) {
    http::Request r;
    r.loopback = true;
    r.url = "http://127.0.0.1:41234/exa.language_server_pb.LanguageServerService/RetrieveUserQuotaSummary";
    EXPECT_TRUE(http::permitted(r));
    r.url = "https://127.0.0.1:41234/x";
    EXPECT_TRUE(http::permitted(r));
    r.url = "http://127.0.0.1/x";
    EXPECT_FALSE(http::permitted(r));
    r.url = "http://localhost:41234/x";
    EXPECT_FALSE(http::permitted(r));
    r.url = "http://192.168.1.2:41234/x";
    EXPECT_FALSE(http::permitted(r));
    r.loopback = false;
    r.url = "https://127.0.0.1:41234/x";
    EXPECT_FALSE(http::permitted(r));
}

TEST(QuotaHttp, ParsesHttpDates) {
    EXPECT_EQ(http::parseHttpDate("Tue, 10 Nov 2026 19:26:13 GMT"), 1794338773000);
    EXPECT_EQ(http::parseHttpDate("Tuesday, 10-Nov-26 19:26:13 GMT"), 1794338773000);
    EXPECT_EQ(http::parseHttpDate("Tue Nov 10 19:26:13 2026"), 1794338773000);
    EXPECT_FALSE(http::parseHttpDate("10 Nov 2026"));
    EXPECT_FALSE(http::parseHttpDate("Tue, 10 Foo 2026 19:26:13 GMT"));
    EXPECT_FALSE(http::parseHttpDate("Tue, 10 Nov 2026 25:26:13 GMT"));
}

TEST(QuotaSources, SwitchesAndDefaults) {
    using agentspeed::SourceKind;
    using agentspeed::quota::routeOf;
    EXPECT_EQ(routeOf(SourceKind::ClaudeCode)->switchKey, "quotaClaudeCli");
    EXPECT_FALSE(routeOf(SourceKind::ClaudeCode)->defaultOn);
    EXPECT_TRUE(routeOf(SourceKind::Codex)->defaultOn);
    EXPECT_EQ(routeOf(SourceKind::QoderCli)->switchKey, "quotaQoder");
    EXPECT_EQ(routeOf(SourceKind::WorkBuddyAi), nullptr);
    EXPECT_EQ(routeOf(SourceKind::Deepseek), nullptr);
    EXPECT_FALSE(routeOf(SourceKind::ZCode)->defaultOn);
}

TEST(QuotaWindow, DefaultsAndLabels) {
    EXPECT_EQ(makeWindow(Kind::FiveHour, 1, std::nullopt).minutes, 300);
    EXPECT_EQ(makeWindow(Kind::ModelWeek, 1, std::nullopt, "Fable").minutes, 10080);
    EXPECT_EQ(makeWindow(Kind::Month, 1, std::nullopt).minutes, 43200);
    EXPECT_FALSE(makeWindow(Kind::Credits, 1, std::nullopt).minutes);
    EXPECT_EQ(agentspeed::quota::englishLabel(makeWindow(Kind::Other, 1, std::nullopt, std::nullopt, 1500)), "25h");
    EXPECT_EQ(agentspeed::quota::englishLabel(makeWindow(Kind::Month, 1, std::nullopt, "MCP")), "MCP mo");
}
