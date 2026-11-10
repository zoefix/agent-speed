#include <algorithm>

#include "agentspeed/fs_util.hpp"
#include "agentspeed/harness.hpp"
#include "agentspeed/hermes.hpp"
#include "agentspeed/monitor.hpp"
#include "agentspeed/monitor_store.hpp"
#include "agentspeed/openclaw.hpp"
#include "agentspeed/opencode_set.hpp"
#include "agentspeed/tz.hpp"
#include "agentspeed/zcode_set.hpp"

// The database and log sources (METRICS 4.5, 4.10, 4.14, 4.15, 4.17, 4.18): each keeps its own state and hands the monitor
// updates.
namespace agentspeed {

std::shared_ptr<const TimeZone> localZone(const Environment& environment) {
    if (auto tz = environment("TZ"); tz && !tz->empty()) {
        const auto id = tz->front() == ':' ? tz->substr(1) : *tz;
        if (auto zone = TimeZone::load(id)) return zone;
    }
    if (auto zone = TimeZone::load("localtime")) return zone;
    return TimeZone::utc();
}

void Monitor::initSources() {
    const auto& l = config_.locations;
    if (config_.enabled.contains(SourceKind::OpenCode) && !l.openCodeRoots.empty()) {
        openCode_ = std::make_unique<opencode::SourceSet>(l.openCodeRoots, deduper_);
    }
    if (config_.enabled.contains(SourceKind::ZCode) && !l.zcodeRoots.empty()) {
        zcode_ = std::make_unique<zcode::SourceSet>(l.zcodeRoots, deduper_);
    }
    if (config_.enabled.contains(SourceKind::OpenClaw) && (l.openClawAutomatic || !l.openClawUserRoots.empty())) {
        openClaw_ = std::make_unique<openclaw::Source>(
            [this] { return paths::openClawDatabases(config_.locations.openClawRootsNow(environment_, home_)); }, deduper_);
        openClaw_->setObserverFactory([this](openclaw::Parser& parser) -> LineObserver* {
            auto& reader = windowActions_[parser.agentId()];
            reader = makeActions(parser.agentId(), SourceKind::OpenClaw);
            reader->setSeeding(!openClawSeeded_);
            return reader.get();
        });
    }
    if (config_.enabled.contains(SourceKind::Hermes) && !l.hermesRoots.empty()) {
        hermes_ = std::make_unique<hermes::Source>(l.hermesRoots, deduper_, zone_);
    }
    initDevin();
    initQoder();
}

void Monitor::seedSources(std::int64_t todayStartMs) {
    if (openCode_ && !openCodeSeeded_) {
        applyOpenCode(openCode_->seed(todayStartMs), true);
        openCodeSeeded_ = true;
    }
    if (zcode_ && !zcodeSeeded_) {
        applyZCode(zcode_->seed(todayStartMs), true);
        zcodeSeeded_ = true;
    }
    if (openClaw_ && !openClawSeeded_) {
        applyOpenClaw(openClaw_->seed(todayStartMs), true);
        openClawSeeded_ = true;
        for (auto& [id, reader] : windowActions_) reader->setSeeding(false);
    }
    if (hermes_ && !hermesSeeded_) {
        applyHermes(hermes_->seed(todayStartMs), true);
        partialHistory_ |= hermes_->partialHistory();
        hermesSeeded_ = true;
    }
    seedDevin(todayStartMs);
    seedQoder(todayStartMs);
}

bool Monitor::refreshSources(bool discover, std::int64_t nowMs, std::int64_t todayStartMs) {
    bool changed = false;
    if (openCode_ && openCodeSeeded_) {
        if (discover) {
            const auto update = openCode_->rediscover(nowMs, todayStartMs);
            changed |= applyOpenCode(update.seeded, true);
            changed |= applyOpenCode(update.live, false);
        } else if (const auto update = openCode_->refreshIfChanged()) {
            changed |= applyOpenCode(*update, false);
        }
    }
    if (zcode_ && zcodeSeeded_) changed |= applyZCode(zcode_->refresh(), false);
    if (openClaw_ && openClawSeeded_) changed |= applyOpenClaw(openClaw_->refresh(discover), false);
    if (hermes_ && hermesSeeded_) changed |= applyHermes(hermes_->refresh(discover), false);
    changed |= refreshDevin(discover, todayStartMs);
    changed |= refreshQoder(discover, todayStartMs);
    return changed;
}

void Monitor::upsert(const ParsedResponse& response, bool completion, bool seeded) {
    store_->setSeeding(seeded);
    store_->onResponse(response, completion);
    store_->setSeeding(false);
}

bool Monitor::applyOpenCode(const opencode::Update& update, bool seeded) {
    for (const auto& id : update.removed) removeAgent(id);
    for (const auto& [id, state] : update.agents) {
        if (!state.lastLineMs) continue;
        AgentInfo info;
        info.id = state.id;
        info.source = SourceKind::OpenCode;
        info.project = state.project;
        info.model = state.model;
        info.isSubagent = state.isSubagent;
        info.phase = state.phase;
        info.inFlightStartMs = state.inFlightStartMs;
        info.lastLineMs = state.lastLineMs;
        info.modes = state.modes;
        pushInfo(std::move(info), {state.id, LinkKind::OpenCodeSession, state.sessionId, state.parentSessionId, state.createdMs, 0});
    }
    for (const auto& [response, completion] : update.responses) upsert(response, completion, seeded);
    return !update.empty();
}

bool Monitor::applyZCode(const zcode::Update& update, bool seeded) {
    for (const auto& id : update.removed) removeAgent(id);
    for (const auto& agent : update.agents) {
        AgentInfo info;
        info.id = agent.id;
        info.source = SourceKind::ZCode;
        info.project = agent.project;
        info.model = agent.model;
        info.isSubagent = agent.parentSessionId.has_value();
        info.phase = agent.phase;
        info.inFlightStartMs = agent.startMs;
        info.lastLineMs = agent.lastMs;
        info.modes = agent.modes;
        pushInfo(std::move(info), {agent.id, LinkKind::ZCodeSession, agent.sessionId, agent.parentSessionId, agent.createdMs, 0});
    }
    for (const auto& [response, completion] : update.responses) upsert(response, completion, seeded);
    return update.changed();
}

bool Monitor::applyOpenClaw(const openclaw::Update& update, bool seeded) {
    for (const auto& id : update.removed) {
        openClawPushed_.erase(id);
        windowActions_.erase(id);
        removeAgent(id);
    }
    for (auto* parser : openClaw_->parsers()) {
        const auto known = openClawPushed_.find(parser->agentId());
        if (known != openClawPushed_.end() && known->second == parser->version()) continue;
        openClawPushed_[parser->agentId()] = parser->version();
        if (!parser->lastLineMs()) continue;
        AgentInfo info;
        info.id = parser->agentId();
        info.source = SourceKind::OpenClaw;
        info.project = parser->project();
        info.model = parser->model();
        info.isSubagent = parser->isSubagent();
        info.phase = parser->phase();
        info.inFlightStartMs = parser->inFlightStartMs();
        info.lastLineMs = parser->lastLineMs();
        info.modes = parser->modes();
        pushInfo(std::move(info), parser->link());
    }
    for (const auto& [response, completion] : update.responses) upsert(response, completion, seeded);
    return update.changed || !update.responses.empty() || !update.removed.empty();
}

bool Monitor::applyHermes(const hermes::Update& update, bool seeded) {
    for (const auto& id : update.removed) {
        hermesPushed_.erase(id);
        agents_.erase(id);
    }
    bool pushed = false;
    for (auto* set : hermes_->sets()) {
        for (const auto* agent : set->agents()) {
            if (!agent->lastLineMs) continue;
            const auto known = hermesPushed_.find(agent->agentId);
            if (known != hermesPushed_.end() && known->second == agent->version) continue;
            hermesPushed_[agent->agentId] = agent->version;
            AgentInfo info;
            info.id = agent->agentId;
            info.source = SourceKind::Hermes;
            info.model = agent->model;
            info.isSubagent = agent->isSubagent;
            info.phase = agent->phase;
            info.inFlightStartMs = agent->inFlightStartMs;
            info.lastLineMs = agent->lastLineMs;
            info.sessionModes = agent->sessionModes;
            info.configuredModes = agent->configuredModes;
            agents_[info.id] = std::move(info);
            pushed = true;
        }
    }
    for (const auto& [response, completion] : update.responses) upsert(response, completion, seeded);
    return pushed || update.changed || !update.responses.empty() || !update.removed.empty();
}

// METRICS 2.1 Detected, counted, named: detected when any root exists; the path is the first detected root, else the
// first root.
std::pair<std::string, bool> Monitor::sourceStatus(SourceKind kind) const {
    const auto& l = config_.locations;
    const auto firstOf = [](const std::vector<std::string>& roots, auto exists) -> std::pair<std::string, bool> {
        for (const auto& root : roots) {
            if (exists(root)) return {root, true};
        }
        return {roots.empty() ? std::string() : roots.front(), false};
    };
    switch (kind) {
        case SourceKind::ZCode:
            if (zcode_) return {zcode_->displayRoot(), zcode_->available()};
            return firstOf(l.zcodeRoots, [](const std::string& r) { return fsx::isRegularFile(harness::zcodeDatabase(r)); });
        case SourceKind::OpenCode:
            if (openCode_) return {openCode_->displayRoot(), openCode_->databaseExists()};
            return firstOf(l.openCodeRoots, [](const std::string& r) { return fsx::isRegularFile(opencode::Database::databasePath(r)); });
        case SourceKind::OpenClaw: return firstOf(l.openClawRootsNow(environment_, home_), paths::openClawExists);
        case SourceKind::Devin: return devinStatus();
        case SourceKind::Qoder: return qoderStatus();
        case SourceKind::Hermes: {
            std::vector<std::string> homes;
            for (const auto& root : l.hermesRoots) homes.push_back(root.path);
            return firstOf(homes, [](const std::string& r) { return fsx::isDirectory(r); });
        }
        default: break;
    }
    const auto family = familyOf(kind);
    std::string first, detected;
    for (const auto& root : roots_) {
        if (root.family != family) continue;
        if (first.empty()) first = root.path;
        if (detected.empty() && root.available) detected = root.path;
    }
    if (family == RootFamily::Antigravity) {
        // The row names the store, not its `conversations` folder.
        if (!detected.empty()) detected = fsx::parentOf(detected);
        if (!l.antigravityStores.empty()) first = l.antigravityStores.front();
    }
    return {detected.empty() ? first : detected, !detected.empty()};
}

}  // namespace agentspeed
