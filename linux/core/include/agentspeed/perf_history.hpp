#pragma once

#include <array>
#include <cstdint>
#include <functional>
#include <map>
#include <optional>
#include <string>
#include <tuple>
#include <vector>

#include "agentspeed/actions.hpp"
#include "agentspeed/engine.hpp"
#include "agentspeed/perf.hpp"

// The performance history (METRICS 6.11, spec/tools/perf_score.py): per-day records of the history analysis, the
// pooled score of any set of them, the 14-day baseline and the History window's per-model rows.
namespace agentspeed::perf {

inline constexpr int kBins = 64;

// E_j = ⌈100 · 2^(j/4)⌉, j = 0 … 64.
std::int64_t latencyEdge(int j);
// Bucket k of an integer latency: the number of edges E_1 … E_63 it reaches.
int latencyBin(std::int64_t ms);
// The geometric centre 100 · 2^((k + 0.5) / 4) of bucket k.
double binCentre(int k);

struct Record {
    std::int64_t n = 0, outputSum = 0, durationMsSum = 0;
    std::array<std::int64_t, kBins> latencyBins{};
    std::int64_t reasonN = 0, reasonOutput = 0, reasonDurationMsSum = 0;
    std::int64_t cacheN = 0, cacheInput = 0, cacheCached = 0;
    std::int64_t toolN = 0;
    double toolLnSum = 0;
    std::int64_t toolFailed = 0, activeMs = 0;

    void add(const Record& other);
    bool operator==(const Record&) const = default;
};

// One record of a local day, a source and a raw model ("" when unknown).
struct DayRecord {
    std::string day;
    SourceKind source = SourceKind::Codex;
    std::string model;
    Record record;
};

// Per (day, source, model) records from completions taken in the order they complete, with the online union rule
// for activeMs (one coveredUntil per agent and model). `dayOf` gives a time's local day, nullopt without a date.
class Recorder {
public:
    using DayOf = std::function<std::optional<std::string>(std::int64_t)>;
    explicit Recorder(DayOf dayOf) : dayOf_(std::move(dayOf)) {}

    // A response with its final values; only a measured one counts.
    void response(const std::string& agentId, SourceKind source, const engine::Response& r);
    // A tool call whose first result was read, within 15 minutes.
    void action(const std::string& agentId, SourceKind source, const work::Action& a);
    // By day, source order, then model (UTF-8 bytes).
    std::vector<DayRecord> rows() const;
    bool empty() const { return records_.empty(); }

private:
    Record& record(const std::string& day, SourceKind source, const std::string& model);
    void active(const std::string& agentId, SourceKind source, const std::string& model, std::int64_t start, std::int64_t end);

    DayOf dayOf_;
    std::map<std::tuple<std::string, SourceKind, std::string>, Record> records_;
    std::map<std::pair<std::string, std::string>, std::int64_t> covered_;
};

// An agent's completion: a response (by its final values) or a tool call, in the order they complete.
struct Completion {
    const engine::Response* response = nullptr;
    const work::Action* action = nullptr;
};

struct Pooled {
    std::int64_t n = 0, tools = 0, activeMs = 0;
    std::optional<double> score;
    std::optional<std::int64_t> shown;
    Components components;
};
// The 6.9 formula over the pooled sums of records of one source; no score below Σ n = 20.
Pooled pooled(const Record& sum, SourceKind source);
Pooled pooled(const std::vector<const Record*>& records, SourceKind source);

// The local date `days` days before `day` (YYYY-MM-DD).
std::string dayMinus(const std::string& day, int days);

// 比平常: the pooled score of (source, raw model) over [today − 14, today − 1]; nullopt below Σ n = 20.
std::optional<double> baseline(const std::vector<DayRecord>& records, SourceKind source, const std::string& model,
                               const std::string& today);

// The History window's 性能表現 over [first, last]: per (source, model) the pooled values and the daily trend.
struct RangeRow {
    SourceKind source = SourceKind::Codex;
    std::string model;
    Pooled pooled;
    std::vector<std::pair<std::string, std::optional<double>>> trend;  // (day, score)
};
std::vector<RangeRow> rangeValues(const std::vector<DayRecord>& records, const std::string& first, const std::string& last,
                                  std::optional<SourceKind> source = std::nullopt);

}  // namespace agentspeed::perf
