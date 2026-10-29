#pragma once

#include <cstdint>
#include <map>
#include <memory>
#include <optional>
#include <string>
#include <vector>

#include "agentspeed/actions.hpp"
#include "agentspeed/engine.hpp"
#include "agentspeed/model.hpp"
#include "agentspeed/perf.hpp"
#include "agentspeed/perf_history.hpp"
#include "agentspeed/report_json.hpp"
#include "agentspeed/report_values.hpp"

// The perfScore extension of live reporting (METRICS 11.10; spec/tools/report_perf_score.py): live values, the
// sufficient statistics of the performance score and the statistics ledger.
namespace agentspeed::report {

inline constexpr std::int64_t kMaxPerfScore = 10'000'000;

// One model and source's statistics of counted completions: the 6.11 record fields. Every field adds.
struct PerfRow {
    perf::Record record;

    PerfRow& operator+=(const PerfRow& o);
    friend PerfRow operator-(const PerfRow& a, const PerfRow& b);
    bool zero() const { return record.n + record.toolN == 0; }
    Json json(SourceKind source) const;
};
using PerfRows = std::map<ModelKey, PerfRow>;

// What a measured response or a tool call adds, activeMs aside.
PerfRow responseRow(SourceKind source, const engine::Response& response);
PerfRow callRow(const work::Action& action);

// An agent of the set with its value (6.9 Values), under its current model's report id.
struct PerfAgent {
    SourceKind source = SourceKind::Codex;
    std::string modelId;
    perf::Form form = perf::Form::None;
    std::optional<double> score;
};

// {score, form, thinkingTokS}: the highest live score, else the highest resting score, else none.
Json perfValue(const std::vector<const PerfAgent*>& agents, double thinking);
// The retired taskScore's {score: null, form: "none", thinkingTokS}.
Json legacyValue(double thinking);

// The live values each snapshot carries for the extension, and the statistics ledger's cumulative rows of the current
// watch generation (a new generation, after a break, starts them over).
struct PerfFacts {
    std::vector<PerfAgent> agents;
    double thinking = 0;                          // all enabled sources' liveThinking
    std::map<std::string, double> modelThinking;  // per report id: the model sums of its raw models
    std::shared_ptr<const PerfRows> rows = std::make_shared<PerfRows>();
    std::uint64_t generation = 0;
    std::int64_t responses = 0, calls = 0;  // completions in `rows`
    std::int64_t counted = 0;               // responses + calls
};

// METRICS 11.10 Watch, Counting and the statistics ledger.
class PerfLedger {
public:
    static constexpr std::int64_t kFirstSeenMs = 120'000;
    static constexpr std::int64_t kMaxResponses = 100'000;
    static constexpr std::int64_t kMaxCalls = 100'000;

    bool watching() const { return watch_; }
    std::uint64_t generation() const { return generation_; }
    // The watch turning off is a break: the ledger is emptied.
    void setWatch(bool on);
    // How many of this generation's responses and calls the builds already took (the caps count the rest).
    void setTaken(std::int64_t responses, std::int64_t calls) {
        takenResponses_ = responses;
        takenCalls_ = calls;
    }
    // A measured response the report ledger counted, at its completion.
    bool response(const std::string& agentId, const ModelKey& key, const engine::Response& response);
    // A tool call whose first result was read live at `now` (seeding: during the backlog).
    bool call(const std::string& agentId, const ModelKey& key, const work::Action& action, std::int64_t now, bool seeding);
    // A relaunch: nothing is kept, the coveredUntil of every agent and id included.
    void reset();

    const PerfRows& rows() const { return rows_; }
    std::int64_t responses() const { return responses_; }
    std::int64_t calls() const { return calls_; }
    std::uint64_t version() const { return version_; }

private:
    void add(const std::string& agentId, const ModelKey& key, PerfRow row, std::int64_t start, std::int64_t end);

    bool watch_ = false;
    std::uint64_t generation_ = 0, version_ = 0;
    std::int64_t responses_ = 0, calls_ = 0, takenResponses_ = 0, takenCalls_ = 0;
    PerfRows rows_;
    std::map<std::pair<std::string, std::string>, std::int64_t> covered_;
};

}  // namespace agentspeed::report
