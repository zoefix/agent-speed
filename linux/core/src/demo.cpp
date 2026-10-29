#include "agentspeed/demo.hpp"

#include <cmath>

namespace agentspeed {

namespace {

double wave(double seconds, double period, double phase) {
    return 0.5 + 0.5 * std::sin(2 * M_PI * seconds / period + phase);
}

}  // namespace

Snapshot demoSnapshot(std::int64_t nowMs, std::int64_t todayStartMs) {
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
        row.liveOutput = row.phase == Phase::Generating ? s.base + s.swing * wave(t, s.period, s.phase) : 0;
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
    return snap;
}

}  // namespace agentspeed
