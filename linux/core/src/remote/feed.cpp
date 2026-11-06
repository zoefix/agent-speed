#include "agentspeed/remote_feed.hpp"

#include <algorithm>

namespace agentspeed::remote {

namespace {

// Changes a monitor has not taken yet beyond this are dropped; the next take replays every host instead.
constexpr std::size_t kMaxPending = 500'000;

bool olderFirst(const RemoteResponse* a, const RemoteResponse* b) {
    return a->record.endMs != b->record.endMs ? a->record.endMs < b->record.endMs : a->record.key < b->record.key;
}

}  // namespace

bool HostMirror::isLive(const Status& status) { return status.code == StatusCode::Connected || status.isSeeding(); }

std::vector<FeedChange> HostMirror::apply(const HostEvent& event, std::int64_t nowMs, std::int64_t todayStartMs) {
    if (event.connection != 0 && event.connection != connection_) {
        connection_ = event.connection;
        ready_ = false;
        sentAgents_.clear();
        sentParents_ = false;
    }
    if (const auto* status = std::get_if<HostStatus>(&event.kind)) {
        status_ = *status;
        std::vector<FeedChange> changes{feed::Status{hostId_, *status}};
        if (!isLive(status->status) && idleAgents()) changes.emplace_back(feed::Idle{hostId_});
        return changes;
    }
    if (const auto* message = std::get_if<HostEvent::Message>(&event.kind)) return applyMessage(message->message, message->seeded, nowMs, todayStartMs);
    return {};
}

std::vector<FeedChange> HostMirror::applyMessage(const RelayMessage& message, bool seeded, std::int64_t nowMs, std::int64_t todayStartMs) {
    if (const auto* agent = std::get_if<RemoteAgent>(&message)) {
        sentAgents_.insert(agent->id);
        agents_[agent->id] = *agent;
        const auto parent = parents_.find(agent->id);
        return {feed::Agent{*agent, parent == parents_.end() ? std::nullopt : std::optional<std::string>(parent->second)}};
    }
    if (const auto* removed = std::get_if<AgentRemoved>(&message)) {
        sentAgents_.erase(removed->id);
        if (agents_.erase(removed->id) == 0) return {};
        return {feed::AgentRemoved{removed->id}};
    }
    if (const auto* parents = std::get_if<Parents>(&message)) {
        sentParents_ = true;
        parents_ = parents->links;
        return {feed::Parents{hostId_, parents->links}};
    }
    if (const auto* response = std::get_if<RemoteResponse>(&message)) {
        store(*response, nowMs, todayStartMs);
        return {feed::Response{*response, !seeded}};
    }
    if (const auto* tool = std::get_if<RemoteTool>(&message)) return {feed::Tool{*tool}};
    if (const auto* report = std::get_if<SourceReport>(&message)) {
        report_ = *report;
        return {feed::Report{hostId_, *report}};
    }
    if (std::holds_alternative<Ready>(message)) {
        ready_ = true;
        std::vector<FeedChange> changes;
        for (auto it = agents_.begin(); it != agents_.end();) {
            if (sentAgents_.contains(it->first)) {
                ++it;
                continue;
            }
            changes.emplace_back(feed::AgentRemoved{it->first});
            it = agents_.erase(it);
        }
        if (!sentParents_ && !parents_.empty()) {
            parents_.clear();
            changes.emplace_back(feed::Parents{hostId_, {}});
        }
        return changes;
    }
    return {};
}

std::vector<FeedChange> HostMirror::replay() const {
    std::vector<FeedChange> changes;
    if (status_) changes.emplace_back(feed::Status{hostId_, *status_});
    if (report_) changes.emplace_back(feed::Report{hostId_, *report_});
    if (!parents_.empty()) changes.emplace_back(feed::Parents{hostId_, parents_});
    for (const auto& [id, agent] : agents_) {
        const auto parent = parents_.find(id);
        changes.emplace_back(feed::Agent{agent, parent == parents_.end() ? std::nullopt : std::optional<std::string>(parent->second)});
    }
    std::vector<const RemoteResponse*> ordered;
    ordered.reserve(responses_.size());
    for (const auto& [_, response] : responses_) ordered.push_back(&response);
    std::sort(ordered.begin(), ordered.end(), olderFirst);
    for (const auto* response : ordered) changes.emplace_back(feed::Response{*response, false});
    return changes;
}

bool HostMirror::idleAgents() {
    bool changed = false;
    for (auto& [_, agent] : agents_) {
        if (agent.phase == Phase::Idle && !agent.inFlightStartMs) continue;
        agent.phase = Phase::Idle;
        agent.inFlightStartMs.reset();
        changed = true;
    }
    return changed;
}

// Keeps today's responses and the retention window, at most `responseLimit_` (the oldest go first).
void HostMirror::store(const RemoteResponse& response, std::int64_t nowMs, std::int64_t todayStartMs) {
    responses_[response.record.key] = response;
    if (!lastPruneMs_ || nowMs - *lastPruneMs_ >= kPruneIntervalMs) {
        lastPruneMs_ = nowMs;
        const auto cutoff = std::min(todayStartMs, nowMs - kRetentionMs);
        std::erase_if(responses_, [&](const auto& entry) { return entry.second.record.endMs < cutoff; });
    }
    if (responses_.size() <= responseLimit_) return;
    const auto excess = responses_.size() - responseLimit_ + std::max<std::size_t>(1, responseLimit_ / 100);
    std::vector<const RemoteResponse*> ordered;
    for (const auto& [_, r] : responses_) ordered.push_back(&r);
    std::partial_sort(ordered.begin(), ordered.begin() + static_cast<std::ptrdiff_t>(std::min(excess, ordered.size())), ordered.end(), olderFirst);
    std::vector<std::string> keys;
    for (std::size_t k = 0; k < excess && k < ordered.size(); ++k) keys.push_back(ordered[k]->record.key);
    for (const auto& key : keys) responses_.erase(key);
}

void Feed::setHosts(std::vector<HostInfo> hosts) {
    std::lock_guard lock(mutex_);
    if (hosts == hosts_) return;
    std::set<std::string> ids;
    for (const auto& h : hosts) ids.insert(h.id);
    for (auto it = mirrors_.begin(); it != mirrors_.end();) {
        if (ids.contains(it->first)) {
            ++it;
            continue;
        }
        pending_.emplace_back(feed::RemoveHost{it->first});
        it = mirrors_.erase(it);
    }
    for (const auto& h : hosts) {
        if (!mirrors_.contains(h.id)) mirrors_.emplace(h.id, HostMirror(h.id));
    }
    hosts_ = std::move(hosts);
    pending_.emplace_back(feed::Hosts{hosts_});
}

void Feed::receive(const HostEvent& event, std::int64_t nowMs, std::int64_t todayStartMs) {
    std::lock_guard lock(mutex_);
    const auto it = mirrors_.find(event.hostId);
    if (it == mirrors_.end()) return;
    auto changes = it->second.apply(event, nowMs, todayStartMs);
    if (overflowed_) return;
    for (auto& change : changes) pending_.push_back(std::move(change));
    if (pending_.size() > kMaxPending) {
        pending_.clear();
        overflowed_ = true;
    }
}

std::vector<FeedChange> Feed::take() {
    std::lock_guard lock(mutex_);
    if (overflowed_) return replayLocked();
    return std::exchange(pending_, {});
}

std::vector<FeedChange> Feed::attach() {
    std::lock_guard lock(mutex_);
    return replayLocked();
}

std::vector<FeedChange> Feed::replayLocked() {
    pending_.clear();
    overflowed_ = false;
    std::vector<FeedChange> changes{feed::Hosts{hosts_}};
    for (const auto& h : hosts_) {
        const auto it = mirrors_.find(h.id);
        if (it == mirrors_.end()) continue;
        auto replayed = it->second.replay();
        changes.insert(changes.end(), std::make_move_iterator(replayed.begin()), std::make_move_iterator(replayed.end()));
    }
    return changes;
}

bool Feed::hasHosts() const {
    std::lock_guard lock(mutex_);
    return !hosts_.empty();
}

std::vector<HostInfo> Feed::hosts() const {
    std::lock_guard lock(mutex_);
    return hosts_;
}

std::optional<HostMirror> Feed::mirror(const std::string& hostId) const {
    std::lock_guard lock(mutex_);
    const auto it = mirrors_.find(hostId);
    if (it == mirrors_.end()) return std::nullopt;
    return it->second;
}

}  // namespace agentspeed::remote
