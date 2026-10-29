#pragma once

#include <cstdint>
#include <map>
#include <memory>
#include <optional>
#include <set>
#include <string>
#include <string_view>
#include <unordered_map>
#include <vector>

#include "agentspeed/model.hpp"

// METRICS 9.1–9.3: the price table, model resolution and the integer cost of one response.
namespace agentspeed::pricing {

// Micro-USD per million tokens (= pico-USD per token), defaults of 9.1 applied.
struct PriceSet {
    std::int64_t input = 0, cachedInput = 0, cacheWrite = 0, cacheWrite1h = 0, output = 0;
};

struct PriceTier {
    std::int64_t above = 0;
    PriceSet prices;
};

struct TieredPrices {
    PriceSet prices;
    std::vector<PriceTier> tiers;
    // The price set for a response of `inputTokens` and the tier used (nullopt: the base prices).
    std::pair<PriceSet, std::optional<int>> select(std::int64_t inputTokens) const;
};

struct OffPeakRule {
    std::vector<std::pair<int, int>> peakWindows;  // [start, end) minutes of the UTC day
    std::set<int> peakWeekdays;
    std::set<std::string> offPeakDates;
    TieredPrices pricing;
    bool isOffPeak(std::int64_t ms) const;
};

struct ModelPricing {
    TieredPrices base;
    std::optional<OffPeakRule> offPeak;
};

struct PricePeriod {
    std::optional<std::int64_t> fromMs, toMs;
    ModelPricing pricing;
    bool covers(std::int64_t ms) const { return (!fromMs || *fromMs <= ms) && (!toMs || ms < *toMs); }
};

struct PriceModel {
    std::string id, provider, name, status;
    bool isFree = false;
    std::optional<ModelPricing> pricing;  // nullopt exactly when free
    std::vector<PricePeriod> periods;
    std::optional<TieredPrices> fast;
    std::vector<std::string> aliases;
    std::string source, checked;
    std::optional<std::string> note;
    // Fast prices for a fast response, else the period covering endMs, then its off-peak prices; nullopt when a fast
    // response has no fast price.
    const TieredPrices* pricingFor(std::optional<std::int64_t> endMs, bool fast) const;
};

struct Normalization {
    std::vector<std::string> freeProviders, cloudSuffixes, bedrockRegions, bedrockVendors, freeSuffixes, variantSuffixes,
        fastSuffixes;
    std::vector<std::pair<std::string, std::string>> rewrites;
    std::vector<std::string> dateSuffixPrefixes;
    std::vector<std::pair<std::string, std::vector<std::string>>> modifierSuffixes;
    std::vector<std::string> dotHyphenPrefixes;
};

struct UnknownRule {
    bool isPrefix = false;
    std::string value, reason;
};

enum class ResolutionKind { Priced, Free, Unknown };

struct Resolution {
    ResolutionKind kind = ResolutionKind::Unknown;
    int index = -1;
    bool fast = false;
    std::optional<std::string> reason;
};

enum class Miss { None, NoModel, NotInTable, NoFastPrice, Rule };

// What a walk through the candidate keys found: the resolution, the key that hit and why it missed.
struct Outcome {
    Resolution resolution;
    std::optional<std::string> key;
    Miss miss = Miss::None;
    std::optional<std::string> rule;
};

class ModelResolver {
public:
    ModelResolver(const Normalization& normalize, const std::vector<PriceModel>& models, const std::vector<UnknownRule>& unknown);
    Resolution resolve(const std::optional<std::string>& model, const std::optional<std::string>& provider) const;
    Outcome walk(const std::optional<std::string>& model, const std::optional<std::string>& provider) const;
    bool isLocalProvider(const std::optional<std::string>& model, const std::optional<std::string>& provider) const;
    bool hasFast(int index) const { return hasFast_[static_cast<std::size_t>(index)]; }

