#include "agentspeed/quota/estimate/entry.hpp"

#include <algorithm>
#include <map>
#include <tuple>

#include "agentspeed/quota/estimate/accounts.hpp"

namespace agentspeed::quota::estimate {

namespace {

using report::Json;

Json optionalInt(const std::optional<std::int64_t>& v) { return v ? Json::of(*v) : Json::null(); }
Json optionalText(const std::optional<std::string>& v) { return v ? Json::of(*v) : Json::null(); }
Json optionalDecimal(const std::optional<double>& v, int places) { return v ? Json::decimal(*v, places) : Json::null(); }

// REPORTING-EXTENSIONS 11.4: (account, window) once per report.
using Identity = std::tuple<std::string, std::string, std::optional<std::string>, std::int64_t>;
Identity identityOf(const Entry& e) { return {e.accountTag, e.kind, e.name, e.minutes}; }

}  // namespace

Json Entry::json() const {
    std::vector<Json> sourceIds;
    for (const auto& s : sources) sourceIds.push_back(Json::of(s));
    std::vector<Json> mixItems;
    for (const auto& m : mix) mixItems.push_back(Json::object({{"model", Json::of(m.model)}, {"share", Json::decimal(m.share, 3)}}));
    return Json::object({
        {"account", Json::of(accountTag)},
        {"source", Json::of(source)},
        {"sources", Json::array(std::move(sourceIds))},
        {"plan", optionalText(plan)},
        {"tier", optionalText(tier)},
        {"window", Json::object({{"kind", Json::of(kind)}, {"name", optionalText(name)}, {"minutes", Json::of(minutes)}})},
        {"usedPercent", optionalDecimal(usedPercent, 2)},
        {"percentAgeMs", optionalInt(percentAgeMs)},
        {"resetsInMs", optionalInt(resetsInMs)},
        {"livePercent", Json::decimal(livePercent, 1)},
        {"liveEstimated", Json::of(liveEstimated)},
        {"usedMicroUsd", Json::of(usedMicroUsd)},
        {"observedUsedMicroUsd", optionalInt(observedUsedMicroUsd)},
        {"unpricedRequests", Json::of(unpricedRequests)},
        {"estimate", Json::object({{"capMicroUsd", optionalInt(capMicroUsd)},
                                   {"lowMicroUsd", optionalInt(lowMicroUsd)},
                                   {"highMicroUsd", optionalInt(highMicroUsd)},
                                   {"confidence", Json::of(confidenceId(confidence))},
                                   {"coverage", optionalDecimal(coverage, 3)},
                                   {"accountUsedMicroUsd", optionalInt(accountUsedMicroUsd)},
                                   {"windows", Json::of(windows)}})},
        {"mix", Json::array(std::move(mixItems))},
        {"priceVersion", Json::of(priceVersion)},
    });
}

Entry reportEntry(const EntryInput& in, std::int64_t nowMs, const Ledger& ledger, int priceVersion, std::string_view origin) {
    const auto& est = in.estimate;
    const auto cap = est.cap;
    const auto name = in.kind == "modelWeek" ? in.name : std::nullopt;
    const auto live = livePercent(in.p, in.t, in.R, in.S, in.minutes, in.kind, cap, ledger, in.account, name, nowMs, in.firstAfterReset);
    const bool passed = nowMs >= in.R;
    const auto start = !passed ? in.S : (live.resetsMs && *live.resetsMs ? *live.resetsMs - in.minutes * kMinute : nowMs);
    const auto used = ledger.outer(in.account, name, start, nowMs);
    const auto observed = ledger.outer(in.account, name, in.S, in.t).costMicroUsd;
    Entry e;
    if (!origin.empty()) e.accountTag = reportAccount(origin, in.account);
    e.source = in.source;
    e.sources = in.sources;
    e.plan = in.plan;
    e.tier = in.tier;
    e.planInferred = in.planInferred;
    e.kind = in.kind;
    e.name = in.name;
    e.minutes = in.minutes;
    if (!passed) {
        e.usedPercent = roundHalfAway(in.p, 2);
        e.percentAgeMs = std::max<std::int64_t>(0, nowMs - in.t);
        e.observedUsedMicroUsd = observed;
    }
    if (live.resetsMs) e.resetsInMs = std::max<std::int64_t>(0, *live.resetsMs - nowMs);
    e.resetsAtMs = live.resetsMs;
    e.account = in.account;
    e.livePercent = roundHalfAway(live.percent, 1);
    e.liveExact = live.percent;
    e.liveEstimated = live.estimated;
    e.usedMicroUsd = used.costMicroUsd;
    e.unpricedRequests = used.unpricedRequests;
    if (cap && *cap && in.p >= 5) {
        e.coverage = roundHalfAway(std::min(1.0, std::max(0.0, static_cast<double>(observed) / (*cap * in.p / 100))), 3);
    }
    if (cap) e.capMicroUsd = micro(*cap);
    if (est.low) e.lowMicroUsd = micro(*est.low);
    if (est.high) e.highMicroUsd = micro(*est.high);
    e.confidence = est.confidence;
    if (cap && *cap) e.accountUsedMicroUsd = micro(*cap * live.percent / 100);
    e.windows = est.windows;
    e.mix = ledger.mix(in.account, name, start, nowMs);
    e.priceVersion = priceVersion;
    return e;
}

bool shouldSend(const std::vector<Entry>& entries, const std::vector<Entry>* lastSent, std::int64_t lastSentMs, std::int64_t nowMs, bool leaving) {
    if (!lastSent || leaving) return true;
    if (nowMs - lastSentMs >= kSendEveryMs) return true;
    std::map<Identity, const Entry*> before, now;
    for (const auto& e : *lastSent) before[identityOf(e)] = &e;
    for (const auto& e : entries) now[identityOf(e)] = &e;
    if (before.size() != now.size()) return true;
    for (auto a = before.begin(), b = now.begin(); a != before.end(); ++a, ++b) {
        if (a->first != b->first) return true;
    }
    for (const auto& [k, e] : now) {
        const auto* b = before.at(k);
        if (e->usedPercent != b->usedPercent || e->confidence != b->confidence) return true;
        if (e->resetsInMs.has_value() != b->resetsInMs.has_value()) return true;
    }
    return false;
}

std::optional<std::vector<Entry>> Reporter::step(std::int64_t nowMs, const std::vector<Entry>& entries, bool switchOn, bool leaving) {
    previous_ = last_;
    previousMs_ = lastMs_;
    previousCleared_ = cleared_;
    if (!switchOn) {
        if (cleared_) return std::nullopt;
        cleared_ = true;
        last_.reset();
        lastMs_ = 0;
        return std::vector<Entry>{};
    }
    if (!shouldSend(entries, last_ ? &*last_ : nullptr, lastMs_, nowMs, leaving)) return std::nullopt;
    last_ = entries;
    lastMs_ = nowMs;
    cleared_ = false;
    return entries;
}

void Reporter::dropped() {
    last_ = previous_;
    lastMs_ = previousMs_;
    cleared_ = previousCleared_;
}

}  // namespace agentspeed::quota::estimate
