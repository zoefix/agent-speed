#include <gtest/gtest.h>

#include "agentspeed/engine.hpp"
#include "remote_live_support.hpp"

// The engine's host dimension (REMOTE 7, DESIGN 13): cross-host deduplication, (host, source) cells, host subset peaks
// and the per-host limits.
using namespace remote_live;

namespace {

engine::Response engineResponse(const std::string& key, const std::string& host, std::int64_t start, std::int64_t end, std::int64_t output,
                                bool final = true, std::optional<std::int64_t> cost = std::nullopt, const std::string& model = "gpt-5.5") {
    engine::Response r;
    r.key = key;
    r.agentId = agentId(host, key);
    r.host = host;
    r.model = model;
    r.startMs = start;
    r.endMs = end;
    r.input = 100;
    r.output = output;
    r.reasoning = output / 4;
    r.final = final;
    r.costMicroUsd = cost;
    return r;
}

// Host a: measured, unmeasured and an in-flight agent; host b: the references; this computer: one priced response.
std::vector<remote::FeedChange> busy() {
    std::vector<remote::FeedChange> out{hostsOf({{kA, "devbox"}, {kB, "gpu"}}), status(kA, remote::StatusCode::Connected),
                                        status(kB, remote::StatusCode::Connected)};
    out.push_back(agentChange(agent(kA, "a", SourceKind::Codex, Phase::Generating, kNow - 1500, kNow - 100)));
    auto thinking = record("a1", kA, kNow - 2800, kNow - 200, 260);
    thinking.tokens.reasoning = 100;
    out.push_back(response(thinking, kA, false));
    out.push_back(response(record("a2", kA, kNow - 1500, kNow - 100, 140, "c", SourceKind::ClaudeCode), kA, false));
    out.push_back(response(record("a3", kA, kNow - 2600, kNow - 900, 90, "d", SourceKind::Codex, false), kA, false));
    out.push_back(response(record("b1", kB, kNow - 2900, kNow - 300, 75, "e"), kB, false));
    out.push_back(response(record("old", kB, kDay - 20'000, kDay - 10'000, 5000, "e"), kB, false));
    return out;
}

}  // namespace

