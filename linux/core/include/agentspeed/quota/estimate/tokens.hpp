#pragma once

#include <cstdint>
#include <map>
#include <optional>
#include <string>
#include <string_view>
#include <utility>
#include <vector>

#include "agentspeed/model.hpp"
#include "agentspeed/report_json.hpp"

// QUOTAS 10.3 Token counts: what the ledger keeps per response and row, and REPORTING-EXTENSIONS 12 `tokenUsage`, the
// counts of the rows a quota entry's `usedMicroUsd` sums.
namespace agentspeed::quota::estimate {

inline constexpr std::size_t kTokenModels = 16;
inline constexpr std::int64_t kMaxSafeInteger = 9'007'199'254'740'991;

// `input` holds cache reads and writes once, `output` reasoning once; a detail is nullopt where the source's logs do
// not report it.
struct Counts {
    std::int64_t input = 0, output = 0;
    std::optional<std::int64_t> cachedInput, cacheWriteInput, reasoning;

    static Counts zero() { return {0, 0, 0, 0, 0}; }
    // Both contributions of one row; a detail either misses stays missing. Sums saturate (stored rows are read back
    // from disk), so an absurd total ends as an unsafe one.
    Counts plus(const Counts& other) const;
    // No value negative, every detail within its clamp: what countsOf can produce.
    bool valid() const;
    bool operator==(const Counts&) const = default;
};

std::int64_t saturatingSum(std::int64_t a, std::int64_t b);

// A response's counts from its normalized usage (METRICS 3.2): cache reads at most the input, cache writes at most the
// rest of it, reasoning at most the output, and the details `source` does not report missing.
Counts countsOf(std::string_view source, const TokenCounts& usage);
// Unmeasured (nullopt) when either is.
std::optional<Counts> merged(const std::optional<Counts>& a, const std::optional<Counts>& b);

struct ModelTokens {
    // A price-table id; nullopt for every model outside the table.
    std::optional<std::string> model;
    std::int64_t inputTokens = 0, outputTokens = 0;
    bool operator==(const ModelTokens&) const = default;
};

struct TokenUsage {
    Counts counts;
    std::vector<ModelTokens> models;

    report::Json json() const;
    bool operator==(const TokenUsage&) const = default;
};

// Input and output per public model id, and those of every other model.
struct ModelSums {
    std::map<std::string, std::pair<std::int64_t, std::int64_t>> named;
    std::pair<std::int64_t, std::int64_t> others{0, 0};
    void add(const std::optional<std::string>& model, const Counts& counts);
};

// `T`: nullopt past 16 models or a total above 2^53 − 1; models without tokens are left out, the rest ordered by id
// (bytes) with the row of the other models last.
std::optional<TokenUsage> tokenUsageOf(const Counts& total, const ModelSums& models);

}  // namespace agentspeed::quota::estimate
