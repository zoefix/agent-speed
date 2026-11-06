#include <gtest/gtest.h>

#include "remote_live_support.hpp"

// Live ingestion of a host's lines (REMOTE 7): seeding, live events, reconnects, disconnects, monitor restarts and the
// snapshot's hosts.
using namespace remote_live;

namespace {

struct Harness {
    explicit Harness(std::vector<remote::HostInfo> hosts = {{kA, "devbox"}}, std::set<SourceKind> sources = {SourceKind::Codex, SourceKind::ClaudeCode})
        : monitor(emptyConfig(std::move(sources))) {
        monitor.start(kNow, kDay);
        monitor.applyRemote({hostsOf(std::move(hosts))}, kNow);
    }
    void send(const std::vector<remote::FeedChange>& changes, std::int64_t now = kNow) { monitor.applyRemote(changes, now); }
    Snapshot snapshot(std::int64_t now = kNow) { return monitor.snapshot(now, kDay); }
    Monitor monitor;
};

}  // namespace

TEST(RemoteIngest, SeededLinesAreSilentAndLiveLinesAnnounce) {
    Harness h;
    h.send(seeding(kA));
    EXPECT_TRUE(h.monitor.takeCompletions().empty()) << "seeding never announces";
    auto snap = h.snapshot();
    EXPECT_EQ(snap.todayOutput, 800);
    ASSERT_NE(agentRow(snap, agentId(kA, "a")), nullptr);
    ASSERT_NE(agentRow(snap, agentId(kA, "b")), nullptr);
    EXPECT_EQ(agentRow(snap, agentId(kA, "b"))->parentId, agentId(kA, "a"));
    for (const auto& a : snap.agents) EXPECT_EQ(a.host, kA);
    EXPECT_EQ(h.monitor.parentOf(agentId(kA, "b")), agentId(kA, "a"));

    h.send({response(record("k3", kA, kNow - 2000, kNow - 100, 200), kA, true)}, kNow + 500);
    const auto completions = h.monitor.takeCompletions();
    ASSERT_EQ(completions.size(), 1u);
    EXPECT_EQ(completions[0].host, kA);
    EXPECT_EQ(completions[0].output, 200);
    snap = h.snapshot(kNow + 500);
    EXPECT_EQ(snap.todayOutput, 1000);
    EXPECT_GT(rowOf(snap, SourceKind::Codex).liveOutput, 0);
}

TEST(RemoteIngest, ALateLiveLineOutsideTheAgeRuleStaysSilent) {
    Harness h;
    h.send(seeding(kA));
    h.send({response(record("old", kA, kNow - 400'000, kNow - 300'000, 50), kA, true)});
    EXPECT_TRUE(h.monitor.takeCompletions().empty());
    EXPECT_EQ(h.snapshot().todayOutput, 850);
}

TEST(RemoteIngest, AReconnectSeedsAgainWithoutCountingTwice) {
    Harness h;
    h.send(seeding(kA));
    const auto first = h.snapshot();
    h.monitor.takeCompletions();

    // The connection drops; a new one seeds the same responses and only one agent; the mirror removes the other at ready.
    h.send({status(kA, remote::StatusCode::Unreachable), remote::feed::Idle{kA}, status(kA, remote::StatusCode::Connecting),
            status(kA, remote::StatusCode::Seeding)});
    h.send({agentChange(agent(kA, "a", SourceKind::Codex, Phase::Idle, std::nullopt, kNow - 200)),
            response(record("k1", kA, kNow - 60'000, kNow - 50'000, 500), kA, false),
            response(record("k2", kA, kNow - 40'000, kNow - 30'000, 300, "b", SourceKind::ClaudeCode), kA, false)},
           kNow + 500);
    EXPECT_TRUE(h.monitor.agents().contains(agentId(kA, "b"))) << "the old state stays until ready";
    auto snap = h.snapshot(kNow + 500);
    EXPECT_TRUE(h.monitor.takeCompletions().empty());
    EXPECT_EQ(snap.todayOutput, first.todayOutput);
    EXPECT_EQ(snap.todayRequests, first.todayRequests);
    EXPECT_DOUBLE_EQ(snap.peakToday, first.peakToday);

    h.send({remote::feed::AgentRemoved{agentId(kA, "b")}, remote::feed::Parents{kA, {}}, status(kA, remote::StatusCode::Connected)},
           kNow + 1000);
    snap = h.snapshot(kNow + 1000);
    EXPECT_TRUE(h.monitor.takeCompletions().empty()) << "no completion from a re-seed";
    EXPECT_FALSE(h.monitor.agents().contains(agentId(kA, "b")));
    EXPECT_TRUE(h.monitor.agents().contains(agentId(kA, "a")));
    EXPECT_EQ(snap.todayOutput, first.todayOutput);
    EXPECT_FALSE(h.monitor.parentOf(agentId(kA, "b")));
}

