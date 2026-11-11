#include <gtest/gtest.h>

#include <chrono>
#include <ctime>
#include <fstream>

#include "agentspeed/codex_parser.hpp"
#include "agentspeed/dedupe.hpp"
#include "agentspeed/monitor.hpp"
#include "agentspeed/quota/codex.hpp"
#include "agentspeed/rfc3339.hpp"
#include "quota_test_support.hpp"

// QUOTAS 3: every `codex` fixture case fed as JSONL lines through real Codex parsers into the collector, and the
// monitor's hook that installs the observer on every Codex parser (seeding included).
using namespace agentspeed;
using namespace agentspeed::quota;
using quota_test::section;

namespace {

struct NoSink : ResponseSink {
    void onResponse(const ParsedResponse&, bool) override {}
};

std::string sessionMeta(const std::string& timestamp, const std::string& extra = "") {
    return "{\"timestamp\":\"" + timestamp + "\",\"type\":\"session_meta\",\"payload\":{\"id\":\"t\",\"cwd\":\"/w\"" + extra + "}}";
}

std::string isoOf(std::int64_t ms) {
    const std::time_t seconds = static_cast<std::time_t>(ms / 1000);
    std::tm utc{};
    gmtime_r(&seconds, &utc);
    char text[32];
    std::strftime(text, sizeof text, "%Y-%m-%dT%H:%M:%SZ", &utc);
    return text;
}

std::string tokenCount(const std::string& timestamp, double used) {
    return "{\"timestamp\":\"" + timestamp +
           "\",\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":null,\"rate_limits\":{\"limit_id\":\"codex\","
           "\"primary\":{\"used_percent\":" +
           std::to_string(used) + ",\"window_minutes\":10080,\"resets_at\":1794897479},\"secondary\":null}}}";
}

void expectSnapshot(const std::optional<quota::Snapshot>& got, const json::Value& want) {
    if (want.isNull()) {
        EXPECT_FALSE(got.has_value());
        return;
    }
    ASSERT_TRUE(got.has_value());
    EXPECT_EQ(got->source, SourceKind::Codex);
    EXPECT_EQ(got->observedMs, *want["observedMs"].truncated());
    quota_test::expectWindows(got->windows, want["windows"]);
}

}  // namespace

// A `copied: true` line is fed to a forked rollout whose `session_meta` is an hour newer, so the timestamp rule of
// METRICS 3.5 makes it copied history, as a fork's copy of its parent's records is.
TEST(QuotaFixtures, CodexLinesThroughTheParser) {
    for (const auto& c : section("codex").elements()) {
        SCOPED_TRACE(*c["name"].string());
        Deduper deduper;
        NoSink sink;
        CodexCollector collector;
        CodexParser main("/s/rollout-main.jsonl", deduper);
        main.setRateLimitObserver(collector.observer());
        main.feed(sessionMeta("2020-01-01T00:00:00Z"), sink);
        for (const auto& line : c["lines"].elements()) {
            const auto text = fixtures::compactJson(line["line"]);
            if (line["copied"].isTrue()) {
                const auto ts = *parseTimestamp(*line["line"]["timestamp"].string());
                CodexParser fork("/s/rollout-fork.jsonl", deduper);
                fork.setRateLimitObserver(collector.observer());
                fork.feed(sessionMeta(isoOf(ts + 3'600'000)), sink);
                fork.feed(text, sink);
            } else {
                main.feed(text, sink);
            }
        }
        expectSnapshot(collector.snapshot(), c["expected"]);
    }
}

// A paginated sub-agent's inherited records (METRICS 3.5 ordinal rule) never reach the observer either.
TEST(QuotaCodex, InheritedOrdinalsAreCopiedHistory) {
    Deduper deduper;
    NoSink sink;
    CodexCollector collector;
    CodexParser parser("/s/subagents/rollout-sub.jsonl", deduper);
    parser.setRateLimitObserver(collector.observer());
    parser.feed(sessionMeta("2026-11-10T19:00:00Z", ",\"subagent_history_start_ordinal\":5"), sink);
    auto inherited = tokenCount("2026-11-10T19:10:00Z", 99);
    inherited.insert(1, "\"ordinal\":2,");
    parser.feed(inherited, sink);
    EXPECT_FALSE(collector.snapshot().has_value());
    auto own = tokenCount("2026-11-10T19:05:00Z", 12);
    own.insert(1, "\"ordinal\":6,");
    parser.feed(own, sink);
    ASSERT_TRUE(collector.snapshot().has_value());
    EXPECT_DOUBLE_EQ(collector.snapshot()->windows.at(0).usedPercent, 12);
}

