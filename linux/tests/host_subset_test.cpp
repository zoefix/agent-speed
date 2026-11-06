#include <gtest/gtest.h>

#include "agentspeed/host_selection.hpp"
#include "agentspeed/host_subset.hpp"
#include "agentspeed/monitor.hpp"

// Orbs and boards over (host, source) cells (DESIGN 13, METRICS 6.8) and the hosts they track.
using namespace agentspeed;

namespace {

const std::string kLocal = "local";
const std::string kA = "8C0D6B0A-1111-4222-8333-00000000000A";
const std::string kB = "8C0D6B0A-1111-4222-8333-00000000000B";

HostSourceRow cell(const std::string& host, SourceKind source, bool available, double live, double estimated, std::int64_t output, double cost) {
    HostSourceRow c;
    c.host = host;
    c.source = source;
    c.available = available;
    c.sessions = 1;
    c.liveOutput = live;
    c.liveTotal = live * 2;
    c.liveEstimated = estimated;
    c.liveCost = cost;
    c.liveCostKnownShare = 1;
    c.todayOutput = output;
    c.todayRequests = output > 0 ? 1 : 0;
    c.todayCostMicroUsd = output;
    return c;
}

AgentRow agent(const std::string& id, const std::string& host, SourceKind source, Phase phase, std::optional<std::string> parent = std::nullopt) {
    AgentRow a;
    a.id = id;
    a.host = host;
    a.source = source;
    a.phase = phase;
    a.depth = parent ? 1 : 0;
    a.parentId = std::move(parent);
    return a;
}

HostRow host(const std::string& id, const std::string& name, bool connected) {
    HostRow h;
    h.id = id;
    h.name = name;
    h.connected = connected;
    return h;
}

Snapshot snapshot() {
    Snapshot snap;
    snap.hostSources = {
        cell(kLocal, SourceKind::Codex, true, 10, 0, 100, 1),
        cell(kA, SourceKind::Codex, true, 20, 5, 200, 2),
        cell(kA, SourceKind::ClaudeCode, true, 30, 0, 300, 3),
        cell(kB, SourceKind::Codex, false, 0, 0, 50, 0),
    };
    snap.hosts = {host(kLocal, kLocal, true), host(kA, "devbox", true), host(kB, "gpu", false)};
    for (const auto kind : MonitorConfig::allSources()) {
        SourceRow row;
        row.source = kind;
        row.enabled = kind != SourceKind::Gemini;
        row.available = kind == SourceKind::Codex || kind == SourceKind::ClaudeCode;
        for (const auto& c : snap.hostSources) {
            if (c.source != kind) continue;
            row.liveOutput += c.liveOutput;
            row.todayOutput += c.todayOutput;
        }
        snap.sources.push_back(row);
    }
    snap.liveOutput = 60;
    snap.todayOutput = 650;
    snap.agents = {
        agent("local-codex", kLocal, SourceKind::Codex, Phase::Generating),
        agent("a-codex", kA, SourceKind::Codex, Phase::Generating),
        agent("a-claude", kA, SourceKind::ClaudeCode, Phase::Tools, "a-codex"),
        agent("b-child", kB, SourceKind::Codex, Phase::Idle, "local-codex"),
    };
    return snap;
}

const SourceRow& row(const Snapshot& snap, SourceKind kind) {
    return *std::find_if(snap.sources.begin(), snap.sources.end(), [&](const SourceRow& r) { return r.source == kind; });
}

std::vector<std::string> ids(const Snapshot& snap) {
    std::vector<std::string> out;
    for (const auto& a : snap.agents) out.push_back(a.id);
    return out;
}

}  // namespace

