#include <algorithm>

#include "agentspeed/remote_history.hpp"

namespace agentspeed::remote {

void HistoryBatch::add(const HistoryDay& line) {
    history::RemoteDay day;
    day.day = line.day;
    day.source = line.source;
    day.aggregate = line.aggregate;
    day.aggregateJson = line.aggregateJson;
    day.digest = line.digest;
    const auto key = day.key();
    if (!keys_.insert(key).second) {
        const auto it = std::find_if(days_.begin(), days_.end(), [&](const history::RemoteDay& d) { return d.key() == key; });
        if (it != days_.end()) {
            byteCount_ -= it->byteCount();
            perfCount_ -= static_cast<std::int64_t>(it->perf.size());
            days_.erase(it);
        }
    } else if (const auto staged = staged_.find(key); staged != staged_.end()) {
        perfCount_ -= staged->second;
    } else {
        ++dayCount_;
    }
    byteCount_ += day.byteCount();
    days_.push_back(std::move(day));
}

void HistoryBatch::add(const HistoryPerf& line) {
    if (days_.empty()) return;
    auto& last = days_.back();
    if (last.day != line.day || last.source != line.source) return;
    history::RemotePerf perf{line.model, line.recordJson, line.record};
    const auto it = std::find_if(last.perf.begin(), last.perf.end(), [&](const history::RemotePerf& p) { return p.model == perf.model; });
    if (it != last.perf.end()) {
        byteCount_ -= it->byteCount();
        *it = std::move(perf);
        byteCount_ += it->byteCount();
        return;
    }
    if (last.perf.size() >= kPerfLimit) return;
    byteCount_ += perf.byteCount();
    last.perf.push_back(std::move(perf));
    ++perfCount_;
}

std::vector<history::RemoteDay> HistoryBatch::takeCompleted() {
    if (days_.size() <= 1) return {};
    std::vector<history::RemoteDay> completed(std::make_move_iterator(days_.begin()), std::make_move_iterator(days_.end() - 1));
    days_.erase(days_.begin(), days_.end() - 1);
    keys_.clear();
    byteCount_ = 0;
    for (const auto& day : days_) {
        keys_.insert(day.key());
        byteCount_ += day.byteCount();
    }
    for (const auto& day : completed) staged_[day.key()] = static_cast<std::int64_t>(day.perf.size());
    return completed;
}

bool HistoryBatch::exceeds(const history::RemoteLimits& limits) const { return dayCount_ > limits.days || perfCount_ > limits.perf; }

HistorySyncState::Action HistorySyncState::apply(const HostEvent& event) {
    const auto& host = event.hostId;
    if (const auto it = batches_.find(host); it != batches_.end() && it->second.connection() != event.connection) batches_.erase(it);
    if (const auto it = overflowed_.find(host); it != overflowed_.end() && it->second != event.connection) overflowed_.erase(it);
    if (const auto* status = std::get_if<HostStatus>(&event.kind)) {
        switch (status->status.code) {
            case StatusCode::Connected: connected_[host] = event.connection; break;
            case StatusCode::Seeding: connected_.erase(host); break;
            default:
                connected_.erase(host);
                batches_.erase(host);
                overflowed_.erase(host);
                break;
        }
        return None{};
    }
    if (std::holds_alternative<HostEvent::HistoryDue>(event.kind)) return Request{host};
    return applyMessage(std::get<HostEvent::Message>(event.kind).message, host, event.connection);
}

void HistorySyncState::drop(const std::string& host) {
    batches_.erase(host);
    overflowed_.erase(host);
}

void HistorySyncState::dropBatches() {
    batches_.clear();
    overflowed_.clear();
}

HistorySyncState::Action HistorySyncState::applyMessage(const RelayMessage& message, const std::string& host, int connection) {
    if (const auto* day = std::get_if<HistoryDay>(&message)) {
        if (overflowed_.contains(host)) return None{};
        auto node = batches_.extract(host);
        HistoryBatch batch = node ? std::move(node.mapped()) : HistoryBatch(host, connection);
        batch.add(*day);
        return keep(std::move(batch), host, connection);
    }
    if (const auto* perf = std::get_if<HistoryPerf>(&message)) {
        if (overflowed_.contains(host)) return None{};
        auto node = batches_.extract(host);
        if (!node) return None{};
        HistoryBatch batch = std::move(node.mapped());
        batch.add(*perf);
        return keep(std::move(batch), host, connection);
    }
    if (std::holds_alternative<HistoryDone>(message)) {
        if (overflowed_.erase(host) > 0) return TooLarge{host};
        auto node = batches_.extract(host);
        if (!node) return Commit{host, connection, {}, false};
        const bool staged = node.mapped().staged();
        return Commit{host, connection, node.mapped().takeDays(), staged};
    }
    if (const auto* error = std::get_if<RelayError>(&message); error && error->code == RelayError::Code::HistoryFailed) {
        batches_.erase(host);
        overflowed_.erase(host);
    }
    return None{};
}

// Keeps the batch, staging its complete days when it holds too much text; a batch beyond the bounds overflows.
HistorySyncState::Action HistorySyncState::keep(HistoryBatch batch, const std::string& host, int connection) {
    if (batch.exceeds(limits_)) {
        overflowed_[host] = connection;
        return Overflow{host};
    }
    const bool first = !batch.staged();
    auto early = batch.byteCount() > flushBytes_ ? batch.takeCompleted() : std::vector<history::RemoteDay>{};
    batches_.insert_or_assign(host, std::move(batch));
    if (early.empty()) return None{};
    return Stage{host, std::move(early), first};
}

}  // namespace agentspeed::remote