TEST(RemoteEngine, TheFirstArrivalOfAKeyWinsAcrossHosts) {
    const auto day = std::int64_t{1792454400000};
    const auto now = day + 30'000'000;
    Monitor local(fixtureConfig({SourceKind::Codex}));
    local.start(now, day);
    const auto held = local.responses();
    ASSERT_FALSE(held.empty());
    const auto key = held.front().key;
    const auto before = local.snapshot(now, day);
    auto copy = held.front();
    copy.agentId = agentId(kA, "copy");
    copy.tokens.output = 999'999;
    local.applyRemote({hostsOf({{kA, "devbox"}}), response(copy, kA, true)}, now);
    EXPECT_TRUE(local.takeCompletions().empty());
    EXPECT_EQ(local.snapshot(now, day).todayOutput, before.todayOutput);
    for (const auto& r : local.responses()) {
        if (r.key == key) {
            EXPECT_NE(r.tokens.output, 999'999);
        }
    }

    // The host's response came first: the local copy read later is ignored, and so is a third host's.
    Monitor remoteFirst(fixtureConfig({SourceKind::Codex}));
    copy.tokens.output = 7;
    remoteFirst.applyRemote({hostsOf({{kA, "devbox"}, {kB, "gpu"}}), response(copy, kA, false)}, now);
    remoteFirst.start(now, day);
    auto other = copy;
    other.agentId = agentId(kB, "copy");
    other.tokens.output = 555;
    remoteFirst.applyRemote({response(other, kB, true)}, now);
    for (const auto& r : remoteFirst.responses()) {
        if (r.key == key) {
            EXPECT_EQ(r.tokens.output, 7);
            EXPECT_EQ(r.agentId, agentId(kA, "copy"));
        }
    }

    // The holder's own updates still apply.
    copy.tokens.output = 8;
    remoteFirst.applyRemote({response(copy, kA, true, false)}, now);
    for (const auto& r : remoteFirst.responses()) {
        if (r.key == key) {
            EXPECT_EQ(r.tokens.output, 8);
        }
    }
}

TEST(RemoteEngine, HostPartsAddUpToTheWholeSet) {
    std::vector<engine::Response> responses{
        engineResponse("l1", kLocal, kNow - 2500, kNow - 500, 400, true, 3000),
        engineResponse("a1", kA, kNow - 2800, kNow - 200, 260, true, 1200),
        engineResponse("a3", kA, kNow - 2600, kNow - 900, 90, false),
        engineResponse("b1", kB, kNow - 2900, kNow - 300, 75),
        engineResponse("b2", kB, kNow - 20'000, kNow - 15'000, 300, true, 800, "other"),
    };
    engine::Agent inFlight;
    inFlight.id = agentId(kA, "x");
    inFlight.host = kA;
    inFlight.model = "other";
    inFlight.phase = Phase::Generating;
    inFlight.inFlightStartMs = kNow - 1500;
    inFlight.lastLineMs = kNow - 100;
    const std::vector<engine::Agent> agents{inFlight};

    const auto rate = engine::liveRate(responses, agents, kNow);
    const auto cost = engine::liveCost(responses, agents, kNow);
    const auto thinking = engine::thinking(responses, agents, kNow);
    const auto parts = engine::liveByHost(responses, agents, kNow);
    EXPECT_EQ(parts.size(), 3u);
    double output = 0, total = 0, estimated = 0, costRate = 0, known = 0, weight = 0, reasoning = 0;
    for (const auto& [host, part] : parts) {
        output += part.rate.output;
        total += part.rate.total;
        estimated += part.estimatedOutput;
        costRate += part.cost.microUsdPerSecond;
        known += part.cost.knownShare * part.rate.output;
        weight += part.thinking.weight;
        reasoning += part.thinking.reasoning;
    }
    EXPECT_NEAR(output, rate.output, 1e-9);
    EXPECT_NEAR(total, rate.total, 1e-9);
    EXPECT_NEAR(estimated, rate.output * rate.estimatedShare, 1e-9);
    EXPECT_NEAR(costRate, cost.microUsdPerSecond, 1e-9);
    EXPECT_NEAR(known, cost.knownShare * rate.output, 1e-9);
    EXPECT_NEAR(weight, thinking.total.weight, 1e-9);
    EXPECT_NEAR(reasoning, thinking.total.reasoning, 1e-9);
    // The in-flight agent's estimate takes host b's reference ("other") and belongs to host a.
    EXPECT_GT(parts.at(kA).estimatedOutput, 0);
    EXPECT_EQ(parts.at(kB).estimatedOutput, 0);

    // One host: exactly the whole set's values.
    std::vector<engine::Response> single{responses[0]};
    const auto alone = engine::liveByHost(single, {}, kNow);
    EXPECT_EQ(alone.at(kLocal).rate.output, engine::liveRate(single, {}, kNow).output);
    EXPECT_EQ(alone.at(kLocal).cost.microUsdPerSecond, engine::liveCost(single, {}, kNow).microUsdPerSecond);
}

TEST(RemoteEngine, CellsAddUpToTheSourceRows) {
    Monitor monitor(emptyConfig());
    monitor.start(kNow, kDay);
    monitor.applyRemote(busy(), kNow);
    const auto snap = monitor.snapshot(kNow, kDay);
    std::set<std::string> hosts;
    for (const auto& cell : snap.hostSources) hosts.insert(cell.host);
    EXPECT_EQ(hosts, (std::set<std::string>{kA, kB}));
    for (const auto kind : {SourceKind::Codex, SourceKind::ClaudeCode}) {
        SCOPED_TRACE(std::string(sourceId(kind)));
        const auto& row = rowOf(snap, kind);
        double live = 0, total = 0, estimated = 0, cost = 0, known = 0;
        std::int64_t today = 0, requests = 0, todayCost = 0, unpriced = 0;
        for (const auto& cell : snap.hostSources) {
            if (cell.source != kind) continue;
            live += cell.liveOutput;
            total += cell.liveTotal;
            estimated += cell.liveEstimated;
            cost += cell.liveCost;
            known += cell.liveCostKnownShare * cell.liveOutput;
            today += cell.todayOutput;
            requests += cell.todayRequests;
            todayCost += cell.todayCostMicroUsd;
            unpriced += cell.unpricedRequests;
        }
        EXPECT_NEAR(live, row.liveOutput, 1e-9);
        EXPECT_NEAR(total, row.liveTotal, 1e-9);
        EXPECT_NEAR(estimated, row.liveOutput * row.liveEstimatedShare, 1e-9);
        EXPECT_NEAR(cost, row.liveCost, 1e-9);
        EXPECT_NEAR(known, row.liveCostKnownShare * row.liveOutput, 1e-9);
        EXPECT_EQ(today, row.todayOutput);
        EXPECT_EQ(requests, row.todayRequests);
        EXPECT_EQ(todayCost, row.todayCostMicroUsd);
        EXPECT_EQ(unpriced, row.unpricedRequests);
    }
    const auto cellOf = [&](const std::string& host, SourceKind kind) {
        return std::find_if(snap.hostSources.begin(), snap.hostSources.end(), [&](const HostSourceRow& c) { return c.host == host && c.source == kind; });
    };
    ASSERT_NE(cellOf(kA, SourceKind::Codex), snap.hostSources.end());
    EXPECT_GT(cellOf(kA, SourceKind::Codex)->liveEstimated, 0) << "the unmeasured a3 and the in-flight agent";
    ASSERT_NE(cellOf(kB, SourceKind::Codex), snap.hostSources.end());
    EXPECT_EQ(cellOf(kB, SourceKind::Codex)->todayRequests, 1) << "yesterday's is not today's";
    // The hosts' rows sum their cells.
    for (const auto& host : snap.hosts) {
        std::int64_t today = 0;
        for (const auto& cell : snap.hostSources) today += cell.host == host.id ? cell.todayOutput : 0;
        EXPECT_EQ(host.todayOutput, today) << host.id;
    }
}

TEST(RemoteEngine, WithoutHostsTheSnapshotIsUnchanged) {
    auto changes = busy();
    Monitor tracked(emptyConfig());
    tracked.start(kNow, kDay);
    tracked.applyRemote(changes, kNow);
    changes.erase(changes.begin(), changes.begin() + 3);
    Monitor plain(emptyConfig());
    plain.start(kNow, kDay);
    plain.applyRemote(changes, kNow);
    const auto a = plain.snapshot(kNow, kDay);
    const auto b = tracked.snapshot(kNow, kDay);
    EXPECT_TRUE(a.hosts.empty() && a.hostSources.empty());
    EXPECT_FALSE(b.hosts.empty() || b.hostSources.empty());
    EXPECT_EQ(a.liveOutput, b.liveOutput);
    EXPECT_EQ(a.liveTotal, b.liveTotal);
    EXPECT_EQ(a.estimatedShare, b.estimatedShare);
    EXPECT_EQ(a.todayOutput, b.todayOutput);
    EXPECT_EQ(a.peakToday, b.peakToday);
    EXPECT_EQ(a.history, b.history);
    ASSERT_EQ(a.agents.size(), b.agents.size());
    for (std::size_t k = 0; k < a.agents.size(); ++k) EXPECT_EQ(a.agents[k].id, b.agents[k].id);
    for (std::size_t k = 0; k < a.sources.size(); ++k) {
        EXPECT_EQ(a.sources[k].liveOutput, b.sources[k].liveOutput);
        EXPECT_EQ(a.sources[k].liveCost, b.sources[k].liveCost);
        EXPECT_EQ(a.sources[k].todayOutput, b.sources[k].todayOutput);
    }
}

TEST(RemoteEngine, HostSubsetPeaksCountTheirCellsOnly) {
    const std::vector<std::pair<ParsedResponse, std::string>> records{
        {record("a1", kA, kDay + 11'000, kDay + 14'000, 600), kA},
        {record("a2", kA, kDay + 11'000, kDay + 13'000, 800, "c", SourceKind::ClaudeCode), kA},
        {record("b1", kB, kDay + 50'000, kDay + 52'000, 2000), kB},
        {record("b2", kB, kDay + 12'000, kDay + 13'000, 900), kB},
    };
    const std::vector<SourceKind> codex{SourceKind::Codex};
    const std::vector<std::pair<hosts::Subset, std::vector<std::string>>> cases{
        {{codex, std::nullopt}, {"a1", "b1", "b2"}},
        {{codex, std::vector<std::string>{kA}}, {"a1"}},
        {{std::nullopt, std::vector<std::string>{kA}}, {"a1", "a2"}},
        {{codex, std::vector<std::string>{kA, kB}}, {"a1", "b1", "b2"}},
        {{codex, std::vector<std::string>{kLocal, kB}}, {"b1", "b2"}},
        {{std::nullopt, std::vector<std::string>{"unknown"}}, {}},
    };
    const auto feed = [&](Monitor& m, const std::vector<std::string>& keys) {
        std::vector<remote::FeedChange> changes{hostsOf({{kA, "devbox"}, {kB, "gpu"}})};
        for (const auto& [r, host] : records) {
            if (std::find(keys.begin(), keys.end(), r.key) != keys.end()) changes.push_back(response(r, host, false));
        }
        m.applyRemote(changes, kNow);
    };
    Monitor all(emptyConfig());
    all.start(kNow, kDay);
    std::vector<hosts::Subset> subsets;
    for (const auto& c : cases) subsets.push_back(c.first);
    all.setPeakSubsets(subsets);
    feed(all, {"a1", "a2", "b1", "b2"});
    const auto snap = all.snapshot(kNow, kDay);
    ASSERT_EQ(snap.peakSets.size(), cases.size());
    EXPECT_GT(snap.peakSets[0], snap.peakSets[1]) << "host b's codex adds to the all-hosts peak";
    EXPECT_GT(snap.peakSets[1], 0);
    for (std::size_t k = 0; k < cases.size(); ++k) {
        SCOPED_TRACE(k);
        Monitor alone(emptyConfig());
        alone.start(kNow, kDay);
        feed(alone, cases[k].second);
        EXPECT_NEAR(snap.peakSets[k], alone.snapshot(kNow, kDay).peakToday, 1e-6);
    }
}

TEST(RemoteEngine, AHostAboveItsLimitLosesItsOldestResponses) {
    Monitor monitor(emptyConfig());
    monitor.start(kNow, kDay);
    monitor.setRemoteResponseLimit(10);
    std::vector<remote::FeedChange> changes{hostsOf({{kA, "devbox"}, {kB, "gpu"}}), response(record("b", kB, kDay + 1000, kDay + 2000, 1), kB, false)};
    for (int k = 0; k < 15; ++k) {
        const std::int64_t end = kDay + 10'000 + k * 1000;
        changes.push_back(response(record("a" + std::to_string(k), kA, end - 500, end, 10), kA, false));
    }
    monitor.applyRemote(changes, kNow);
    std::set<std::string> keys;
    for (const auto& r : monitor.responses()) keys.insert(r.key);
    EXPECT_TRUE(keys.contains("b"));
    keys.erase("b");
    EXPECT_LE(keys.size(), 10u);
    EXPECT_GE(keys.size(), 9u);
    EXPECT_TRUE(keys.contains("a14"));
    EXPECT_FALSE(keys.contains("a0"));
    EXPECT_EQ(monitor.snapshot(kNow, kDay).todayRequests, static_cast<std::int64_t>(keys.size()) + 1);
}

TEST(RemoteEngine, IdleAndRemovalTouchOnlyTheirHost) {
    Monitor monitor(emptyConfig());
    monitor.start(kNow, kDay);
    monitor.applyRemote(busy(), kNow);
    monitor.applyRemote({agentChange(agent(kB, "g", SourceKind::Codex, Phase::Generating, kNow - 800, kNow - 100)), remote::feed::Idle{kA}}, kNow);
    EXPECT_EQ(monitor.agents().at(agentId(kA, "a")).phase, Phase::Idle);
    EXPECT_FALSE(monitor.agents().at(agentId(kA, "a")).inFlightStartMs);
    EXPECT_EQ(monitor.agents().at(agentId(kB, "g")).phase, Phase::Generating);
    monitor.applyRemote({remote::feed::RemoveHost{kA}, hostsOf({{kB, "gpu"}})}, kNow);
    EXPECT_FALSE(monitor.agents().contains(agentId(kA, "a")));
    for (const auto& r : monitor.responses()) EXPECT_NE(r.agentId.rfind(remote::agentPrefix(kA), 0), 0u);
    const auto snap = monitor.snapshot(kNow, kDay);
    for (const auto& cell : snap.hostSources) EXPECT_NE(cell.host, kA);
}

TEST(RemoteEngine, ScoreSetsWithHostsCountTheirAgents) {
    Monitor monitor(emptyConfig());
    monitor.start(kNow, kDay);
    monitor.setScoreSubsets({{}, {std::nullopt, std::vector<std::string>{kB}}, {std::nullopt, std::vector<std::string>{kLocal}}});
    monitor.applyRemote(busy(), kNow);
    const auto snap = monitor.snapshot(kNow, kDay);
    ASSERT_EQ(snap.scoreSets.size(), 3u);
    EXPECT_TRUE(snap.scoreSets[0].generating);
    EXPECT_FALSE(snap.scoreSets[1].generating) << "host a's agent generates, host b's does not";
    EXPECT_EQ(snap.scoreSets[1].hosts, std::vector<std::string>{kB});
    EXPECT_FALSE(snap.scoreSets[2].generating);
    EXPECT_GT(snap.scoreSets[0].liveThinking, 0) << "host a's a1 thinks";
    EXPECT_EQ(snap.scoreSets[1].liveThinking, 0);
    EXPECT_EQ(snap.scoreSets[2].liveThinking, 0);
}
