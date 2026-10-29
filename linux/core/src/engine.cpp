#include "agentspeed/engine.hpp"

#include <algorithm>
#include <functional>
#include <limits>
#include <set>
#include <tuple>

namespace agentspeed::engine {

namespace {

constexpr std::int64_t kRecentHorizonMs = 600'000;
constexpr std::int64_t kFallbackHorizonMs = 1'800'000;
constexpr std::int64_t kStaleMs = 300'000;
constexpr std::int64_t kListHorizonMs = 600'000;

double overlap(std::int64_t a0, std::int64_t a1, std::int64_t b0, std::int64_t b1) {
    return static_cast<double>(std::max<std::int64_t>(0, std::min(a1, b1) - std::max(a0, b0)));
}

using Predicate = std::function<bool(const Response&)>;

std::vector<const Response*> recentSet(const std::vector<const Response*>& valid, std::int64_t now,
                                       const Predicate& predicate, std::size_t limit, std::int64_t horizon) {
    std::vector<const Response*> picked;
    for (const auto* r : valid) {
        if (predicate(*r) && r->endMs >= now - horizon) picked.push_back(r);
    }
    std::stable_sort(picked.begin(), picked.end(), [](const Response* a, const Response* b) { return a->endMs > b->endMs; });
    if (picked.size() > limit) picked.resize(limit);
    return picked;
}

std::optional<Speed> speedOf(const std::vector<const Response*>& responses) {
    double out = 0, tot = 0, dur = 0;
    for (const auto* r : responses) {
        const auto iv = interval(*r);
        out += static_cast<double>(r->output);
        tot += static_cast<double>(r->output + r->input);
        dur += static_cast<double>(iv->second - iv->first) / 1000.0;
    }
    if (dur <= 0) return std::nullopt;
    return Speed{out / dur, tot / dur};
}

std::vector<const Response*> referenceSet(const std::vector<const Response*>& valid, std::int64_t now, const Agent& agent) {
    auto set = recentSet(valid, now, [&](const Response& r) { return r.agentId == agent.id; }, 3, kRecentHorizonMs);
    if (!set.empty()) return set;
    if (agent.model && !agent.model->empty()) {
        set = recentSet(valid, now, [&](const Response& r) { return r.model == agent.model; }, 10, kFallbackHorizonMs);
        if (!set.empty()) return set;
    }
    return recentSet(valid, now, [&](const Response& r) { return r.source == agent.source; }, 10, kFallbackHorizonMs);
}

struct Split {
    std::vector<const Response*> valid;
    std::vector<const Response*> unmeasured;
};

Split split(const std::vector<Response>& responses) {
    Split s;
    for (const auto& r : responses) {
        if (!interval(r)) continue;
        (r.final ? s.valid : s.unmeasured).push_back(&r);
    }
    return s;
}

Agent agentOf(const Response& r, const std::vector<Agent>& agents) {
    for (const auto& a : agents) {
        if (a.id == r.agentId) return a;
    }
    Agent fallback;
    fallback.id = r.agentId;
    fallback.model = r.model;
    fallback.source = r.source;
    return fallback;
}

bool estimatesInFlight(const Agent& a, std::int64_t now) {
    return a.phase == Phase::Generating && a.inFlightStartMs && a.lastLineMs && now - *a.lastLineMs <= kStaleMs;
}

std::int64_t estimateStart(const Agent& a, const std::vector<Response>& responses) {
    std::int64_t start = *a.inFlightStartMs;
    for (const auto& r : responses) {
        if (r.agentId == a.id) start = std::max(start, r.endMs);
    }
    return start;
}

}  // namespace

std::optional<std::pair<std::int64_t, std::int64_t>> interval(const Response& r) {
    if (!r.startMs || r.endMs < *r.startMs || r.endMs - *r.startMs > 15 * 60'000) return std::nullopt;
    return std::make_pair(std::min(*r.startMs, r.endMs - 500), r.endMs);
}

std::optional<Speed> estimatedSpeed(const std::vector<const Response*>& valid, std::int64_t now, const Agent& agent) {
    const auto own = recentSet(valid, now, [&](const Response& r) { return r.agentId == agent.id; }, 3, kRecentHorizonMs);
    if (!own.empty()) {
        if (auto s = speedOf(own)) return s;
    }
    if (agent.model && !agent.model->empty()) {
        const auto same = recentSet(valid, now, [&](const Response& r) { return r.model == agent.model; }, 10,
                                    kFallbackHorizonMs);
        if (!same.empty()) {
            if (auto s = speedOf(same)) return s;
        }
    }
    const auto source = recentSet(valid, now, [&](const Response& r) { return r.source == agent.source; }, 10,
                                  kFallbackHorizonMs);
    if (!source.empty()) return speedOf(source);
    return std::nullopt;
}

LiveRate liveRate(const std::vector<Response>& responses, const std::vector<Agent>& agents, std::int64_t now) {
    const std::int64_t w0 = now - kWindowMs;
    const auto parts = split(responses);
    double measuredOut = 0, measuredTot = 0, estimatedOut = 0, estimatedTot = 0;
    for (const auto* r : parts.valid) {
        const auto [s, e] = *interval(*r);
        const double frac = overlap(w0, now, s, e) / static_cast<double>(e - s);
        measuredOut += static_cast<double>(r->output) * frac;
        measuredTot += static_cast<double>(r->output + r->input) * frac;
    }
    for (const auto* r : parts.unmeasured) {
        const auto sp = estimatedSpeed(parts.valid, now, agentOf(*r, agents));
        if (!sp) continue;
        const auto [s, e] = *interval(*r);
        const double secs = overlap(w0, now, s, e) / 1000.0;
        estimatedOut += sp->output * secs;
        estimatedTot += sp->total * secs;
    }
    for (const auto& a : agents) {
        if (!estimatesInFlight(a, now)) continue;
        const auto sp = estimatedSpeed(parts.valid, now, a);
        if (!sp) continue;
        const double secs = overlap(w0, now, estimateStart(a, responses), now) / 1000.0;
        estimatedOut += sp->output * secs;
        estimatedTot += sp->total * secs;
    }
    const double window = static_cast<double>(kWindowMs) / 1000.0;
    const double denom = measuredOut + estimatedOut;
    return {(measuredOut + estimatedOut) / window, (measuredTot + estimatedTot) / window,
            denom > 0 ? estimatedOut / denom : 0.0};
}

LiveCost liveCost(const std::vector<Response>& responses, const std::vector<Agent>& agents, std::int64_t now) {
    const std::int64_t w0 = now - kWindowMs;
    const auto parts = split(responses);
    double cost = 0, knownTokens = 0, allTokens = 0;
    for (const auto* r : parts.valid) {
        const auto [s, e] = *interval(*r);
        const double frac = overlap(w0, now, s, e) / static_cast<double>(e - s);
        allTokens += static_cast<double>(r->output) * frac;
        if (r->costMicroUsd) {
            cost += static_cast<double>(*r->costMicroUsd) * frac;
            knownTokens += static_cast<double>(r->output) * frac;
        }
    }
    std::vector<std::pair<Agent, double>> estimates;
    for (const auto* r : parts.unmeasured) {
        const auto [s, e] = *interval(*r);
        estimates.emplace_back(agentOf(*r, agents), overlap(w0, now, s, e) / 1000.0);
    }
    for (const auto& a : agents) {
        if (!estimatesInFlight(a, now)) continue;
        estimates.emplace_back(a, overlap(w0, now, estimateStart(a, responses), now) / 1000.0);
    }
    for (const auto& [agent, secs] : estimates) {
        const auto refs = referenceSet(parts.valid, now, agent);
        if (refs.empty()) continue;
        const double tokens = speedOf(refs)->output * secs;
        allTokens += tokens;
        double knownCost = 0, knownSecs = 0;
        bool anyKnown = false;
        for (const auto* r : refs) {
            if (!r->costMicroUsd) continue;
            anyKnown = true;
            const auto iv = interval(*r);
            knownSecs += static_cast<double>(iv->second - iv->first) / 1000.0;
            knownCost += static_cast<double>(*r->costMicroUsd);
        }
        if (anyKnown && knownSecs > 0) {
            cost += knownCost / knownSecs * secs;
            knownTokens += tokens;
        }
    }
    const double window = static_cast<double>(kWindowMs) / 1000.0;
    return {cost / window, allTokens > 0 ? knownTokens / allTokens : 1.0};
}

AgentList agentList(const std::vector<Response>& responses, const std::vector<Agent>& agents, std::int64_t now) {
    std::map<std::string, Agent> byId;
    for (auto a : agents) {
        if (a.phase == Phase::Generating && a.lastLineMs && now - *a.lastLineMs > kStaleMs) a.phase = Phase::Idle;
        byId[a.id] = a;
    }
    std::map<std::string, double> live;
    for (const auto& [id, a] : byId) {
        std::vector<Response> own;
        for (const auto& r : responses) {
            if (r.agentId == id) own.push_back(r);
        }
        live[id] = liveRate(own, {a}, now).output;
    }
    std::set<std::string> chosen;
    for (const auto& [id, a] : byId) {
        if (a.phase == Phase::Generating || (a.lastLineMs && now - *a.lastLineMs <= kListHorizonMs)) chosen.insert(id);
    }
    for (const auto& id : std::vector<std::string>(chosen.begin(), chosen.end())) {
        auto current = byId[id].parentId;
        while (current && byId.contains(*current) && !chosen.contains(*current)) {
            chosen.insert(*current);
            current = byId[*current].parentId;
        }
    }
    std::map<std::string, std::optional<std::string>> parent;
    for (const auto& id : chosen) {
        const auto& p = byId[id].parentId;
        parent[id] = (p && chosen.contains(*p)) ? p : std::nullopt;
    }
    std::map<std::optional<std::string>, std::vector<std::string>> children;
    for (const auto& id : chosen) children[parent[id]].push_back(id);

    std::function<std::vector<std::string>(const std::string&)> members = [&](const std::string& id) {
        std::vector<std::string> out{id};
        for (const auto& c : children[std::optional<std::string>(id)]) {
            auto sub = members(c);
            out.insert(out.end(), sub.begin(), sub.end());
        }
        return out;
    };
    auto last = [&](const std::string& id) { return byId[id].lastLineMs.value_or(0); };
    auto generating = [&](const std::string& id) { return byId[id].phase == Phase::Generating; };
    auto siblingKey = [&](const std::string& id) {
        return std::make_tuple(generating(id) ? 0 : 1, -live[id], -last(id), id);
    };
    auto groupKey = [&](const std::string& root) {
        const auto m = members(root);
        bool anyGenerating = false;
        double sum = 0;
        std::int64_t latest = std::numeric_limits<std::int64_t>::min();
        for (const auto& x : m) {
            anyGenerating = anyGenerating || generating(x);
            sum += live[x];
            latest = std::max(latest, last(x));
        }
        return std::make_tuple(anyGenerating ? 0 : 1, -sum, -latest, root);
    };
    AgentList list;
    std::function<void(const std::string&)> walk = [&](const std::string& id) {
        list.order.push_back(id);
        auto kids = children[std::optional<std::string>(id)];
        std::sort(kids.begin(), kids.end(), [&](const auto& a, const auto& b) { return siblingKey(a) < siblingKey(b); });
        for (const auto& c : kids) walk(c);
    };
    auto roots = children[std::nullopt];
    std::sort(roots.begin(), roots.end(), [&](const auto& a, const auto& b) { return groupKey(a) < groupKey(b); });
    for (const auto& root : roots) walk(root);
    for (const auto& [id, p] : parent) {
        if (p) list.parents[id] = *p;
    }
    return list;
}

std::map<std::int64_t, double> buckets(const std::vector<Response>& responses, std::int64_t first, std::int64_t last) {
    std::map<std::int64_t, double> values;
    for (const auto& r : responses) {
        const auto iv = interval(r);
        if (!iv || !r.final) continue;
        const auto [s, e] = *iv;
        std::int64_t k = (s >= 0 ? s / 1000 : (s - 999) / 1000) * 1000;
        while (k < e) {
            if (first <= k && k <= last) {
                values[k] += static_cast<double>(r.output) * overlap(k, k + 1000, s, e) / static_cast<double>(e - s);
            }
            k += 1000;
        }
    }
    return values;
}

TodayStats todayStats(const std::vector<Response>& responses, std::int64_t todayStart, std::int64_t now) {
    TodayStats stats;
    std::vector<Response> today;
    for (const auto& r : responses) {
        if (r.endMs >= todayStart) today.push_back(r);
    }
    std::vector<const Response*> valid;
    for (const auto& r : today) {
        stats.requests += r.requestCount;
        stats.output += r.output;
        stats.input += r.input;
        if (!r.final) stats.unfinalized += r.requestCount;
        if (r.costMicroUsd) {
            stats.costMicroUsd += *r.costMicroUsd;
        } else {
            stats.unpricedRequests += r.requestCount;
        }
        if (interval(r) && r.final) valid.push_back(&r);
    }
    if (auto sp = speedOf(valid); sp && !valid.empty()) stats.average = sp->output;
    stats.buckets = buckets(today, todayStart, now);
    for (const auto& [k, _] : stats.buckets) {
        double sum = 0;
        for (int i = 0; i < 3; ++i) {
            if (auto it = stats.buckets.find(k - i * 1000); it != stats.buckets.end()) sum += it->second;
        }
        stats.peak = std::max(stats.peak, sum / 3.0);
    }
    return stats;
}

}  // namespace agentspeed::engine
