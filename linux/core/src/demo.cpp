#include "agentspeed/demo.hpp"

#include <algorithm>
#include <cmath>

namespace agentspeed {

namespace {

double wave(double seconds, double period, double phase) {
    return 0.5 + 0.5 * std::sin(2 * M_PI * seconds / period + phase);
}

// Every 50 s the agents surge for 12 s, to different heights, so the orb climbs through every tier.
double surge(double seconds) {
    constexpr double kCycle = 50, kStart = 30, kLength = 12;
    constexpr double kHeights[] = {0.55, 1.0, 0.75, 1.3};
    const double cycle = std::floor(seconds / kCycle);
    const double into = seconds - cycle * kCycle - kStart;
    if (into <= 0 || into >= kLength) return 1;
    const double envelope = std::pow(std::sin(M_PI * into / kLength), 2);
    const auto index = static_cast<std::size_t>(static_cast<std::int64_t>(cycle) & 3);
    return 1 + 5.5 * kHeights[index] * envelope;
}

constexpr double kResponsePeriods[] = {2.9, 3.7, 4.3};

}  // namespace

Snapshot demoAt(std::int64_t nowMs, std::int64_t todayStartMs, bool withHistory);

Snapshot demoSnapshot(std::int64_t nowMs, std::int64_t todayStartMs) { return demoAt(nowMs, todayStartMs, true); }

Snapshot demoAt(std::int64_t nowMs, std::int64_t todayStartMs, bool withHistory) {
    Snapshot snap;
    snap.nowMs = nowMs;
    snap.demo = true;
    const double t = static_cast<double>(nowMs) / 1000.0;
    struct Spec {
        const char* id;
        SourceKind source;
        const char* project;
        const char* model;
        int depth;
        double base, swing, period, phase;
        Phase phaseKind;
    };
    const Spec specs[] = {
        {"demo-nova", SourceKind::ClaudeCode, "nova", "claude-opus-5-5", 0, 110, 70, 11, 0.0, Phase::Generating},
        {"demo-nova-sub", SourceKind::ClaudeCode, "nova", "claude-sonnet-5-5", 1, 60, 50, 7, 1.3, Phase::Generating},
        {"demo-rocket", SourceKind::Codex, "rocket", "gpt-6.1-sol", 0, 85, 45, 13, 2.1, Phase::Generating},
        {"demo-orbit", SourceKind::Codex, "orbit", "gpt-6.1-terra", 0, 0, 0, 1, 0, Phase::Tools},
        {"demo-lumen", SourceKind::ClaudeCode, "lumen", "claude-haiku-5-5", 0, 0, 0, 1, 0, Phase::Idle},
    };
    double codex = 0, claude = 0;
    for (const auto& s : specs) {
        AgentRow row;
        row.id = s.id;
        row.source = s.source;
        row.project = s.project;
        row.model = s.model;
        row.depth = s.depth;
        row.subagent = s.depth > 0;
        // Agents pause now and then, like real ones waiting on tools.
        const bool pausing = s.phaseKind == Phase::Generating && wave(t, s.period * 2.3, s.phase) < 0.18;
        row.phase = pausing ? Phase::Tools : s.phaseKind;
        row.liveOutput = row.phase == Phase::Generating ? (s.base + s.swing * wave(t, s.period, s.phase)) * surge(t) : 0;
        (s.source == SourceKind::Codex ? codex : claude) += row.liveOutput;
        if (row.phase == Phase::Generating) ++snap.generating;
        if (row.phase == Phase::Tools) ++snap.tools;
        snap.agents.push_back(row);
    }
    snap.liveOutput = codex + claude;
    snap.liveTotal = snap.liveOutput * 18.5;
    snap.estimatedShare = 0.12;
    const double secondsToday = std::max(0.0, static_cast<double>(nowMs - todayStartMs) / 1000.0);
    snap.todayOutput = static_cast<std::int64_t>(secondsToday * 21.0);
    snap.todayInput = snap.todayOutput * 24;
    snap.todayRequests = static_cast<std::int64_t>(secondsToday / 9.0);
    snap.todayAverage = 96.4;
    snap.peakToday = 412.0;
    snap.sources.push_back({SourceKind::ClaudeCode, static_cast<std::int64_t>(secondsToday * 13.0), claude});
    snap.sources.push_back({SourceKind::Codex, static_cast<std::int64_t>(secondsToday * 8.0), codex});
    if (withHistory) {
        for (int k = 119; k >= 1; --k) snap.history.push_back(demoAt(nowMs - k * 1000LL, todayStartMs, false).liveOutput);
        snap.history.push_back(snap.liveOutput);
        for (double v : snap.history) snap.peakToday = std::max(snap.peakToday, v);
    }
    return snap;
}

std::vector<Completion> demoCompletions(std::int64_t fromMs, std::int64_t toMs) {
    std::vector<Completion> out;
    if (toMs <= fromMs || toMs - fromMs > 10'000) return out;
    const Snapshot now = demoAt(toMs, 0, false);
    for (std::size_t i = 0; i < 3; ++i) {
        const double period = kResponsePeriods[i];
        const auto before = std::floor(static_cast<double>(fromMs) / 1000.0 / period);
        const auto after = std::floor(static_cast<double>(toMs) / 1000.0 / period);
        if (after <= before || now.agents[i].phase != Phase::Generating) continue;
        Completion c;
        c.source = now.agents[i].source;
        c.output = 180 + static_cast<std::int64_t>(std::fmod(after * 7919.0, 2400.0));
        c.speed = now.agents[i].liveOutput;
        out.push_back(c);
    }
    return out;
}

}  // namespace agentspeed
