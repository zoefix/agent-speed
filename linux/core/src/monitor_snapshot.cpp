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
constexpr std::int64_t kListHorizonMs = 600'000;

}  // namespace

work::ActionReader* Monitor::actionsOf(const std::string& id) const {
    if (const auto it = files_.find(id); it != files_.end()) return it->second->actions.get();
    if (const auto it = windowActions_.find(id); it != windowActions_.end()) return it->second.get();
    return nullptr;
}

std::unique_ptr<work::ActionReader> Monitor::makeActions(const std::string& id, SourceKind source) {
    auto reader = std::make_unique<work::ActionReader>(id);
    reader->setResultHandler([this, id, source](const work::Action& action) {
        if (!config_.enabled.contains(source)) return;
        const report::ModelKey key{store_->reportModelId(action.model, store_->providerOf(id)), source};
        perfLedger_.call(id, key, action, clockMs_, action.seeded);
    });
    return reader;
}

// METRICS 6.9, 6.10: the listed agents' performance scores, work signals and thinking, then the sets and board groups.
void Monitor::scoreAgents(Snapshot& snap, const std::vector<engine::Response>& recent, const std::vector<engine::Agent>& agents,
                          std::int64_t nowMs) {
    const auto thinking = engine::thinking(recent, agents, nowMs);
    lastThinking_ = thinking;
    std::map<std::string, std::vector<const engine::Response*>> byAgent;
    for (const auto& r : recent) byAgent[r.agentId].push_back(&r);
    static const std::deque<work::Action> kNoActions;
    std::map<std::string, perf::Valued> values;
    for (auto& row : snap.agents) {
        if (const auto it = thinking.perAgent.find(row.id); it != thinking.perAgent.end()) {
            row.liveThinking = it->second.liveThinking();
            row.thinkingShare = it->second.thinkingShare();
        }
        const auto& info = agents_.at(row.id);
        const auto* reader = actionsOf(row.id);
        const auto& actions = reader ? reader->actions() : kNoActions;
        AgentPerf value;
        value.score = perf::evaluate(info.source, info.phase, info.lastLineMs, byAgent[row.id], actions, nowMs);
        if (value.score.score) {
            const auto baseline = perfBaselines_.find({info.source, value.score.model.value_or("")});
            perf::applyBaseline(value.score, baseline == perfBaselines_.end() ? std::nullopt : std::optional<double>(baseline->second));
        }
        value.workSignals = work::signalsOf(actions, nowMs);
        values[row.id] = {row.id, info.lastLineMs, value.score.form, value.score.score};
        row.perf = std::move(value);
    }

    for (const auto& sources : scoreSets_) {
        const auto inSet = [&](SourceKind kind) { return !sources || std::find(sources->begin(), sources->end(), kind) != sources->end(); };
        ScoreSet set;
        set.sources = sources;
        std::vector<perf::Valued> members;
        for (const auto& a : snap.agents) {
            if (!inSet(a.source)) continue;
            members.push_back(values[a.id]);
            if (a.phase == Phase::Generating) set.generating = true;
        }
        set.value = perf::highest(members);
        for (const auto& a : snap.agents) {
            if (!inSet(a.source) || !a.perf) continue;
            if (set.value.form != perf::Form::None && a.perf->score.form == set.value.form) ++set.scored;
            if (set.value.agent && a.id == *set.value.agent) set.provisional = a.perf->score.provisional;
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

    std::vector<engine::Agent> listed;
    for (const auto& a : snap.agents) {
        engine::Agent agent;
        agent.id = a.id;
        agent.source = a.source;
        agent.parentId = a.parentId;
        listed.push_back(std::move(agent));
    }
    for (const auto& [root, members] : engine::boardGroups(listed)) {
        std::vector<perf::Valued> group;
        for (const auto& id : members) group.push_back(values[id]);
        snap.scoreGroups.push_back({root, members, perf::highest(group)});
    }
}

// METRICS 11.2: agents of the 6.7 base set (staleness applied, no ancestor closure) per source, generating agents per
// reported model id, and the report ledger (copied only after a change).
report::Facts Monitor::reportFacts(std::int64_t nowMs, const Snapshot& snap) {
    report::Facts facts;
    for (const auto& [id, info] : agents_) {
        if (!config_.enabled.contains(info.source)) continue;
        auto phase = info.phase;
        if (phase == Phase::Generating && info.lastLineMs && nowMs - *info.lastLineMs > kStaleGeneratingMs) phase = Phase::Idle;
        const bool recent = info.lastLineMs && nowMs - *info.lastLineMs <= kListHorizonMs;
        if (!recent && phase != Phase::Generating) continue;
        auto& counts = facts.agents[info.source];
        ++counts.active;
        if (phase == Phase::Generating) {
            ++counts.generating;
            ++facts.generatingModels[{store_->reportModelId(info.model, store_->providerOf(id)), info.source}];
        } else if (phase == Phase::Tools) {
            ++counts.tools;
        }
    }
    if (store_->ledger().counters().version() != reportCountersVersion_) {
        reportCounters_ = std::make_shared<const report::Counters>(store_->ledger().counters());
        reportCountersVersion_ = store_->ledger().counters().version();
    }
    facts.counters = reportCounters_;
    facts.epoch = epoch_;
    // METRICS 11.10: the agents list's values under their current model's report id, the thinking sums, the ledger.
    auto perf = std::make_shared<report::PerfFacts>();
    for (const auto& row : snap.agents) {
        if (!row.perf || !row.perf->score.score) continue;
        perf->agents.push_back({row.source, store_->reportModelId(row.model, store_->providerOf(row.id)), row.perf->score.form, row.perf->score.score});
    }
    perf->thinking = lastThinking_.total.liveThinking();
    for (const auto& [model, window] : lastThinking_.perModel)
        perf->modelThinking[store_->reportModelId(model.empty() ? std::nullopt : std::optional<std::string>(model), std::nullopt)] += window.liveThinking();
    if (perfLedger_.version() != perfRowsVersion_) {
        perfRows_ = std::make_shared<const report::PerfRows>(perfLedger_.rows());
        perfRowsVersion_ = perfLedger_.version();
    }
    perf->rows = perfRows_;
    perf->generation = perfLedger_.generation();
    perf->responses = perfLedger_.responses();
    perf->calls = perfLedger_.calls();
    perf->counted = perf->responses + perf->calls;
    facts.perf = perf;
    return facts;
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
        const auto rate = engine::liveRate(byAgent[id], {self}, nowMs);
        row.liveOutput = rate.output;
        row.liveTotal = rate.total;
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

    scoreAgents(snap, recent, agents, nowMs);
    snap.traffic = traffic_->view(nowMs);
    snap.report = reportFacts(nowMs, snap);

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
            const auto ownRate = engine::liveRate(own, ownAgents, nowMs);
            row.liveOutput = ownRate.output;
            row.liveTotal = ownRate.total;
            row.liveEstimatedShare = ownRate.estimatedShare;
            const auto ownCost = engine::liveCost(own, ownAgents, nowMs);
            row.liveCost = ownCost.microUsdPerSecond;
            row.liveCostKnownShare = ownCost.knownShare;
            row.activeAgents = static_cast<int>(std::count_if(snap.agents.begin(), snap.agents.end(), [&](const AgentRow& a) {
                return a.source == kind && a.phase == Phase::Generating;
            }));
        }
        snap.sources.push_back(std::move(row));
    }
    // METRICS 6.8: only enabled sources count; the live value of the set joins its peak.
    for (const auto& set : peakSets_) {
        if (!set) {
            snap.peakSets.push_back(snap.peakToday);
            continue;
        }
        std::vector<SourceKind> enabled;
        double setLive = 0;
        for (const auto& row : snap.sources) {
            if (!row.enabled || std::find(set->begin(), set->end(), row.source) == set->end()) continue;
            enabled.push_back(row.source);
            setLive += row.liveOutput;
        }
        snap.peakSets.push_back(std::max(engine::setPeak(responses, enabled, todayStartMs, nowMs), setLive));
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
