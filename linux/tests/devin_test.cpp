#include <gtest/gtest.h>
#include <unistd.h>

#include "agentspeed/devin.hpp"
#include "agentspeed/history_analyzer.hpp"
#include "agentspeed/monitor.hpp"
#include "agentspeed/price_book.hpp"
#include "agentspeed/tz.hpp"
#include "fixture_support.hpp"

using namespace agentspeed;
namespace fs = std::filesystem;

namespace {

constexpr std::int64_t kDayStart = 1792454400000;
constexpr std::int64_t kNow = 1792497700000;

std::string fixtureRoot() { return fsx::normalize((fixtures::fixtureDir() / "devin").string()); }

MonitorConfig config() {
    RootSettings settings;
    settings.devin = fixtureRoot();
    return MonitorConfig::fromEnvironment(settings, {SourceKind::Devin});
}

void expectResponse(const ParsedResponse& got, const json::Value& want) {
    SCOPED_TRACE(got.key);
    EXPECT_EQ(got.key, *want["key"].string());
    EXPECT_EQ(got.source, SourceKind::Devin);
    EXPECT_EQ(got.startMs, fixtures::optInt(want["startMs"]));
    EXPECT_EQ(got.endMs, fixtures::optInt(want["endMs"]));
    EXPECT_EQ(got.tokens.input, fixtures::optInt(want["input"]));
    EXPECT_EQ(got.tokens.cachedInput, fixtures::optInt(want["cachedInput"]));
    EXPECT_EQ(got.tokens.cacheWriteInput, fixtures::optInt(want["cacheWriteInput"]));
    EXPECT_EQ(got.tokens.output, fixtures::optInt(want["output"]));
    EXPECT_EQ(got.tokens.reasoning, 0);
    EXPECT_EQ(got.model, fixtures::optString(want["model"]));
    EXPECT_EQ(got.agentId, fsx::normalize((fixtures::fixtureDir() / *want["agentId"].string()).string()));
}

}  // namespace

TEST(Devin, SharedFixtureCombinesCLIAndDesktopOnce) {
    fixtures::JsonFile expected(fixtures::fixtureDir() / "devin-expected.json");
    Deduper deduper;
    devin::Source source(devin::resolveLocation(fixtureRoot(), processEnvironment(), "/nonexistent"), deduper);
    const auto update = source.seed(kDayStart);
    const auto& responses = expected.root()["responses"].elements();
    ASSERT_EQ(update.responses.size(), responses.size());
    for (std::size_t i = 0; i < responses.size(); ++i) expectResponse(update.responses[i].first, responses[i]);
    EXPECT_EQ(update.agents.size(), expected.root()["agents"].members().size());
    EXPECT_EQ(source.malformedLines(), 2);
    EXPECT_TRUE(source.refreshIfChanged().responses.empty());
    EXPECT_TRUE(source.refreshIfChanged().agents.empty());
    EXPECT_EQ(source.activeSessionCount(kNow + 100'000'000'000), 0);
}

TEST(Devin, LiveAndHistoryShareTotalsWithoutRestartAccumulation) {
    Monitor monitor(config());
    monitor.start(kNow, kDayStart);
    const auto snapshot = monitor.snapshot(kNow, kDayStart);
    EXPECT_EQ(snapshot.todayRequests, 7);
    EXPECT_EQ(snapshot.todayInput, 424);
    EXPECT_EQ(snapshot.todayOutput, 98);
    ASSERT_TRUE(snapshot.todayAverage);
    EXPECT_DOUBLE_EQ(*snapshot.todayAverage, 81.0 / 6.5);
    EXPECT_EQ(monitor.sessionCount(SourceKind::Devin, kDayStart), 6);
    const auto row = std::find_if(snapshot.sources.begin(), snapshot.sources.end(), [](const SourceRow& row) {
        return row.source == SourceKind::Devin;
    });
    ASSERT_NE(row, snapshot.sources.end());
    EXPECT_TRUE(row->available);

    char pattern[] = "/tmp/agentspeed-devin-history-XXXXXX";
    const fs::path temp = fs::path(mkdtemp(pattern));
    auto store = history::Store::open((temp / "history.sqlite").string());
    history::Analyzer first(*store, std::make_shared<pricing::PriceBook>());
    const auto locations = config().locations;
    const auto report = first.run(locations, {SourceKind::Devin}, TimeZone::utc(), "UTC");
    EXPECT_EQ(report.filesUnreadable, 0);
    const auto days = first.days();
    ASSERT_EQ(days.size(), 1);
    EXPECT_EQ(days[0].key.source, SourceKind::Devin);
    EXPECT_EQ(days[0].totals.requests, 7);
    EXPECT_EQ(days[0].totals.input, 424);
    EXPECT_EQ(days[0].totals.output, 98);
    EXPECT_EQ(days[0].totals.measuredOutput, 81);
    EXPECT_EQ(days[0].totals.measuredMs, 6500);
    history::Analyzer second(*store, std::make_shared<pricing::PriceBook>());
    const auto again = second.run(locations, {SourceKind::Devin}, TimeZone::utc(), "UTC");
    EXPECT_EQ(again.filesAnalyzed, 0);
    EXPECT_EQ(again.sessionsAnalyzed, 0);
    fs::remove_all(temp);
}
