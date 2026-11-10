#include "agentspeed/remote_hub.hpp"

#include "agentspeed/price_book.hpp"

namespace agentspeed::remote {

namespace {

const std::string& bundledPrices() {
    static const std::string kPrices = compactPriceTable(pricing::bundledPricesJson()).value_or("{}");
    return kPrices;
}

}  // namespace

Hub::Hub(ConnectionEnvironment environment, Sink sink) : environment_(std::move(environment)), sink_(std::move(sink)) {}

Hub::~Hub() { stopAll(); }

void Hub::update(const std::vector<Host>& hosts, bool networkEnabled, const std::set<SourceKind>& sources, std::string_view priceTable,
                 const QuotaSwitches& quota) {
    auto prices = compactPriceTable(priceTable);
    if (!prices) prices = settings_ ? settings_->prices : bundledPrices();
    SessionSettings next{sources, *prices, settings_ ? settings_->watch : false, quota};
    const bool sourcesChanged = settings_ && settings_->sources != sources;
    settings_ = next;

    const auto valid = HostsDocument::normalized(hosts);
    std::set<std::string> ids;
    for (const auto& h : valid) ids.insert(h.id);
    std::vector<std::string> gone;
    for (const auto& [id, _] : connections_) {
        if (!ids.contains(id)) gone.push_back(id);
    }
    for (const auto& id : gone) removeConnection(id, std::nullopt);
    std::erase_if(idleStatuses_, [&](const auto& entry) { return !ids.contains(entry.first); });

    for (const auto& host : valid) {
        std::optional<Status> idle;
        if (!networkEnabled) {
            idle = Status::of(StatusCode::NetworkOff);
        } else if (!host.enabled) {
            idle = Status::of(StatusCode::Disabled);
        } else if (!host.connectable()) {
            idle = Status::of(StatusCode::InvalidDestination);
        }
        if (idle) {
            if (connections_.contains(host.id)) {
                removeConnection(host.id, *idle);
                idleStatuses_[host.id] = *idle;
            } else {
                reportIdle(host.id, *idle);
            }
            continue;
        }
        idleStatuses_.erase(host.id);
        if (const auto it = connections_.find(host.id); it != connections_.end()) {
            const auto previous = connectedHosts_.find(host.id);
            const bool edited = previous == connectedHosts_.end() || previous->second.connectionIdentity() != host.connectionIdentity();
            if (edited || sourcesChanged) {
                removeConnection(host.id, std::nullopt);
            } else {
                connectedHosts_[host.id] = host;
                it->second->updatePrices(next.prices);
                it->second->setQuota(quota, host.quotaClaudeCli);
                continue;
            }
        }
        auto connection = Connection::make(host, next, environment_, sink_);
        connections_[host.id] = connection;
        connectedHosts_[host.id] = host;
        connection->start();
    }
}

void Hub::retryNow(const std::string& hostId) {
    if (const auto it = connections_.find(hostId); it != connections_.end()) it->second->retryNow();
}

void Hub::requestHistory(const std::string& hostId, const std::map<std::string, std::string>& digests) {
    if (const auto it = connections_.find(hostId); it != connections_.end()) it->second->requestHistory(digests);
}

void Hub::setWatch(bool on) {
    if (settings_) settings_->watch = on;
    for (const auto& [_, connection] : connections_) connection->setWatch(on);
}

void Hub::noteWake() {
    for (const auto& [_, connection] : connections_) connection->noteWake();
}

void Hub::noteTimeZoneChange() {
    for (const auto& [_, connection] : connections_) connection->noteTimeZoneChange();
}

void Hub::stopAll() {
    std::vector<std::string> ids;
    for (const auto& [id, _] : connections_) ids.push_back(id);
    for (const auto& id : ids) removeConnection(id, std::nullopt);
}

std::set<std::string> Hub::connectedHostIds() const {
    std::set<std::string> out;
    for (const auto& [id, _] : connections_) out.insert(id);
    return out;
}

std::shared_ptr<Connection> Hub::connection(const std::string& hostId) const {
    const auto it = connections_.find(hostId);
    return it == connections_.end() ? nullptr : it->second;
}

void Hub::removeConnection(const std::string& id, std::optional<Status> finalStatus) {
    if (const auto it = connections_.find(id); it != connections_.end()) {
        auto connection = it->second;
        connections_.erase(it);
        connection->stop(finalStatus);
    }
    connectedHosts_.erase(id);
}

void Hub::reportIdle(const std::string& id, const Status& status) {
    if (const auto it = idleStatuses_.find(id); it != idleStatuses_.end() && it->second == status) return;
    idleStatuses_[id] = status;
    HostStatus value;
    value.status = status;
    if (sink_) sink_(HostEvent{id, 0, value});
}

}  // namespace agentspeed::remote
