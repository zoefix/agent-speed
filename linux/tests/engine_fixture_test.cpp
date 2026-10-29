#include <gtest/gtest.h>

#include <set>

#include "agentspeed/engine.hpp"
#include "fixture_support.hpp"

using namespace agentspeed;
using namespace agentspeed::engine;

namespace {

constexpr double kTolerance = 1e-6;

Phase phaseFrom(std::string_view id) {
    if (id == "generating") return Phase::Generating;
    if (id == "tools") return Phase::Tools;
    return Phase::Idle;
}

std::vector<Response> responsesOf(const json::Value& list) {
    std::vector<Response> out;
    for (const auto& r : list.elements()) {
        Response response;
        response.key = *r["key"].string();
        response.agentId = *r["agentId"].string();
        response.source = *sourceFromId(*r["source"].string());
        response.model = fixtures::optString(r["model"]);
        response.startMs = fixtures::optInt(r["startMs"]);
        response.endMs = *fixtures::optInt(r["endMs"]);
        response.input = fixtures::optInt(r["input"]).value_or(0);
        response.output = fixtures::optInt(r["output"]).value_or(0);
        response.final = !r["final"].valid() || r["final"].isTrue();
        response.costMicroUsd = fixtures::optInt(r["costMicroUsd"]);
        if (r["requestCount"].valid()) response.requestCount = *fixtures::optInt(r["requestCount"]);
        out.push_back(response);
    }
    return out;
}

std::vector<Agent> agentsOf(const json::Value& list) {
    std::vector<Agent> out;
    for (const auto& a : list.elements()) {
        Agent agent;
        agent.id = *a["id"].string();
        agent.source = *sourceFromId(*a["source"].string());
        agent.model = fixtures::optString(a["model"]);
        agent.phase = phaseFrom(*a["phase"].string());
        agent.inFlightStartMs = fixtures::optInt(a["inFlightStartMs"]);
        agent.lastLineMs = fixtures::optInt(a["lastLineMs"]);
        agent.parentId = fixtures::optString(a["parentId"]);
        out.push_back(agent);
    }
    return out;
}

template <typename T>
std::vector<T> filtered(const std::vector<T>& items, const std::set<SourceKind>& sources) {
    std::vector<T> out;
    for (const auto& item : items) {
        if (sources.contains(item.source)) out.push_back(item);
    }
    return out;
}

}  // namespace

// spec/fixtures/engine-cases.json (METRICS 6.2-6.8, 9.5, 9.6).
TEST(EngineFixtures, MatchTheReference) {
    fixtures::JsonFile file(fixtures::fixtureDir() / "engine-cases.json");
    ASSERT_TRUE(file.ok);
    int queries = 0;
    for (const auto& c : file.root()["cases"].elements()) {
        SCOPED_TRACE(*c["name"].string());
        const auto responses = responsesOf(c["responses"]);
        const auto agents = agentsOf(c["agents"]);
        const std::int64_t todayStart = fixtures::optInt(c["todayStartMs"]).value_or(0);
        for (const auto& q : c["queries"].elements()) {
            const std::int64_t now = *fixtures::optInt(q["nowMs"]);
            SCOPED_TRACE(now);
            const auto live = liveRate(responses, agents, now);
            EXPECT_NEAR(live.output, *q["liveOutput"].number(), kTolerance);
            EXPECT_NEAR(live.total, *q["liveTotal"].number(), kTolerance);
            EXPECT_NEAR(live.estimatedShare, *q["estimatedShare"].number(), kTolerance);
            if (q["agentsOrder"].valid()) {
                const auto list = agentList(responses, agents, now);
                std::vector<std::string> order;
                for (const auto& id : q["agentsOrder"].elements()) order.push_back(*id.string());
                EXPECT_EQ(list.order, order);
                std::map<std::string, std::string> parents;
                for (const auto& [id, parent] : q["agentParents"].members()) parents[id] = *parent.string();
                EXPECT_EQ(list.parents, parents);
            }
            if (q["liveCost"].valid()) {
                const auto cost = liveCost(responses, agents, now);
                EXPECT_NEAR(cost.microUsdPerSecond, *q["liveCost"].number(), kTolerance);
                EXPECT_NEAR(cost.knownShare, *q["liveCostKnownShare"].number(), kTolerance);
            }
            if (q["todayCostMicroUsd"].valid()) {
                const auto stats = todayStats(responses, todayStart, now);
                EXPECT_EQ(stats.costMicroUsd, *fixtures::optInt(q["todayCostMicroUsd"]));
                EXPECT_EQ(stats.unpricedRequests, *fixtures::optInt(q["todayUnpricedRequests"]));
            }
            for (const auto& sub : q["subsets"].elements()) {
                std::set<SourceKind> chosen;
                for (const auto& s : sub["sources"].elements()) chosen.insert(*sourceFromId(*s.string()));
                const auto resp = filtered(responses, chosen);
                const auto ags = filtered(agents, chosen);
                if (sub["liveOutput"].valid()) {
                    EXPECT_NEAR(liveRate(resp, ags, now).output, *sub["liveOutput"].number(), kTolerance);
                }
                if (sub["peakTodayOutput"].valid()) {
                    EXPECT_NEAR(todayStats(resp, todayStart, now).peak, *sub["peakTodayOutput"].number(), kTolerance);
                }
                if (sub["liveCost"].valid()) {
                    EXPECT_NEAR(liveCost(resp, ags, now).microUsdPerSecond, *sub["liveCost"].number(), kTolerance);
                }
            }
            if (q["todayRequests"].valid()) {
                const auto stats = todayStats(responses, todayStart, now);
                EXPECT_EQ(stats.requests, *fixtures::optInt(q["todayRequests"]));
                EXPECT_EQ(stats.output, *fixtures::optInt(q["todayOutput"]));
                EXPECT_EQ(stats.input, *fixtures::optInt(q["todayInput"]));
                EXPECT_NEAR(stats.average.value_or(0), q["averageToday"].number().value_or(0), kTolerance);
                EXPECT_NEAR(stats.peak, *q["peakTodayOutput"].number(), kTolerance);
                EXPECT_EQ(stats.unfinalized, *fixtures::optInt(q["todayUnfinalized"]));
                for (const auto& [k, v] : q["historyMeasuredOutput"].members()) {
                    const auto it = stats.buckets.find(std::stoll(k));
                    EXPECT_NEAR(it == stats.buckets.end() ? 0.0 : it->second, *v.number(), kTolerance) << k;
                }
            }
            ++queries;
        }
    }
    EXPECT_EQ(queries, 31);
}
