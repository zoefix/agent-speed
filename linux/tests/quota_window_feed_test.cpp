#include <gtest/gtest.h>
#include <unistd.h>

#include <algorithm>
#include <chrono>
#include <filesystem>

#include "agentspeed/quota/cli_cache.hpp"
#include "agentspeed/quota/estimate/estimator.hpp"
#include "agentspeed/quota/estimate/window_feed.hpp"
#include "quota_test_support.hpp"

// QUOTAS 5.6 Estimate: Claude Code's joined snapshot reaches the estimate window by window, each at its own time,
// through the `feed` steps of spec/fixtures/quota-cases.json and through the app's estimator.
namespace {

namespace fs = std::filesystem;
namespace est = agentspeed::quota::estimate;
using namespace agentspeed::quota;
using quota_test::section;

constexpr std::int64_t kMin = 60'000, kHour = 60 * kMin, kDay = 24 * kHour;

est::SnapshotEvent eventOf(const Snapshot& snapshot, const std::string& machine = std::string(est::kLocal)) {
    est::SnapshotEvent e;
    e.source = std::string(agentspeed::sourceId(snapshot.source));
    e.machine = machine;
    e.observedMs = snapshot.observedMs;
    e.windows = snapshot.windows;
    return e;
}

Snapshot joinedOf(std::vector<Window> bridge, std::int64_t bridgeMs, std::vector<Window> cache, std::int64_t cacheMs) {
    return *cli::joined(Snapshot{agentspeed::SourceKind::ClaudeCode, bridgeMs, std::move(bridge)},
                        Snapshot{agentspeed::SourceKind::ClaudeCode, cacheMs, std::move(cache)});
}

std::int64_t nowMs() {
    return std::chrono::duration_cast<std::chrono::milliseconds>(std::chrono::system_clock::now().time_since_epoch()).count();
}

class Folder {
public:
    Folder() : path_(fs::temp_directory_path() / ("as-window-feed-" + std::to_string(::getpid()))) { fs::remove_all(path_); }
    ~Folder() { fs::remove_all(path_); }
    std::string quota() const { return (path_ / "data/quota").string(); }

private:
    fs::path path_;
};

const est::Entry* entryOf(const std::vector<est::Entry>& entries, const char* kind, std::optional<std::string> name = std::nullopt) {
    const auto it = std::find_if(entries.begin(), entries.end(), [&](const est::Entry& e) { return e.kind == kind && e.name == name; });
    return it == entries.end() ? nullptr : &*it;
}

}  // namespace

TEST(QuotaWindowFeed, StepsOfTheFixtures) {
    est::WindowFeed feed;
    const auto steps = section("claudeCodeCache")["feed"].elements();
    ASSERT_GE(steps.size(), 6u);
    for (std::size_t i = 0; i < steps.size(); ++i) {
        SCOPED_TRACE("step " + std::to_string(i));
        const auto snapshot = quota_test::snapshotOf(steps[i]["snapshot"]);
        ASSERT_TRUE(snapshot);
        const auto got = feed.take(eventOf(*snapshot));
        const auto want = steps[i]["expected"].elements();
        ASSERT_EQ(got.size(), want.size());
        for (std::size_t g = 0; g < got.size(); ++g) {
            EXPECT_EQ(got[g].source, "claudeCode");
            EXPECT_EQ(got[g].machine, est::kLocal);
            EXPECT_EQ(got[g].observedMs, *want[g]["observedMs"].truncated());
            quota_test::expectWindows(got[g].windows, want[g]["windows"]);
        }
    }
}

