#pragma once

#include <cstdint>
#include <deque>
#include <map>
#include <memory>
#include <optional>
#include <string>
#include <unordered_map>

#include "agentspeed/model.hpp"

// The values live reporting takes from the engine (METRICS 11.2–11.4): the report ledger's cumulative counters and the
// agent counts of each tick. A report window is the difference of two counter values, so every increment lands in
// exactly one window however snapshots are coalesced.
namespace agentspeed::report {

struct Usage {
    std::int64_t requests = 0, output = 0, input = 0, cachedInput = 0, cacheWriteInput = 0, reasoning = 0, measuredOutput = 0,
                 measuredMs = 0, costMicroUsd = 0, unpricedRequests = 0;

    bool operator==(const Usage&) const = default;
    bool zero() const { return *this == Usage{}; }
    // Any token field changed (the activity rule of METRICS 11.5).
    bool hasTokens() const { return requests || output || input || cachedInput || cacheWriteInput || reasoning; }
    Usage& operator+=(const Usage& o);
    // Field-wise difference of two cumulative values, never negative.
    friend Usage operator-(const Usage& a, const Usage& b);
};

// A resolved model id (METRICS 11.3) used by one source.
struct ModelKey {
    std::string modelId;
    SourceKind source = SourceKind::Codex;
    auto operator<=>(const ModelKey&) const = default;
};

class Counters {
public:
    const std::map<ModelKey, Usage>& byModel() const { return byModel_; }
    // Changes with every non-zero add, so an owner copies only after a change.
    std::uint64_t version() const { return version_; }
    Usage total() const;
    // The increments since `baseline`, per model and source; zero entries are left out.
    Counters since(const Counters& baseline) const;
    bool hasTokensSince(const Counters& baseline) const;
    void add(const Usage& usage, const ModelKey& key);

private:
    std::map<ModelKey, Usage> byModel_;
    std::uint64_t version_ = 0;
};

// Agents of the base set of METRICS 6.7 (staleness applied, no ancestor closure) of one source.
struct AgentCounts {
    int generating = 0, tools = 0, active = 0;
};

// What each snapshot carries for live reporting (METRICS 11.2, 11.4).
struct Facts {
    std::map<SourceKind, AgentCounts> agents;
    std::map<ModelKey, int> generatingModels;
    std::shared_ptr<const Counters> counters = std::make_shared<Counters>();
    std::int64_t epoch = 0;  // the engine instance the counters belong to; a new monitor starts them over
};

// The report ledger of METRICS 11.4: which responses count for live reporting and what each one already added.
class Ledger {
public:
    static constexpr std::size_t kMaxCountedKeys = 65'536;

    const Counters& counters() const { return counters_; }
    std::size_t countedKeys() const { return counted_.size(); }
    bool counted(const std::string& key) const { return counted_.contains(key); }
    // Counts a response at its first live emission (a key already counted grows instead).
    void count(const ParsedResponse& response, std::optional<std::int64_t> costMicroUsd, const std::string& modelId);
    // Growth of a counted response (an upsert or a re-pricing): each field adds what exceeds what it already added.
    void grow(const ParsedResponse& response, std::optional<std::int64_t> costMicroUsd, const std::string& modelId);
    // Forgets counted keys whose response left the engine's retention (by its latest end).
    void prune(std::int64_t cutoffMs);

private:
    struct Reported {
        Usage usage;
        std::int64_t endMs = 0;
    };
    static Reported valuesOf(const ParsedResponse& response, std::optional<std::int64_t> costMicroUsd);

    std::unordered_map<std::string, Reported> counted_;
    std::deque<std::string> order_;
    Counters counters_;
};

}  // namespace agentspeed::report
