#include <gtest/gtest.h>

#include "agentspeed/remote_feed.hpp"
#include "remote_live_support.hpp"

// The mirror of each host and the feed between the hub's thread and the monitor (REMOTE 7 Reconnect, Disconnected,
// Monitor restart), driven by the events a connection reports.
namespace {

using namespace agentspeed;
using namespace agentspeed::remote;
using namespace remote_live;

HostEvent statusEvent(const std::string& host, int connection, StatusCode code) {
    HostStatus s;
    s.status = Status::of(code);
    return {host, connection, s};
}

HostEvent message(const std::string& host, int connection, RelayMessage m, bool seeded) {
    return {host, connection, HostEvent::Message{std::move(m), seeded}};
}

RemoteResponse remoteResponse(const std::string& key, std::int64_t end, std::int64_t output, const std::string& agentName = "a") {
    return {kA, record(key, kA, end - 2000, end, output, agentName), true, "proj"};
}

// One connection's seeding of agents `names`, one response each, then ready.
std::vector<HostEvent> connection(int number, const std::vector<std::string>& names, bool withParents = true) {
    std::vector<HostEvent> out{statusEvent(kA, number, StatusCode::Connecting), statusEvent(kA, number, StatusCode::Seeding)};
    for (const auto& name : names) out.push_back(message(kA, number, agent(kA, name), true));
    if (withParents && names.size() > 1) out.push_back(message(kA, number, Parents{{{agentId(kA, names[1]), agentId(kA, names[0])}}}, true));
    for (const auto& name : names) out.push_back(message(kA, number, remoteResponse("k-" + name, kNow - 10'000, 100, name), true));
    out.push_back(message(kA, number, Ready{}, true));
    out.push_back(statusEvent(kA, number, StatusCode::Connected));
    return out;
}

TEST(RemoteMirror, AReadyRemovesTheAgentsTheNewConnectionDidNotSend) {
    HostMirror mirror(kA);
    for (const auto& e : connection(1, {"a", "b"})) mirror.apply(e, kNow, kDay);
    EXPECT_EQ(mirror.agents().size(), 2u);
    EXPECT_EQ(mirror.parents().size(), 1u);
    std::vector<FeedChange> changes;
    for (const auto& e : connection(2, {"a"}, false)) {
        auto c = mirror.apply(e, kNow, kDay);
        changes.insert(changes.end(), c.begin(), c.end());
    }
    EXPECT_EQ(mirror.agents().size(), 1u);
    EXPECT_TRUE(mirror.parents().empty());
    EXPECT_TRUE(std::any_of(changes.begin(), changes.end(), [](const FeedChange& c) {
        const auto* removed = std::get_if<feed::AgentRemoved>(&c);
        return removed && removed->id == agentId(kA, "b");
    }));
    EXPECT_TRUE(std::any_of(changes.begin(), changes.end(), [](const FeedChange& c) {
        const auto* parents = std::get_if<feed::Parents>(&c);
        return parents && parents->links.empty();
    }));
    EXPECT_EQ(mirror.responses().size(), 2u);
}

TEST(RemoteMirror, ADisconnectIdlesTheAgentsOnce) {
    HostMirror mirror(kA);
    for (const auto& e : connection(1, {"a"})) mirror.apply(e, kNow, kDay);
    mirror.apply(message(kA, 1, agent(kA, "a", SourceKind::Codex, Phase::Generating, kNow - 500), false), kNow, kDay);
    auto changes = mirror.apply(statusEvent(kA, 1, StatusCode::Unreachable), kNow, kDay);
    ASSERT_EQ(changes.size(), 2u);
    EXPECT_TRUE(std::holds_alternative<feed::Idle>(changes[1]));
    EXPECT_EQ(mirror.agents().at(agentId(kA, "a")).phase, Phase::Idle);
    EXPECT_FALSE(mirror.agents().at(agentId(kA, "a")).inFlightStartMs);
    EXPECT_EQ(mirror.apply(statusEvent(kA, 0, StatusCode::Disabled), kNow, kDay).size(), 1u);
}

TEST(RemoteMirror, ReplayIsSeededAndBounded) {
    HostMirror mirror(kA);
    mirror.setResponseLimit(100);
    for (const auto& e : connection(1, {"a"})) mirror.apply(e, kNow, kDay);
    for (int i = 0; i < 150; ++i) mirror.apply(message(kA, 1, remoteResponse("r" + std::to_string(i), kNow - 1000 + i, 1), false), kNow, kDay);
    EXPECT_LE(mirror.responses().size(), 100u);
    EXPECT_TRUE(mirror.responses().contains("r149"));
    EXPECT_FALSE(mirror.responses().contains("r0"));
    mirror.apply(message(kA, 1, remoteResponse("yesterday", kDay - 3'600'000, 1), false), kNow + 120'000, kDay);
    EXPECT_FALSE(mirror.responses().contains("yesterday"));
    const auto replay = mirror.replay();
    EXPECT_TRUE(std::holds_alternative<feed::Status>(replay.front()));
    std::int64_t previous = 0;
    for (const auto& change : replay) {
        if (const auto* r = std::get_if<feed::Response>(&change)) {
            EXPECT_FALSE(r->live);
            EXPECT_GE(r->response.record.endMs, previous);
            previous = r->response.record.endMs;
        }
    }
}

TEST(RemoteFeed, ForgetsRemovedHostsAndDropsUnknownOnes) {
    Feed feed;
    feed.setHosts({{kA, "devbox"}, {kB, "other"}});
    auto changes = feed.take();
    ASSERT_EQ(changes.size(), 1u);
    EXPECT_EQ(std::get<feed::Hosts>(changes[0]).hosts.size(), 2u);
    feed.receive(statusEvent("8C0D6B0A-1111-4222-8333-0000000000FF", 1, StatusCode::Connecting), kNow, kDay);
    EXPECT_TRUE(feed.take().empty());
    feed.setHosts({{kA, "devbox"}, {kB, "other"}});
    EXPECT_TRUE(feed.take().empty());
    feed.setHosts({{kA, "devbox"}});
    changes = feed.take();
    ASSERT_EQ(changes.size(), 2u);
    EXPECT_EQ(std::get<feed::RemoveHost>(changes[0]).id, kB);
    EXPECT_FALSE(feed.mirror(kB));
    EXPECT_TRUE(feed.hasHosts());
}

// The feed into a monitor, then a second monitor (a restart) that starts from the replay without reconnecting.
TEST(RemoteFeed, AMonitorAndItsRestartSeeTheSameHost) {
    Feed feed;
    feed.setHosts({{kA, "devbox"}});
    for (const auto& e : connection(1, {"a", "b"})) feed.receive(e, kNow, kDay);
    Monitor first(emptyConfig());
    first.start(kNow, kDay);
    first.applyRemote(feed.attach(), kNow);
    feed.receive(message(kA, 1, remoteResponse("live", kNow - 100, 40), false), kNow, kDay);
    first.applyRemote(feed.take(), kNow);
    const auto completions = first.takeCompletions();
    ASSERT_EQ(completions.size(), 1u);
    EXPECT_EQ(completions[0].host, kA);
    const auto snap = first.snapshot(kNow, kDay);
    EXPECT_EQ(snap.todayOutput, 240);
    ASSERT_EQ(snap.hosts.size(), 2u);
    EXPECT_TRUE(snap.hosts[1].connected);

    Monitor second(emptyConfig());
    second.start(kNow, kDay);
    second.applyRemote(feed.attach(), kNow);
    EXPECT_TRUE(second.takeCompletions().empty());
    const auto again = second.snapshot(kNow, kDay);
    EXPECT_EQ(again.todayOutput, 240);
    EXPECT_EQ(again.agents.size(), snap.agents.size());
    EXPECT_TRUE(feed.take().empty());

    // A reconnect that sends only `a`: `b` leaves at ready, today's totals stay.
    for (const auto& e : connection(2, {"a"}, false)) feed.receive(e, kNow, kDay);
    second.applyRemote(feed.take(), kNow);
    EXPECT_TRUE(second.takeCompletions().empty());
    const auto after = second.snapshot(kNow, kDay);
    EXPECT_EQ(after.todayOutput, 240);
    EXPECT_FALSE(agentRow(after, agentId(kA, "b")));
}

TEST(RemoteFeed, ChangesAMonitorNeverTookAreReplayed) {
    Feed feed;
    feed.setHosts({{kA, "devbox"}});
    for (const auto& e : connection(1, {"a"})) feed.receive(e, kNow, kDay);
    for (int i = 0; i < 510'000; ++i) feed.receive(statusEvent(kA, 1, StatusCode::Connected), kNow, kDay);
    const auto changes = feed.take();
    EXPECT_LT(changes.size(), 100u);
    EXPECT_TRUE(std::holds_alternative<feed::Hosts>(changes.front()));
}

}  // namespace