TEST(HostSubset, OneHostCountsItsCellsAndAgents) {
    const auto view = hosts::restricted(snapshot(), {kA});
    EXPECT_EQ(view.liveOutput, 50);
    EXPECT_EQ(view.liveTotal, 100);
    EXPECT_EQ(view.todayOutput, 500);
    EXPECT_EQ(view.todayCostMicroUsd, 500);
    EXPECT_EQ(view.liveCost, 5);
    EXPECT_NEAR(view.estimatedShare, 0.1, 1e-12);
    EXPECT_EQ(ids(view), (std::vector<std::string>{"a-codex", "a-claude"}));
    EXPECT_EQ(view.agents[1].parentId, "a-codex");
    EXPECT_EQ(view.agents[1].depth, 1);
    EXPECT_EQ(view.generating, 1);
    EXPECT_EQ(view.tools, 1);
    EXPECT_EQ(row(view, SourceKind::Codex).liveOutput, 20);
    EXPECT_NEAR(row(view, SourceKind::Codex).liveEstimatedShare, 0.25, 1e-12);
    EXPECT_EQ(row(view, SourceKind::Codex).activeAgents, 1);
    EXPECT_EQ(row(view, SourceKind::ClaudeCode).todayOutput, 300);

    const auto pair = hosts::restricted(snapshot(), {kLocal, kA});
    EXPECT_EQ(row(pair, SourceKind::Codex).liveOutput, 30);
    EXPECT_EQ(row(pair, SourceKind::Codex).todayOutput, 300);
    EXPECT_EQ(ids(pair), (std::vector<std::string>{"local-codex", "a-codex", "a-claude"}));
}

TEST(HostSubset, AParentOutsideTheHostsIsCleared) {
    const auto view = hosts::restricted(snapshot(), {kB});
    ASSERT_EQ(view.agents.size(), 1u);
    EXPECT_FALSE(view.agents[0].parentId);
    EXPECT_EQ(view.agents[0].depth, 0);
}

TEST(HostSubset, ASourceIsAvailableOnlyWhereItIsDetected) {
    const auto local = hosts::restricted(snapshot(), {kLocal});
    EXPECT_TRUE(row(local, SourceKind::Codex).available);
    EXPECT_FALSE(row(local, SourceKind::ClaudeCode).available);
    const auto remote = hosts::restricted(snapshot(), {kA});
    EXPECT_TRUE(row(remote, SourceKind::ClaudeCode).available);
    EXPECT_FALSE(row(remote, SourceKind::Gemini).available);
    const auto offline = hosts::restricted(snapshot(), {kB});
    EXPECT_FALSE(row(offline, SourceKind::Codex).available);
    EXPECT_EQ(row(offline, SourceKind::Codex).todayOutput, 50) << "a disconnected host's responses keep counting";
}

TEST(HostSubset, OnlyDisconnectedHostsAreOffline) {
    const auto snap = snapshot();
    EXPECT_TRUE(hosts::isOffline(snap, {kB}));
    EXPECT_FALSE(hosts::isOffline(snap, {kB, kLocal}));
    EXPECT_FALSE(hosts::isOffline(snap, {kB, kA}));
    EXPECT_FALSE(hosts::isOffline(snap, {kA}));
    EXPECT_TRUE(hosts::isOffline(snap, {"removed"}));
}

TEST(HostSubset, EventsFollowHostsAndSources) {
    Completion c;
    c.source = SourceKind::Codex;
    c.host = kA;
    const std::vector<SourceKind> codex{SourceKind::Codex}, claude{SourceKind::ClaudeCode};
    EXPECT_TRUE(hosts::accepts(c, std::nullopt, std::nullopt));
    EXPECT_TRUE(hosts::accepts(c, codex, std::vector<std::string>{kA}));
    EXPECT_FALSE(hosts::accepts(c, std::nullopt, std::vector<std::string>{kLocal}));
    EXPECT_FALSE(hosts::accepts(c, claude, std::vector<std::string>{kA}));
    EXPECT_TRUE(hosts::acceptsNewPeak(std::nullopt, std::nullopt));
    EXPECT_FALSE(hosts::acceptsNewPeak(std::nullopt, std::vector<std::string>{kLocal, kA, kB}));
}

TEST(HostSubset, WithoutRemoteHostsOnlyThisComputerExists) {
    auto snap = snapshot();
    snap.hosts.clear();
    snap.hostSources.clear();
    const auto local = hosts::restricted(snap, {kLocal});
    EXPECT_EQ(local.liveOutput, snap.liveOutput);
    EXPECT_EQ(ids(local), ids(snap));
    const auto remote = hosts::restricted(snap, {kA});
    EXPECT_EQ(remote.liveOutput, 0);
    EXPECT_TRUE(hosts::isOffline(snap, {kA}));
    EXPECT_FALSE(hosts::isOffline(snap, {kLocal}));
}

