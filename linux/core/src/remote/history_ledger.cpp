#include "agentspeed/remote_history.hpp"
#include "agentspeed/remote_protocol.hpp"

namespace agentspeed::remote {

HistoryLedger::HistoryLedger(history::RemoteLimits limits, std::size_t flushBytes, Zone zone)
    : sync_(limits, flushBytes), zone_(std::move(zone)) {
    if (!zone_) zone_ = [] { return currentZone().id; };
}

std::vector<std::string> HistoryLedger::connectedHosts() const {
    std::vector<std::string> out;
    for (const auto& [host, connection] : sync_.connected()) out.push_back(host);
    return out;
}

void HistoryLedger::load(history::Store& store) {
    try {
        rows_ = HistoryRows::load(store);
    } catch (const std::exception&) {
        rows_ = HistoryRows();
    }
    ++version_;
    applyNames(store);
}

void HistoryLedger::reset() {
    sync_.dropBatches();
    requested_.clear();
    unstaged_.clear();
    rows_ = HistoryRows();
    ++version_;
}

HistoryEffect HistoryLedger::receive(const HostEvent& event, history::Store& store) {
    auto action = sync_.apply(event);
    if (const auto* request = std::get_if<HistorySyncState::Request>(&action)) {
        // Rows of another zone are not compared: the whole history comes again and replaces them.
        const auto current = zone_();
        requested_[request->host] = {event.connection, current};
        const auto stored = rows_.zones().find(request->host);
        const bool sameZone = stored == rows_.zones().end() || stored->second == current;
        return HistoryEffect::request(request->host, sameZone ? rows_.digests(request->host) : std::map<std::string, std::string>{});
    }
    if (auto* stage = std::get_if<HistorySyncState::Stage>(&action)) {
        if (stage->first) unstaged_.erase(stage->host);
        try {
            store.stageRemote(stage->days, stage->host, stage->first);
        } catch (const std::exception&) {
            unstaged_.insert(stage->host);
        }
        return HistoryEffect::none();
    }
    if (auto* commit = std::get_if<HistorySyncState::Commit>(&action)) {
        if (commit->staged && unstaged_.erase(commit->host) > 0) {
            requested_.erase(commit->host);
            try {
                store.clearRemoteStaging(commit->host);
            } catch (const std::exception&) {
            }
            return HistoryEffect::none();
        }
        return this->commit(std::move(commit->days), commit->host, commit->connection, commit->staged, store);
    }
    if (const auto* overflow = std::get_if<HistorySyncState::Overflow>(&action)) {
        unstaged_.erase(overflow->host);
        try {
            store.clearRemoteStaging(overflow->host);
        } catch (const std::exception&) {
        }
        return HistoryEffect::none();
    }
    if (const auto* tooLarge = std::get_if<HistorySyncState::TooLarge>(&action)) {
        requested_.erase(tooLarge->host);
        return tooLarge_.insert(tooLarge->host).second ? HistoryEffect::tooLargeChanged() : HistoryEffect::none();
    }
    return HistoryEffect::none();
}

// A `historyDone`: the batch merges into the host's rows, or replaces them when its request ran in another zone than
// they were computed in. Rows stored without a zone take the batch's. Without a request of this connection, the rows
// keep their zone.
HistoryEffect HistoryLedger::commit(std::vector<history::RemoteDay> days, const std::string& host, int connection, bool staged,
                                    history::Store& store) {
    std::optional<std::string> requestZone;
    if (const auto it = requested_.find(host); it != requested_.end()) {
        if (it->second.first == connection) requestZone = it->second.second;
        requested_.erase(it);
    }
    const auto storedIt = rows_.zones().find(host);
    const std::optional<std::string> stored = storedIt == rows_.zones().end() ? std::nullopt : std::optional<std::string>(storedIt->second);
    const auto zone = requestZone ? *requestZone : stored ? *stored : zone_();
    const bool replace = stored && *stored != zone;
    const bool relabels = !stored && rows_.hosts().contains(host);
    if (!replace && !staged && days.empty() && !relabels) return succeeded(host);
    const auto nameIt = names_.find(host);
    const std::optional<std::string> name = nameIt == names_.end() ? std::nullopt : std::optional<std::string>(nameIt->second);
    try {
        store.commitRemote(days, host, name, zone, replace, staged, sync_.limits());
    } catch (const history::RemoteTooLarge&) {
        return tooLarge_.insert(host).second ? HistoryEffect::tooLargeChanged() : HistoryEffect::none();
    } catch (const std::exception&) {
        // Nothing is stored, so the next sync sends the days again.
        return HistoryEffect::none();
    }
    succeeded(host);
    if (staged) {
        load(store);
    } else {
        rows_.store(days, host, name, zone, replace);
        ++version_;
    }
    return HistoryEffect::changed();
}

HistoryEffect HistoryLedger::succeeded(const std::string& host) {
    return tooLarge_.erase(host) > 0 ? HistoryEffect::tooLargeChanged() : HistoryEffect::none();
}

HistoryEffect HistoryLedger::setHosts(std::map<std::string, std::string> names, history::Store* store) {
    names_ = std::move(names);
    if (!store) return HistoryEffect::none();
    return applyNames(*store) ? HistoryEffect::changed() : HistoryEffect::none();
}

HistoryEffect HistoryLedger::deleteHost(const std::string& id, history::Store& store) {
    sync_.drop(id);
    requested_.erase(id);
    unstaged_.erase(id);
    const bool wasTooLarge = tooLarge_.erase(id) > 0;
    const auto unchanged = wasTooLarge ? HistoryEffect::tooLargeChanged() : HistoryEffect::none();
    try {
        store.deleteRemoteHost(id);
    } catch (const std::exception&) {
        return unchanged;
    }
    if (!rows_.hosts().contains(id) && !rows_.names().contains(id)) return unchanged;
    rows_.remove(id);
    ++version_;
    return HistoryEffect::changed();
}

bool HistoryLedger::applyNames(history::Store& store) {
    bool changed = false;
    for (const auto& host : rows_.hosts()) {
        const auto name = names_.find(host);
        if (name == names_.end()) continue;
        const auto stored = rows_.names().find(host);
        if (stored != rows_.names().end() && stored->second == name->second) continue;
        try {
            store.renameRemoteHost(host, name->second);
        } catch (const std::exception&) {
            continue;
        }
        rows_.rename(host, name->second);
        changed = true;
    }
    if (changed) ++version_;
    return changed;
}

}  // namespace agentspeed::remote
