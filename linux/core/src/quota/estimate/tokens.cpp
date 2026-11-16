#include "agentspeed/quota/estimate/tokens.hpp"

#include <algorithm>
#include <array>
#include <limits>

namespace agentspeed::quota::estimate {

namespace {

using report::Json;

struct Details {
    std::string_view source;
    bool cachedInput, cacheWriteInput, reasoning;
};

// The details each source's logs report; the parsers write 0 for an absent field, so the others are missing.
constexpr std::array kDetails = {
    Details{"codex", true, false, true},
    Details{"claudeCode", true, true, false},
    Details{"claudeDesktop", true, true, false},
    Details{"gemini", true, false, true},
    Details{"antigravity", true, false, true},
};

std::optional<std::int64_t> sumOf(const std::optional<std::int64_t>& a, const std::optional<std::int64_t>& b) {
    if (!a || !b) return std::nullopt;
    return saturatingSum(*a, *b);
}

Json optionalInt(const std::optional<std::int64_t>& v) { return v ? Json::of(*v) : Json::null(); }

}  // namespace

std::int64_t saturatingSum(std::int64_t a, std::int64_t b) {
    std::int64_t out = 0;
    if (__builtin_add_overflow(a, b, &out)) return b > 0 ? std::numeric_limits<std::int64_t>::max() : std::numeric_limits<std::int64_t>::min();
    return out;
}

Counts Counts::plus(const Counts& o) const {
    return {saturatingSum(input, o.input), saturatingSum(output, o.output), sumOf(cachedInput, o.cachedInput),
            sumOf(cacheWriteInput, o.cacheWriteInput), sumOf(reasoning, o.reasoning)};
}

bool Counts::valid() const {
    if (input < 0 || output < 0) return false;
    const auto cached = cachedInput.value_or(0);
    if (cached < 0 || cached > input) return false;
    if (cacheWriteInput && (*cacheWriteInput < 0 || *cacheWriteInput > input - cached)) return false;
    return !reasoning || (*reasoning >= 0 && *reasoning <= output);
}

Counts countsOf(std::string_view source, const TokenCounts& usage) {
    const auto input = std::max<std::int64_t>(0, usage.input), output = std::max<std::int64_t>(0, usage.output);
    const auto cached = std::min(std::max<std::int64_t>(0, usage.cachedInput), input);
    const auto it = std::find_if(kDetails.begin(), kDetails.end(), [&](const Details& d) { return d.source == source; });
    const auto kept = it == kDetails.end() ? Details{source, false, false, false} : *it;
    Counts out{input, output, std::nullopt, std::nullopt, std::nullopt};
    if (kept.cachedInput) out.cachedInput = cached;
    if (kept.cacheWriteInput) out.cacheWriteInput = std::min(std::max<std::int64_t>(0, usage.cacheWriteInput), input - cached);
    if (kept.reasoning) out.reasoning = std::min(std::max<std::int64_t>(0, usage.reasoning), output);
    return out;
}

std::optional<Counts> merged(const std::optional<Counts>& a, const std::optional<Counts>& b) {
    if (!a || !b) return std::nullopt;
    return a->plus(*b);
}

void ModelSums::add(const std::optional<std::string>& model, const Counts& counts) {
    auto& sums = model ? named[*model] : others;
    sums.first = saturatingSum(sums.first, counts.input);
    sums.second = saturatingSum(sums.second, counts.output);
}

std::optional<TokenUsage> tokenUsageOf(const Counts& total, const ModelSums& models) {
    TokenUsage out;
    out.counts = total;
    for (const auto& [model, sums] : models.named) {
        if (sums.first || sums.second) out.models.push_back({model, sums.first, sums.second});
    }
    if (models.others.first || models.others.second) out.models.push_back({std::nullopt, models.others.first, models.others.second});
    if (out.models.size() > kTokenModels || total.input > kMaxSafeInteger || total.output > kMaxSafeInteger) return std::nullopt;
    return out;
}

Json TokenUsage::json() const {
    std::vector<Json> rows;
    rows.reserve(models.size());
    for (const auto& m : models) {
        rows.push_back(Json::object({{"model", m.model ? Json::of(*m.model) : Json::null()},
                                     {"inputTokens", Json::of(m.inputTokens)},
                                     {"outputTokens", Json::of(m.outputTokens)}}));
    }
    return Json::object({{"inputTokens", Json::of(counts.input)},
                         {"outputTokens", Json::of(counts.output)},
                         {"cachedInputTokens", optionalInt(counts.cachedInput)},
                         {"cacheWriteInputTokens", optionalInt(counts.cacheWriteInput)},
                         {"reasoningTokens", optionalInt(counts.reasoning)},
                         {"models", Json::array(std::move(rows))}});
}

}  // namespace agentspeed::quota::estimate