TEST(QuotaWindowFeed, AnOlderCacheValueAfterANewerBridgeValueIsSkipped) {
    est::WindowFeed feed;
    const std::int64_t t = 1794338473000, r5 = t + 2 * kHour;
    const auto bridge = std::vector{makeWindow(Kind::FiveHour, 53, r5)};
    auto first = feed.take(eventOf(Snapshot{agentspeed::SourceKind::ClaudeCode, t, bridge}));
    ASSERT_EQ(first.size(), 1u);
    // The cache, fetched 20 s before the bridge's value, already sees the next period: its own time is older.
    const auto next = joinedOf(bridge, t, {makeWindow(Kind::FiveHour, 2, r5 + 5 * kHour)}, t - 20'000);
    ASSERT_EQ(next.windows.front().observedMs, t - 20'000);
    EXPECT_TRUE(feed.take(eventOf(next)).empty());
    // The same values again: nothing.
    EXPECT_TRUE(feed.take(eventOf(Snapshot{agentspeed::SourceKind::ClaudeCode, t, bridge})).empty());
}

TEST(QuotaWindowFeed, AFableOnlyChangeGoesThoughTheNewestTimeStays) {
    est::WindowFeed feed;
    const std::int64_t tb = 1794338473000, r5 = tb + 2 * kHour, r7 = tb + 3 * kDay;
    const auto bridge = std::vector{makeWindow(Kind::FiveHour, 52, r5), makeWindow(Kind::SevenDay, 80, r7)};
    const auto first = joinedOf(bridge, tb, {makeWindow(Kind::ModelWeek, 90, r7, "Fable")}, tb - 2 * kHour);
    ASSERT_EQ(feed.take(eventOf(first)).size(), 2u);
    const auto second = joinedOf(bridge, tb, {makeWindow(Kind::ModelWeek, 95, r7, "Fable")}, tb - 30 * kMin);
    ASSERT_EQ(second.observedMs, first.observedMs);
    const auto got = feed.take(eventOf(second));
    ASSERT_EQ(got.size(), 1u);
    EXPECT_EQ(got[0].observedMs, tb - 30 * kMin);
    ASSERT_EQ(got[0].windows.size(), 1u);
    EXPECT_EQ(got[0].windows[0].name, "Fable");
    EXPECT_DOUBLE_EQ(got[0].windows[0].usedPercent, 95);
    EXPECT_FALSE(got[0].windows[0].observedMs);
}

TEST(QuotaWindowFeed, EachMachineAndSourceHasItsOwnWindows) {
    est::WindowFeed feed;
    const Snapshot s{agentspeed::SourceKind::ClaudeCode, 1794338473000, {makeWindow(Kind::FiveHour, 52, 1794344400000)}};
    EXPECT_EQ(feed.take(eventOf(s)).size(), 1u);
    EXPECT_EQ(feed.take(eventOf(s, "host-1")).size(), 1u);
    EXPECT_TRUE(feed.take(eventOf(s, "host-1")).empty());
    auto other = eventOf(s);
    other.source = "codex";
    EXPECT_EQ(feed.take(other).size(), 1u);
    EXPECT_TRUE(est::feedsByWindow("claudeCode"));
    EXPECT_FALSE(est::feedsByWindow("codex"));
    EXPECT_FALSE(est::feedsByWindow("claudeDesktop"));
}

// Through the estimator, as a host's quota lines reach it: each window's observation at its own time, an older value
// of a window never after a newer one.
TEST(QuotaWindowFeed, TheEstimatorTakesClaudeCodeWindowByWindow) {
    Folder folder;
    est::Estimator e(folder.quota());
    ASSERT_TRUE(e.available());
    const auto now = nowMs();
    const auto tb = now - 10 * kMin, r5 = now + 2 * kHour, r7 = now + 3 * kDay;
    const auto bridge = std::vector{makeWindow(Kind::FiveHour, 52, r5), makeWindow(Kind::SevenDay, 80, r7)};
    const auto line = [&](const Snapshot& s) {
        auto event = eventOf(s, "host-cc");
        e.addSnapshot(std::move(event));
    };
    line(joinedOf(bridge, tb, {makeWindow(Kind::ModelWeek, 90, r7, "Fable")}, tb - 2 * kHour));
    // Fable alone changes; the snapshot's newest time stays the bridge's.
    line(joinedOf(bridge, tb, {makeWindow(Kind::ModelWeek, 95, r7, "Fable")}, tb - 30 * kMin));
    // An older cache value after it is skipped.
    line(joinedOf(bridge, tb, {makeWindow(Kind::ModelWeek, 70, r7, "Fable")}, tb - kHour));

    const auto shown = e.build(now, {{"host-cc", "claudeCode"}});
    ASSERT_TRUE(shown.contains("claudeCode@host-cc"));
    const auto& entries = shown.at("claudeCode@host-cc");
    const auto* fable = entryOf(entries, "modelWeek", "Fable");
    ASSERT_TRUE(fable);
    EXPECT_EQ(fable->usedPercent, 95);
    EXPECT_EQ(fable->percentAgeMs, now - (tb - 30 * kMin));
    const auto* five = entryOf(entries, "fiveHour");
    ASSERT_TRUE(five);
    EXPECT_EQ(five->usedPercent, 52);
    EXPECT_EQ(five->percentAgeMs, now - tb);
}
