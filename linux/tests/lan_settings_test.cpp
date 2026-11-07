#include <gtest/gtest.h>

#include <set>

#include "agentspeed/lan_push.hpp"
#include "agentspeed/lan_settings.hpp"
#include "lan_vectors.hpp"

// LAN 2.1, 4.7 and 5.3: the settings document, keys and ids, the push rules and the HTTP backoff; and that these tests
// cover every section of the vectors.
using namespace agentspeed;
using namespace agentspeed::lan;

TEST(LanSettings, Vectors) {
    const auto cases = lan_vectors::section("settings").elements();
    ASSERT_EQ(cases.size(), 6u);
    for (const auto& c : cases) {
        const auto settings = Settings::parse(fixtures::compactJson(c["document"]));
        EXPECT_EQ(settings.json(), fixtures::compactJson(c["settings"])) << *c["name"].string();
        // Writing and reading again keeps the document.
        EXPECT_EQ(Settings::parse(settings.json()), settings) << *c["name"].string();
    }
}

TEST(LanSettings, UnreadableTextReadsAsDefaults) {
    EXPECT_EQ(Settings::parse(""), Settings{});
    EXPECT_EQ(Settings::parse("{"), Settings{});
    EXPECT_EQ(Settings::parse(R"({"version":1,"server":{"port":7300.0}})").server.port, kDefaultPort);
}

TEST(LanSettings, KeysAndIds) {
    EXPECT_EQ(displayKey(*lan_vectors::section("key").string()), *lan_vectors::section("keyDisplay").string());
    std::set<std::string> seen;
    for (int i = 0; i < 64; ++i) {
        const auto key = newKey();
        EXPECT_TRUE(validKey(key)) << key;
        seen.insert(key);
        EXPECT_TRUE(validUuid(newUuid()));
        const auto run = newRunId();
        EXPECT_EQ(run.size(), 16u);
        EXPECT_EQ(run.find_first_not_of("0123456789abcdef"), std::string::npos);
    }
    EXPECT_EQ(seen.size(), 64u);
    EXPECT_FALSE(validKey("K7Q4M2X9PL3I")) << "I is not in the alphabet";
    EXPECT_FALSE(validUuid("3F2504E0-4F89-41D3-9A0C-0305E82C3301"));
}

TEST(LanSettings, CleanText) {
    EXPECT_EQ(cleanText("  Desk  ", 0, 64), std::optional<std::string>("Desk"));
    EXPECT_EQ(cleanText(std::string(64, 'x'), 0, 64), std::optional<std::string>(std::string(64, 'x')));
    EXPECT_EQ(cleanText(std::string(65, 'x'), 0, 64), std::nullopt);
    EXPECT_EQ(cleanText("鍵", 1, 128), std::optional<std::string>("鍵"));
    EXPECT_EQ(cleanText("a\x01" "b", 0, 64), std::nullopt);
    EXPECT_EQ(cleanText(" ", 1, 128), std::nullopt);
}

TEST(LanPush, Vectors) {
    const auto cases = lan_vectors::section("push").elements();
    ASSERT_EQ(cases.size(), 4u);
    for (const auto& c : cases) {
        const auto kind = *c["kind"].string();
        const auto receiver = kind == "stream" ? Receiver::Stream : kind == "udp" ? Receiver::Udp : Receiver::Http;
        PushScheduler scheduler(pushRule(receiver));
        std::vector<std::int64_t> sent;
        for (const auto& tick : c["ticks"].elements()) {
            const auto t = *tick.at(0).truncated();
            if (scheduler.due(t, tick.at(1).isTrue())) {
                scheduler.sent(t);
                sent.push_back(t);
            }
        }
        std::vector<std::int64_t> expected;
        for (const auto& t : c["sent"].elements()) expected.push_back(*t.truncated());
        EXPECT_EQ(sent, expected) << kind;
    }
}

TEST(LanPush, ABusyReceiverSendsTheLatestWhenFree) {
    PushScheduler scheduler(pushRule(Receiver::Stream));
    ASSERT_TRUE(scheduler.due(0, false));
    scheduler.sent(0);
    EXPECT_TRUE(scheduler.due(500, true));  // busy: not sent
    EXPECT_TRUE(scheduler.due(1000, false)) << "the held change goes out at the next tick it can";
    scheduler.sent(1000);
    EXPECT_FALSE(scheduler.due(1500, false));
}

TEST(LanPush, HttpBackoff) {
    const auto expected = lan_vectors::section("httpBackoffMs").elements();
    ASSERT_EQ(expected.size(), 8u);
    for (std::size_t n = 0; n < expected.size(); ++n) EXPECT_EQ(httpBackoffMs(static_cast<int>(n)), *expected[n].truncated()) << n;
}

TEST(LanVectors, EverySectionIsChecked) {
    ASSERT_TRUE(lan_vectors::file().ok);
    const std::set<std::string> checked = {"_comment", "key",      "keyDisplay", "frames", "history", "datagrams",
                                           "firstLines", "requests", "hellos",     "websocket", "local",   "targets",
                                           "resolutions", "signatures", "settings", "push",      "keys",    "httpBackoffMs"};
    std::set<std::string> present;
    for (const auto& [name, _] : lan_vectors::file().root().members()) present.insert(name);
    EXPECT_EQ(present, checked);
    std::set<std::string> ws;
    for (const auto& [name, _] : lan_vectors::section("websocket").members()) ws.insert(name);
    EXPECT_EQ(ws, (std::set<std::string>{"accept", "clientFrames", "serverFrames", "closes"}));
}

TEST(LanPush, TimerJitterStillSendsEveryTick) {
    PushScheduler scheduler(pushRule(Receiver::Stream));
    std::int64_t t = 0;
    int sent = 0;
    for (const std::int64_t step : {0, 497, 503, 495, 505, 499}) {
        t += step;
        if (scheduler.due(t, true)) {
            scheduler.sent(t);
            ++sent;
        }
    }
    EXPECT_EQ(sent, 6);
}