TEST(RemoteIngest, ADisconnectedHostsAgentsBecomeIdle) {
    Harness h;
    h.send(seeding(kA, true));
    const auto before = h.snapshot();
    EXPECT_GT(rowOf(before, SourceKind::Codex).liveEstimatedShare, 0) << "the in-flight estimate runs while connected";

    h.send({status(kA, remote::StatusCode::Unreachable), remote::feed::Idle{kA}});
    const auto& agent = h.monitor.agents().at(agentId(kA, "a"));
    EXPECT_EQ(agent.phase, Phase::Idle);
    EXPECT_FALSE(agent.inFlightStartMs);
    const auto after = h.snapshot(kNow + 500);
    EXPECT_EQ(rowOf(after, SourceKind::Codex).liveEstimatedShare, 0);
    EXPECT_EQ(after.todayOutput, before.todayOutput) << "its responses keep counting";
    EXPECT_FALSE(after.hosts[1].connected);
}

TEST(RemoteIngest, ANewMonitorTakesTheReplayAsSeeded) {
    Harness h;
    auto changes = seeding(kA);
    h.send(changes);
    const auto old = h.snapshot();

    // A restart: the new monitor receives everything as seeded, without a reconnect.
    Monitor restarted(emptyConfig());
    restarted.start(kNow, kDay);
    std::vector<remote::FeedChange> replay{hostsOf({{kA, "devbox"}})};
    replay.insert(replay.end(), changes.begin(), changes.end());
    replay.push_back(response(record("k3", kA, kNow - 2000, kNow - 100, 200), kA, false));
    restarted.applyRemote(replay, kNow);
    EXPECT_TRUE(restarted.takeCompletions().empty());
    auto snap = restarted.snapshot(kNow, kDay);
    EXPECT_EQ(snap.todayOutput, old.todayOutput + 200);
    EXPECT_EQ(snap.agents.size(), old.agents.size());
    EXPECT_EQ(restarted.parentOf(agentId(kA, "b")), agentId(kA, "a"));
    EXPECT_TRUE(snap.hosts[1].connected);

    restarted.applyRemote({response(record("k4", kA, kNow - 1000, kNow, 50), kA, true)}, kNow + 500);
    const auto completions = restarted.takeCompletions();
    ASSERT_EQ(completions.size(), 1u);
    EXPECT_EQ(completions[0].output, 50);
    EXPECT_EQ(restarted.snapshot(kNow + 500, kDay).todayOutput, old.todayOutput + 250);
}

TEST(RemoteIngest, RemovedHostsLeave) {
    Harness h({{kA, "devbox"}, {kB, "gpu"}});
    h.send(seeding(kA));
    h.send({response(record("b1", kB, kNow - 9000, kNow - 8000, 70), kB, false)});
    EXPECT_EQ(h.snapshot().todayOutput, 870);
    h.send({remote::feed::RemoveHost{kA}, hostsOf({{kB, "gpu"}})});
    const auto snap = h.snapshot(kNow + 500);
    EXPECT_EQ(snap.todayOutput, 70);
    for (const auto& a : snap.agents) EXPECT_NE(a.host, kA);
    EXPECT_FALSE(h.monitor.parentOf(agentId(kA, "b")));
    ASSERT_EQ(snap.hosts.size(), 2u);
    EXPECT_EQ(snap.hosts[1].id, kB);
}

TEST(RemoteIngest, DisabledSourcesStayOut) {
    Harness h({{kA, "devbox"}}, {SourceKind::Codex});
    h.send(seeding(kA));
    const auto snap = h.snapshot();
    EXPECT_EQ(snap.todayOutput, 500);
    for (const auto& a : snap.agents) EXPECT_EQ(a.source, SourceKind::Codex);
    for (const auto& cell : snap.hostSources) EXPECT_NE(cell.source, SourceKind::ClaudeCode);
}

