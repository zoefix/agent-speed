#include <gtest/gtest.h>

#include <algorithm>
#include <map>
#include <set>

#include "agentspeed/devin_source.hpp"
#include "agentspeed/history_analyzer.hpp"
#include "agentspeed/history_store.hpp"
#include "agentspeed/price_book.hpp"
#include "agentspeed/tz.hpp"
#include "devin_test_support.hpp"
#include "fixture_support.hpp"

// The shared Devin CLI fixtures (spec/fixtures/devin, devin-expected.json, METRICS 4.17) through the live source and
// the daily history, like devin_reference.py.
using namespace agentspeed;
namespace fs = std::filesystem;

namespace {

class DevinFixture : public ::testing::TestWithParam<std::string> {
protected:
    void SetUp() override {
        fs::copy(fixtures::fixtureDir() / "devin", temp_.path / "devin", fs::copy_options::recursive);
        fs::create_directories(temp_.path / "history");
        ASSERT_TRUE(expected_.ok);
    }

    json::Value scenario(const std::string& name) const { return expected_.root()["scenarios"][name]; }
    std::string pathOf(const std::string& relative) const { return temp_.file(relative); }
    std::string relative(const std::string& path) const { return fs::path(path).lexically_relative(temp_.path).string(); }
    std::string agentRelative(const std::string& agentId) const {
        const auto hash = agentId.rfind('#');
        return relative(agentId.substr(0, hash)) + agentId.substr(hash);
    }
    std::vector<std::string> locations(const json::Value& s) const {
        std::vector<std::string> out;
        for (const auto& l : s["locations"].elements()) out.push_back(pathOf(*l.string()));
        return out;
    }
    std::vector<devin::Root> roots(const json::Value& s) const {
        std::vector<devin::Root> out;
        for (const auto& l : locations(s)) out.push_back(devin::Root::location(l));
        return out;
    }
    SourceLocations sourceLocations(const json::Value& s) const {
        LocationList list;
        for (const auto& l : locations(s)) list.push_back(DataLocation::at(l));
        LocationLists lists;
        lists.set(RootFamily::Devin, list);
        return SourceLocations::resolve(lists, [](const std::string&) -> std::optional<std::string> { return std::nullopt; },
                                        "/nonexistent-home");
    }

    history::Report run(history::Analyzer& analyzer, const json::Value& s) const {
        return analyzer.run(sourceLocations(s), {SourceKind::Devin}, TimeZone::utc(), "UTC");
    }

    void expectDays(const history::Analyzer& analyzer, const json::Value& s) const {
        const std::set<SourceKind> only{SourceKind::Devin};
        const auto days = analyzer.days(&only);
        const auto wanted = s["history"].members();
        ASSERT_EQ(days.size(), wanted.size());
        for (std::size_t i = 0; i < days.size(); ++i) {
            const auto& [day, want] = wanted[i];
            SCOPED_TRACE(day);
            const auto& t = days[i].totals;
            EXPECT_EQ(days[i].key.day, day);
            EXPECT_EQ(t.requests, *want["requests"].truncated());
            EXPECT_EQ(t.input, *want["input"].truncated());
            EXPECT_EQ(t.cachedInput, *want["cachedInput"].truncated());
            EXPECT_EQ(t.cacheWriteInput, *want["cacheWriteInput"].truncated());
            EXPECT_EQ(t.output, *want["output"].truncated());
            EXPECT_EQ(t.reasoning, *want["reasoning"].truncated());
            EXPECT_EQ(t.measuredOutput, *want["measuredOutput"].truncated());
            EXPECT_EQ(t.measuredMs, *want["measuredMs"].truncated());
            EXPECT_EQ(t.agents, *want["agents"].truncated());
            std::map<std::string, std::pair<std::int64_t, std::int64_t>> wantModels, gotModels;
            for (const auto& [model, m] : want["models"].members()) wantModels[model] = {*m["requests"].truncated(), *m["output"].truncated()};
            for (const auto& [model, m] : t.models) gotModels[model] = {m.requests, m.output};
            EXPECT_EQ(gotModels, wantModels);
        }
    }

    // Reading never creates a `-wal`, `-shm` or journal next to a fixture database.
    std::vector<std::string> createdFiles() const {
        std::vector<std::string> out;
        for (const auto& file : fixtures::listFiles(temp_.path / "devin")) {
            if (file.ends_with("-wal") || file.ends_with("-shm") || file.ends_with("-journal")) out.push_back(file);
        }
        return out;
    }

    devin_test::TempDir temp_;
    fixtures::JsonFile expected_{fixtures::fixtureDir() / "devin-expected.json"};
};

void expectResponse(const ParsedResponse& r, const json::Value& want, const std::string& agent) {
    SCOPED_TRACE(r.key);
    EXPECT_EQ(agent, *want["agentId"].string());
    EXPECT_EQ(r.startMs, fixtures::optInt(want["startMs"]));
    EXPECT_EQ(r.endMs, *want["endMs"].truncated());
    EXPECT_EQ(r.firstMs, fixtures::optInt(want["firstMs"]));
    EXPECT_EQ(r.tokens.input, *want["input"].truncated());
    EXPECT_EQ(r.tokens.cachedInput, *want["cachedInput"].truncated());
    EXPECT_EQ(r.tokens.cacheWriteInput, *want["cacheWriteInput"].truncated());
    EXPECT_EQ(r.tokens.cacheWrite1hInput, *want["cacheWrite1hInput"].truncated());
    EXPECT_EQ(r.tokens.output, *want["output"].truncated());
    EXPECT_EQ(r.tokens.reasoning, *want["reasoning"].truncated());
    EXPECT_EQ(r.model, fixtures::optString(want["model"]));
    EXPECT_EQ(r.provider, fixtures::optString(want["provider"]));
    EXPECT_EQ(r.final, want["final"].isTrue());
    EXPECT_EQ(r.fast, want["fast"].isTrue());
    EXPECT_EQ(r.requestCount, 1);
    EXPECT_EQ(r.source, SourceKind::Devin);
}

}  // namespace

