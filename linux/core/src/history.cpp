#include "agentspeed/history.hpp"

#include "agentspeed/price_book.hpp"
#include "agentspeed/pricing.hpp"
#include "agentspeed/tz.hpp"

namespace agentspeed::history {

namespace {

constexpr std::int64_t kMinMs = -62'135'596'800'000 + 2 * 86'400'000LL;
constexpr std::int64_t kMaxMs = 253'402'300'799'999 - 2 * 86'400'000LL;
constexpr const char* kOtherOwner = "\x01other";

// METRICS 3.3: the widened duration of a measured response.
std::optional<std::int64_t> measuredDuration(const ParsedResponse& r) {
    if (!r.startMs || r.endMs < *r.startMs || r.endMs - *r.startMs > 15 * 60'000) return std::nullopt;
    return r.endMs - std::min(*r.startMs, r.endMs - 500);
}

void addCost(std::optional<std::int64_t>& cost, std::optional<std::int64_t>& unpriced, std::optional<std::int64_t> value,
             std::int64_t requests) {
    if (!cost) cost = 0;
    if (!unpriced) unpriced = 0;
    if (value) {
        *cost += *value;
    } else {
        *unpriced += requests;
    }
}

// CostMerge.Merged: a side without requests and without costs contributes nothing; a side without cost data counts its
// requests as unpriced.
void mergeCost(std::optional<std::int64_t>& cost, std::optional<std::int64_t>& unpriced, std::int64_t requests,
               const std::optional<std::int64_t>& otherCost, const std::optional<std::int64_t>& otherUnpriced, std::int64_t otherRequests) {
    const bool selfEmpty = requests == 0 && (!cost || (*cost == 0 && unpriced.value_or(0) == 0));
    const bool otherEmpty = otherRequests == 0 && (!otherCost || (*otherCost == 0 && otherUnpriced.value_or(0) == 0));
    if (selfEmpty) {
        cost = otherCost;
        unpriced = otherUnpriced;
        return;
    }
    if (otherEmpty) return;
    if (!cost && !otherCost) {
        unpriced.reset();
        return;
    }
    if (cost && !otherCost) {
        *unpriced = unpriced.value_or(0) + otherRequests;
        return;
    }
    if (!cost && otherCost) {
        cost = otherCost;
        unpriced = otherUnpriced.value_or(0) + requests;
        return;
    }
    *cost += *otherCost;
    unpriced = unpriced.value_or(0) + otherUnpriced.value_or(0);
}

}  // namespace

std::uint64_t fnv1a(std::string_view text) {
    std::uint64_t hash = 0xCBF29CE484222325ULL;
    for (const unsigned char c : text) {
        hash ^= c;
        hash *= 0x100000001B3ULL;
    }
    return hash;
}

UnpricedFact UnpricedFact::of(const std::optional<std::string>& model, const std::optional<std::string>& provider, bool fast) {
    UnpricedFact fact;
    fact.model = model ? pricing::strip(*model) : std::string();
    if (provider && !provider->empty()) fact.provider = provider;
    fact.fast = fast;
    return fact;
}

bool UnpricedFact::operator<(const UnpricedFact& other) const {
    if (model != other.model) return model < other.model;
    if (provider.has_value() != other.provider.has_value()) return !provider.has_value();
    if (provider != other.provider) return provider.value_or("") < other.provider.value_or("");
    if (fast != other.fast) return !fast;
    return !noDate && other.noDate;
}

void DayAggregate::add(const ParsedResponse& r, std::optional<std::int64_t> cost) {
    addCost(costMicroUsd, unpricedRequests, cost, r.requestCount);
    if (!cost) unpriced[UnpricedFact::of(r.model, r.provider, r.fast)] += r.requestCount;
    requests += r.requestCount;
    input += r.tokens.input;
    cachedInput += r.tokens.cachedInput;
    cacheWriteInput += r.tokens.cacheWriteInput;
    output += r.tokens.output;
    reasoning += r.tokens.reasoning;
    auto& model = models[r.model.value_or("")];
    model.requests += r.requestCount;
    model.output += r.tokens.output;
    addCost(model.costMicroUsd, model.unpricedRequests, cost, r.requestCount);
    if (r.final) {
        if (const auto ms = measuredDuration(r)) {
            measuredOutput += r.tokens.output;
            measuredMs += *ms;
            model.measuredOutput += r.tokens.output;
            model.measuredMs += *ms;
        }
    } else {
        unfinalized += r.requestCount;
    }
}

void DayAggregate::merge(const DayAggregate& other) {
    mergeCost(costMicroUsd, unpricedRequests, requests, other.costMicroUsd, other.unpricedRequests, other.requests);
    requests += other.requests;
    input += other.input;
    cachedInput += other.cachedInput;
    cacheWriteInput += other.cacheWriteInput;
    output += other.output;
    reasoning += other.reasoning;
    measuredOutput += other.measuredOutput;
    measuredMs += other.measuredMs;
    unfinalized += other.unfinalized;
    agents += other.agents;
    for (const auto& [fact, n] : other.unpriced) unpriced[fact] += n;
    for (const auto& [name, usage] : other.models) {
        auto it = models.find(name);
        if (it == models.end()) {
            models[name] = usage;
            continue;
        }
        auto& known = it->second;
        mergeCost(known.costMicroUsd, known.unpricedRequests, known.requests, usage.costMicroUsd, usage.unpricedRequests, usage.requests);
        known.requests += usage.requests;
        known.output += usage.output;
        known.measuredOutput += usage.measuredOutput;
        known.measuredMs += usage.measuredMs;
    }
}

std::optional<std::string> DayLabeler::label(std::int64_t unixMs) const {
    if (unixMs < kMinMs || unixMs > kMaxMs) return std::nullopt;
    return pricing::UtcDay(unixMs + (zone_ ? zone_->offsetMs(unixMs) : 0)).label();
}

Contributions contributionsOf(const std::vector<ParsedResponse>& responses, SourceKind source, const DayLabeler& labeler,
                              pricing::PriceBook& prices) {
    Contributions days;
    for (const auto& r : responses) {
        const auto day = labeler.label(r.endMs);
        if (!day) continue;
        auto [it, inserted] = days.try_emplace(DayKey{*day, source});
        if (inserted) it->second.agents = 1;
        it->second.add(r, prices.cost(r, *day));
    }
    return days;
}

bool KeyLedger::tryClaim(const std::string& key, const std::string& agentId, std::string* owner) {
    const auto hash = fnv1a(key);
    if (settled_.contains(hash)) {
        if (owner) *owner = kOtherOwner;
        return false;
    }
    if (const auto it = current_.find(hash); it != current_.end()) {
        if (owner) *owner = it->second;
        return false;
    }
    current_.emplace(hash, agentId);
    order_.push_back(hash);
    if (owner) *owner = agentId;
    return true;
}

std::optional<std::string> KeyLedger::owner(const std::string& key) const {
    const auto hash = fnv1a(key);
    if (settled_.contains(hash)) return std::string(kOtherOwner);
    if (const auto it = current_.find(hash); it != current_.end()) return it->second;
    return std::nullopt;
}

bool KeyLedger::ownedByOther(const std::string& key, const std::string& agentId) const {
    const auto found = owner(key);
    return found && *found != agentId;
}

void KeyLedger::settle(const std::vector<std::uint64_t>& hashes) { settled_.insert(hashes.begin(), hashes.end()); }

std::vector<std::uint64_t> KeyLedger::finish(const std::string& owner) {
    std::vector<std::uint64_t> owned, rest;
    for (const auto hash : order_) {
        const auto it = current_.find(hash);
        if (it == current_.end()) continue;
        if (it->second == owner) {
            owned.push_back(hash);
            settled_.insert(hash);
            current_.erase(it);
        } else {
            rest.push_back(hash);
        }
    }
    order_ = std::move(rest);
    return owned;
}

std::map<std::string, std::vector<std::uint64_t>> KeyLedger::finishAll() {
    std::map<std::string, std::vector<std::uint64_t>> out;
    for (const auto hash : order_) {
        const auto it = current_.find(hash);
        if (it == current_.end()) continue;
        out[it->second].push_back(hash);
        settled_.insert(hash);
    }
    current_.clear();
    order_.clear();
    return out;
}

void KeyLedger::abandon() {
    current_.clear();
    order_.clear();
}

}  // namespace agentspeed::history
