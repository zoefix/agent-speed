#pragma once

#include <cstdint>
#include <map>
#include <memory>
#include <optional>
#include <set>
#include <string>
#include <unordered_map>
#include <unordered_set>
#include <vector>

#include "agentspeed/dedupe.hpp"
#include "agentspeed/model.hpp"

namespace agentspeed {
class TimeZone;
namespace pricing {
class PriceBook;
}
}  // namespace agentspeed

// METRICS 8: daily history — per local day and source aggregates, built from per-file contributions.
namespace agentspeed::history {

std::uint64_t fnv1a(std::string_view text);

struct DayKey {
    std::string day;
    SourceKind source = SourceKind::Codex;
    auto operator<=>(const DayKey&) const = default;
};

// METRICS 9.10: the raw facts of an unpriced response.
struct UnpricedFact {
    std::string model;
    std::optional<std::string> provider;
    bool fast = false;
    bool noDate = false;
    static UnpricedFact of(const std::optional<std::string>& model, const std::optional<std::string>& provider, bool fast);
    bool operator<(const UnpricedFact& other) const;
    bool operator==(const UnpricedFact&) const = default;
};

struct ModelTotals {
    std::int64_t requests = 0, output = 0, measuredOutput = 0, measuredMs = 0;
    std::optional<std::int64_t> costMicroUsd, unpricedRequests;
};

// EXECUTION-MODES Local aggregates and history: a model's usage under one attributed mode of one kind (effort and the
// derived speed included). Every response adds once per kind it has a known value of; the unknown bucket is the rest.
struct ModeKey {
    std::string model;
    ModeKind kind = ModeKind::ServiceTier;
    std::string mode, evidence;
    std::optional<std::string> provider;
    auto operator<=>(const ModeKey&) const = default;
};
struct ModeTotals {
    std::int64_t requests = 0, output = 0, measuredOutput = 0, measuredMs = 0;
};

struct DayAggregate {
    std::int64_t requests = 0, input = 0, cachedInput = 0, cacheWriteInput = 0, output = 0, reasoning = 0;
    std::int64_t measuredOutput = 0, measuredMs = 0, unfinalized = 0;
    int agents = 0;
    // Null for days kept from a store without cost data.
    std::optional<std::int64_t> costMicroUsd, unpricedRequests;
    std::map<std::string, ModelTotals> models;
    std::map<UnpricedFact, std::int64_t> unpriced;
    std::map<ModeKey, ModeTotals> modes;

    void add(const ParsedResponse& response, std::optional<std::int64_t> cost);
    void merge(const DayAggregate& other);
};

// EXECUTION-MODES: a day's usage per model and kind — the known modes and, for each kind, the unknown bucket filled
// from the ordinary model totals, so every kind adds up to the day. Ordered as the mode CSV writes them.
struct ModeRow {
    ModeKey key;
    ModeTotals totals;
};
std::vector<ModeRow> modeBreakdown(const DayAggregate& day);
// The mode CSV of EXECUTION-MODES over (day, source, aggregate) rows: UTF-8 with a BOM, CRLF line ends.
struct ModeCsvDay {
    std::string day;
    SourceKind source = SourceKind::Codex;
    const DayAggregate* totals = nullptr;
};
std::string modeCsv(const std::vector<ModeCsvDay>& days);

// The local calendar day of an instant; nullopt outside 0001-01-03 … 9999-12-29 (METRICS 8).
class DayLabeler {
public:
    explicit DayLabeler(std::shared_ptr<const TimeZone> zone) : zone_(std::move(zone)) {}
    std::optional<std::string> label(std::int64_t unixMs) const;

private:
    std::shared_ptr<const TimeZone> zone_;
};

using Contributions = std::map<DayKey, DayAggregate>;
Contributions contributionsOf(const std::vector<ParsedResponse>& responses, SourceKind source, const DayLabeler& labeler,
                              pricing::PriceBook& prices);

// One day and source of the history, as the UI and the fixtures read it.
struct DailyUsage {
    DayKey key;
    DayAggregate totals;
    std::optional<double> peak;
    std::optional<int> priceVersion;
};

// The dedupe of a history analysis (METRICS 8): keys owned by files analysed before (settled, hashed) and the keys the
// files being analysed claim, per owner, until they are finished.
class KeyLedger : public Deduper {
public:
    bool tryClaim(const std::string& key, const std::string& agentId, std::string* owner = nullptr) override;
    std::optional<std::string> owner(const std::string& key) const override;
    bool ownedByOther(const std::string& key, const std::string& agentId) const override;

    void settle(const std::vector<std::uint64_t>& hashes);
    // The hashes the owner claimed, settled now.
    std::vector<std::uint64_t> finish(const std::string& owner);
    // Every owner's claims, settled now, by owner.
    std::map<std::string, std::vector<std::uint64_t>> finishAll();
    void abandon();

private:
    std::unordered_set<std::uint64_t> settled_;
    std::unordered_map<std::uint64_t, std::string> current_;
    std::vector<std::uint64_t> order_;
};

}  // namespace agentspeed::history
