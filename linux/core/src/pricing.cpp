#include "agentspeed/pricing.hpp"

#include <algorithm>
#include <cstdio>

#include "agentspeed/text.hpp"

namespace agentspeed::pricing {

using text::appendUtf8;
using text::isPythonSpace;
using text::nextCodePoint;

namespace {

constexpr std::string_view kCustomPrefix = "custom:";

bool startsWith(std::string_view text, std::string_view prefix) { return text.substr(0, prefix.size()) == prefix; }
bool endsWith(std::string_view text, std::string_view suffix) {
    return text.size() >= suffix.size() && text.substr(text.size() - suffix.size()) == suffix;
}

bool contains(const std::vector<std::string>& list, std::string_view value) {
    return std::find(list.begin(), list.end(), value) != list.end();
}

bool endsWithAny(std::string_view text, const std::vector<std::string>& suffixes) {
    return std::any_of(suffixes.begin(), suffixes.end(), [&](const std::string& s) { return endsWith(text, s); });
}

// Unicode Nd blocks whose digits are contiguous from a zero code point (the common scripts).
bool isDecimalDigit(std::uint32_t cp) {
    static constexpr std::uint32_t kZeros[] = {0x30,   0x660,  0x6F0,  0x7C0,  0x966,  0x9E6,  0xA66,  0xAE6,  0xB66,
                                               0xBE6,  0xC66,  0xCE6,  0xD66,  0xDE6,  0xE50,  0xED0,  0xF20,  0x1040,
                                               0x1090, 0x17E0, 0x1810, 0x1946, 0x19D0, 0xA620, 0xA8D0, 0xA900, 0xFF10};
    return std::any_of(std::begin(kZeros), std::end(kZeros), [&](std::uint32_t zero) { return cp >= zero && cp <= zero + 9; });
}

std::uint32_t lowerCodePoint(std::uint32_t cp) {
    if (cp >= 'A' && cp <= 'Z') return cp + 32;
    if (cp < 0x80) return cp;
    if ((cp >= 0xC0 && cp <= 0xDE && cp != 0xD7)) return cp + 32;
    if (cp >= 0x100 && cp <= 0x17E && cp != 0x130 && cp != 0x131 && cp != 0x138 && cp != 0x149 && cp != 0x178) {
        const bool evenUpper = (cp >= 0x100 && cp <= 0x137) || (cp >= 0x14A && cp <= 0x177);
        if (evenUpper) return (cp % 2 == 0) ? cp + 1 : cp;
        return (cp % 2 == 1) ? cp + 1 : cp;
    }
    if (cp == 0x178) return 0xFF;
    if (cp >= 0x391 && cp <= 0x3AB && cp != 0x3A2) return cp + 32;
    if (cp >= 0x410 && cp <= 0x42F) return cp + 32;
    if (cp >= 0x400 && cp <= 0x40F) return cp + 80;
    if (cp >= 0xFF21 && cp <= 0xFF3A) return cp + 32;
    return cp;
}

// Code points of a UTF-8 string, for the steps that look at digits from the end.
std::vector<std::uint32_t> codePoints(std::string_view s, std::vector<std::size_t>* offsets = nullptr) {
    std::vector<std::uint32_t> out;
    std::size_t i = 0;
    while (i < s.size()) {
        if (offsets) offsets->push_back(i);
        out.push_back(nextCodePoint(s, i));
    }
    if (offsets) offsets->push_back(s.size());
    return out;
}

std::string removeBrackets(const std::string& text) {
    if (text.find('[') == std::string::npos) return text;
    std::string out;
    std::size_t i = 0;
    while (i < text.size()) {
        const auto close = text[i] == '[' ? text.find(']', i + 1) : std::string::npos;
        if (close != std::string::npos) {
            out += ' ';
            i = close + 1;
        } else {
            out += text[i++];
        }
    }
    return out;
}

std::string collapseWhitespace(std::string_view value) {
    std::string out;
    bool inRun = false;
    std::size_t i = 0;
    while (i < value.size()) {
        const auto start = i;
        const auto cp = nextCodePoint(value, i);
        if (isPythonSpace(cp)) {
            if (!inRun) out += ' ';
            inRun = true;
        } else {
            out.append(value.substr(start, i - start));
            inRun = false;
        }
    }
    return out;
}

std::string customKey(const std::string& key) {
    std::size_t end = key.size();
    while (end > kCustomPrefix.size() && key[end - 1] >= '0' && key[end - 1] <= '9') --end;
    if (end < key.size() && end > kCustomPrefix.size() && key[end - 1] == '-') {
        --end;
    } else {
        end = key.size();
    }
    std::size_t start = kCustomPrefix.size();
    while (start < end && (key[start] == ' ' || key[start] == '-')) ++start;
    while (end > start && (key[end - 1] == ' ' || key[end - 1] == '-')) --end;
    return key.substr(start, end - start);
}

// The byte offset where exactly `count` trailing decimal digits start, or npos.
std::size_t trailingDigitsStart(const std::string& key, int count) {
    std::vector<std::size_t> offsets;
    const auto cps = codePoints(key, &offsets);
    if (static_cast<int>(cps.size()) < count) return std::string::npos;
    for (int n = 1; n <= count; ++n) {
        if (!isDecimalDigit(cps[cps.size() - static_cast<std::size_t>(n)])) return std::string::npos;
    }
    return offsets[cps.size() - static_cast<std::size_t>(count)];
}

std::string removeBedrockVersion(const std::string& key) {
    for (std::size_t i = 0; i + 2 < key.size(); ++i) {
        if (key[i] != '-' || key[i + 1] != 'v') continue;
        const auto tail = std::string_view(key).substr(i + 2);
        std::size_t j = 0;
        int digits = 0;
        while (j < tail.size()) {
            std::size_t next = j;
            if (!isDecimalDigit(nextCodePoint(tail, next))) break;
            j = next;
            ++digits;
        }
        if (digits == 0) continue;
        if (j == tail.size()) return key.substr(0, i);
        if (tail[j] != ':' || j + 1 == tail.size()) continue;
        ++j;
        bool all = true;
        while (j < tail.size()) {
            std::size_t next = j;
            if (!isDecimalDigit(nextCodePoint(tail, next))) {
                all = false;
                break;
            }
            j = next;
        }
        if (all) return key.substr(0, i);
    }
    return key;
}

std::string vertexDate(const std::string& key) {
    const auto at = trailingDigitsStart(key, 8);
    return at != std::string::npos && at > 0 && key[at - 1] == '@' ? key.substr(0, at - 1) + "-" + key.substr(at) : key;
}

std::string removeVertexTag(const std::string& key) {
    const auto at = key.rfind('@');
    return at == std::string::npos || key.find('/', at) != std::string::npos ? key : key.substr(0, at);
}

std::string removeDateSuffix(const std::string& key) {
    const auto at = trailingDigitsStart(key, 8);
    return at != std::string::npos && at > 0 && key[at - 1] == '-' ? key.substr(0, at - 1) : key;
}

}  // namespace

std::string strip(std::string_view value) { return text::strip(value); }

std::string lower(std::string_view value) {
    std::string out;
    out.reserve(value.size());
    std::size_t i = 0;
    while (i < value.size()) appendUtf8(out, lowerCodePoint(nextCodePoint(value, i)));
    return out;
}

bool isTrimmedLowercase(std::string_view value) { return lower(strip(value)) == value; }

std::pair<PriceSet, std::optional<int>> TieredPrices::select(std::int64_t inputTokens) const {
    PriceSet chosen = prices;
    std::optional<int> index;
    for (std::size_t i = 0; i < tiers.size(); ++i) {
        if (inputTokens > tiers[i].above) {
            chosen = tiers[i].prices;
            index = static_cast<int>(i);
        }
    }
    return {chosen, index};
}

bool OffPeakRule::isOffPeak(std::int64_t ms) const {
    const UtcDay day(ms);
    if (offPeakDates.contains(day.label()) || !peakWeekdays.contains(day.isoWeekday())) return true;
    const auto minute = day.minuteOfDay();
    return std::none_of(peakWindows.begin(), peakWindows.end(), [&](const auto& w) { return w.first <= minute && minute < w.second; });
}

const TieredPrices* PriceModel::pricingFor(std::optional<std::int64_t> endMs, bool fastMode) const {
    if (fastMode) return fast ? &*fast : nullptr;
    if (!pricing) return nullptr;
    const ModelPricing* applied = &*pricing;
    if (endMs) {
        for (const auto& period : periods) {
            if (period.covers(*endMs)) {
                applied = &period.pricing;
                break;
            }
        }
        if (applied->offPeak && applied->offPeak->isOffPeak(*endMs)) return &applied->offPeak->pricing;
    }
    return &applied->base;
}

ModelResolver::ModelResolver(const Normalization& normalize, const std::vector<PriceModel>& models, const std::vector<UnknownRule>& unknown)
    : normalize_(normalize), free_(models.size()), hasFast_(models.size()) {
    for (std::size_t i = 0; i < models.size(); ++i) {
        byKey_[models[i].id] = static_cast<int>(i);
        for (const auto& alias : models[i].aliases) byKey_[alias] = static_cast<int>(i);
        free_[i] = models[i].isFree;
        hasFast_[i] = models[i].fast.has_value();
    }
    for (const auto& rule : unknown) {
        if (rule.isPrefix) {
            unknownPrefixes_.emplace_back(rule.value, rule.reason);
        } else {
            unknownExact_[rule.value] = rule.reason;
        }
    }
}

std::string ModelResolver::exactKey(const std::string& model) { return lower(strip(collapseWhitespace(removeBrackets(model)))); }

bool ModelResolver::isLocalProvider(const std::optional<std::string>& model, const std::optional<std::string>& provider) const {
    return provider && contains(normalize_.freeProviders, lower(strip(*provider))) &&
           !(model && endsWithAny(lower(strip(*model)), normalize_.cloudSuffixes));
}

Resolution ModelResolver::resolve(const std::optional<std::string>& model, const std::optional<std::string>& provider) const {
    const auto key = std::make_pair(provider, model);
    if (const auto hit = cache_.find(key); hit != cache_.end()) return hit->second;
    auto result = walk(model, provider).resolution;
    if (cache_.size() >= 4096) cache_.clear();
    cache_[key] = result;
    return result;
}

Outcome ModelResolver::walk(const std::optional<std::string>& model, const std::optional<std::string>& provider) const {
    if (isLocalProvider(model, provider)) return {Resolution{ResolutionKind::Free, -1, false, std::nullopt}, std::nullopt, Miss::None, std::nullopt};
    if (!model || strip(*model).empty()) {
        return {Resolution{ResolutionKind::Unknown, -1, false, "no model"}, std::nullopt, Miss::NoModel, std::nullopt};
    }
    std::set<std::pair<std::string, bool>> seen;
    for (const auto& candidate : candidates(*model)) {
        if (!candidate.key) return {Resolution{ResolutionKind::Free, -1, false, std::nullopt}, std::nullopt, Miss::None, std::nullopt};
        if (candidate.key->empty() || !seen.emplace(*candidate.key, candidate.fast).second) continue;
        if (auto found = lookup(*candidate.key, candidate.fast)) return *found;
    }
    return {Resolution{ResolutionKind::Unknown, -1, false, "not in table"}, std::nullopt, Miss::NotInTable, std::nullopt};
}

std::optional<Outcome> ModelResolver::lookup(const std::string& key, bool fast) const {
    if (const auto it = byKey_.find(key); it != byKey_.end()) {
        const auto index = static_cast<std::size_t>(it->second);
        if (free_[index]) return Outcome{Resolution{ResolutionKind::Free, it->second, false, std::nullopt}, key, Miss::None, std::nullopt};
        if (fast && !hasFast_[index]) {
            return Outcome{Resolution{ResolutionKind::Unknown, -1, false, "no fast-mode price"}, key, Miss::NoFastPrice, std::nullopt};
        }
        return Outcome{Resolution{ResolutionKind::Priced, it->second, fast, std::nullopt}, key, Miss::None, std::nullopt};
    }
    if (const auto it = unknownExact_.find(key); it != unknownExact_.end()) {
        return Outcome{Resolution{ResolutionKind::Unknown, -1, false, it->second}, key, Miss::Rule, it->second};
    }
    for (const auto& [prefix, reason] : unknownPrefixes_) {
        if (startsWith(key, prefix)) return Outcome{Resolution{ResolutionKind::Unknown, -1, false, reason}, key, Miss::Rule, reason};
    }
    return std::nullopt;
}

std::vector<ModelResolver::Candidate> ModelResolver::candidates(const std::string& model) const {
    const auto& n = normalize_;
    std::vector<Candidate> out;
    auto k = exactKey(model);
    out.push_back({"exact", k, false});
    if (startsWith(k, kCustomPrefix)) {
        k = customKey(k);
        out.push_back({"custom", k, false});
    }
    if (const auto slash = k.rfind('/'); slash != std::string::npos) {
        k = k.substr(slash + 1);
        out.push_back({"route", k, false});
    }
    for (const auto& region : n.bedrockRegions) {
        const auto head = region + ".";
        if (!startsWith(k, head)) continue;
        const auto rest = k.substr(head.size());
        if (std::any_of(n.bedrockVendors.begin(), n.bedrockVendors.end(), [&](const std::string& v) { return startsWith(rest, v + "."); })) {
            k = rest;
            break;
        }
    }
    for (const auto& vendor : n.bedrockVendors) {
        const auto head = vendor + ".";
        if (startsWith(k, head)) {
            k = removeBedrockVersion(k.substr(head.size()));
            break;
        }
    }
    k = removeVertexTag(vertexDate(k));
    out.push_back({"cloud", k, false});
    for (const auto& suffix : n.freeSuffixes) {
        if (k.size() > suffix.size() && endsWith(k, suffix)) {
            out.push_back({"free", std::nullopt, false});
            return out;
        }
    }
    for (bool stripped = true; stripped;) {
        stripped = false;
        for (const auto& suffix : n.variantSuffixes) {
            if (k.size() > suffix.size() && endsWith(k, suffix)) {
                k.resize(k.size() - suffix.size());
                stripped = true;
            }
        }
    }
    out.push_back({"variant", k, false});
    for (const auto& [prefix, to] : n.rewrites) {
        if (startsWith(k, prefix)) {
            k = to + k.substr(prefix.size());
            break;
        }
    }
    out.push_back({"rewrite", k, false});
    bool fast = false;
    for (const auto& suffix : n.fastSuffixes) {
        if (k.size() > suffix.size() && endsWith(k, suffix)) {
            k.resize(k.size() - suffix.size());
            fast = true;
            break;
        }
    }
    if (fast) out.push_back({"fast", k, true});
    if (std::any_of(n.dateSuffixPrefixes.begin(), n.dateSuffixPrefixes.end(), [&](const std::string& p) { return startsWith(k, p); })) {
        k = removeDateSuffix(k);
    }
    out.push_back({"date", k, fast});
    for (const auto& [prefix, suffixes] : n.modifierSuffixes) {
        if (!startsWith(k, prefix)) continue;
        for (bool stripped = true; stripped;) {
            stripped = false;
            for (const auto& suffix : suffixes) {
                if (k.size() > prefix.size() + suffix.size() && endsWith(k, suffix)) {
                    k.resize(k.size() - suffix.size());
                    stripped = true;
                }
            }
        }
        break;
    }
    out.push_back({"modifier", k, fast});
    if (std::any_of(n.dotHyphenPrefixes.begin(), n.dotHyphenPrefixes.end(), [&](const std::string& p) { return startsWith(k, p); })) {
        std::replace(k.begin(), k.end(), '.', '-');
    }
    out.push_back({"dotHyphen", k, fast});
    return out;
}

Cost picoUsd(const TokenCounts& tokens, const TieredPrices& pricing) {
    const auto clamp = [](std::int64_t v) { return std::min(v, kMaxClassTokens); };
    const auto [prices, tier] = pricing.select(tokens.input);
    const auto write = tokens.cacheWriteInput;
    const auto write1h = std::min(tokens.cacheWrite1hInput, write);
    const auto cached = tokens.cachedInput;
    const auto uncached = clamp(std::max<std::int64_t>(0, tokens.input - cached - write));
    const auto pico = uncached * prices.input + clamp(cached) * prices.cachedInput + clamp(write - write1h) * prices.cacheWrite +
                      clamp(write1h) * prices.cacheWrite1h + clamp(tokens.output) * prices.output;
    return {pico, tier};
}

std::string TableError::describe() const {
    switch (kind) {
        case TableErrorKind::TooLarge: return "too large";
        case TableErrorKind::NotJson: return "not a JSON object";
        case TableErrorKind::UnsupportedSchema: return "schema " + (schema ? std::to_string(*schema) : std::string("missing"));
        case TableErrorKind::Invalid: break;
    }
    return std::to_string(errors.size()) + " validation errors, first: " + (errors.empty() ? std::string() : errors.front());
}

std::optional<std::int64_t> PriceTable::cost(const std::optional<std::string>& model, const std::optional<std::string>& provider,
                                             const TokenCounts& tokens, std::optional<std::int64_t> endMs, bool fast) const {
    const auto resolution = resolver_->resolve(model, provider);
    if (resolution.kind == ResolutionKind::Free) return 0;
    if (resolution.kind == ResolutionKind::Unknown) return std::nullopt;
    const auto* pricing = models_[static_cast<std::size_t>(resolution.index)].pricingFor(endMs, resolution.fast || fast);
    if (!pricing) return std::nullopt;
    return microUsd(picoUsd(tokens, *pricing).pico);
}

std::string PriceTable::label(const Resolution& resolution) const {
    switch (resolution.kind) {
        case ResolutionKind::Priced: return models_[static_cast<std::size_t>(resolution.index)].id + (resolution.fast ? " fast" : "");
        case ResolutionKind::Free: return "free";
        case ResolutionKind::Unknown: break;
    }
    return "unknown";
}

std::string PriceTable::reportModelId(const std::optional<std::string>& model, const std::optional<std::string>& provider) const {
    const auto resolution = resolver_->resolve(model, provider);
    std::optional<std::string> id;
    if (resolution.kind == ResolutionKind::Priced || (resolution.kind == ResolutionKind::Free && resolution.index >= 0)) {
        id = models_[static_cast<std::size_t>(resolution.index)].id;
    } else if (resolution.kind == ResolutionKind::Free && resolver_->isLocalProvider(model, provider)) {
        id = "local";
    }
    const auto valid = [](const std::string& s) {
        const auto lowerAlnum = [](char c) { return (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9'); };
        return !s.empty() && s.size() <= 64 && lowerAlnum(s[0]) &&
               std::all_of(s.begin(), s.end(), [&](char c) { return lowerAlnum(c) || c == '.' || c == '_' || c == ':' || c == '-'; });
    };
    return id && valid(*id) ? *id : "unknown";
}

void PriceTable::finish() { resolver_ = std::make_unique<ModelResolver>(normalize_, models_, unknown_); }

UtcDay::UtcDay(std::int64_t ms) {
    constexpr std::int64_t kMsPerDay = 86'400'000;
    daysSinceEpoch = ms / kMsPerDay;
    msOfDay = ms % kMsPerDay;
    if (msOfDay < 0) {
        --daysSinceEpoch;
        msOfDay += kMsPerDay;
    }
}

int UtcDay::isoWeekday() const {
    auto offset = static_cast<int>((daysSinceEpoch + 3) % 7);
    return (offset < 0 ? offset + 7 : offset) + 1;
}

std::string UtcDay::label() const {
    const std::int64_t z = daysSinceEpoch + 719'468;
    const std::int64_t era = (z >= 0 ? z : z - 146'096) / 146'097;
    const std::int64_t doe = z - era * 146'097;
    const std::int64_t yoe = (doe - doe / 1460 + doe / 36524 - doe / 146'096) / 365;
    const std::int64_t doy = doe - (365 * yoe + yoe / 4 - yoe / 100);
    const std::int64_t mp = (5 * doy + 2) / 153;
    const std::int64_t day = doy - (153 * mp + 2) / 5 + 1;
    const std::int64_t month = mp < 10 ? mp + 3 : mp - 9;
    const std::int64_t year = yoe + era * 400 + (month <= 2 ? 1 : 0);
    char buffer[64];
    std::snprintf(buffer, sizeof buffer, "%04lld-%02lld-%02lld", static_cast<long long>(year), static_cast<long long>(month),
                  static_cast<long long>(day));
    return buffer;
}

bool UtcDay::isDateText(std::string_view text) {
    if (text.size() != 10) return false;
    for (std::size_t i = 0; i < 10; ++i) {
        if (i == 4 || i == 7 ? text[i] != '-' : (text[i] < '0' || text[i] > '9')) return false;
    }
    return true;
}

std::optional<std::int64_t> UtcDay::instantMs(std::string_view text) {
    if ((text.size() != 10 && text.size() != 17) || text[4] != '-' || text[7] != '-') return std::nullopt;
    const auto number = [&](std::size_t start, std::size_t length) -> std::optional<std::int64_t> {
        std::int64_t value = 0;
        for (std::size_t i = start; i < start + length; ++i) {
            if (text[i] < '0' || text[i] > '9') return std::nullopt;
            value = value * 10 + (text[i] - '0');
        }
        return value;
    };
    const auto year = number(0, 4), month = number(5, 2), day = number(8, 2);
    if (!year || !month || !day || *month < 1 || *month > 12 || *year < 1) return std::nullopt;
    const bool leap = (*year % 4 == 0 && *year % 100 != 0) || *year % 400 == 0;
    const std::int64_t lengths[] = {31, leap ? 29 : 28, 31, 30, 31, 30, 31, 31, 30, 31, 30, 31};
    if (*day < 1 || *day > lengths[*month - 1]) return std::nullopt;
    std::int64_t minutes = 0;
    if (text.size() == 17) {
        const auto hour = number(11, 2), minute = number(14, 2);
        if (text[10] != 'T' || text[13] != ':' || text[16] != 'Z' || !hour || !minute || *hour >= 24 || *minute >= 60) return std::nullopt;
        minutes = *hour * 60 + *minute;
    }
    const std::int64_t y = *month <= 2 ? *year - 1 : *year;
    const std::int64_t era = (y >= 0 ? y : y - 399) / 400;
    const std::int64_t yoe = y - era * 400;
    const std::int64_t mp = *month > 2 ? *month - 3 : *month + 9;
    const std::int64_t doy = (153 * mp + 2) / 5 + *day - 1;
    const std::int64_t doe = yoe * 365 + yoe / 4 - yoe / 100 + doy;
    const std::int64_t days = era * 146'097 + doe - 719'468;
    return days * 86'400'000 + minutes * 60'000;
}

}  // namespace agentspeed::pricing
