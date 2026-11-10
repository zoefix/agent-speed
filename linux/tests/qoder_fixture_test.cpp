#include <gtest/gtest.h>

#include <algorithm>
#include <map>
#include <set>

#include "agentspeed/history_analyzer.hpp"
#include "agentspeed/history_store.hpp"
#include "agentspeed/locations.hpp"
#include "agentspeed/parents.hpp"
#include "agentspeed/price_book.hpp"
#include "agentspeed/qoder_source.hpp"
#include "agentspeed/tz.hpp"
#include "fixture_support.hpp"
#include "qoder_test_support.hpp"

// The shared Qoder IDE fixtures (spec/fixtures/qoder, qoder-expected.json, METRICS 4.18) through the live source and the
// daily history, like qoder_reference.py, and the roots of both Qoder sources.
using namespace agentspeed;
namespace fs = std::filesystem;

namespace {

Environment environmentOf(std::map<std::string, std::string> values) {
    return [values](const std::string& name) -> std::optional<std::string> {
        const auto it = values.find(name);
        return it == values.end() ? std::nullopt : std::optional<std::string>(it->second);
    };
}

class QoderFixture : public ::testing::TestWithParam<std::string> {
protected:
    void SetUp() override {
        fs::copy(fixtures::fixtureDir() / "qoder", temp_.path / "qoder", fs::copy_options::recursive);
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
    // `@auto` reads the scenario's home as the macOS home: on Linux, XDG_CONFIG_HOME = <home>/Library/Application Support.
    Environment environment(const json::Value& s) const {
        if (const auto home = fixtures::optString(s["home"])) {
            return environmentOf({{"XDG_CONFIG_HOME", pathOf(*home + "/Library/Application Support")}});
        }
        return environmentOf({});
    }
    SourceLocations sourceLocations(const json::Value& s) const {
        LocationList list;
        for (const auto& l : s["locations"].elements()) {
            list.push_back(*l.string() == "@auto" ? DataLocation::automaticEntry() : DataLocation::at(pathOf(*l.string())));
        }
        LocationLists lists;
        lists.set(RootFamily::Qoder, list);
        return SourceLocations::resolve(lists, environment(s), "/nonexistent-home");
    }

    history::Report run(history::Analyzer& analyzer, const json::Value& s) const {
        return analyzer.run(sourceLocations(s), {SourceKind::Qoder}, TimeZone::utc(), "UTC");
    }

    void expectDays(const history::Analyzer& analyzer, const json::Value& s) const {
        const std::set<SourceKind> only{SourceKind::Qoder};
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

    qoder_test::TempDir temp_;
    fixtures::JsonFile expected_{fixtures::fixtureDir() / "qoder-expected.json"};
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
    EXPECT_EQ(r.source, SourceKind::Qoder);
}

}  // namespace

TEST_P(QoderFixture, TheLiveSourceReadsEveryScenario) {
    const auto s = scenario(GetParam());
    Deduper deduper;
    const auto locations = sourceLocations(s);
    qoder::Source source(locations.qoderRoots, deduper);
    std::vector<std::string> databases;
    for (const auto& d : qoder::databases(locations.qoderRoots)) databases.push_back(relative(d));
    std::vector<std::string> wantDatabases;
    for (const auto& d : s["databases"].elements()) wantDatabases.push_back(*d.string());
    EXPECT_EQ(databases, wantDatabases);

    // Without a start of day, before every fixture row: the startup window covers them all, as the history reads them.
    const auto todayStart = fixtures::optInt(s["todayStartMs"]).value_or(0);
    const auto updates = source.seed(todayStart);
    EXPECT_TRUE(source.available());
    std::vector<std::string> unsupported, wantUnsupported;
    for (const auto& reader : source.readers()) {
        if (reader->status() == qoder::Status::Unsupported) unsupported.push_back(relative(reader->path()));
    }
    for (const auto& d : s["unsupported"].elements()) wantUnsupported.push_back(*d.string());
    EXPECT_EQ(unsupported, wantUnsupported);
    EXPECT_EQ(source.unsupported(), !wantUnsupported.empty());

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

    // Parent links as the monitor resolves them (METRICS 6.7): the parent session when it is a tracked agent.
    std::vector<AgentLink> links;
    std::map<std::string, qoder::Agent> states;
    for (const auto& agent : source.agents()) {
        states[agentRelative(agent.id)] = agent;
        links.push_back({agent.id, LinkKind::QoderSession, agent.id, agent.parentAgentId, std::nullopt, 0});
    }
    const auto parents = parent_links::resolve(links);
    std::set<std::string> wantAgents, gotAgents;
    for (const auto& [name, agent] : s["agents"].members()) wantAgents.insert(name);
    for (const auto& [name, state] : states) gotAgents.insert(name);
    EXPECT_EQ(gotAgents, wantAgents);
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
        EXPECT_EQ(state.isSubagent, want["isSubagent"].isTrue());
        const auto parent = parents.find(state.id);
        EXPECT_EQ(parent == parents.end() ? std::nullopt : std::optional<std::string>(agentRelative(parent->second)),
                  fixtures::optString(want["parentId"]));
        EXPECT_EQ(phaseId(state.phase), *want["phase"].string());
        EXPECT_EQ(state.inFlightStartMs, fixtures::optInt(want["inFlightStartMs"]));
        EXPECT_EQ(state.lastLineMs, fixtures::optInt(want["lastLineMs"]));
        EXPECT_EQ(state.malformedRows, *want["malformedLines"].truncated());
        EXPECT_TRUE(updated.contains(state.id));
    }
    EXPECT_EQ(qoder_test::sideFiles(temp_.path / "qoder"), std::vector<std::string>{});
}

INSTANTIATE_TEST_SUITE_P(Scenarios, QoderFixture, ::testing::Values("one", "two", "startup"));

// The scenarios read completely, as the history reads them.
class QoderHistoryFixture : public QoderFixture {};

TEST_P(QoderHistoryFixture, TheHistoryMatchesTheScenarioDays) {
    const auto s = scenario(GetParam());
    auto store = history::Store::open(temp_.file("history/history.sqlite"));
    history::Analyzer analyzer(*store, std::make_shared<pricing::PriceBook>());
    const auto first = run(analyzer, s);
    expectDays(analyzer, s);
    EXPECT_GT(first.sessionsAnalyzed, 0);
    const auto second = run(analyzer, s);
    EXPECT_EQ(second.filesAnalyzed, 0);
    EXPECT_EQ(second.sessionsAnalyzed, 0) << "an unchanged session is not read again";

    // Each session's identity: its row count and its highest rowid (METRICS 4.18 History).
    history::QoderPlanner planner;
    std::map<std::string, std::pair<std::int64_t, std::int64_t>> got, want;
    for (const auto& session : planner.plan(sourceLocations(s).qoderRoots, [](const auto&, auto, auto) { return false; })) {
        got[agentRelative(session.key)] = {session.size, session.modified};
    }
    for (const auto& [id, identity] : s["sessions"].members()) want[id] = {*identity["rows"].truncated(), *identity["highestRowid"].truncated()};
    EXPECT_EQ(got, want);
    EXPECT_EQ(qoder_test::sideFiles(temp_.path / "qoder"), std::vector<std::string>{});
}

INSTANTIATE_TEST_SUITE_P(Scenarios, QoderHistoryFixture, ::testing::Values("one", "two"));

class QoderFixtureLists : public QoderFixture {};

TEST_F(QoderFixtureLists, LocationsResolveToTheIdeDatabaseOnly) {
    for (const auto& [location, want] : expected_.root()["resolve"].members()) {
        SCOPED_TRACE(location);
        const auto database = qoder::Root::location(pathOf(location)).databaseNow();
        EXPECT_EQ(database ? std::optional<std::string>(relative(*database)) : std::nullopt, fixtures::optString(want));
    }
}

// The second scenario adds databases to a stored history: copied sessions add nothing, their own sessions count.
TEST_F(QoderFixtureLists, AddingDatabasesAddsOnlyTheirOwnSessions) {
    auto store = history::Store::open(temp_.file("history/history.sqlite"));
    history::Analyzer analyzer(*store, std::make_shared<pricing::PriceBook>());
    run(analyzer, scenario("one"));
    expectDays(analyzer, scenario("one"));
    const auto added = run(analyzer, scenario("two"));
    EXPECT_EQ(added.sessionsAnalyzed, 2);
    expectDays(analyzer, scenario("two"));
}

// The automatic entries of both sources on Linux, and Qoder CLI's user locations.
TEST(QoderRoots, TheAutomaticRootsFollowTheEnvironment) {
    fixtures::JsonFile expected(fixtures::fixtureDir() / "qoder-expected.json");
    ASSERT_TRUE(expected.ok);
    int checked = 0;
    for (const auto& c : expected.root()["automatic"].elements()) {
        if (!c["platform"].equals("linux")) continue;
        std::map<std::string, std::string> env;
        for (const auto& [name, value] : c["env"].members()) env[name] = *value.string();
        std::vector<std::string> want;
        for (const auto& d : c["databases"].elements()) want.push_back(*d.string());
        EXPECT_EQ(qoder::automaticDatabases(environmentOf(env), *c["home"].string()), want);
        ++checked;
    }
    const auto cli = expected.root()["cli"];
    for (const auto& c : cli["automatic"].elements()) {
        if (!c["platform"].equals("linux")) continue;
        std::map<std::string, std::string> env;
        for (const auto& [name, value] : c["env"].members()) env[name] = *value.string();
        std::vector<std::string> want;
        for (const auto& r : c["roots"].elements()) want.push_back(*r.string());
        EXPECT_EQ(qoder::cliRoots(environmentOf(env), *c["home"].string()), want);
        ++checked;
    }
    EXPECT_GE(checked, 5);
    for (const auto& [location, want] : cli["locations"].members()) {
        EXPECT_EQ(qoder::cliProjectsOf(location), *want.string()) << location;
        EXPECT_EQ(userRoot(location, RootFamily::QoderCli), *want.string()) << location;
    }
}
