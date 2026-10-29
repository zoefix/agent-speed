#include <gtest/gtest.h>

#include <algorithm>

#include "agentspeed/fs_util.hpp"
#include "agentspeed/monitor.hpp"
#include "fixture_support.hpp"

using namespace agentspeed;

namespace {

std::string fixturePath(const std::string& relative) { return fsx::normalize((fixtures::fixtureDir() / relative).string()); }

// Every source's root points into spec/fixtures; nothing of this machine is read.
MonitorConfig fixtureConfig(std::set<SourceKind> enabled) {
    RootSettings s;
    s.codex = fixturePath("codex/sessions");
    s.claude = fixturePath("claude/projects");
    s.dsh = fixturePath("dsh/sessions");
    s.openCode = fixturePath("opencode");
    s.gemini = fixturePath("gemini/tmp");
    s.omp = fixturePath("omp/agent/sessions");
    s.antigravity = fixturePath("antigravity/antigravity-cli");
    s.workBuddy = fixturePath("workbuddy/.workbuddy");
    s.workBuddyAi = fixturePath("workbuddy/.workbuddy-ai");
    s.zcode = fixturePath("zcode/cli/db/db.sqlite");
    s.kimi = fixturePath("kimi/sessions");
    s.grokBuild = fixturePath("grok/sessions");
    s.pi = fixturePath("pi/agent/sessions");
    s.openClaw = fixturePath("openclaw/.openclaw");
    s.hermes = fixturePath("hermes/home");
    s.droid = fixturePath("droid/.factory");
    MonitorConfig config;
    config.locations = SourceLocations::resolve(s, processEnvironment(), "/nonexistent-home");
    config.enabled = std::move(enabled);
    return config;
}

constexpr std::int64_t kDayStart = 1792454400000;  // 2026-10-20T00:00:00Z

const SourceRow& rowOf(const Snapshot& snap, SourceKind kind) {
    return *std::find_if(snap.sources.begin(), snap.sources.end(), [&](const SourceRow& r) { return r.source == kind; });
}

}  // namespace

TEST(Monitor, HarnessesReachTheLiveMonitor) {
    Monitor monitor(fixtureConfig({SourceKind::ZCode, SourceKind::Kimi, SourceKind::GrokBuild}));
    const auto now = kDayStart + 30'000'000;
    monitor.start(now, kDayStart);
    const auto snap = monitor.snapshot(now, kDayStart);
    EXPECT_EQ(snap.todayRequests, 12);
    EXPECT_EQ(snap.todayOutput, 1475);
    EXPECT_EQ(monitor.sessionCount(SourceKind::ZCode, kDayStart), 2);
    EXPECT_EQ(monitor.sessionCount(SourceKind::Kimi, kDayStart), 2);
    const auto subagentWithParent = [&](SourceKind kind) {
        for (const auto& [id, agent] : monitor.agents()) {
            if (agent.source == kind && agent.isSubagent && monitor.parentOf(id)) return true;
        }
        return false;
    };
    EXPECT_TRUE(subagentWithParent(SourceKind::Kimi));
    EXPECT_TRUE(subagentWithParent(SourceKind::ZCode));
    EXPECT_TRUE(rowOf(snap, SourceKind::Kimi).available);
    EXPECT_TRUE(rowOf(snap, SourceKind::ZCode).available);
}

TEST(Monitor, EverySourceIsReadFromItsRoot) {
    Monitor monitor(fixtureConfig(MonitorConfig::allSources()));
    const auto now = kDayStart + 86'399'000;
    monitor.start(now, kDayStart);
    monitor.poll(now + 1000, kDayStart);
    const auto snap = monitor.snapshot(now + 1000, kDayStart);
    for (const auto& row : snap.sources) {
        SCOPED_TRACE(std::string(sourceId(row.source)));
        EXPECT_TRUE(row.enabled);
        EXPECT_TRUE(row.available) << row.path;
    }
    const auto responses = monitor.responses();
    std::set<SourceKind> seen;
    for (const auto& r : responses) seen.insert(r.source);
    for (const auto kind : MonitorConfig::allSources()) {
        EXPECT_TRUE(seen.contains(kind)) << sourceId(kind);
    }
    EXPECT_GT(snap.todayOutput, 0);
    EXPECT_GT(monitor.trackedFiles(), 40u);
}
