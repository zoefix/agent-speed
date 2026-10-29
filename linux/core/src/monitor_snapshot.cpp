#include <algorithm>

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
