#include <algorithm>
#include <charconv>
#include <cmath>

#include "agentspeed/json.hpp"
#include "agentspeed/pricing.hpp"

// `validate_table` of spec/tools/prices.py, rule for rule, on the raw document (METRICS 9.1, NETWORK 3).
namespace agentspeed::pricing {

namespace {

using json::Value;
__extension__ using u128 = unsigned __int128;

constexpr const char* kPriceFields[] = {"input", "cachedInput", "cacheWrite", "cacheWrite1h", "output"};
constexpr std::int64_t kMaxPriceMicro = 10'000LL * 1'000'000;
constexpr std::size_t kMaxModels = 2000;

// A JSON number as mantissa × 10^exponent, the way Python's Decimal(repr(float)) or int sees it.
struct Exact {
    bool negative = false;
    u128 mantissa = 0;
    int exponent = 0;
    bool huge = false;  // more digits than 128 bits hold
};

std::optional<Exact> parseDecimal(std::string_view text) {
    Exact e;
    std::size_t i = 0;
    if (i < text.size() && (text[i] == '-' || text[i] == '+')) e.negative = text[i++] == '-';
    int digits = 0;
    bool dot = false;
    for (; i < text.size(); ++i) {
        const char c = text[i];
        if (c >= '0' && c <= '9') {
            if (e.mantissa > (~u128(0) - 9) / 10) {
                e.huge = true;
            } else {
                e.mantissa = e.mantissa * 10 + static_cast<unsigned>(c - '0');
            }
            if (e.huge && !dot) ++e.exponent;
            ++digits;
            if (dot && !e.huge) --e.exponent;
        } else if (c == '.' && !dot) {
            dot = true;
        } else {
            break;
        }
    }
    if (digits == 0) return std::nullopt;
    if (i < text.size()) {
        if (text[i] != 'e' && text[i] != 'E') return std::nullopt;
        int power = 0;
        const auto* begin = text.data() + i + 1;
        const auto* end = text.data() + text.size();
        if (begin < end && *begin == '+') ++begin;
        const auto [ptr, ec] = std::from_chars(begin, end, power);
        if (ec != std::errc() || ptr != end || power > 100'000 || power < -100'000) return std::nullopt;
        e.exponent += power;
    }
    return e;
}

bool isInteger(const Value& v) { return v.isNumber() && v.raw().find_first_of(".eE") == std::string_view::npos; }

std::optional<std::int64_t> int64Of(const Value& v) {
    if (!isInteger(v)) return std::nullopt;
    std::int64_t out = 0;
    const auto raw = v.raw();
    const auto [ptr, ec] = std::from_chars(raw.data(), raw.data() + raw.size(), out);
    if (ec != std::errc() || ptr != raw.data() + raw.size()) return std::nullopt;
    return out;
}

std::optional<Exact> exactOf(const Value& v) {
    if (!v.isNumber()) return std::nullopt;
    if (isInteger(v)) return parseDecimal(v.raw());
    const auto number = v.number();
    if (!number || !std::isfinite(*number)) return std::nullopt;
    char buffer[64];
    const auto [ptr, ec] = std::to_chars(buffer, buffer + sizeof buffer, *number);
    if (ec != std::errc()) return std::nullopt;
    return parseDecimal(std::string_view(buffer, static_cast<std::size_t>(ptr - buffer)));
}

bool truthy(const Value& v) {
    if (!v.valid() || v.isNull()) return false;
    switch (v.type()) {
        case json::Type::True: return true;
        case json::Type::False: return false;
        case json::Type::Number: {
            const auto e = exactOf(v);
            return !e || e->huge || e->mantissa != 0;
        }
        case json::Type::String: return !v.string()->empty();
        default: return v.size() > 0;
    }
}

std::string pythonString(const Value& v) {
    if (!v.valid() || v.isNull()) return "None";
    if (v.isString()) return *v.string();
    if (v.isNumber()) return std::string(v.raw());
    if (v.isBool()) return v.isTrue() ? "True" : "False";
    return "…";
}

std::optional<std::string> stringOf(const Value& v) { return v.isString() ? v.string() : std::nullopt; }

// Unique member names in first-occurrence order (values: the last, through operator[]).
std::vector<std::string> keysOf(const Value& v) {
    std::vector<std::string> keys;
    if (!v.isObject()) return keys;
    for (const auto& [key, value] : v.members()) {
        if (std::find(keys.begin(), keys.end(), key) == keys.end()) keys.push_back(key);
    }
    return keys;
}

bool isId(std::string_view text) {
    const auto lowerAlnum = [](char c) { return (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9'); };
    if (text.empty() || !lowerAlnum(text[0])) return false;
    return std::all_of(text.begin() + 1, text.end(), [&](char c) { return lowerAlnum(c) || c == '.' || c == '-' || c == '_' || c == ':' || c == '+'; });
}

std::optional<std::vector<std::string>> lowercaseList(const std::vector<Value>& items, bool unique = true) {
    std::vector<std::string> values;
    for (const auto& item : items) {
        const auto text = stringOf(item);
        if (!text || text->empty() || !isTrimmedLowercase(*text)) return std::nullopt;
        values.push_back(*text);
    }
    if (unique) {
        auto sorted = values;
        std::sort(sorted.begin(), sorted.end());
        if (std::adjacent_find(sorted.begin(), sorted.end()) != sorted.end()) return std::nullopt;
    }
    return values;
}

std::optional<std::vector<std::string>> lowercaseList(const Value& v) {
    if (!v.isArray()) return std::nullopt;
    return lowercaseList(v.elements());
}

// Aliases: trimmed lowercase non-empty strings; duplicates are reported across the table.
std::optional<std::vector<std::string>> aliasesOf(const Value& v) {
    if (!v.valid()) return std::vector<std::string>{};
    if (!v.isArray()) return std::nullopt;
    return lowercaseList(v.elements(), false);
}

std::optional<std::pair<int, int>> window(const std::optional<std::string>& text) {
    if (!text || text->size() != 11 || (*text)[2] != ':' || (*text)[5] != '-' || (*text)[8] != ':') return std::nullopt;
    const auto two = [&](std::size_t at) -> std::optional<int> {
        const char a = (*text)[at], b = (*text)[at + 1];
        if (a < '0' || a > '9' || b < '0' || b > '9') return std::nullopt;
        return (a - '0') * 10 + (b - '0');
    };
    const auto h0 = two(0), m0 = two(3), h1 = two(6), m1 = two(9);
    if (!h0 || !m0 || !h1 || !m1) return std::nullopt;
    const int start = *h0 * 60 + *m0, end = *h1 * 60 + *m1;
    if (start < end && end <= 1440) return std::make_pair(start, end);
    return std::nullopt;
}

std::optional<int> weekday(const Value& v) {
    if (!v.isNumber()) return std::nullopt;
    const auto number = v.number();
    if (!number || !std::isfinite(*number)) return std::nullopt;
    for (int day = 1; day <= 7; ++day) {
        if (*number == day) return day;
    }
    return std::nullopt;
}

std::string sortedQuoted(std::vector<std::string> keys) {
    std::sort(keys.begin(), keys.end());
    std::string out;
    for (std::size_t i = 0; i < keys.size(); ++i) out += (i ? ", '" : "'") + keys[i] + "'";
    return out;
}

}  // namespace

class Validator {
public:
    std::vector<std::string> errors;

    std::shared_ptr<PriceTable> validate(const Value& t, std::string data);

private:
    std::optional<Normalization> validateNormalize(const Value& norm);
    std::optional<PriceModel> validateModel(const Value& raw, std::size_t index, const std::map<std::string, std::pair<std::string, std::string>>& providers,
                                            const std::optional<std::string>& updated);
    std::vector<PricePeriod> checkPeriods(const Value& raw, const std::string& where);
    std::optional<ModelPricing> checkPricing(const Value& raw, const std::string& where, const std::vector<std::string>& allowed, bool allowsOffPeak);
    std::optional<OffPeakRule> checkOffPeak(const Value& raw, const std::string& where);
    std::optional<PriceSet> checkPriceSet(const Value& raw, const std::string& where);
    std::optional<std::int64_t> checkPrice(const Value& value, const std::string& where);
    static std::set<std::string> setFields(const Value& obj);
};

// price × 1 000 000, or nullopt when out of range; `fraction` when it is not an integer.
static std::optional<std::pair<std::int64_t, bool>> micro(const Value& value) {
    auto exact = exactOf(value);
    if (!exact) return std::nullopt;
    auto exponent = exact->exponent + 6;
    if (exact->negative && exact->mantissa != 0) return std::nullopt;
    if (exact->mantissa == 0 && !exact->huge) return std::make_pair(std::int64_t{0}, false);
    if (exact->huge) return std::nullopt;
    const u128 limit = static_cast<u128>(kMaxPriceMicro);
    if (exponent >= 0) {
        if (exponent > 20 || exact->mantissa > limit) return std::nullopt;
        u128 whole = exact->mantissa;
        for (int i = 0; i < exponent; ++i) {
            whole *= 10;
            if (whole > limit) return std::nullopt;
        }
        return std::make_pair(static_cast<std::int64_t>(whole), false);
    }
    if (-exponent > 38) return std::make_pair(std::int64_t{0}, true);
    u128 divisor = 1;
    for (int i = 0; i < -exponent; ++i) divisor *= 10;
    const u128 quotient = exact->mantissa / divisor, remainder = exact->mantissa % divisor;
    if (quotient > limit || (quotient == limit && remainder != 0)) return std::nullopt;
    return std::make_pair(static_cast<std::int64_t>(quotient), remainder != 0);
}

std::shared_ptr<PriceTable> Validator::validate(const Value& t, std::string data) {
    if (!t.isObject()) {
        errors.push_back("table is not an object");
        return nullptr;
    }
    if (int64Of(t["schema"]) != PriceTable::kSupportedSchema) errors.push_back("schema must be 1");
    const auto version = int64Of(t["version"]);
    if (!version || *version < 1) errors.push_back("version must be a positive integer");
    const auto updated = stringOf(t["updated"]);
    if (!updated || !UtcDay::isDateText(*updated)) errors.push_back("updated must be YYYY-MM-DD");
    if (!t["currency"].equals("USD") || !t["currency"].isString() || !t["unit"].isString() || !t["unit"].equals("perMillionTokens")) {
        errors.push_back("currency must be \"USD\" and unit \"perMillionTokens\"");
    }

    std::map<std::string, std::pair<std::string, std::string>> providers;
    const auto rawProviders = t["providers"];
    if (rawProviders.isObject() && rawProviders.size() > 0) {
        for (const auto& key : keysOf(rawProviders)) {
            const auto entry = rawProviders[key];
            const auto name = entry["name"];
            const auto url = pythonString(entry["url"]);
            if (!isId(key) || !entry.isObject() || !truthy(name) || url.rfind("https://", 0) != 0) {
                errors.push_back("provider " + key + ": needs name and an https url");
            }
            providers[key] = {stringOf(name).value_or(""), url};
        }
    } else {
        errors.push_back("providers must be a non-empty object");
    }

    static const std::vector<std::string> kNormalizeKeys = {"freeProviders", "cloudSuffixes", "bedrockRegions", "bedrockVendors",
                                                            "freeSuffixes", "variantSuffixes", "fastSuffixes", "dateSuffixPrefixes",
                                                            "dotHyphenPrefixes", "rewrites", "modifierSuffixes"};
    const auto norm = t["normalize"];
    auto normKeys = keysOf(norm);
    auto wanted = kNormalizeKeys;
    std::sort(normKeys.begin(), normKeys.end());
    std::sort(wanted.begin(), wanted.end());
    if (!norm.isObject() || normKeys != wanted) {
        errors.push_back("normalize must have exactly [" + sortedQuoted(kNormalizeKeys) + "]");
        return nullptr;
    }
    auto normalize = validateNormalize(norm);
    if (!normalize || !errors.empty()) return nullptr;

    const auto rawModels = t["models"];
    if (!rawModels.isArray() || rawModels.size() == 0 || rawModels.size() > kMaxModels) {
        errors.push_back("models must be a list of 1.." + std::to_string(kMaxModels) + " entries");
        return nullptr;
    }
    std::vector<PriceModel> models;
    std::map<std::string, int> keyCounts;
    std::vector<std::string> keyOrder;
    const auto modelItems = rawModels.elements();
    for (std::size_t index = 0; index < modelItems.size(); ++index) {
        const auto& raw = modelItems[index];
        if (auto model = validateModel(raw, index, providers, updated)) models.push_back(std::move(*model));
        if (!raw.isObject()) continue;
        auto keys = aliasesOf(raw["aliases"]).value_or(std::vector<std::string>{});
        if (const auto id = stringOf(raw["id"])) keys.insert(keys.begin(), *id);
        for (const auto& key : keys) {
            if (!keyCounts.contains(key)) keyOrder.push_back(key);
            ++keyCounts[key];
        }
    }
    for (const auto& key : keyOrder) {
        if (keyCounts[key] > 1) errors.push_back("id or alias \"" + key + "\" is used " + std::to_string(keyCounts[key]) + " times");
    }

    std::vector<UnknownRule> unknown;
    if (const auto rawUnknown = t["unknown"]; rawUnknown.valid()) {
        if (rawUnknown.isArray()) {
            const auto items = rawUnknown.elements();
            for (std::size_t index = 0; index < items.size(); ++index) {
                const auto& entry = items[index];
                const auto match = stringOf(entry["match"]);
                const auto value = stringOf(entry["value"]);
                if ((match != "exact" && match != "prefix") || !value || value->empty() || !isTrimmedLowercase(*value) ||
                    !truthy(entry["reason"])) {
                    errors.push_back("unknown[" + std::to_string(index) + "]: needs match exact|prefix, a lowercase value and a reason");
                    continue;
                }
                const bool isPrefix = match == "prefix";
                for (const auto& key : keyOrder) {
                    if (isPrefix ? key.rfind(*value, 0) == 0 : key == *value) {
                        errors.push_back("unknown[" + std::to_string(index) + "] \"" + *value + "\" shadows the id or alias \"" + key + "\"");
                    }
                }
                unknown.push_back({isPrefix, *value, pythonString(entry["reason"])});
            }
        } else {
            errors.push_back("unknown must be a list");
        }
    }
    if (!errors.empty() || !version || *version < 1 || *version > INT32_MAX || !updated) {
        if (errors.empty()) errors.push_back("version must be a positive integer");
        return nullptr;
    }

    std::vector<std::pair<std::optional<std::string>, std::optional<std::string>>> tests;
    std::vector<std::string> expects;
    std::vector<std::size_t> testIndices;
    if (const auto rawTests = t["tests"]; rawTests.valid()) {
        if (rawTests.isArray()) {
            const auto items = rawTests.elements();
            for (std::size_t index = 0; index < items.size(); ++index) {
                const auto& entry = items[index];
                const auto expect = stringOf(entry["expect"]);
                if (!entry.isObject() || !expect || !entry["model"].valid()) {
                    errors.push_back("tests[" + std::to_string(index) + "]: needs model and expect");
                    continue;
                }
                tests.emplace_back(stringOf(entry["model"]), stringOf(entry["provider"]));
                expects.push_back(*expect);
                testIndices.push_back(index);
            }
        } else {
            errors.push_back("tests must be a list");
        }
    }

    auto table = std::shared_ptr<PriceTable>(new PriceTable());
    table->version_ = static_cast<int>(*version);
    table->updated_ = *updated;
    table->providers_ = std::move(providers);
    table->normalize_ = std::move(*normalize);
    table->models_ = std::move(models);
    table->unknown_ = std::move(unknown);
    table->testModels_ = tests;
    table->testExpect_ = expects;
    table->data_ = std::move(data);
    table->finish();
    for (std::size_t index = 0; index < table->models_.size(); ++index) {
        const auto& model = table->models_[index];
        std::vector<std::string> keys{model.id};
        keys.insert(keys.end(), model.aliases.begin(), model.aliases.end());
        for (const auto& key : keys) {
            const auto resolution = table->resolver().resolve(key, std::nullopt);
            const bool ok = resolution.index == static_cast<int>(index) && !resolution.fast &&
                            resolution.kind == (model.isFree ? ResolutionKind::Free : ResolutionKind::Priced);
            if (!ok) errors.push_back("\"" + key + "\" does not resolve to " + model.id + " (got " + table->label(resolution) + ")");
        }
    }
    for (std::size_t i = 0; i < tests.size(); ++i) {
        const auto got = table->label(table->resolver().resolve(tests[i].first, tests[i].second));
        if (got != expects[i]) {
            errors.push_back("tests[" + std::to_string(testIndices[i]) + "]: " + tests[i].first.value_or("null") + " (provider " +
                             tests[i].second.value_or("None") + ") expected " + expects[i] + " got " + got);
        }
    }
    return errors.empty() ? table : nullptr;
}

std::optional<Normalization> Validator::validateNormalize(const Value& norm) {
    std::map<std::string, std::vector<std::string>> lists;
    for (const char* key : {"freeProviders", "cloudSuffixes", "bedrockRegions", "bedrockVendors", "freeSuffixes", "variantSuffixes",
                            "fastSuffixes", "dateSuffixPrefixes", "dotHyphenPrefixes"}) {
        if (auto list = lowercaseList(norm[key])) {
            lists[key] = std::move(*list);
        } else {
            errors.push_back(std::string("normalize.") + key + " must be unique lowercase strings");
        }
    }
    Normalization n;
    const auto rewrites = norm["rewrites"];
    bool rewritesOk = rewrites.isArray();
    if (rewritesOk) {
        for (const auto& entry : rewrites.elements()) {
            const bool ok = entry.isObject() && keysOf(entry).size() == 2 && entry["prefix"].valid() && entry["to"].valid() &&
                            lowercaseList(std::vector<Value>{entry["prefix"], entry["to"]}).has_value();
            if (!ok) {
                rewritesOk = false;
                break;
            }
            n.rewrites.emplace_back(*entry["prefix"].string(), *entry["to"].string());
        }
    }
    if (!rewritesOk) errors.push_back("normalize.rewrites must be {prefix, to} objects with lowercase strings");
    const auto modifiers = norm["modifierSuffixes"];
    bool modifiersOk = modifiers.isArray();
    if (modifiersOk) {
        for (const auto& entry : modifiers.elements()) {
            const auto suffixes = entry["suffixes"];
            const bool ok = entry.isObject() && keysOf(entry).size() == 2 && entry["prefix"].valid() && suffixes.valid() &&
                            lowercaseList(std::vector<Value>{entry["prefix"]}).has_value() && lowercaseList(suffixes).has_value();
            if (!ok) {
                modifiersOk = false;
                break;
            }
            n.modifierSuffixes.emplace_back(*entry["prefix"].string(), *lowercaseList(suffixes));
        }
    }
    if (!modifiersOk) errors.push_back("normalize.modifierSuffixes must be {prefix, suffixes} objects with lowercase strings");
    if (!errors.empty()) return std::nullopt;
    n.freeProviders = lists["freeProviders"];
    n.cloudSuffixes = lists["cloudSuffixes"];
    n.bedrockRegions = lists["bedrockRegions"];
    n.bedrockVendors = lists["bedrockVendors"];
    n.freeSuffixes = lists["freeSuffixes"];
    n.variantSuffixes = lists["variantSuffixes"];
    n.fastSuffixes = lists["fastSuffixes"];
    n.dateSuffixPrefixes = lists["dateSuffixPrefixes"];
    n.dotHyphenPrefixes = lists["dotHyphenPrefixes"];
    return n;
}

std::optional<PriceModel> Validator::validateModel(const Value& raw, std::size_t index,
                                                   const std::map<std::string, std::pair<std::string, std::string>>& providers,
                                                   const std::optional<std::string>& updated) {
    static const std::vector<std::string> kModelKeys = {"id", "provider", "name", "status", "free", "prices", "tiers",
                                                        "offPeak", "periods", "fast", "aliases", "source", "checked", "note"};
    static const std::vector<std::string> kFreeKeys = {"id", "provider", "name", "status", "free", "aliases", "source", "checked", "note"};
    static const std::vector<std::string> kFastKeys = {"prices", "tiers"};
    static const std::set<std::string> kStatuses = {"current", "preview", "legacy", "deprecated", "retired"};
    const auto start = errors.size();
    const std::string where = "models[" + std::to_string(index) + "] " + (raw.isObject() ? pythonString(raw["id"]) : "?");
    if (!raw.isObject()) {
        errors.push_back(where + ": not an object");
        return std::nullopt;
    }
    const auto id = stringOf(raw["id"]);
    if (!id || !isId(*id)) errors.push_back(where + ": id must be lowercase [a-z0-9._:+-]");
    const auto provider = stringOf(raw["provider"]);
    if (!provider || !providers.contains(*provider)) {
        const auto repr = raw["provider"].isString() ? "'" + *raw["provider"].string() + "'" : pythonString(raw["provider"]);
        errors.push_back(where + ": provider " + repr + " is not in providers");
    }
    const auto name = stringOf(raw["name"]);
    if (!name || strip(*name).empty()) errors.push_back(where + ": name is required");
    const auto status = stringOf(raw["status"]);
    if (!status || !kStatuses.contains(*status)) {
        errors.push_back(where + ": status must be one of ['current', 'deprecated', 'legacy', 'preview', 'retired']");
    }
    const auto source = pythonString(raw["source"]);
    if (!raw["source"].valid() || source.rfind("https://", 0) != 0) errors.push_back(where + ": source must be an https URL");
    const auto checked = stringOf(raw["checked"]);
    if (!checked || !UtcDay::isDateText(*checked) || (updated && *checked > *updated)) {
        errors.push_back(where + ": checked must be YYYY-MM-DD, not after updated");
    }
    if (raw["note"].valid() && !raw["note"].isString()) errors.push_back(where + ": note must be a string");

    PriceModel model;
    const auto free = raw["free"];
    model.isFree = free.isTrue();
    if (model.isFree) {
        std::vector<std::string> extra;
        for (const auto& key : keysOf(raw)) {
            if (std::find(kFreeKeys.begin(), kFreeKeys.end(), key) == kFreeKeys.end()) extra.push_back(key);
        }
        std::sort(extra.begin(), extra.end());
        if (!extra.empty()) {
            std::string list;
            for (std::size_t i = 0; i < extra.size(); ++i) list += (i ? ", " : "") + extra[i];
            errors.push_back(where + ": a free model has no " + list);
        }
    } else if (free.valid()) {
        errors.push_back(where + ": free must be true or absent");
    } else {
        model.pricing = checkPricing(raw, where, kModelKeys, true);
        if (const auto fast = raw["fast"]; fast.valid()) {
            if (auto pricing = checkPricing(fast, where + ".fast", kFastKeys, false)) model.fast = pricing->base;
        }
        model.periods = checkPeriods(raw["periods"], where);
    }
    if (raw["aliases"].valid()) {
        if (auto aliases = aliasesOf(raw["aliases"])) {
            model.aliases = std::move(*aliases);
        } else {
            errors.push_back(where + ": aliases must be trimmed lowercase strings");
        }
    }
    if (errors.size() != start || !id || !provider || !name || !status || !checked) return std::nullopt;
    model.id = *id;
    model.provider = *provider;
    model.name = *name;
    model.status = *status;
    model.source = source;
    model.checked = *checked;
    model.note = stringOf(raw["note"]);
    return model;
}

std::vector<PricePeriod> Validator::checkPeriods(const Value& raw, const std::string& where) {
    static const std::vector<std::string> kPeriodKeys = {"from", "to", "prices", "tiers", "offPeak", "note"};
    std::vector<PricePeriod> periods;
    if (!raw.valid()) return periods;
    if (!raw.isArray()) {
        errors.push_back(where + ": periods must be a list");
        return periods;
    }
    std::vector<std::pair<std::int64_t, std::int64_t>> spans;
    const auto items = raw.elements();
    for (std::size_t index = 0; index < items.size(); ++index) {
        const auto pw = where + ".periods[" + std::to_string(index) + "]";
        const auto& entry = items[index];
        const auto f = entry["from"], t = entry["to"];
        const auto instant = [](const Value& v) { return v.isString() ? UtcDay::instantMs(*v.string()) : std::nullopt; };
        if (!entry.isObject() || (!f.valid() && !t.valid()) || (f.valid() && !instant(f)) || (t.valid() && !instant(t))) {
            errors.push_back(pw + ": needs from and/or to (YYYY-MM-DD or YYYY-MM-DDTHH:MMZ, UTC)");
            continue;
        }
        const auto from = f.valid() ? instant(f) : std::nullopt;
        const auto to = t.valid() ? instant(t) : std::nullopt;
        const auto lo = from.value_or(INT64_MIN), hi = to.value_or(INT64_MAX);
        if (lo >= hi) errors.push_back(pw + ": from must be before to");
        if (std::any_of(spans.begin(), spans.end(), [&](const auto& s) { return lo < s.second && s.first < hi; })) {
            errors.push_back(pw + ": overlaps another period");
        }
        spans.emplace_back(lo, hi);
        if (auto pricing = checkPricing(entry, pw, kPeriodKeys, true)) periods.push_back({from, to, std::move(*pricing)});
    }
    return periods;
}

std::optional<ModelPricing> Validator::checkPricing(const Value& raw, const std::string& where, const std::vector<std::string>& allowed,
                                                    bool allowsOffPeak) {
    const auto start = errors.size();
    if (!raw.isObject()) {
        errors.push_back(where + ": not an object");
        return std::nullopt;
    }
    for (const auto& key : keysOf(raw)) {
        if (std::find(allowed.begin(), allowed.end(), key) == allowed.end()) errors.push_back(where + ": unknown field " + key);
    }
    const auto base = checkPriceSet(raw["prices"], where + ".prices");
    const auto baseObject = raw["prices"];
    const auto fields = baseObject.isObject() ? setFields(baseObject) : std::set<std::string>{};
    if (baseObject.isObject() && baseObject["above"].valid()) errors.push_back(where + ".prices: above belongs in tiers");
    std::int64_t last = 0;
    std::vector<PriceTier> tiers;
    std::vector<Value> rawTiers;
    if (const auto t = raw["tiers"]; t.valid()) {
        if (t.isArray()) {
            rawTiers = t.elements();
        } else {
            errors.push_back(where + ": tiers must be a list");
        }
    }
    for (std::size_t index = 0; index < rawTiers.size(); ++index) {
        const auto tw = where + ".tiers[" + std::to_string(index) + "]";
        const auto& tier = rawTiers[index];
        const auto prices = checkPriceSet(tier, tw);
        if (!tier.isObject()) continue;
        if (const auto above = int64Of(tier["above"]); above && *above > last) {
            last = *above;
            if (prices) tiers.push_back({*above, *prices});
        } else {
            errors.push_back(tw + ": above must be an integer greater than the previous threshold");
        }
        if (setFields(tier) != fields) {
            errors.push_back(tw + ": must set exactly the price fields of the base prices [" +
                             sortedQuoted(std::vector<std::string>(fields.begin(), fields.end())) + "]");
        }
    }
    std::optional<OffPeakRule> rule;
    if (const auto rawRule = raw["offPeak"]; rawRule.valid()) {
        if (!allowsOffPeak) {
            errors.push_back(where + ": offPeak is not allowed here");
            return std::nullopt;
        }
        rule = checkOffPeak(rawRule, where + ".offPeak");
    }
    if (errors.size() != start || !base) return std::nullopt;
    return ModelPricing{TieredPrices{*base, std::move(tiers)}, std::move(rule)};
}

std::optional<OffPeakRule> Validator::checkOffPeak(const Value& raw, const std::string& ow) {
    static const std::vector<std::string> kOffPeakKeys = {"peakUtc", "peakWeekdays", "offPeakDates", "prices", "tiers"};
    const auto start = errors.size();
    auto pricing = checkPricing(raw, ow, kOffPeakKeys, false);
    if (!raw.isObject()) return std::nullopt;
    OffPeakRule rule;
    const auto list = raw["peakUtc"];
    bool windowsOk = list.isArray() && list.size() > 0;
    if (windowsOk) {
        for (const auto& w : list.elements()) {
            const auto parsed = window(stringOf(w));
            if (!parsed) {
                windowsOk = false;
                break;
            }
            rule.peakWindows.push_back(*parsed);
        }
    }
    if (!windowsOk) errors.push_back(ow + ".peakUtc: windows HH:MM-HH:MM with start < end <= 24:00");
    const auto days = raw["peakWeekdays"];
    bool daysOk = days.isArray() && days.size() > 0;
    if (daysOk) {
        for (const auto& d : days.elements()) {
            const auto day = weekday(d);
            if (!day) {
                daysOk = false;
                break;
            }
            rule.peakWeekdays.insert(*day);
        }
    }
    if (!daysOk) errors.push_back(ow + ".peakWeekdays: ISO weekdays 1 (Monday) .. 7");
    if (const auto dates = raw["offPeakDates"]; dates.valid()) {
        bool ok = dates.isArray();
        if (ok) {
            for (const auto& d : dates.elements()) {
                const auto text = stringOf(d);
                if (!text || !UtcDay::isDateText(*text)) {
                    ok = false;
                    break;
                }
                rule.offPeakDates.insert(*text);
            }
        }
        if (!ok) errors.push_back(ow + ".offPeakDates: YYYY-MM-DD dates");
    }
    if (errors.size() != start || !pricing) return std::nullopt;
    rule.pricing = pricing->base;
    return rule;
}

std::optional<PriceSet> Validator::checkPriceSet(const Value& raw, const std::string& where) {
    const auto start = errors.size();
    if (!raw.isObject()) {
        errors.push_back(where + ": not an object");
        return std::nullopt;
    }
    for (const auto& key : keysOf(raw)) {
        if (std::none_of(std::begin(kPriceFields), std::end(kPriceFields), [&](const char* f) { return key == f; }) && key != "above") {
            errors.push_back(where + ": unknown field " + key);
        }
    }
    for (const char* key : {"input", "output"}) {
        if (!raw[key].valid() || raw[key].isNull()) errors.push_back(where + ": " + key + " is required");
    }
    std::map<std::string, std::int64_t> prices;
    for (const char* key : kPriceFields) {
        const auto value = raw[key];
        if (value.valid() && !value.isNull()) {
            if (const auto price = checkPrice(value, where + "." + key)) prices[key] = *price;
        }
    }
    if (errors.size() != start || !prices.contains("input") || !prices.contains("output")) return std::nullopt;
    const auto get = [&](const char* key, std::int64_t fallback) {
        const auto it = prices.find(key);
        return it == prices.end() ? fallback : it->second;
    };
    const auto input = prices["input"];
    const auto cacheWrite = get("cacheWrite", input);
    return PriceSet{input, get("cachedInput", input), cacheWrite, get("cacheWrite1h", cacheWrite), prices["output"]};
}

std::optional<std::int64_t> Validator::checkPrice(const Value& value, const std::string& where) {
    if (!value.isNumber()) {
        errors.push_back(where + ": not a number");
        return std::nullopt;
    }
    const auto scaled = micro(value);
    if (!scaled) {
        errors.push_back(where + ": " + std::string(value.raw()) + " out of range 0..10000");
        return std::nullopt;
    }
    if (scaled->second) {
        errors.push_back(where + ": price " + std::string(value.raw()) + " has more than 6 decimals");
        return std::nullopt;
    }
    return scaled->first;
}

std::set<std::string> Validator::setFields(const Value& obj) {
    std::set<std::string> fields;
    for (const char* f : kPriceFields) {
        if (obj[f].valid() && !obj[f].isNull()) fields.insert(f);
    }
    return fields;
}

std::shared_ptr<const PriceTable> PriceTable::load(std::string_view utf8, TableError* error) {
    const auto fail = [&](TableErrorKind kind, std::optional<std::int64_t> schema = std::nullopt, std::vector<std::string> errors = {}) {
        if (error) *error = TableError{kind, schema, std::move(errors)};
        return std::shared_ptr<const PriceTable>();
    };
    if (utf8.size() > kMaxBytes) return fail(TableErrorKind::TooLarge);
    std::string data(utf8);
    json::Document document;
    if (!document.parse(data, 64) || !document.root().isObject()) return fail(TableErrorKind::NotJson);
    const auto schema = int64Of(document.root()["schema"]);
    if (schema != kSupportedSchema) return fail(TableErrorKind::UnsupportedSchema, schema);
    Validator validator;
    auto table = validator.validate(document.root(), data);
    if (!table) return fail(TableErrorKind::Invalid, kSupportedSchema, validator.errors);
    return table;
}

}  // namespace agentspeed::pricing