TEST_P(DevinFixture, TheLiveSourceReadsEveryScenario) {
    const auto s = scenario(GetParam());
    Deduper deduper;
    devin::Source source(roots(s), deduper);
    // A start of day before every fixture row: the startup window covers them all.
    const auto updates = source.seed(0);
    EXPECT_TRUE(source.available());
    std::map<std::string, ParsedResponse> latest;
    std::vector<std::string> order;
    std::set<std::string> updated;
    for (const auto& update : updates) {
        EXPECT_TRUE(update.seeded || update.responses.empty()) << "startup responses raise no live event";
        for (const auto& [response, completion] : update.responses) {
            if (!latest.contains(response.key)) order.push_back(response.key);
            latest[response.key] = response;
        }
        for (const auto& [id, agent] : update.agents) updated.insert(id);
    }
    std::map<std::string, std::vector<ParsedResponse>> byAgent;
    for (const auto& key : order) byAgent[agentRelative(latest[key].agentId)].push_back(latest[key]);
    std::set<std::string> wantAgents, gotAgents;
    for (const auto& [name, agent] : s["agents"].members()) wantAgents.insert(name);
    for (const auto& [name, responses] : byAgent) gotAgents.insert(name);
    EXPECT_EQ(gotAgents, wantAgents);
    std::map<std::string, devin::Agent> states;
    for (const auto& agent : source.agents()) states[agentRelative(agent.id)] = agent;
    for (const auto& [name, want] : s["agents"].members()) {
        SCOPED_TRACE(name);
        const auto wanted = want["responses"].elements();
        const auto& records = byAgent[name];
        ASSERT_EQ(records.size(), wanted.size());
        for (std::size_t i = 0; i < records.size(); ++i) {
            EXPECT_EQ(records[i].key, *wanted[i]["key"].string());
            expectResponse(records[i], wanted[i], agentRelative(records[i].agentId));
        }
        ASSERT_TRUE(states.contains(name));
        const auto& state = states[name];
        EXPECT_EQ(state.project, *want["project"].string());
        EXPECT_EQ(state.cwdPath, fixtures::optString(want["cwdPath"]));
        EXPECT_EQ(state.model, fixtures::optString(want["model"]));
        EXPECT_EQ(phaseId(state.phase), *want["phase"].string());
        EXPECT_EQ(state.inFlightStartMs, fixtures::optInt(want["inFlightStartMs"]));
        EXPECT_EQ(state.lastLineMs, fixtures::optInt(want["lastLineMs"]));
        EXPECT_EQ(state.malformedRows, *want["malformedLines"].truncated());
        EXPECT_TRUE(updated.contains(state.id));
    }
    EXPECT_EQ(createdFiles(), std::vector<std::string>{});
}

TEST_P(DevinFixture, TheHistoryMatchesTheScenarioDays) {
    const auto s = scenario(GetParam());
    auto store = history::Store::open(temp_.file("history/history.sqlite"));
    history::Analyzer analyzer(*store, std::make_shared<pricing::PriceBook>());
    const auto first = run(analyzer, s);
    expectDays(analyzer, s);
    EXPECT_GT(first.sessionsAnalyzed, 0);
    const auto second = run(analyzer, s);
    EXPECT_EQ(second.filesAnalyzed, 0);
    EXPECT_EQ(second.sessionsAnalyzed, 0) << "an unchanged session is not read again";
    EXPECT_EQ(createdFiles(), std::vector<std::string>{});
}

INSTANTIATE_TEST_SUITE_P(Scenarios, DevinFixture, ::testing::Values("one", "two"));

class DevinFixtureLists : public DevinFixture {};

TEST_F(DevinFixtureLists, LocationsResolveToTheCliDatabaseOnly) {
    for (const auto& [location, want] : expected_.root()["resolve"].members()) {
        SCOPED_TRACE(location);
        const auto database = devin::Root::location(pathOf(location)).databaseNow();
        EXPECT_EQ(database ? std::optional<std::string>(relative(*database)) : std::nullopt, fixtures::optString(want));
    }
    for (const char* name : {"one", "two"}) {
        const auto s = scenario(name);
        std::vector<std::string> want, got;
        for (const auto& d : s["databases"].elements()) want.push_back(*d.string());
        for (const auto& d : devin::databases(roots(s))) got.push_back(relative(d));
        EXPECT_EQ(got, want) << name;
    }
}

// The second database added to a stored history: its copy of a session adds nothing, its own session counts.
TEST_F(DevinFixtureLists, AddingASecondDatabaseAddsOnlyItsOwnSessions) {
    auto store = history::Store::open(temp_.file("history/history.sqlite"));
    history::Analyzer analyzer(*store, std::make_shared<pricing::PriceBook>());
    run(analyzer, scenario("one"));
    expectDays(analyzer, scenario("one"));
    const auto added = run(analyzer, scenario("two"));
    EXPECT_EQ(added.sessionsAnalyzed, 1);
    expectDays(analyzer, scenario("two"));
}
