#include "agentspeed/history.hpp"

#include <algorithm>
#include <cstdio>

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
    const auto measured = r.final ? measuredDuration(r) : std::nullopt;
    for (const auto kind : {ModeKind::ServiceTier, ModeKind::Accelerator, ModeKind::ModelVariant, ModeKind::Routing, ModeKind::Workflow,
                            ModeKind::Effort, ModeKind::Speed}) {
        const auto a = r.modes.attribute(kind);
        if (!a.known()) continue;
        auto& m = modes[ModeKey{r.model.value_or(""), kind, a.mode, a.evidence, a.provider}];
        m.requests += r.requestCount;
        m.output += r.tokens.output;
        if (measured) {
            m.measuredOutput += r.tokens.output;
            m.measuredMs += *measured;
        }
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
    for (const auto& [key, usage] : other.modes) {
        auto& m = modes[key];
        m.requests += usage.requests;
        m.output += usage.output;
        m.measuredOutput += usage.measuredOutput;
        m.measuredMs += usage.measuredMs;
    }
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

namespace {

constexpr ModeKind kModeKinds[] = {ModeKind::ServiceTier, ModeKind::Accelerator, ModeKind::ModelVariant, ModeKind::Routing,
                                   ModeKind::Workflow, ModeKind::Effort, ModeKind::Speed};

std::string modeId(const ModeKey& k) {
    return std::to_string(k.model.size()) + ":" + k.model + "/" + std::string(modeKindName(k.kind)) + "/" + k.provider.value_or("") + "/" + k.mode + "/" +
           k.evidence;
}

std::string csvField(const std::string& raw) {
    auto value = raw;
    if (!value.empty() && std::string_view("=+-@\t\r").find(value.front()) != std::string_view::npos) value = "'" + value;
    if (value.find_first_of(",\"\r\n") == std::string::npos) return value;
    std::string out = "\"";
    for (const char c : value) out += c == '"' ? std::string("\"\"") : std::string(1, c);
    return out + "\"";
}

}  // namespace

std::vector<ModeRow> modeBreakdown(const DayAggregate& day) {
    // The models, with what no model row explains on the model-less row.
    std::map<std::string, ModeTotals> base;
    ModeTotals rest{day.requests, day.output, day.measuredOutput, day.measuredMs};
    for (const auto& [name, m] : day.models) {
        base[name] = {m.requests, m.output, m.measuredOutput, m.measuredMs};
        rest.requests -= m.requests;
        rest.output -= m.output;
        rest.measuredOutput -= m.measuredOutput;
        rest.measuredMs -= m.measuredMs;
    }
    rest = {std::max<std::int64_t>(0, rest.requests), std::max<std::int64_t>(0, rest.output), std::max<std::int64_t>(0, rest.measuredOutput),
            std::max<std::int64_t>(0, rest.measuredMs)};
    if (rest.requests > 0 || rest.output > 0 || rest.measuredMs > 0) {
        auto& empty = base[""];
        empty.requests += rest.requests;
        empty.output += rest.output;
        empty.measuredOutput += rest.measuredOutput;
        empty.measuredMs += rest.measuredMs;
    }
    std::vector<ModeRow> out;
    for (const auto kind : kModeKinds) {
        for (const auto& [model, totals] : base) {
            std::vector<ModeRow> parts;
            for (const auto& [key, usage] : day.modes) {
                if (key.model == model && key.kind == kind) parts.push_back({key, usage});
            }
            auto left = totals;
            bool complete = true;
            for (const auto& p : parts) {
                const auto& u = p.totals;
                if (u.requests < 0 || u.output < 0 || u.measuredOutput < 0 || u.measuredMs < 0 || u.requests > left.requests || u.output > left.output ||
                    u.measuredOutput > left.measuredOutput || u.measuredMs > left.measuredMs) {
                    complete = false;
                    break;
                }
                left.requests -= u.requests;
                left.output -= u.output;
                left.measuredOutput -= u.measuredOutput;
                left.measuredMs -= u.measuredMs;
            }
            if (complete) {
                out.insert(out.end(), parts.begin(), parts.end());
            } else {
                left = totals;
            }
            if (left.requests > 0 || left.output > 0 || left.measuredOutput > 0 || left.measuredMs > 0) {
                out.push_back({ModeKey{model, kind, "unknown", "unknown", std::nullopt}, left});
            }
        }
    }
    std::sort(out.begin(), out.end(), [](const ModeRow& a, const ModeRow& b) { return modeId(a.key) < modeId(b.key); });
    return out;
}

std::string modeCsv(const std::vector<ModeCsvDay>& days) {
    std::string out = "\xEF\xBB\xBF";
    out += "date,source,model,kind,mode,evidence,provider,requests,output_tokens,measured_output_tokens,measured_ms,avg_speed_tok_s\r\n";
    auto sorted = days;
    std::sort(sorted.begin(), sorted.end(), [](const ModeCsvDay& a, const ModeCsvDay& b) {
        return std::make_pair(a.day, a.source) < std::make_pair(b.day, b.source);
    });
    for (const auto& d : sorted) {
        if (!d.totals) continue;
        for (const auto& row : modeBreakdown(*d.totals)) {
            const auto& k = row.key;
            const auto& t = row.totals;
            std::string speed;
            if (t.measuredMs > 0) {
                char buffer[64];
                std::snprintf(buffer, sizeof buffer, "%.1f", static_cast<double>(t.measuredOutput) / (static_cast<double>(t.measuredMs) / 1000));
                speed = buffer;
            }
            const std::vector<std::string> fields = {d.day, std::string(sourceId(d.source)), k.model, std::string(modeKindName(k.kind)), k.mode, k.evidence,
                                                     k.provider.value_or(""), std::to_string(t.requests), std::to_string(t.output),
                                                     std::to_string(t.measuredOutput), std::to_string(t.measuredMs), speed};
            for (std::size_t i = 0; i < fields.size(); ++i) out += (i ? "," : "") + csvField(fields[i]);
            out += "\r\n";
        }
    }
    return out;
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