    struct Candidate {
        const char* step;
        std::optional<std::string> key;  // nullopt: the free step
        bool fast;
    };
    std::vector<Candidate> candidates(const std::string& model) const;
    static std::string exactKey(const std::string& model);

private:
    std::optional<Outcome> lookup(const std::string& key, bool fast) const;

    const Normalization& normalize_;
    std::unordered_map<std::string, int> byKey_;
    std::vector<bool> free_, hasFast_;
    std::unordered_map<std::string, std::string> unknownExact_;
    std::vector<std::pair<std::string, std::string>> unknownPrefixes_;
    mutable std::map<std::pair<std::optional<std::string>, std::optional<std::string>>, Resolution> cache_;
};

// Python string rules the resolver and validator share.
std::string strip(std::string_view text);
std::string lower(std::string_view text);
bool isTrimmedLowercase(std::string_view text);

struct Cost {
    std::int64_t pico = 0;
    std::optional<int> tier;
};
constexpr std::int64_t kMaxClassTokens = 100'000'000;
Cost picoUsd(const TokenCounts& tokens, const TieredPrices& pricing);
inline std::int64_t microUsd(std::int64_t pico) { return (pico + 500'000) / 1'000'000; }

enum class TableErrorKind { TooLarge, NotJson, UnsupportedSchema, Invalid };
struct TableError {
    TableErrorKind kind = TableErrorKind::Invalid;
    std::optional<std::int64_t> schema;
    std::vector<std::string> errors;
    std::string describe() const;
};

class PriceTable {
public:
    static constexpr int kSupportedSchema = 1;
    static constexpr std::size_t kMaxBytes = 1 << 20;

    // Parses and validates exactly like spec/tools/prices.py (NETWORK 3 checks 1–4).
    static std::shared_ptr<const PriceTable> load(std::string_view utf8, TableError* error = nullptr);

    int version() const { return version_; }
    const std::string& updated() const { return updated_; }
    const std::vector<PriceModel>& models() const { return models_; }
    const std::map<std::string, std::pair<std::string, std::string>>& providers() const { return providers_; }
    const ModelResolver& resolver() const { return *resolver_; }
    const std::string& data() const { return data_; }

    // Integer micro-USD of one response; nullopt when its price is unknown. Free models cost 0.
    std::optional<std::int64_t> cost(const std::optional<std::string>& model, const std::optional<std::string>& provider,
                                     const TokenCounts& tokens, std::optional<std::int64_t> endMs, bool fast) const;
    // `Resolution.label` of prices.py: the model id (with " fast"), "free" or "unknown".
    std::string label(const Resolution& resolution) const;
    // The id a response's model is reported as (METRICS 11.3).
    std::string reportModelId(const std::optional<std::string>& model, const std::optional<std::string>& provider) const;

private:
    friend class Validator;
    PriceTable() = default;
    void finish();

    int version_ = 0;
    std::string updated_;
    std::map<std::string, std::pair<std::string, std::string>> providers_;  // key → name, url
    Normalization normalize_;
    std::vector<PriceModel> models_;
    std::vector<UnknownRule> unknown_;
    std::vector<std::pair<std::optional<std::string>, std::optional<std::string>>> testModels_;
    std::vector<std::string> testExpect_;
    std::string data_;
    std::unique_ptr<ModelResolver> resolver_;
};

// UTC calendar facts of an instant (periods and off-peak hours).
struct UtcDay {
    explicit UtcDay(std::int64_t ms);
    std::int64_t daysSinceEpoch = 0, msOfDay = 0;
    int minuteOfDay() const { return static_cast<int>(msOfDay / 60'000); }
    int isoWeekday() const;
    std::string label() const;
    // YYYY-MM-DD (00:00 UTC) or YYYY-MM-DDTHH:MMZ; nullopt for anything else.
    static std::optional<std::int64_t> instantMs(std::string_view text);
    static bool isDateText(std::string_view text);
};

}  // namespace agentspeed::pricing
