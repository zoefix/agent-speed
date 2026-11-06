#include "agentspeed/devin_source.hpp"
#include "agentspeed/monitor.hpp"

// Devin CLI (METRICS 4.17): one SQLite database per root of the list. Rediscovery looks the user locations' databases
// up again, and a list of databases that changed starts every reader over.
namespace agentspeed {

void Monitor::initDevin() {
    const auto& roots = config_.locations.devinRoots;
    if (config_.enabled.contains(SourceKind::Devin) && !roots.empty()) devin_ = std::make_unique<devin::Source>(roots, deduper_);
}

void Monitor::seedDevin(std::int64_t todayStartMs) {
    if (!devin_ || devinSeeded_) return;
    for (const auto& update : devin_->seed(todayStartMs)) applyDevin(update, true);
    devinSeeded_ = true;
}

// The 1-second check reads every database whose file or `-wal` changed; rediscovery resolves the roots again first.
bool Monitor::refreshDevin(bool discover, std::int64_t todayStartMs) {
    if (!devin_ || !devinSeeded_) return false;
    bool changed = false;
    for (const auto& update : discover ? devin_->rediscover(todayStartMs) : devin_->refresh()) changed |= applyDevin(update, update.seeded);
    return changed;
}

bool Monitor::applyDevin(const devin::Update& update, bool seeded) {
    for (const auto& id : update.removed) {
        devinPushed_.erase(id);
        removeAgent(id);
    }
    bool pushed = false;
    for (const auto& [id, agent] : update.agents) {
        if (!agent.lastLineMs) continue;
        if (const auto known = devinPushed_.find(id); known != devinPushed_.end() && known->second == agent) continue;
        devinPushed_[id] = agent;
        // No sub-agents: the database names no parent (METRICS 6.7).
        AgentInfo info;
        info.id = id;
        info.source = SourceKind::Devin;
        info.project = agent.project;
        info.model = agent.model;
        info.phase = agent.phase;
        info.inFlightStartMs = agent.inFlightStartMs;
        info.lastLineMs = agent.lastLineMs;
        agents_[id] = std::move(info);
        pushed = true;
    }
    for (const auto& [response, completion] : update.responses) upsert(response, completion, seeded);
    return pushed || !update.responses.empty() || !update.removed.empty();
}

// METRICS 2.1: the first root that names an existing database, else the first root, and whether any database exists.
std::pair<std::string, bool> Monitor::devinStatus() const {
    if (devin_) return {devin_->displayRoot(), devin_->available()};
    const auto& roots = config_.locations.devinRoots;
    return {devin::displayRoot(roots), !devin::databases(roots).empty()};
}

}  // namespace agentspeed
