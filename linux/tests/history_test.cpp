#include <gtest/gtest.h>
#include <fcntl.h>
#include <sys/stat.h>
#include <unistd.h>

#include <filesystem>
#include <fstream>

#include "agentspeed/fs_util.hpp"
#include "agentspeed/history_analyzer.hpp"
#include "agentspeed/price_book.hpp"
#include "agentspeed/tz.hpp"
#include "fixture_support.hpp"

using namespace agentspeed;
using namespace agentspeed::history;
namespace fs = std::filesystem;

namespace {

fs::path makeTemp() {
    char pattern[] = "/tmp/agentspeed-history-XXXXXX";
    return fs::path(mkdtemp(pattern));
}

void setMtime(const fs::path& path, std::int64_t ms) {
    struct timespec times[2];
    times[0].tv_sec = times[1].tv_sec = static_cast<time_t>(ms / 1000);
    times[0].tv_nsec = times[1].tv_nsec = static_cast<long>((ms % 1000) * 1'000'000);
    utimensat(AT_FDCWD, path.c_str(), times, 0);
}

// spec/fixtures copied, with each file's modification time from parser-expected.json `mtimes`.
std::string copyFixtures(const fs::path& destination) {
    fs::copy(fixtures::fixtureDir(), destination, fs::copy_options::recursive | fs::copy_options::copy_symlinks);
    fixtures::JsonFile expected(fixtures::fixtureDir() / "parser-expected.json");
    for (const auto& [relative, ms] : expected.root()["mtimes"].members()) setMtime(destination / relative, *fixtures::optInt(ms));
    return fsx::normalize(destination.string());
}

Environment only(std::string name, std::string value) {
    return [name, value](const std::string& asked) -> std::optional<std::string> {
        if (asked == name) return value;
        return std::nullopt;
    };
}

SourceLocations locationsIn(const std::string& copy) {
    SourceLocations l;
    l.codexRoot = copy + "/codex/sessions";
    l.claudeRoot = copy + "/claude/projects";
    l.dshRoot = copy + "/dsh/sessions";
    l.openCodeRoot = copy + "/opencode";
    l.geminiRoots = {copy + "/gemini/tmp"};
    l.ompRoots = paths::ompRoots(only("PI_CONFIG_DIR", "omp"), copy);
    l.antigravityStores = paths::antigravityStores(copy + "/antigravity", std::nullopt);
    l.workBuddyRoot = paths::workBuddyProjectsOf(copy + "/workbuddy/.workbuddy");
    l.workBuddyAiRoot = paths::workBuddyProjectsOf(copy + "/workbuddy/.workbuddy-ai");
    l.piRoots = paths::piRoots(only("PI_CODING_AGENT_DIR", copy + "/pi/agent"), copy);
    l.openClawRoots = paths::openClawStateFolders([](const std::string&) { return std::nullopt; }, copy + "/openclaw");
    l.hermesRoot = copy + "/hermes/home";
    l.droidRoot = droid::defaultRoot(copy + "/droid");
    return l;
}

// METRICS 6.11 Reading: a day's record of a (source, model) is the sum over files; by day, source order, model bytes.
std::vector<perf::DayRecord> summed(const std::vector<perf::DayRecord>& records) {
    std::map<std::tuple<std::string, SourceKind, std::string>, perf::Record> sums;
    for (const auto& r : records) sums[{r.day, r.source, r.model}].add(r.record);
    std::vector<perf::DayRecord> out;
    for (const auto& [key, record] : sums) out.push_back({std::get<0>(key), std::get<1>(key), std::get<2>(key), record});
    return out;
}

void expectPerf(const std::vector<perf::DayRecord>& got, const json::Value& want) {
    const auto rows = want.elements();
    ASSERT_EQ(got.size(), rows.size());
    for (std::size_t i = 0; i < rows.size(); ++i) {
        const auto& g = got[i];
        const auto& w = rows[i];
        SCOPED_TRACE(*w["day"].string() + " " + *w["source"].string() + " " + *w["model"].string());
        EXPECT_EQ(g.day, *w["day"].string());
        EXPECT_EQ(sourceId(g.source), *w["source"].string());
        EXPECT_EQ(g.model, *w["model"].string());
        const auto& r = g.record;
        EXPECT_EQ(r.n, *fixtures::optInt(w["n"]));
        EXPECT_EQ(r.outputSum, *fixtures::optInt(w["outputSum"]));
        EXPECT_EQ(r.durationMsSum, *fixtures::optInt(w["durationMsSum"]));
        for (std::size_t k = 0; k < r.latencyBins.size(); ++k) EXPECT_EQ(r.latencyBins[k], *fixtures::optInt(w["latencyBins"].at(static_cast<std::uint32_t>(k)))) << k;
        EXPECT_EQ(r.reasonN, *fixtures::optInt(w["reasonN"]));
        EXPECT_EQ(r.reasonOutput, *fixtures::optInt(w["reasonOutput"]));
        EXPECT_EQ(r.reasonDurationMsSum, *fixtures::optInt(w["reasonDurationMsSum"]));
        EXPECT_EQ(r.cacheN, *fixtures::optInt(w["cacheN"]));
        EXPECT_EQ(r.cacheInput, *fixtures::optInt(w["cacheInput"]));
        EXPECT_EQ(r.cacheCached, *fixtures::optInt(w["cacheCached"]));
        EXPECT_EQ(r.toolN, *fixtures::optInt(w["toolN"]));
        EXPECT_NEAR(r.toolLnSum, *w["toolLnSum"].number(), 1e-9);
        EXPECT_EQ(r.toolFailed, *fixtures::optInt(w["toolFailed"]));
        EXPECT_EQ(r.activeMs, *fixtures::optInt(w["activeMs"]));
    }
}

void expectOptional(const std::optional<std::int64_t>& got, const json::Value& want, const char* what) {
    SCOPED_TRACE(what);
    if (want.isNull()) {
        EXPECT_FALSE(got.has_value());
    } else {
        EXPECT_EQ(got, fixtures::optInt(want));
    }
}

}  // namespace

