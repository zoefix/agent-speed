#include <algorithm>
#include <set>

#include "agentspeed/antigravity.hpp"
#include "agentspeed/engine.hpp"
#include "agentspeed/hermes.hpp"
#include "agentspeed/monitor.hpp"
#include "agentspeed/monitor_store.hpp"
#include "agentspeed/openclaw.hpp"
#include "agentspeed/opencode.hpp"
#include "agentspeed/price_book.hpp"
#include "agentspeed/zcode.hpp"

// The engine tick (METRICS 6): live rate, today, history buckets, the agents list and the source rows.
namespace agentspeed {

namespace {

constexpr std::int64_t kStaleGeneratingMs = 300'000;

}  // namespace

const tasks::TaskTracker* Monitor::trackerOf(const std::string& id) const {
    if (const auto it = files_.find(id); it != files_.end()) return it->second->tasks.get();
    if (const auto it = windowTasks_.find(id); it != windowTasks_.end()) return it->second.get();
    return nullptr;
}

// METRICS 6.9–6.11: the listed agents' task scores, predictions, work signals and thinking, then the score sets.
void Monitor::scoreTasks(Snapshot& snap, const std::vector<engine::Response>& recent, const std::vector<engine::Agent>& agents,
                         std::int64_t nowMs) {
    const auto thinking = engine::thinking(recent, agents, nowMs);
    std::vector<tasks::AgentRow> rows;
    std::set<std::string> present;
    for (auto& row : snap.agents) {
        if (const auto it = thinking.perAgent.find(row.id); it != thinking.perAgent.end()) {
            row.liveThinking = it->second.liveThinking();
            row.thinkingShare = it->second.thinkingShare();
        }
        const auto* tracker = trackerOf(row.id);
        if (!tracker) continue;
        present.insert(row.id);
        const auto& info = agents_.at(row.id);
        const auto phase = tasks::effectivePhase(info.phase, info.lastLineMs, nowMs);
        auto& cached = taskCache_[row.id];
        const auto version = tracker->composer().version();
        if (cached.events != version || cached.models != taskModelsVersion_ || cached.phase != phase || cached.parent != row.parentId ||
            cached.future) {
            const auto models = taskModels_.find(info.source);
            const tasks::ScoredAgent scored{row.id, info.source, phase, info.lastLineMs, row.parentId, &tracker->composer().events()};
            cached.row = tasks::agentRow(scored, nowMs, models == taskModels_.end() ? tasks::priorModels() : models->second);
            cached.events = version;
            cached.models = taskModelsVersion_;
            cached.phase = phase;
            cached.parent = row.parentId;
            cached.future = false;
            for (const auto& e : tracker->composer().events()) {
                if (e.t > nowMs || (e.outcome && e.outcome->t && *e.outcome->t > nowMs)) cached.future = true;
            }
        }
        cached.row.agent.lastLineMs = info.lastLineMs;
        row.task = AgentTask{cached.row.value,       cached.row.task,   cached.row.taskStartMs,
                             cached.row.predictions, cached.row.models, work::signalsOf(tracker->actions().actions(), nowMs)};
        rows.push_back(cached.row);
    }
    std::erase_if(taskCache_, [&](const auto& entry) { return !present.contains(entry.first) && !agents_.contains(entry.first); });

    const auto values = tasks::setValues(rows, nowMs, scoreSets_);
    for (std::size_t i = 0; i < scoreSets_.size(); ++i) {
        const auto& sources = scoreSets_[i];
        const auto inSet = [&](SourceKind kind) {
            return !sources || std::find(sources->begin(), sources->end(), kind) != sources->end();
        };
        ScoreSet set;
        set.sources = sources;
        set.value = values.sets[i].value;
        for (const auto& r : rows) {
            if (!inSet(r.agent.source)) continue;
            if (set.value.form != "none" && r.value.form == set.value.form) ++set.scored;
            if (set.value.agent && r.agent.id == *set.value.agent) set.state = r.value.state;
        }
        for (const auto& a : snap.agents) {
            if (inSet(a.source) && a.phase == Phase::Generating) set.generating = true;
        }
        engine::ThinkingWindow window = thinking.total;
        if (sources) {
            std::vector<engine::Response> ownResponses;
            std::vector<engine::Agent> ownAgents;
            for (const auto& r : recent) {
                if (inSet(r.source)) ownResponses.push_back(r);
            }
            for (const auto& a : agents) {
                if (inSet(a.source)) ownAgents.push_back(a);
            }
            window = engine::thinking(ownResponses, ownAgents, nowMs).total;
        }
        set.liveThinking = window.liveThinking();
        set.thinkingShare = window.thinkingShare();
        snap.scoreSets.push_back(std::move(set));
    }
    snap.scoreGroups = values.groups;
}

Snapshot Monitor::snapshot(std::int64_t nowMs, std::int64_t todayStartMs) {
    resolveParents();
    Snapshot snap;
    snap.nowMs = nowMs;
    snap.todayStartMs = todayStartMs;
    const auto responses = store_->all();
    std::vector<engine::Response> recent;
    for (const auto& r : responses) {
        if (r.endMs >= nowMs - Store::kRetentionMs) recent.push_back(r);
    }

    std::vector<engine::Agent> agents;
    agents.reserve(agents_.size());
    for (const auto& [id, info] : agents_) {
        engine::Agent a;
        a.id = id;
        a.source = info.source;
        a.model = info.model;
        a.phase = info.phase;
        a.inFlightStartMs = info.inFlightStartMs;
        a.lastLineMs = info.lastLineMs;
        if (const auto parent = parents_.find(id); parent != parents_.end()) a.parentId = parent->second;
        agents.push_back(std::move(a));
    }

    const auto live = engine::liveRate(recent, agents, nowMs);
    snap.liveOutput = live.output;
    snap.liveTotal = live.total;
    snap.estimatedShare = live.estimatedShare;
    const auto today = engine::todayStats(responses, todayStartMs, nowMs);
    snap.todayOutput = today.output;
    snap.todayInput = today.input;
    snap.todayRequests = today.requests;
    snap.todayAverage = today.average;
    snap.peakToday = std::max(today.peak, live.output);
    const auto cost = engine::liveCost(recent, agents, nowMs);
    snap.liveCost = cost.microUsdPerSecond;
    snap.liveCostKnownShare = cost.knownShare;
    snap.todayCostMicroUsd = today.costMicroUsd;
    snap.todayUnpricedRequests = today.unpricedRequests;
    snap.priceVersion = config_.prices->currentVersion();
    const std::int64_t lastBucket = (nowMs / 1000) * 1000;
    const std::int64_t firstBucket = lastBucket - 119'000;
    const auto measured = engine::buckets(recent, firstBucket, lastBucket);
    snap.history.reserve(120);
    for (std::int64_t k = firstBucket; k <= lastBucket; k += 1000) {
        const auto it = measured.find(k);
        snap.history.push_back(it == measured.end() ? 0.0 : it->second);
    }
    snap.history.back() = std::max(snap.history.back(), live.output);

    std::map<std::string, std::vector<engine::Response>> byAgent;
    for (const auto& r : recent) byAgent[r.agentId].push_back(r);
    const auto list = engine::agentList(recent, agents, nowMs);
    std::map<std::string, int> depth;
    std::map<std::string, const engine::Agent*> engineAgents;
    for (const auto& a : agents) engineAgents[a.id] = &a;
    for (const auto& id : list.order) {
        const auto parent = list.parents.find(id);
        depth[id] = parent == list.parents.end() ? 0 : depth[parent->second] + 1;
        const auto& info = agents_.at(id);
        AgentRow row;
        row.id = id;
        row.source = info.source;
        row.project = info.project;
        row.model = info.model;
        row.phase = info.phase;
        if (row.phase == Phase::Generating && info.lastLineMs && nowMs - *info.lastLineMs > kStaleGeneratingMs) row.phase = Phase::Idle;
        engine::Agent self = *engineAgents.at(id);
        if (row.phase == Phase::Idle) self.phase = Phase::Idle;
        row.liveOutput = engine::liveRate(byAgent[id], {self}, nowMs).output;
        row.depth = depth[id];
        row.subagent = info.isSubagent;
        row.parentId = self.parentId;
        row.lastLineMs = info.lastLineMs;
        if (const auto effort = effort_speed::effort(info.modes)) row.effort = effort->mode;
        if (const auto speed = effort_speed::speed(info.modes)) row.speed = speed->mode;
        if (row.phase == Phase::Generating) ++snap.generating;
        if (row.phase == Phase::Tools) ++snap.tools;
        snap.agents.push_back(std::move(row));
    }

    scoreTasks(snap, recent, agents, nowMs);

    for (const auto kind : MonitorConfig::allSources()) {
        SourceRow row;
        row.source = kind;
        row.enabled = config_.enabled.contains(kind);
        const auto [path, available] = sourceStatus(kind);
        row.path = path;
        row.available = available;
        row.sessions = sessionCount(kind, todayStartMs);
        if (row.enabled) {
            std::vector<engine::Response> own, ownToday;
            for (const auto& r : responses) {
                if (r.source != kind) continue;
                ownToday.push_back(r);
                if (r.endMs >= nowMs - Store::kRetentionMs) own.push_back(r);
            }
            const auto stats = engine::todayStats(ownToday, todayStartMs, nowMs);
            row.todayOutput = stats.output;
            row.todayInput = stats.input;
            row.todayRequests = stats.requests;
            row.todayCostMicroUsd = stats.costMicroUsd;
            row.unpricedRequests = stats.unpricedRequests;
            std::vector<engine::Agent> ownAgents;
            for (const auto& a : agents) {
                if (a.source == kind) ownAgents.push_back(a);
            }
            row.liveOutput = engine::liveRate(own, ownAgents, nowMs).output;
            const auto ownCost = engine::liveCost(own, ownAgents, nowMs);
            row.liveCost = ownCost.microUsdPerSecond;
            row.liveCostKnownShare = ownCost.knownShare;
            row.activeAgents = static_cast<int>(std::count_if(snap.agents.begin(), snap.agents.end(), [&](const AgentRow& a) {
                return a.source == kind && a.phase == Phase::Generating;
            }));
        }
        snap.sources.push_back(std::move(row));
    }

    snap.warnings.partialHistory = partialHistory_;
    std::int64_t malformed = removedMalformed_;
    int unreadable = 0;
    for (const auto& [path, file] : files_) {
        malformed += file->parser->malformedLines();
        unreadable += file->unreadable ? 1 : 0;
        if (const auto* conversation = dynamic_cast<const antigravity::Parser*>(file->parser.get()); conversation && conversation->unsupported()) {
            if (std::find(snap.warnings.unsupported.begin(), snap.warnings.unsupported.end(), SourceKind::Antigravity) ==
                snap.warnings.unsupported.end()) {
                snap.warnings.unsupported.push_back(SourceKind::Antigravity);
            }
        }
    }
    if (openClaw_) malformed += openClaw_->malformedLines();
    if (hermes_) malformed += hermes_->malformedLines();
    if (openCode_) {
        if (openCode_->status() == opencode::Status::Unreadable) ++unreadable;
        if (openCode_->status() == opencode::Status::Unsupported) snap.warnings.unsupported.push_back(SourceKind::OpenCode);
    }
    if (zcode_) {
        if (zcode_->unreadable()) ++unreadable;
        if (zcode_->unsupported()) snap.warnings.unsupported.push_back(SourceKind::ZCode);
    }
    if (openClaw_ && openClaw_->unsupported()) snap.warnings.unsupported.push_back(SourceKind::OpenClaw);
    snap.warnings.malformedLines = malformed;
    snap.warnings.unreadableFiles = unreadable;
    snap.warnings.unfinalized = today.unfinalized;
    return snap;
}

}  // namespace agentspeed