TEST(RemoteIngest, TheSnapshotListsHostsAndCells) {
    Harness h({{kA, "devbox"}, {kB, "gpu"}});
    h.send(seeding(kA));
    h.send({status(kB, remote::StatusCode::Disabled)});
    const auto snap = h.snapshot();
    ASSERT_EQ(snap.hosts.size(), 3u);
    EXPECT_EQ(snap.hosts[0].id, kLocal);
    EXPECT_EQ(snap.hosts[1].id, kA);
    EXPECT_EQ(snap.hosts[2].id, kB);
    EXPECT_EQ(snap.hosts[0].name, kLocal);
    EXPECT_EQ(snap.hosts[1].name, "devbox");
    EXPECT_EQ(snap.hosts[2].name, "gpu");
    EXPECT_TRUE(snap.hosts[0].connected && snap.hosts[0].local());
    EXPECT_TRUE(snap.hosts[1].connected);
    EXPECT_FALSE(snap.hosts[2].connected);
    EXPECT_FALSE(snap.hosts[0].remote);
    ASSERT_TRUE(snap.hosts[1].remote);
    EXPECT_EQ(snap.hosts[1].remote->status.code, remote::StatusCode::Connected);
    EXPECT_EQ(snap.hosts[2].remote->status.code, remote::StatusCode::Disabled);
    EXPECT_EQ(snap.hosts[1].todayOutput, 800);
    EXPECT_EQ(snap.hosts[1].sessions, 2);
    EXPECT_TRUE(snap.hosts[1].listed());
    EXPECT_FALSE(snap.hosts[2].listed());
    EXPECT_EQ(agentRow(snap, agentId(kA, "a"))->host, kA);
    // Not detected on this computer, but on the connected host.
    EXPECT_TRUE(rowOf(snap, SourceKind::Codex).available);
    EXPECT_EQ(rowOf(snap, SourceKind::Codex).sessions, 1);
    const auto codex = std::find_if(snap.hostSources.begin(), snap.hostSources.end(),
                                    [](const HostSourceRow& c) { return c.host == kA && c.source == SourceKind::Codex; });
    ASSERT_NE(codex, snap.hostSources.end());
    EXPECT_TRUE(codex->available);
    EXPECT_EQ(codex->todayOutput, 500);
    EXPECT_EQ(codex->sessions, 1);
    for (const auto& row : snap.sources) {
        std::int64_t today = 0, requests = 0;
        double live = 0;
        for (const auto& cell : snap.hostSources) {
            if (cell.source != row.source) continue;
            today += cell.todayOutput;
            requests += cell.todayRequests;
            live += cell.liveOutput;
        }
        EXPECT_EQ(today, row.todayOutput) << sourceId(row.source);
        EXPECT_EQ(requests, row.todayRequests) << sourceId(row.source);
        EXPECT_NEAR(live, row.liveOutput, 1e-9);
    }

    // Without any configured host the snapshot has neither.
    Harness plain(std::vector<remote::HostInfo>{});
    const auto quiet = plain.snapshot();
    EXPECT_TRUE(quiet.hosts.empty());
    EXPECT_TRUE(quiet.hostSources.empty());
}

TEST(RemoteIngest, ToolCallsCountOnlyWhileTheWatchIsOn) {
    Harness h;
    h.send(seeding(kA));
    remote::RemoteTool tool;
    tool.hostId = kA;
    tool.agentId = agentId(kA, "a");
    tool.source = SourceKind::Codex;
    tool.action.callMs = kNow - 3000;
    tool.action.resultMs = kNow - 1000;
    h.send({remote::feed::Tool{tool}});
    EXPECT_EQ(h.snapshot().report.perf->calls, 0);
    h.monitor.setPerfWatch(true, 0, 0);
    h.send({remote::feed::Tool{tool}});
    EXPECT_EQ(h.snapshot().report.perf->calls, 1);
}

TEST(RemoteIngest, RemoteAgentsHaveTheirActionsAndAProjectFallback) {
    Harness h;
    auto a = agent(kA, "x");
    a.project.reset();
    work::Action action;
    action.callMs = kNow - 5000;
    action.resultMs = kNow - 4000;
    action.tool = "Bash";
    action.digest = "0123456789abcdef";
    a.actions = {action};
    h.send({status(kA, remote::StatusCode::Connected), agentChange(a)});
    const auto snap = h.snapshot();
    const auto* row = agentRow(snap, agentId(kA, "x"));
    ASSERT_NE(row, nullptr);
    EXPECT_EQ(row->project, "sessions");
    EXPECT_EQ(h.monitor.cwdOf(agentId(kA, "x")), "/home/u/proj");
    ASSERT_TRUE(row->perf);
}
