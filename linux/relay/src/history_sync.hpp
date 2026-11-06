#pragma once

#include <map>
#include <string>
#include <string_view>
#include <utility>
#include <vector>

#include "agentspeed/history.hpp"
#include "agentspeed/perf_history.hpp"

// REMOTE 5.4, 8.2: a host's daily history as the relay sends it — one aggregate per day and source, its performance
// records per model, and the digest that lets the app skip what it already holds.
namespace agentspeed::relay {

struct SyncDay {
    std::string day;
    SourceKind source = SourceKind::Codex;
    std::string aggregate;                                   // the store's encoding (history::encode)
    std::vector<std::pair<std::string, std::string>> perf;  // (model, record JSON) by the model's UTF-8 bytes
    std::string digest;

    std::string key() const;  // "<day>/<source>"
};

// The first 16 lowercase hex digits of SHA-256.
std::string sha256Hex16(std::string_view bytes);
// A performance record (METRICS 6.11 Records) as compact JSON.
std::string encodeRecord(const perf::Record& record);
// SHA-256 (first 16 hex digits) over the aggregate's text followed by the records' texts, no separators.
std::string digestOf(std::string_view aggregate, const std::vector<std::pair<std::string, std::string>>& perf);

// Per day and source (the analyzer's day order), with the per-file records summed per model. A day and source that
// has records but no aggregate gets an empty aggregate, so its records reach the app too.
std::vector<SyncDay> syncDays(const std::vector<history::DailyUsage>& days, const std::vector<perf::DayRecord>& records);

// The days whose digest differs from or is missing in what the app holds.
std::vector<const SyncDay*> changedDays(const std::vector<SyncDay>& days, const std::map<std::string, std::string>& held);

}  // namespace agentspeed::relay
