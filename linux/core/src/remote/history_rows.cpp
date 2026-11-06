#include <algorithm>

#include "agentspeed/remote_history.hpp"
#include "agentspeed/remote_protocol.hpp"

namespace agentspeed::remote {

namespace {

history::DailyUsage usageOf(const std::string& host, const std::string& day, SourceKind source, history::DayAggregate aggregate) {
    history::DailyUsage usage;
    usage.key = {day, source};
    usage.totals = std::move(aggregate);
    usage.host = host;
    return usage;
}

bool isLocal(const std::string& host) { return host == kLocalHost; }

}  // namespace

HistoryRows HistoryRows::load(const history::Store& store) {
    HistoryRows rows;
    for (const auto& [id, name] : store.remoteHostNames()) {
        if (name) rows.names_[id] = *name;
    }
    rows.zones_ = store.remoteHostZones();
    for (const auto& row : store.remoteDayRows()) {
        const auto source = sourceFromId(row.source);
        auto aggregate = decodeAggregateText(row.data);
        if (!source || !aggregate) continue;
        rows.days_[{row.host, row.day, *source}] = Day{usageOf(row.host, row.day, *source, std::move(*aggregate)), row.digest};
    }
    for (const auto& row : store.remotePerfRows()) {
        const auto source = sourceFromId(row.source);
        const auto record = decodePerfRecordText(row.data);
        if (!source || !record) continue;
        rows.perf_[{row.host, row.day, *source}][row.model] = *record;
    }
    return rows;
}

std::map<std::string, std::string> HistoryRows::digests(const std::string& host) const {
    std::map<std::string, std::string> out;
    for (const auto& [key, day] : days_) {
        if (std::get<0>(key) == host) out[std::get<1>(key) + "/" + std::string(sourceId(std::get<2>(key)))] = day.digest;
    }
    return out;
}

std::vector<history::DailyUsage> HistoryRows::usage(const std::set<SourceKind>* sources, const Peak& peak) const {
    std::vector<history::DailyUsage> out;
    for (const auto& [key, day] : days_) {
        if (sources && !sources->contains(std::get<2>(key))) continue;
        auto usage = day.usage;
        usage.peak = peak ? peak(std::get<1>(key)) : std::nullopt;
        out.push_back(std::move(usage));
    }
    return out;
}

std::vector<perf::DayRecord> HistoryRows::perfRecords(const std::set<SourceKind>* sources) const {
    // The keys are ordered by host, day and source, the models by their bytes.
    std::vector<perf::DayRecord> out;
    for (const auto& [key, models] : perf_) {
        if (sources && !sources->contains(std::get<2>(key))) continue;
        for (const auto& [model, record] : models) out.push_back({std::get<1>(key), std::get<2>(key), model, record});
    }
    return out;
}

void HistoryRows::store(const std::vector<history::RemoteDay>& batch, const std::string& host, const std::optional<std::string>& name,
                        const std::string& zone, bool replace) {
    if (replace) {
        std::erase_if(days_, [&](const auto& entry) { return std::get<0>(entry.first) == host; });
        std::erase_if(perf_, [&](const auto& entry) { return std::get<0>(entry.first) == host; });
    }
    if (batch.empty() && !hosts().contains(host)) {
        names_.erase(host);
        zones_.erase(host);
        return;
    }
    if (name) names_[host] = *name;
    zones_[host] = zone;
    for (const auto& day : batch) {
        const Key key{host, day.day, day.source};
        days_[key] = Day{usageOf(host, day.day, day.source, day.aggregate), day.digest};
        std::map<std::string, perf::Record> records;
        for (const auto& item : day.perf) records[item.model] = item.record;
        if (records.empty()) {
            perf_.erase(key);
        } else {
            perf_[key] = std::move(records);
        }
    }
}

std::set<std::string> HistoryRows::hosts() const {
    std::set<std::string> out;
    for (const auto& [key, day] : days_) out.insert(std::get<0>(key));
    for (const auto& [key, models] : perf_) out.insert(std::get<0>(key));
    return out;
}

void HistoryRows::remove(const std::string& host) {
    names_.erase(host);
    zones_.erase(host);
    std::erase_if(days_, [&](const auto& entry) { return std::get<0>(entry.first) == host; });
    std::erase_if(perf_, [&](const auto& entry) { return std::get<0>(entry.first) == host; });
}

std::vector<history::DailyUsage> mergeDays(std::vector<history::DailyUsage> local, std::vector<history::DailyUsage> remote) {
    if (remote.empty()) return local;
    local.insert(local.end(), std::make_move_iterator(remote.begin()), std::make_move_iterator(remote.end()));
    std::stable_sort(local.begin(), local.end(), [](const history::DailyUsage& a, const history::DailyUsage& b) {
        if (a.key.day != b.key.day) return a.key.day < b.key.day;
        if (a.key.source != b.key.source) return sourceId(a.key.source) < sourceId(b.key.source);
        if (isLocal(a.host) != isLocal(b.host)) return isLocal(a.host);
        return a.host < b.host;
    });
    return local;
}

std::vector<perf::DayRecord> mergePerf(const std::vector<perf::DayRecord>& local, const std::vector<perf::DayRecord>& remote) {
    if (remote.empty()) return local;
    std::map<std::tuple<std::string, SourceKind, std::string>, perf::Record> sums;
    for (const auto* list : {&local, &remote}) {
        for (const auto& item : *list) sums[{item.day, item.source, item.model}].add(item.record);
    }
    std::vector<perf::DayRecord> out;
    out.reserve(sums.size());
    for (const auto& [key, record] : sums) out.push_back({std::get<0>(key), std::get<1>(key), std::get<2>(key), record});
    return out;
}

}  // namespace agentspeed::remote
