#include <algorithm>
#include <cmath>
#include <map>
#include <set>

#include "agentspeed/quota/estimate/engine.hpp"

// QUOTAS 10.4 Store: the last 8 days of observations (thinned per instance), the closed instances' intervals (the
// newest 64 per class) and the key timelines.
namespace agentspeed::quota::estimate {

namespace {

std::string quoted(const std::string& s) { return "'" + s + "'"; }
std::string reprOf(const std::optional<std::string>& s) { return s ? quoted(*s) : std::string("None"); }

// Each run of equal values keeps its first and last; above 256 each run keeps its last; still above, 256 runs evenly
// spaced, the first and last included.
std::vector<Observation> thin(std::vector<Observation> observations) {
    std::stable_sort(observations.begin(), observations.end(), [](const Observation& a, const Observation& b) {
        return a.t != b.t ? a.t < b.t : a.tLo < b.tLo;
    });
    std::vector<std::vector<const Observation*>> runs;
    for (const auto& o : observations) {
        if (!runs.empty() && runs.back().back()->p == o.p) {
            runs.back().push_back(&o);
        } else {
            runs.push_back({&o});
        }
    }
    std::vector<Observation> kept;
    for (const auto& r : runs) {
        kept.push_back(*r.front());
        if (r.size() > 1) kept.push_back(*r.back());
    }
    if (kept.size() <= kObservationsPerInstance) return kept;
    std::vector<Observation> lasts;
    for (const auto& r : runs) lasts.push_back(*r.back());
    if (lasts.size() <= kObservationsPerInstance) return lasts;
    std::vector<Observation> out;
    const auto n = kObservationsPerInstance;
    for (std::size_t i = 0; i < n; ++i) {
        const auto at = pyRoundToInt(static_cast<double>(i * (lasts.size() - 1)) / static_cast<double>(n - 1));
        out.push_back(lasts[static_cast<std::size_t>(at)]);
    }
    return out;
}

}  // namespace

Store Engine::store() const {
    Store out;
    std::map<std::string, std::vector<Observation>> groups;
    for (const auto& o : obs_) {
        if (o.t < nowMs_ - kKeepMs) continue;
        auto key = "(" + quoted(o.account) + ", " + quoted(o.kind) + ", " + reprOf(o.name) + ", " + std::to_string(o.minutes) + ", " +
                   std::to_string(pyRoundToInt(static_cast<double>(o.R) / static_cast<double>(kInstanceJoinMs))) + ", " + reprOf(o.plan) + ", " +
                   reprOf(o.tier) + (o.machine == kLocal ? std::string() : ", " + quoted(o.machine)) + ")";
        groups[key].push_back(o);
    }
    for (auto& [_, list] : groups) {
        for (auto& o : thin(std::move(list))) out.observations.push_back(std::move(o));
    }
    std::map<std::string, std::vector<StoredInstance>> closed;
    std::set<std::tuple<CapClass, std::string, std::int64_t>> have;
    for (const auto& x : insts_) {
        if (!x.closed || !x.bounds) continue;
        const auto& b = *x.bounds;
        StoredInstance s;
        s.cls = x.cls;
        s.account = x.account;
        s.R = x.inst.R;
        s.lo = b.lo;
        if (std::isfinite(b.hi)) s.hi = b.hi;
        s.pMax = b.dp;
        s.tLast = b.t;
        closed[x.cls.repr()].push_back(s);
        have.insert({s.cls, s.account, s.R});
    }
    for (const auto& s : stored_.instances) {
        if (!have.contains({s.cls, s.account, s.R})) closed[s.cls.repr()].push_back(s);
    }
    for (auto& [_, list] : closed) {
        std::stable_sort(list.begin(), list.end(), [](const StoredInstance& a, const StoredInstance& b) { return a.tLast < b.tLast; });
        const auto from = list.size() > kInstancesPerClass ? list.size() - kInstancesPerClass : 0;
        out.instances.insert(out.instances.end(), list.begin() + static_cast<std::ptrdiff_t>(from), list.end());
    }
    auto timelines = tl_;
    timelines["claudeDesktop"] = desktopOrgs_;
    for (const auto& [source, timeline] : timelines) {
        for (const auto& e : timeline) {
            StoredKey k;
            k.source = source;
            k.entry = e;
            if (source == "claudeCode") {
                if (const auto it = orgKeys_.find(e); it != orgKeys_.end()) k.orgKey = it->second;
            }
            out.keys.push_back(std::move(k));
        }
    }
    return out;
}

}  // namespace agentspeed::quota::estimate
