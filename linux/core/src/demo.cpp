#include "agentspeed/demo.hpp"

#include <algorithm>
#include <array>
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

// A demo agent's performance score with every component the score card shows (METRICS 6.9): its measures drift
// slowly, so the value moves like a real one; `n` below 10 marks it provisional.
AgentPerf demoPerf(SourceKind source, Phase phase, double t, double offset, const std::array<double, 7>& measures, std::int64_t n) {
    perf::Measures m;
    for (std::size_t i = 0; i < measures.size(); ++i) {
        if (measures[i] <= 0) continue;
        const double drift = i == 0 ? 0.9 + 0.2 * wave(t, 97, offset) : i == 1 ? 1.1 - 0.2 * wave(t, 131, offset) : 1;
        m.measure[i] = measures[i] * drift;
        m.samples[i] = i <= 1 || i == 3 || i == 4 ? n : 12;
    }
    AgentPerf value;
    auto& e = value.score;
    const auto [components, score] = perf::combine(source, m);
    e.components = components;
    e.score = score;
    e.shown = perf::roundHalfAway(*score);
    e.form = phase == Phase::Idle ? perf::Form::Resting : perf::Form::Live;
    e.provisional = n < perf::kProvisionalBelow;
    e.n = n;
    e.tools = m.measure[2] ? 12 : 0;
    e.activeMs = 6 * 60'000;
    perf::applyBaseline(e, *score / 1.08);
    value.workSignals.novelty = 0.78;
    return value;
}

}  // namespace

Snapshot demoAt(std::int64_t nowMs, std::int64_t todayStartMs, bool withHistory, const std::vector<std::optional<std::vector<SourceKind>>>& sets);

Snapshot demoSnapshot(std::int64_t nowMs, std::int64_t todayStartMs, const std::vector<std::optional<std::vector<SourceKind>>>& sets) {
    return demoAt(nowMs, todayStartMs, true, sets);
}

Snapshot demoAt(std::int64_t nowMs, std::int64_t todayStartMs, bool withHistory, const std::vector<std::optional<std::vector<SourceKind>>>& sets) {
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
        std::array<double, 7> measures;  // T, L, N, R, H, U, S; 0: absent
        std::int64_t samples;
    };
    const Spec specs[] = {
        {"demo-nova", SourceKind::ClaudeCode, "nova", "claude-opus-5-5", 0, 110, 70, 11, 0.0, Phase::Generating,
         {72, 2300, 4.2, 26, 0.93, 310, 0.96}, 20},
        {"demo-nova-sub", SourceKind::ClaudeCode, "nova", "claude-sonnet-5-5", 1, 60, 50, 7, 1.3, Phase::Generating,
         {58, 3100, 3.1, 18, 0.9, 420, 0.92}, 7},
        {"demo-rocket", SourceKind::Codex, "rocket", "gpt-6.1-sol", 0, 85, 45, 13, 2.1, Phase::Generating,
         {41, 4200, 2.4, 15, 0.88, 650, 0.85}, 20},
        {"demo-orbit", SourceKind::Codex, "orbit", "gpt-6.1-terra", 0, 0, 0, 1, 0, Phase::Tools,
         {96, 1600, 5.6, 31, 0.95, 240, 0.97}, 16},
        {"demo-lumen", SourceKind::ClaudeCode, "lumen", "claude-haiku-5-5", 0, 0, 0, 1, 0, Phase::Idle,
         {130, 1200, 3.8, 0, 0.94, 280, 0.95}, 11},
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
        row.perf = demoPerf(s.source, row.phase, t, s.phase, s.measures, s.samples);
        row.perf->score.model = s.model;
        row.liveThinking = row.liveOutput * 0.35;
        snap.agents.push_back(row);
    }
    for (const auto& sources : sets) {
        const auto inSet = [&](SourceKind kind) { return !sources || std::find(sources->begin(), sources->end(), kind) != sources->end(); };
        std::vector<perf::Valued> valued;
        for (const auto& a : snap.agents) {
            if (inSet(a.source)) valued.push_back({a.id, nowMs, a.perf->score.form, a.perf->score.score});
        }
        ScoreSet set;
        set.sources = sources;
        set.value = perf::highest(valued);
        for (const auto& a : snap.agents) {
            if (!inSet(a.source)) continue;
            if (set.value.agent && a.id == *set.value.agent) set.provisional = a.perf->score.provisional;
            if (set.value.form != perf::Form::None && a.perf->score.form == set.value.form) ++set.scored;
            set.liveThinking += a.liveThinking;
            set.generating |= a.phase == Phase::Generating;
        }
        set.thinkingShare = set.liveThinking > 0 ? 0.35 : 0;
        snap.scoreSets.push_back(std::move(set));
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
    for (const auto& [kind, rate, live] : {std::tuple{SourceKind::ClaudeCode, 13.0, claude}, std::tuple{SourceKind::Codex, 8.0, codex}}) {
        SourceRow row;
        row.source = kind;
        row.todayOutput = static_cast<std::int64_t>(secondsToday * rate);
        row.liveOutput = live;
        row.available = true;
        snap.sources.push_back(row);
    }
    if (withHistory) {
        for (int k = 119; k >= 1; --k) snap.history.push_back(demoAt(nowMs - k * 1000LL, todayStartMs, false, {}).liveOutput);
        snap.history.push_back(snap.liveOutput);
        for (double v : snap.history) snap.peakToday = std::max(snap.peakToday, v);
    }
    return snap;
}

std::vector<Completion> demoCompletions(std::int64_t fromMs, std::int64_t toMs) {
    std::vector<Completion> out;
    if (toMs <= fromMs || toMs - fromMs > 10'000) return out;
    const Snapshot now = demoAt(toMs, 0, false, {});
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