TEST(HostSelection, NormalizesLocalFirstWithoutDuplicates) {
    using hosts::HostSelection;
    EXPECT_TRUE(HostSelection::normalized({}).isAll());
    EXPECT_TRUE(HostSelection::normalized({""}).isAll());
    EXPECT_EQ(HostSelection::normalized({kA, kLocal, kA, "", kB}).ids(), (std::vector<std::string>{kLocal, kA, kB}));
    EXPECT_FALSE(HostSelection::all().hostSet());
    EXPECT_EQ(HostSelection::normalized({kA}).hostSet(), std::vector<std::string>{kA});
    EXPECT_TRUE(HostSelection::all().tracks(kB));
    EXPECT_FALSE(HostSelection::normalized({kA}).tracks(kB));
}

TEST(HostSelection, TogglingRemovingAndOrdering) {
    using hosts::HostSelection;
    const auto all = HostSelection::all();
    EXPECT_EQ(all.toggling(kA)->ids(), std::vector<std::string>{kA});
    const auto one = HostSelection::normalized({kA});
    EXPECT_FALSE(one.toggling(kA)) << "the last host cannot be removed";
    EXPECT_EQ(one.toggling(kLocal)->ids(), (std::vector<std::string>{kLocal, kA}));
    EXPECT_EQ(HostSelection::normalized({kLocal, kA}).toggling(kA)->ids(), std::vector<std::string>{kLocal});
    EXPECT_TRUE(one.removing(kA).isAll()) << "an emptied selection becomes all";
    EXPECT_EQ(HostSelection::normalized({kLocal, kA}).removing(kA).ids(), std::vector<std::string>{kLocal});
    EXPECT_TRUE(all.removing(kA).isAll());

    const std::vector<std::string> known{kLocal, kB, kA};
    EXPECT_EQ(HostSelection::normalized({kA, "other", kB}).ordered(known).ids(), (std::vector<std::string>{kB, kA, "other"}));
    EXPECT_EQ(HostSelection::normalized({kA}).toggling(kB, known)->ids(), (std::vector<std::string>{kB, kA}));
    EXPECT_FALSE(HostSelection::normalized({kA, "other"}).toggling(kA, known)) << "unknown ids alone are no selection";
    EXPECT_EQ(all.toggling(kB, known)->ids(), std::vector<std::string>{kB});
}

TEST(HostSelection, EntriesAndThePicker) {
    using hosts::HostChoice;
    using hosts::HostSelection;
    const std::vector<HostChoice> choices{{kLocal, "This Mac"}, {kA, "devbox"}};
    const auto all = HostSelection::all().entries(choices);
    ASSERT_EQ(all.size(), 2u);
    EXPECT_FALSE(all[0].checked || all[1].checked);
    EXPECT_TRUE(all[0].enabled && all[1].enabled);
    const auto one = HostSelection::normalized({kA}).entries(choices);
    EXPECT_FALSE(one[0].checked);
    EXPECT_TRUE(one[1].checked);
    EXPECT_FALSE(one[1].enabled) << "the last tracked host";
    EXPECT_TRUE(HostSelection::normalized({kLocal, kA}).entries(choices)[1].enabled);
    EXPECT_FALSE(hosts::pickerShown({{kLocal, "This Mac"}}));
    EXPECT_TRUE(hosts::pickerShown(choices));
}

TEST(HostSelection, JsonRoundTrip) {
    using hosts::HostSelection;
    const auto parse = [](std::string_view text) {
        json::Document doc;
        EXPECT_TRUE(doc.parse(text));
        return HostSelection::fromJson(doc.root());
    };
    EXPECT_TRUE(parse(R"("all")").isAll());
    EXPECT_EQ(parse(R"("8C0D6B0A-1111-4222-8333-00000000000A")").ids(), std::vector<std::string>{kA});
    EXPECT_EQ(parse(R"(["future-id", "8C0D6B0A-1111-4222-8333-00000000000A", "local"])").ids(),
              (std::vector<std::string>{kLocal, "future-id", kA}))
        << "unknown ids are kept";
    EXPECT_TRUE(parse("[]").isAll());
    EXPECT_TRUE(parse("42").isAll());
    EXPECT_EQ(HostSelection::all().json(), R"("all")");
    const auto selection = HostSelection::normalized({kA, kLocal});
    EXPECT_EQ(selection.json(), R"(["local","8C0D6B0A-1111-4222-8333-00000000000A"])");
    EXPECT_EQ(parse(selection.json()), selection);
}