TEST(QuotaCodex, WindowRules) {
    json::Document doc;
    ASSERT_TRUE(doc.parse(R"({"limit_id": 7, "primary": {"used_percent": 1, "window_minutes": 300}})"));
    EXPECT_TRUE(codexWindows(doc.root(), 0).empty());
    ASSERT_TRUE(doc.parse(R"({"primary": {"used_percent": true, "window_minutes": 300},
                              "secondary": {"used_percent": 4, "window_minutes": 300.9, "resets_at": 10.7}})"));
    const auto windows = codexWindows(doc.root(), 0);
    ASSERT_EQ(windows.size(), 1u);
    EXPECT_EQ(windows[0].kind, Kind::FiveHour);
    EXPECT_EQ(windows[0].resetsAtMs, 10'000);
    ASSERT_TRUE(doc.parse(R"({"limit_id": null, "primary": {"used_percent": 1, "window_minutes": 60},
                              "secondary": {"used_percent": 2, "window_minutes": 60}})"));
    const auto others = codexWindows(doc.root(), 0);
    ASSERT_EQ(others.size(), 1u);
    EXPECT_EQ(others[0].kind, Kind::Other);
    EXPECT_EQ(others[0].minutes, 60);
    EXPECT_EQ(others[0].resetsAtMs, std::nullopt);
}

TEST(QuotaCodex, DisabledCollectorKeepsNothing) {
    json::Document doc;
    ASSERT_TRUE(doc.parse(R"({"primary": {"used_percent": 5, "window_minutes": 10080, "resets_at": 1794897479}})"));
    CodexCollector collector;
    collector.observe(doc.root(), 1000);
    ASSERT_TRUE(collector.snapshot().has_value());
    const auto before = collector.version();
    collector.setEnabled(false);
    EXPECT_FALSE(collector.snapshot().has_value());
    EXPECT_GT(collector.version(), before);
    collector.observe(doc.root(), 2000);
    EXPECT_FALSE(collector.snapshot().has_value());
    collector.setEnabled(true);
    collector.observe(doc.root(), 3000);
    ASSERT_TRUE(collector.snapshot().has_value());
    EXPECT_EQ(collector.snapshot()->observedMs, 3000);
}

namespace {

MonitorConfig codexOnly(const std::string& sessions) {
    RootSettings s;
    s.codex = sessions;
    MonitorConfig config;
    config.locations = SourceLocations::resolve(s, agentspeed::processEnvironment(), "/nonexistent-home");
    config.enabled = {SourceKind::Codex};
    return config;
}

std::int64_t wallMs() {
    return std::chrono::duration_cast<std::chrono::milliseconds>(std::chrono::system_clock::now().time_since_epoch()).count();
}

void append(const quota_test::fs::path& file, const std::string& line) {
    std::ofstream out(file, std::ios::binary | std::ios::app);
    out << line << "\n";
}

}  // namespace

// Seeding reads the same lines, so the newest snapshot exists right after launch; live lines follow.
TEST(QuotaCodex, MonitorFeedsTheCollectorFromSeedingAndLiveLines) {
    quota_test::TempDir home;
    const auto sessions = home / "sessions";
    const auto rollout = sessions / "2026" / "11" / "10" / "rollout-2026-11-10T19-00-00-a.jsonl";
    quota_test::writeFile(rollout, "");
    append(rollout, sessionMeta("2026-11-10T19:00:00Z"));
    append(rollout, tokenCount("2026-11-10T19:01:00Z", 23));
    const auto now = wallMs();
    CodexCollector collector;
    Monitor monitor(codexOnly(sessions.string()));
    monitor.setRateLimitObserver(collector.observer());
    monitor.start(now, now - 3'600'000);
    ASSERT_TRUE(collector.snapshot().has_value());
    EXPECT_DOUBLE_EQ(collector.snapshot()->windows.at(0).usedPercent, 23);

    append(rollout, tokenCount("2026-11-10T19:02:00Z", 24));
    monitor.poll(now + 2000, now - 3'600'000);
    ASSERT_TRUE(collector.snapshot().has_value());
    EXPECT_DOUBLE_EQ(collector.snapshot()->windows.at(0).usedPercent, 24);
}

TEST(QuotaCodex, ObserverSetAfterStartReachesTrackedParsers) {
    quota_test::TempDir home;
    const auto sessions = home / "sessions";
    const auto rollout = sessions / "2026" / "11" / "10" / "rollout-2026-11-10T19-00-00-b.jsonl";
    quota_test::writeFile(rollout, "");
    append(rollout, sessionMeta("2026-11-10T19:00:00Z"));
    const auto now = wallMs();
    CodexCollector collector;
    Monitor monitor(codexOnly(sessions.string()));
    monitor.start(now, now - 3'600'000);
    monitor.setRateLimitObserver(collector.observer());
    append(rollout, tokenCount("2026-11-10T19:03:00Z", 31));
    monitor.poll(now + 2000, now - 3'600'000);
    ASSERT_TRUE(collector.snapshot().has_value());
    EXPECT_EQ(collector.snapshot()->observedMs, *parseTimestamp("2026-11-10T19:03:00Z"));
}
