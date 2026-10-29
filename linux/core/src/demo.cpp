#include "agentspeed/demo.hpp"

#include <algorithm>
#include <cmath>
#include <tuple>

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

// A demo task that moves through the states on a 120-second loop, so every form of 智力 shows.
AgentTask demoTask(double t, double offset, Phase phase, const char* suite) {
    const double into = std::fmod(t + offset, 120.0);
    AgentTask task;
    tasks::TaskResult r;
    r.counts.actions = 6 + static_cast<std::int64_t>(into / 6);
    r.counts.counted = r.counts.actions;
    r.counts.edits = 2;
    r.ig = 0.82;
    r.dims.E = 10 * std::min(1.0, static_cast<double>(r.counts.counted) / 8) * r.ig;
    if (into < 25) {
        r.state = "unverified";
    } else if (into < 55) {
        r.state = "failing";
        r.dims.C = 15;
        r.openFailure = tasks::OpenFailure{suite, "test", "demo", 3, 5, true, false};
        r.counts.runs = 2;
        r.counts.failingRuns = r.counts.fixAttempts = r.counts.episodes = 2;
    } else if (into < 100) {
        r.state = "passing";
        r.dims = {40, 25, 7.5, r.dims.E, 10};
        r.lastPass = tasks::LastPass{suite, "test", "demo", false};
        r.counts.runs = 3;
    } else {
        r.state = "pending";
        r.dims = {0, 25, 7.5, r.dims.E, 4};
        r.vHeld = 40;
        r.lastPass = tasks::LastPass{suite, "test", "demo", false};
        r.counts.runs = 3;
    }
    const double score = r.dims.V + r.dims.C + r.dims.R + r.dims.E + r.dims.K;
    r.score = score;
    r.shown = tasks::roundHalfAway(score);
    task.value.form = phase == Phase::Idle ? "resting" : "live";
    task.value.score = score;
    task.value.shown = r.shown;
    task.value.state = r.state;
    task.models = tasks::priorModels();
    task.models.success.tasks = task.models.stall.tasks = 23;
    task.models.fix.tasks = 9;
    tasks::Predictions p;
    p.success = r.state == "passing" ? 0.91 : r.state == "failing" ? 0.37 : 0.58;
    p.stall = r.state == "failing" ? 0.19 : 0.08;
    if (r.state == "failing") p.fix = 0.34;
    task.predictions = p;
    task.workSignals.failed = r.state == "failing" ? 2 : 0;
    task.workSignals.novelty = 0.78;
    task.task = r;
    return task;
}

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
        row.effort = s.depth == 0 ? (s.source == SourceKind::Codex ? "xhigh" : "high") : "medium";
        if (s.source == SourceKind::Codex) row.speed = "fast";
        row.task = demoTask(t, s.period * 9 + s.phase * 13, row.phase, s.source == SourceKind::Codex ? "cargo test" : "pytest");
        row.liveThinking = row.liveOutput * 0.35;
        snap.agents.push_back(row);
    }
    std::vector<tasks::ValuedAgent> valued;
    for (const auto& a : snap.agents) valued.push_back({a.id, nowMs, a.task->value});
    ScoreSet all;
    all.value = tasks::highest(valued);
    for (const auto& a : snap.agents) {
        if (all.value.agent && a.id == *all.value.agent) all.state = a.task->value.state;
        if (all.value.form != "none" && a.task->value.form == all.value.form) ++all.scored;
        all.liveThinking += a.liveThinking;
        all.generating |= a.phase == Phase::Generating;
    }
    all.thinkingShare = 0.35;
    snap.scoreSets.push_back(all);
    snap.liveOutput = codex + claude;
    snap.liveTotal = snap.liveOutput * 18.5;
    snap.estimatedShare = 0.12;
    const double secondsToday = std::max(0.0, static_cast<double>(nowMs - todayStartMs) / 1000.0);
    snap.todayOutput = static_cast<std::int64_t>(secondsToday * 21.0);
    snap.todayInput = snap.todayOutput * 24;
    snap.todayRequests = static_cast<std::int64_t>(secondsToday / 9.0);
    snap.todayAverage = 96.4;
    snap.peakToday = 412.0;
    for (const auto& [kind, rate, live] : {std::tuple{SourceKind::ClaudeCode, 13.0, claude}, std::tuple{SourceKind::Codex, 8.0, codex}}) {
        SourceRow row;
        row.source = kind;
        row.todayOutput = static_cast<std::int64_t>(secondsToday * rate);
        row.liveOutput = live;
        row.available = true;
        snap.sources.push_back(row);
    }
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
