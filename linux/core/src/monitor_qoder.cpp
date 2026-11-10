#include "agentspeed/monitor.hpp"
#include "agentspeed/qoder_source.hpp"

// Qoder IDE (METRICS 4.18): one SQLite database per root of the list. Rediscovery looks the user locations' databases
// up again, and a list of databases that changed starts every reader over.
namespace agentspeed {

void Monitor::initQoder() {
    const auto& roots = config_.locations.qoderRoots;
    if (config_.enabled.contains(SourceKind::Qoder) && !roots.empty()) qoder_ = std::make_unique<qoder::Source>(roots, deduper_);
}

void Monitor::seedQoder(std::int64_t todayStartMs) {
    if (!qoder_ || qoderSeeded_) return;
    for (const auto& update : qoder_->seed(todayStartMs)) applyQoder(update, true);
    qoderSeeded_ = true;
}

// The 1-second check reads every database whose file or `-wal` changed; rediscovery resolves the roots again first.
bool Monitor::refreshQoder(bool discover, std::int64_t todayStartMs) {
    if (!qoder_ || !qoderSeeded_) return false;
    bool changed = false;
    for (const auto& update : discover ? qoder_->rediscover(todayStartMs) : qoder_->refresh()) changed |= applyQoder(update, update.seeded);
    return changed;
}

bool Monitor::applyQoder(const qoder::Update& update, bool seeded) {
    for (const auto& id : update.removed) {
        qoderPushed_.erase(id);
        removeAgent(id);
    }
    bool pushed = false;
    for (const auto& [id, agent] : update.agents) {
        if (!agent.lastLineMs) continue;
        if (const auto known = qoderPushed_.find(id); known != qoderPushed_.end() && known->second == agent) continue;
        qoderPushed_[id] = agent;
        AgentInfo info;
        info.id = id;
        info.source = SourceKind::Qoder;
        info.project = agent.project;
        info.model = agent.model;
        info.isSubagent = agent.isSubagent;
        info.phase = agent.phase;
        info.inFlightStartMs = agent.inFlightStartMs;
        info.lastLineMs = agent.lastLineMs;
        // A sub-agent session's parent is its database's session of `parent_session_id`, when tracked (METRICS 6.7).
        pushInfo(std::move(info), {id, LinkKind::QoderSession, id, agent.parentAgentId, std::nullopt, 0});
        pushed = true;
    }
    for (const auto& [response, completion] : update.responses) upsert(response, completion, seeded);
    return pushed || !update.responses.empty() || !update.removed.empty();
}

// METRICS 2.1: the first root that names an existing database, else the first root, and whether any database exists.
std::pair<std::string, bool> Monitor::qoderStatus() const {
    if (qoder_) return {qoder_->displayRoot(), qoder_->available()};
    const auto& roots = config_.locations.qoderRoots;
    return {qoder::displayRoot(roots), !qoder::databases(roots).empty()};
}

}  // namespace agentspeed
