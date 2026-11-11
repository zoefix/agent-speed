#include "agentspeed/quota/estimate/vote.hpp"

#include <algorithm>
#include <cmath>
#include <set>

namespace agentspeed::quota::estimate {

namespace {

bool narrow(const Interval& x) { return x.lo > 0 && std::isfinite(x.hi) && x.hi / x.lo - 1 <= kHighWidth; }

}  // namespace

std::string_view confidenceId(Confidence confidence) {
    switch (confidence) {
        case Confidence::None: return "none";
        case Confidence::Low: return "low";
        case Confidence::Medium: return "medium";
        case Confidence::High: return "high";
    }
    return "none";
}

std::optional<Confidence> confidenceFromId(std::string_view id) {
    for (const auto c : {Confidence::None, Confidence::Low, Confidence::Medium, Confidence::High}) {
        if (confidenceId(c) == id) return c;
    }
    return std::nullopt;
}

std::int64_t halfLifeMs(std::int64_t minutes) { return minutes >= 10'000 ? 7 * kDay : 2 * kDay; }

double weight(double dp, std::int64_t t, std::int64_t nowMs, std::int64_t halfLife) {
    const auto age = std::max<std::int64_t>(0, nowMs - t);
    return std::min(1.0, dp / kFullStep) * std::pow(0.5, static_cast<double>(age) / static_cast<double>(halfLife));
}

std::optional<VoteResult> vote(const std::vector<Weighted>& weighted) {
    std::vector<Weighted> items;
    for (const auto& x : weighted) {
        if ((x.lo > 0 || (std::isfinite(x.hi) && x.hi > 0)) && x.w >= kMinWeight) items.push_back(x);
    }
    std::optional<double> floor;
    for (const auto& x : items) {
        if (x.lo > 0 && (!floor || x.lo < *floor)) floor = x.lo;
    }
    if (!floor) return std::nullopt;
    std::set<double> pointSet;
    for (const auto& x : items) {
        for (const double v : {x.lo, x.hi}) {
            if (std::isfinite(v) && v >= *floor) pointSet.insert(v);
        }
    }
    const std::vector<double> points(pointSet.begin(), pointSet.end());
    std::vector<std::pair<double, double>> cells;
    for (std::size_t i = 0; i < points.size(); ++i) {
        cells.emplace_back(points[i], points[i]);
        cells.emplace_back(points[i], i + 1 < points.size() ? points[i + 1] : kInfinity);
    }
    std::vector<std::pair<double, double>> los, his;
    for (const auto& x : items) {
        los.emplace_back(x.lo, x.w);
        if (std::isfinite(x.hi)) his.emplace_back(x.hi, x.w);
    }
    std::sort(los.begin(), los.end());
    std::sort(his.begin(), his.end());
    std::vector<double> loKeys, hiKeys;
    for (const auto& [v, _] : los) loKeys.push_back(v);
    for (const auto& [v, _] : his) hiKeys.push_back(v);
    std::vector<double> loSuffix(los.size() + 1, 0.0);
    for (std::size_t i = los.size(); i-- > 0;) loSuffix[i] = loSuffix[i + 1] + los[i].second;
    std::vector<double> hiPrefix{0.0};
    for (const auto& [_, w] : his) hiPrefix.push_back(hiPrefix.back() + w);
    struct Scored {
        double a, b, loss;
    };
    std::vector<Scored> scored;
    for (const auto& [a, b] : cells) {
        const double probe = a == b ? a : (std::isfinite(b) ? std::sqrt(a * b) : a * 2);
        const auto above = static_cast<std::size_t>(std::upper_bound(loKeys.begin(), loKeys.end(), probe) - loKeys.begin());
        const auto below = static_cast<std::size_t>(std::lower_bound(hiKeys.begin(), hiKeys.end(), probe) - hiKeys.begin());
        scored.push_back({a, b, pyRound(loSuffix[above] + kHiPenalty * hiPrefix[below], 9)});
    }
    double least = scored.front().loss;
    for (const auto& c : scored) least = std::min(least, c.loss);
    std::vector<std::pair<double, double>> regions;
    for (const auto& c : scored) {
        if (c.loss != least) continue;
        if (!regions.empty() && regions.back().second >= c.a) {
            regions.back().second = std::max(regions.back().second, c.b);
        } else {
            regions.emplace_back(c.a, c.b);
        }
    }
    const auto holders = [&](const std::pair<double, double>& region) {
        std::vector<const Weighted*> out;
        for (const auto& x : items) {
            if (x.lo <= region.first * (1 + 1e-12) && x.hi >= region.second * (1 - 1e-12)) out.push_back(&x);
        }
        return out;
    };
    const auto heldWeight = [](const std::vector<const Weighted*>& held) {
        double sum = 0;
        for (const auto* h : held) sum += h->w;
        return sum;
    };
    std::vector<std::pair<double, std::pair<double, double>>> ranked;
    for (const auto& r : regions) ranked.emplace_back(-pyRound(heldWeight(holders(r)), 9), r);
    std::stable_sort(ranked.begin(), ranked.end(), [](const auto& x, const auto& y) {
        return x.first != y.first ? x.first < y.first : x.second.first < y.second.first;
    });
    const auto region = ranked.front().second;
    const auto held = holders(region);
    std::set<std::int64_t> narrowIds;
    for (const auto* h : held) {
        if (h->lo > 0 && std::isfinite(h->hi) && h->hi / h->lo - 1 <= kHighWidth) narrowIds.insert(h->id);
    }
    return VoteResult{region.first, region.second, heldWeight(held), static_cast<int>(narrowIds.size()), least};
}

Confidence confidenceOf(double low, double high, double inside, int narrowCount, double above) {
    if (!std::isfinite(high)) return Confidence::Low;
    const double width = high / low - 1;
    if (width <= kHighWidth && inside >= 1.5 && narrowCount >= 2 && above == 0) return Confidence::High;
    if (width <= 0.25 && inside >= 0.5) return Confidence::Medium;
    return Confidence::Low;
}

std::vector<Interval> afterCut(const std::vector<Interval>& items) {
    std::vector<Interval> ns;
    for (const auto& x : items) {
        if (narrow(x)) ns.push_back(x);
    }
    std::stable_sort(ns.begin(), ns.end(), [](const Interval& a, const Interval& b) { return a.t < b.t; });
    if (ns.size() < 2) return items;
    const auto& a = ns[ns.size() - 2];
    const auto& b = ns.back();
    if (std::max(a.lo, b.lo) > std::min(a.hi, b.hi)) return items;
    const double top = std::max(a.hi, b.hi);
    std::vector<Interval> out;
    for (const auto& x : items) {
        if (!(x.t < a.t && x.lo > top)) out.push_back(x);
    }
    return out;
}

Estimate estimate(const std::vector<Interval>& input, std::int64_t nowMs, std::int64_t halfLife) {
    const auto items = afterCut(input);
    std::vector<Weighted> weighted;
    for (const auto& s : items) weighted.push_back({s.lo, s.hi, weight(s.dp, s.t, nowMs, halfLife), s.R});
    const auto result = vote(weighted);
    Estimate out;
    if (!result) return out;
    double above = 0;
    std::set<std::int64_t> windows;
    for (const auto& x : weighted) {
        if (x.w < kMinWeight) continue;
        windows.insert(x.id);
        if (x.lo > result->low * (1 + 1e-12)) above += x.w;
    }
    out.confidence = confidenceOf(result->low, result->high, result->inside, result->insideWindows, above);
    const bool bounded = std::isfinite(result->high);
    if (bounded) out.cap = std::sqrt(result->low * result->high);
    out.low = result->low;
    if (bounded) out.high = result->high;
    if (out.cap) {
        out.low = std::min(*out.low, *out.cap / (1 + kPrecisionFloor));
        out.high = std::max(*out.high, *out.cap * (1 + kPrecisionFloor));
    }
    out.regionLow = result->low;
    if (bounded) out.regionHigh = result->high;
    out.loss = result->loss;
    out.inside = result->inside;
    out.windows = static_cast<int>(windows.size());
    return out;
}

std::vector<Interval> pruneClosed(std::vector<Interval> items, std::size_t limit) {
    std::stable_sort(items.begin(), items.end(), [](const Interval& a, const Interval& b) { return a.t < b.t; });
    if (items.size() > limit) items.erase(items.begin(), items.end() - static_cast<std::ptrdiff_t>(limit));
    return items;
}

CapClass CapClass::of(const std::string& account, const std::optional<std::string>& vendorPlan, const std::optional<std::string>& tier,
                      const std::string& window) {
    if (vendorPlan && !vendorPlan->empty()) return {true, *vendorPlan, tier, window};
    return {false, account, std::nullopt, window};
}

std::string CapClass::repr() const {
    const auto quoted = [](const std::string& s) { return "'" + s + "'"; };
    if (byPlan) return "('plan', " + quoted(id) + ", " + (tier ? quoted(*tier) : std::string("None")) + ", " + quoted(window) + ")";
    return "('account', " + quoted(id) + ", " + quoted(window) + ")";
}

std::string CapClass::joined() const {
    if (byPlan) return "plan|" + id + "|" + tier.value_or("None") + "|" + window;
    return "account|" + id + "|" + window;
}

Live livePercent(double p, std::int64_t t, std::int64_t R, std::int64_t S, std::int64_t minutes, std::string_view kind, std::optional<double> cap,
                 const Ledger& ledger, const std::string& account, const std::optional<std::string>& name, std::int64_t nowMs,
                 std::optional<std::int64_t> firstAfterReset) {
    if (nowMs < R) {
        if (!cap) return {p, false, R};
        const auto spent = ledger.outer(account, name, S, nowMs).costMicroUsd - ledger.outer(account, name, S, t).costMicroUsd;
        double live = p + 100.0 * static_cast<double>(spent) / *cap;
        live = std::min(std::max(live, p), std::max(100.0, p));
        return {live, live - p >= 0.5, R};
    }
    std::int64_t s2 = 0;
    if (kind == "fiveHour") {
        if (!firstAfterReset) return {0.0, false, std::nullopt};
        s2 = minuteOf(*firstAfterReset);
    } else {
        const auto periods = floorDiv(nowMs - R, minutes * kMinute);
        s2 = R + periods * minutes * kMinute;
    }
    const auto r2 = s2 + minutes * kMinute;
    if (!cap) return {0.0, false, r2};
    const double live = std::min(100.0, 100.0 * static_cast<double>(ledger.outer(account, name, s2, nowMs).costMicroUsd) / *cap);
    return {live, true, r2};
}

}  // namespace agentspeed::quota::estimate
