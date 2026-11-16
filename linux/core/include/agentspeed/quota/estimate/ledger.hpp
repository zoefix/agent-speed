#pragma once

#include <cstdint>
#include <map>
#include <mutex>
#include <optional>
#include <string>
#include <string_view>
#include <tuple>
#include <vector>

#include "agentspeed/quota/estimate/tokens.hpp"

// QUOTAS 10.3: the spend ledger per (account, source, price-table model, UTC minute of the response's end), its sums
// and token counts.
namespace agentspeed::quota::estimate {

// A `modelWeek` window counts only the models whose id holds every word of its name as a `-`-separated token.
bool modelMatches(std::string_view model, std::string_view name);

struct Spend {
    std::int64_t costMicroUsd = 0;
    std::int64_t unpricedRequests = 0;
};

struct MixShare {
    std::string model;
    double share = 0;
    bool operator==(const MixShare&) const = default;
};

// Sums are cached per (account, name) until the next change; the cache is guarded, so a built ledger can be read from
// any thread.
class Ledger {
public:
    Ledger() = default;
    Ledger(const Ledger& other);
    Ledger& operator=(const Ledger& other);

    // `cost` nullopt: an unpriced response; `counts` nullopt: recorded without them (unmeasured); `publicModel`: the
    // model is an id of the price table.
    void add(const std::string& account, const std::string& source, const std::string& model, std::int64_t endMs,
             std::optional<std::int64_t> cost, std::int64_t requests = 1, std::int64_t tokens = 0,
             const std::optional<Counts>& counts = std::nullopt, bool publicModel = false);
    void prune(std::int64_t nowMs);
    bool empty() const { return rows_.empty(); }

    // C⁻: the minutes wholly inside [start, end] (unpriced counts 0).
    Spend inner(const std::string& account, const std::optional<std::string>& name, std::int64_t start, std::int64_t end) const;
    // The minutes touching [start, end] (unpriced counts 0) and their unpriced requests.
    Spend outer(const std::string& account, const std::optional<std::string>& name, std::int64_t start, std::int64_t end) const;
    // C⁺: outer() with each unpriced token at the account's highest priced cost per token; infinite without one.
    double outerHigh(const std::string& account, const std::optional<std::string>& name, std::int64_t start, std::int64_t end) const;
    // The `top` models of the span by cost, with their shares at three decimals.
    std::vector<MixShare> mix(const std::string& account, const std::optional<std::string>& name, std::int64_t start, std::int64_t end,
                              std::size_t top = 3) const;
    // `T`: the token counts of the rows outer() sums (REPORTING-EXTENSIONS 12 `tokenUsage`); nullopt when a row is
    // unmeasured, past 16 models or an unsafe total.
    std::optional<TokenUsage> tokenUsage(const std::string& account, const std::optional<std::string>& name, std::int64_t start,
                                         std::int64_t end) const;

private:
    struct Row {
        std::int64_t cost = 0, unpricedRequests = 0, pricedTokens = 0, unpricedTokens = 0;
        // nullopt once a response recorded without counts joined the row.
        std::optional<Counts> counts = Counts::zero();
        // The model is an id of the price table for every response of the row.
        bool publicModel = true;
    };
    struct Counted {
        std::int64_t minute = 0;
        // nullopt: not a price-table id.
        std::optional<std::string> model;
        std::optional<Counts> counts;
    };
    using RowKey = std::tuple<std::string, std::string, std::string, std::int64_t>;
    struct Series {
        std::vector<std::int64_t> minutes;
        // Prefix sums of cost, unpriced requests and unpriced tokens.
        std::vector<std::int64_t> cost, unpriced, tokens;
        std::optional<double> rate;
        // Every row's token counts, by minute.
        std::vector<Counted> counted;
    };
    struct Range {
        std::int64_t cost = 0, unpriced = 0, tokens = 0;
        std::optional<double> rate;
    };
    const Series& series(const std::string& account, const std::string& name) const;
    Range range(const std::string& account, const std::optional<std::string>& name, std::int64_t first, std::int64_t last) const;

    std::map<RowKey, Row> rows_;
    mutable std::mutex mutex_;
    mutable std::map<std::pair<std::string, std::string>, Series> cache_;
};

}  // namespace agentspeed::quota::estimate
