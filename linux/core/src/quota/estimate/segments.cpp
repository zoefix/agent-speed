#include "agentspeed/quota/estimate/segments.hpp"

#include <algorithm>
#include <cmath>

namespace agentspeed::quota::estimate {

namespace {

struct Run {
    double p = 0;
    const Observation* first = nullptr;
    const Observation* last = nullptr;
    std::int64_t tLoMax = 0;
    double q = 1;
};

std::vector<Run> runsOf(const std::vector<Observation>& obs, const std::map<std::string, double>& qOf) {
    std::vector<Run> out;
    for (const auto& o : obs) {
        const double q = qOf.at(o.source);
        if (!out.empty() && std::fabs(out.back().p - o.p) < 1e-9) {
            auto& r = out.back();
            r.last = &o;
            r.tLoMax = std::max(r.tLoMax, o.tLo);
            r.q = std::max(r.q, q);
        } else {
            out.push_back({o.p, &o, &o, o.tLo, q});
        }
    }
    return out;
}

}  // namespace

double resolution(double p) {
    double scale = 1;
    for (int digits = 0; digits < 2; ++digits) {
        const double scaled = p * scale;
        if (std::fabs(scaled - std::round(scaled)) < 1e-9) return digits == 0 ? 1.0 : 0.1;
        scale = 10;
    }
    return 0.01;
}

std::vector<Instance> instances(std::vector<Observation> observations) {
    std::stable_sort(observations.begin(), observations.end(), [](const Observation& a, const Observation& b) {
        return a.R != b.R ? a.R < b.R : a.t < b.t;
    });
    std::vector<Instance> out;
    for (auto& o : observations) {
        if (!out.empty() && o.R - out.back().R <= kInstanceJoinMs) {
            out.back().obs.push_back(std::move(o));
        } else {
            Instance inst;
            inst.R = o.R;
            inst.obs.push_back(std::move(o));
            out.push_back(std::move(inst));
        }
    }
    std::vector<Instance> kept;
    for (auto& inst : out) {
        std::int64_t maxR = inst.obs.front().R, minR = inst.obs.front().R;
        for (const auto& o : inst.obs) {
            maxR = std::max(maxR, o.R);
            minR = std::min(minR, o.R);
        }
        const auto minutes = inst.obs.front().minutes;
        inst.R = maxR;
        inst.S = maxR - minutes * kMinute;
        const auto slack = inst.obs.front().kind == "fiveHour" && minR - floorDiv(minR, kHour) * kHour == 0 ? kHourRoundedSlackMs : 0;
        inst.Shi = minR - minutes * kMinute - slack;
        std::vector<Observation> inside;
        for (auto& o : inst.obs) {
            if (inst.Shi - kMinute <= o.t && o.t <= inst.R + kMinute) inside.push_back(std::move(o));
        }
        std::stable_sort(inside.begin(), inside.end(), [](const Observation& a, const Observation& b) {
            return a.t != b.t ? a.t < b.t : a.tLo < b.tLo;
        });
        inst.obs = std::move(inside);
        if (!inst.obs.empty()) kept.push_back(std::move(inst));
    }
    return kept;
}

std::map<std::string, double> windowQ(const std::vector<Observation>& observations) {
    std::map<std::string, double> q;
    for (const auto& o : observations) {
        const auto it = q.try_emplace(o.source, 1.0).first;
        it->second = std::min(it->second, resolution(o.p));
    }
    return q;
}

std::vector<Segment> segments(const Instance& inst, const Ledger& ledger, const std::string& account, const std::optional<std::string>& name,
                              std::int64_t lag, std::optional<std::int64_t> nowMs) {
    const auto S = inst.S, Shi = inst.Shi;
    const auto upto = [&](std::int64_t t) { return nowMs ? std::min(t, *nowMs) : t; };
    const auto qOf = windowQ(inst.obs);
    const auto rs = runsOf(inst.obs, qOf);
    std::vector<Segment> segs;
    for (const auto& r : rs) {
        const double p = r.p, q = r.q;
        if (p <= 0) continue;
        const auto cLo = ledger.inner(account, name, S, r.tLoMax - lag).costMicroUsd;
        const auto cHi = ledger.outerHigh(account, name, Shi, upto(r.first->t + lag));
        const double lo = p >= 100 ? 0.0 : 100.0 * static_cast<double>(cLo) / (p + q);
        const double pTop = std::min(p, 100.0);
        const double hi = pTop - q <= 0 ? kInfinity : 100.0 * cHi / (pTop - q);
        segs.push_back({false, lo, hi, p, r.last->t});
    }
    std::vector<const Run*> anchors;
    for (const auto& r : rs) {
        if (r.p <= 0) continue;
        if (anchors.empty() || r.p >= anchors.back()->p + std::max(3 * std::max(r.q, anchors.back()->q), kIncrementStep) - 1e-9) anchors.push_back(&r);
    }
    std::vector<std::pair<const Run*, const Run*>> pairs;
    for (std::size_t i = 0; i < anchors.size(); ++i) {
        for (std::size_t j = i + 1; j < anchors.size(); ++j) pairs.emplace_back(anchors[i], anchors[j]);
    }
    std::stable_sort(pairs.begin(), pairs.end(), [](const auto& x, const auto& y) {
        const double wx = -(x.second->p - x.first->p), wy = -(y.second->p - y.first->p);
        return wx != wy ? wx < wy : x.first->p < y.first->p;
    });
    if (pairs.size() > kSegmentsPerInstance / 2) pairs.resize(kSegmentsPerInstance / 2);
    std::stable_sort(pairs.begin(), pairs.end(), [](const auto& x, const auto& y) {
        return x.first->p != y.first->p ? x.first->p < y.first->p : x.second->p < y.second->p;
    });
    for (const auto& [a, b] : pairs) {
        const double q2 = a->q + b->q;
        const double dp = b->p - a->p;
        const auto cLo = std::max<std::int64_t>(0, ledger.inner(account, name, S, b->tLoMax - lag).costMicroUsd -
                                                       ledger.outer(account, name, S, upto(a->first->t + lag)).costMicroUsd);
        const double hiEnd = ledger.outerHigh(account, name, Shi, upto(b->first->t + lag));
        const auto hiStart = ledger.inner(account, name, Shi, a->tLoMax - lag).costMicroUsd;
        const double lo = b->p >= 100 ? 0.0 : 100.0 * static_cast<double>(cLo) / (dp + q2);
        const double dpTop = std::min(b->p, 100.0) - a->p;
        const double hi = dpTop - q2 <= 0 ? kInfinity : 100.0 * std::max(0.0, hiEnd - static_cast<double>(hiStart)) / (dpTop - q2);
        segs.push_back({true, lo, hi, dp, b->last->t});
    }
    std::erase_if(segs, [](const Segment& s) { return !(s.lo > 0 || (std::isfinite(s.hi) && s.hi > 0)); });
    if (segs.size() > kSegmentsPerInstance) {
        std::vector<Segment> cum, inc;
        for (const auto& s : segs) (s.increment ? inc : cum).push_back(s);
        const auto room = kSegmentsPerInstance - inc.size();
        const double stride = static_cast<double>(cum.size()) / static_cast<double>(room);
        std::vector<Segment> keep;
        for (std::size_t i = 0; i < room; ++i) {
            const auto at = static_cast<std::size_t>(std::max(0.0, static_cast<double>(cum.size() - 1) - static_cast<double>(i) * stride));
            keep.push_back(cum[std::min(cum.size() - 1, at)]);
        }
        std::reverse(keep.begin(), keep.end());
        keep.insert(keep.end(), inc.begin(), inc.end());
        segs = std::move(keep);
    }
    return segs;
}

std::optional<Interval> instanceBounds(const std::vector<Segment>& segs, std::int64_t R) {
    const auto bounds = [](const std::vector<const Segment*>& group) -> std::optional<std::pair<double, double>> {
        std::optional<double> low;
        for (const auto* x : group) {
            if (x->lo > 0 && (!low || x->lo > *low)) low = x->lo;
        }
        if (!low) return std::nullopt;
        double high = kInfinity;
        bool any = false;
        for (const auto* x : group) {
            if (std::isfinite(x->hi) && x->hi >= *low && (!any || x->hi < high)) {
                high = x->hi;
                any = true;
            }
        }
        return std::make_pair(*low, high);
    };
    std::vector<const Segment*> all, incs;
    double cumLo = 0;
    bool anyCum = false;
    double incHi = kInfinity;
    for (const auto& s : segs) {
        all.push_back(&s);
        if (s.increment) {
            incs.push_back(&s);
            incHi = std::min(incHi, s.hi);
        } else {
            cumLo = anyCum ? std::max(cumLo, s.lo) : s.lo;
            anyCum = true;
        }
    }
    const bool offset = cumLo > incHi;
    const auto incBounds = bounds(incs);
    const auto b = offset && incBounds ? incBounds : bounds(all);
    if (!b) return std::nullopt;
    Interval out;
    out.R = R;
    out.lo = b->first;
    out.hi = b->second;
    out.offset = offset;
    for (std::size_t i = 0; i < segs.size(); ++i) {
        out.dp = i == 0 ? segs[i].dp : std::max(out.dp, segs[i].dp);
        out.t = i == 0 ? segs[i].t : std::max(out.t, segs[i].t);
    }
    return out;
}

}  // namespace agentspeed::quota::estimate