TEST(History, OverAllFixturesMatchesTheExpectation) {
    const auto temp = makeTemp();
    const auto copy = copyFixtures(temp / "fixtures");
    fixtures::JsonFile expected(fixtures::fixtureDir() / "history-expected.json");
    ASSERT_TRUE(expected.ok);
    std::vector<std::shared_ptr<const pricing::PriceTable>> tables;
    for (const auto& path : expected.root()["prices"]["tables"].elements()) {
        tables.push_back(pricing::PriceTable::load(fixtures::readText(fixtures::fixtureDir() / *path.string())));
    }
    std::map<std::string, int> bindings;
    for (const auto& [day, version] : expected.root()["prices"]["bindings"].members()) {
        bindings[day] = static_cast<int>(*fixtures::optInt(version));
    }
    fixtures::JsonFile parsers(fixtures::fixtureDir() / "parser-expected.json");
    const auto hermesZone = TimeZone::load(*parsers.root()["hermes"]["timeZone"].string());
    ASSERT_TRUE(hermesZone);
    auto store = Store::open((temp / "store" / "history.sqlite").string());
    Analyzer analyzer(*store, std::make_shared<pricing::PriceBook>(tables, bindings), hermesZone);
    auto enabled = std::set<SourceKind>{};
    for (int k = 0; k <= static_cast<int>(SourceKind::Droid); ++k) enabled.insert(static_cast<SourceKind>(k));
    const auto report = analyzer.run(locationsIn(copy), enabled, TimeZone::utc(), "UTC");
    EXPECT_FALSE(report.cancelled);
    EXPECT_EQ(report.filesUnreadable, 0);
    EXPECT_EQ(report.sessionsAnalyzed, 2);

    const auto days = analyzer.days();
    const auto rows = expected.root()["days"].elements();
    std::vector<std::string> gotKeys, wantKeys;
    for (const auto& d : days) gotKeys.push_back(d.key.day + " " + std::string(sourceId(d.key.source)));
    for (const auto& r : rows) wantKeys.push_back(*r["date"].string() + " " + *r["source"].string());
    ASSERT_EQ(gotKeys, wantKeys);
    for (std::size_t i = 0; i < rows.size(); ++i) {
        const auto& got = days[i];
        const auto& want = rows[i];
        SCOPED_TRACE(wantKeys[i]);
        const auto& t = got.totals;
        EXPECT_EQ(t.requests, *fixtures::optInt(want["requests"]));
        EXPECT_EQ(t.input, *fixtures::optInt(want["input"]));
        EXPECT_EQ(t.cachedInput, *fixtures::optInt(want["cachedInput"]));
        EXPECT_EQ(t.cacheWriteInput, *fixtures::optInt(want["cacheWriteInput"]));
        EXPECT_EQ(t.output, *fixtures::optInt(want["output"]));
        EXPECT_EQ(t.reasoning, *fixtures::optInt(want["reasoning"]));
        EXPECT_EQ(t.measuredOutput, *fixtures::optInt(want["measuredOutput"]));
        EXPECT_EQ(t.measuredMs, *fixtures::optInt(want["measuredMs"]));
        EXPECT_EQ(t.unfinalized, *fixtures::optInt(want["unfinalized"]));
        EXPECT_EQ(t.agents, *fixtures::optInt(want["agents"]));
        expectOptional(t.costMicroUsd, want["costMicroUsd"], "costMicroUsd");
        expectOptional(t.unpricedRequests, want["unpricedRequests"], "unpricedRequests");
        const auto version = got.priceVersion ? std::optional<std::int64_t>(*got.priceVersion) : std::nullopt;
        expectOptional(version, want["priceVersion"], "priceVersion");
        std::map<std::string, json::Value> models;
        for (const auto& [name, m] : want["models"].members()) models[name] = m;
        ASSERT_EQ(t.models.size(), models.size());
        for (const auto& [name, m] : t.models) {
            SCOPED_TRACE("model " + name);
            ASSERT_TRUE(models.contains(name));
            const auto& w = models[name];
            EXPECT_EQ(m.requests, *fixtures::optInt(w["requests"]));
            EXPECT_EQ(m.output, *fixtures::optInt(w["output"]));
            EXPECT_EQ(m.measuredOutput, *fixtures::optInt(w["measuredOutput"]));
            EXPECT_EQ(m.measuredMs, *fixtures::optInt(w["measuredMs"]));
            expectOptional(m.costMicroUsd, w["costMicroUsd"], "model costMicroUsd");
            expectOptional(m.unpricedRequests, w["unpricedRequests"], "model unpricedRequests");
        }
        const auto unpriced = want["unpriced"].elements();
        ASSERT_EQ(t.unpriced.size(), unpriced.size());
        std::size_t k = 0;
        for (const auto& [fact, n] : t.unpriced) {
            EXPECT_EQ(fact.model, *unpriced[k]["model"].string());
            EXPECT_EQ(fact.provider, fixtures::optString(unpriced[k]["provider"]));
            EXPECT_EQ(fact.fast, unpriced[k]["fast"].isTrue());
            EXPECT_EQ(n, *fixtures::optInt(unpriced[k]["requests"]));
            ++k;
        }
    }

    expectPerf(summed(analyzer.perfRecords()), expected.root()["perf"]);
    // EXECUTION-MODES history: every kind's rows (the unknown bucket included) add up to the day's totals.
    std::int64_t knownEffort = 0;
    for (const auto& d : days) {
        std::map<ModeKind, std::int64_t> requests, output;
        for (const auto& row : modeBreakdown(d.totals)) {
            requests[row.key.kind] += row.totals.requests;
            output[row.key.kind] += row.totals.output;
            if (row.key.kind == ModeKind::Effort && row.key.mode != "unknown") knownEffort += row.totals.requests;
        }
        for (const auto& [kind, n] : requests) {
            EXPECT_EQ(n, d.totals.requests) << d.key.day << " " << modeKindName(kind);
            EXPECT_EQ(output[kind], d.totals.output) << d.key.day << " " << modeKindName(kind);
        }
    }
    EXPECT_GT(knownEffort, 0);

    // Unchanged files are skipped, and a reopened store gives the same days and performance records.
    const auto again = analyzer.run(locationsIn(copy), enabled, TimeZone::utc(), "UTC");
    EXPECT_EQ(again.filesPlanned, 0);
    store.reset();
    auto reopened = Store::open((temp / "store" / "history.sqlite").string());
    Analyzer second(*reopened, std::make_shared<pricing::PriceBook>(tables, bindings), hermesZone);
    const auto reread = second.days();
    ASSERT_EQ(reread.size(), days.size());
    for (std::size_t i = 0; i < days.size(); ++i) {
        EXPECT_EQ(reread[i].totals.output, days[i].totals.output);
        EXPECT_EQ(reread[i].totals.costMicroUsd, days[i].totals.costMicroUsd);
        EXPECT_EQ(reread[i].totals.agents, days[i].totals.agents);
    }
    expectPerf(summed(second.perfRecords()), expected.root()["perf"]);
    for (std::size_t i = 0; i < days.size(); ++i) EXPECT_EQ(reread[i].totals.modes.size(), days[i].totals.modes.size()) << days[i].key.day;
    fs::remove_all(temp);
}
