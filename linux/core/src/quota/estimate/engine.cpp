#include "agentspeed/quota/estimate/engine.hpp"

#include <algorithm>
#include <climits>
#include <set>
#include <tuple>

#include "agentspeed/model.hpp"

namespace agentspeed::quota::estimate {

namespace {

bool isClaude(std::string_view source) { return source == "claudeCode" || source == "claudeDesktop"; }
bool hasTimeline(std::string_view source) { return source == "codex" || isClaude(source); }

std::string vendorOf(const std::string& source) {
    if (source == "codex") return "openai";
    if (isClaude(source)) return "anthropic";
    return source;
}

std::int64_t minutesOf(const Window& w) { return w.minutes.value_or(defaultMinutes(w.kind).value_or(0)); }

int sourceRank(const std::string& source) {
    const auto kind = sourceFromId(source);
    return kind ? static_cast<int>(*kind) : static_cast<int>(SourceKind::QoderCli) + 1;
}

using WindowRank = std::tuple<int, int, std::int64_t, int>;
WindowRank windowRank(const std::string& kind, const std::optional<std::string>& name, std::int64_t minutes, const std::vector<std::string>& names) {
    const auto k = kindFromId(kind);
    const int kindIndex = k ? static_cast<int>(*k) : 0;
    const auto length = minutes ? minutes : (std::int64_t{1} << 30);
    if (name) {
        const auto at = std::find(names.begin(), names.end(), *name);
        return {1, static_cast<int>(at - names.begin()), length, kindIndex};
    }
    return {0, 0, length, kindIndex};
}

Observation observationOf(const std::string& account, const std::string& source, const Window& w, std::int64_t t, std::int64_t tLo,
                          const std::optional<std::string>& plan, const std::optional<std::string>& tier, const std::string& machine) {
    Observation o;
    o.account = account;
    o.source = source;
    o.kind = std::string(kindId(w.kind));
    o.name = w.name;
    o.minutes = minutesOf(w);
    o.p = w.usedPercent;
    o.t = t;
    o.tLo = tLo;
    o.R = *w.resetsAtMs;
    o.plan = plan;
    o.tier = tier;
    o.machine = machine;
    return o;
}

using SeenKey = std::tuple<std::string, std::string, std::optional<std::string>, std::int64_t, std::int64_t, double, std::int64_t>;
SeenKey seenKey(const Observation& o) { return {o.account, o.kind, o.name, o.minutes, o.t, o.p, o.R}; }

void sortUnique(std::vector<KeyEntry>& timeline) {
    std::sort(timeline.begin(), timeline.end());
    timeline.erase(std::unique(timeline.begin(), timeline.end()), timeline.end());
}

}  // namespace

Engine::Engine(Salt salt, int priceVersion) : salt_(std::move(salt)), priceVersion_(priceVersion) {}

void Engine::add(Response response) { responses_.push_back(std::move(response)); }
void Engine::add(CodexLine line) { lines_.push_back(std::move(line)); }
void Engine::addCodexKey(std::int64_t at, std::string key) { codexAuth_.emplace_back(at, std::move(key)); }
void Engine::add(ClaudeProfile profile) { profiles_.push_back(std::move(profile)); }
void Engine::add(SnapshotEvent snapshot) { snapshots_.push_back(std::move(snapshot)); }

void Engine::load(const Store& store) {
    if (store.version == kStoreVersion) stored_ = store;
}

const KeyEntry* Engine::inForce(const Timeline& timeline, std::int64_t at) {
    if (timeline.empty()) return nullptr;
    const auto it = std::upper_bound(timeline.begin(), timeline.end(), at, [](std::int64_t v, const KeyEntry& e) { return v < e.at; });
    return it == timeline.begin() ? &timeline.front() : &*(it - 1);
}

void Engine::timelines() {
    tl_.clear();
    orgKeys_.clear();
    auto auth = codexAuth_;
    std::sort(auth.begin(), auth.end());
    auto& codexTl = tl_["codex"];
    for (const auto& [at, key] : auth) codexTl.push_back({at, key, std::nullopt, std::nullopt});
    auto profiles = profiles_;
    std::stable_sort(profiles.begin(), profiles.end(), [](const ClaudeProfile& a, const ClaudeProfile& b) { return a.at < b.at; });
    for (const auto& p : profiles) {
        const KeyEntry entry{p.at, p.key, p.plan, p.tier};
        tl_["claudeCode"].push_back(entry);
        orgKeys_[entry] = p.orgKey;
    }
    for (const auto& k : stored_.keys) {
        tl_[k.source].push_back(k.entry);
        if (k.source == "claudeCode" && k.orgKey) orgKeys_.try_emplace(k.entry, *k.orgKey);
    }
    for (const auto& s : snapshots_) {
        if (s.source == "claudeDesktop" && s.machine == kLocal && s.orgKey) tl_["claudeDesktop"].push_back({s.observedMs, *s.orgKey, std::nullopt, std::nullopt});
    }
    for (auto& [_, timeline] : tl_) sortUnique(timeline);
    tl_.try_emplace("claudeCode");
    desktopOrgs_ = std::move(tl_["claudeDesktop"]);
    tl_.erase("claudeDesktop");
    tl_["claudeDesktop"] = desktopTimeline(tl_["claudeCode"]);
}

std::vector<std::pair<WindowPoint, std::optional<std::string>>> Engine::windowPoints(const std::string& source) const {
    std::vector<std::pair<WindowPoint, std::optional<std::string>>> out;
    for (const auto& s : snapshots_) {
        if (s.source != source || s.machine != kLocal) continue;
        for (const auto& w : s.windows) {
            if (w.resetsAtMs) out.push_back({{s.observedMs, windowId(kindId(w.kind), w.name, minutesOf(w)), *w.resetsAtMs, minutesOf(w)}, std::nullopt});
        }
    }
    for (const auto& o : stored_.observations) {
        if (o.source == source && o.machine == kLocal) out.push_back({{o.t, o.window(), o.R, o.minutes}, o.account});
    }
    return out;
}

Engine::Timeline Engine::desktopTimeline(const Timeline& cli) const {
    if (desktopOrgs_.empty()) return {};
    std::vector<WindowPoint> desk;
    for (auto& [p, _] : windowPoints("claudeDesktop")) desk.push_back(p);
    std::vector<std::pair<WindowPoint, std::optional<std::string>>> cliW;
    for (auto& [p, account] : windowPoints("claudeCode")) {
        std::optional<std::string> key = account;
        if (!key) {
            if (const auto* e = inForce(cli, p.t)) key = e->key;
        }
        cliW.emplace_back(p, key);
    }
    std::set<std::int64_t> ats;
    for (const auto& o : desktopOrgs_) ats.insert(o.at);
    for (const auto& c : cli) ats.insert(c.at);
    std::map<std::string, Fingerprint> prints;
    Timeline out;
    for (const auto at : ats) {
        const auto& own = inForce(desktopOrgs_, at)->key;
        if (const auto* c = inForce(cli, at)) {
            auto print = prints.find(c->key);
            if (print == prints.end()) {
                std::vector<WindowPoint> mine;
                for (const auto& [p, key] : cliW) {
                    if (key == c->key) mine.push_back(p);
                }
                print = prints.emplace(c->key, Fingerprint(desk, mine)).first;
            }
            const auto org = orgKeys_.find(*c);
            const std::optional<std::string> cliOrg = org == orgKeys_.end() ? std::nullopt : std::optional<std::string>(org->second);
            if (desktopSharesCli(own, cliOrg, c->plan, print->second.at(at))) {
                out.push_back({at, c->key, c->plan, c->tier});
                continue;
            }
        }
        out.push_back({at, own, std::nullopt, std::nullopt});
    }
    return out;
}

std::string Engine::hostAccount(const std::string& machine, const std::string& source, const std::optional<std::string>& key) {
    if (!key) return machine + ":" + source;
    const auto cacheKey = std::make_tuple(machine, source, *key);
    if (const auto it = hostShared_.find(cacheKey); it != hostShared_.end()) return it->second;
    std::vector<WindowPoint> local, host;
    for (const auto& o : obs_) {
        if (o.machine == kLocal && o.account == *key) local.push_back({o.t, o.window(), o.R, o.minutes});
    }
    for (const auto& s : snapshots_) {
        if (s.machine != machine || s.source != source) continue;
        for (const auto& w : s.windows) {
            if (w.resetsAtMs) host.push_back({s.observedMs, windowId(kindId(w.kind), w.name, minutesOf(w)), *w.resetsAtMs, minutesOf(w)});
        }
    }
    for (const auto& o : stored_.observations) {
        if (o.machine == machine && o.source == source) host.push_back({o.t, o.window(), o.R, o.minutes});
    }
    const auto account = keysShare(Fingerprint(host, local).at(INT64_MAX)) ? *key : *key + "@" + machine;
    hostShared_[cacheKey] = account;
    return account;
}

void Engine::build(std::int64_t nowMs) {
    nowMs_ = nowMs;
    hostShared_.clear();
    timelines();
    codex();
    ledger_ = std::make_shared<Ledger>();
    responseAccounts_.assign(responses_.size(), std::nullopt);
    const auto take = [&](std::size_t i, const std::string& account) {
        const auto& r = responses_[i];
        responseAccounts_[i] = account;
        ledger_->add(account, r.source, r.model, r.endMs, r.cost, r.requests, r.tokens);
    };
    const auto subscription = [](const Response& r) {
        if (r.source == "codex") return !r.provider || *r.provider == "openai";
        return !(isClaude(r.source) && r.cloudRoute);
    };
    for (std::size_t i = 0; i < responses_.size(); ++i) {
        const auto& r = responses_[i];
        if (r.machine != kLocal || !subscription(r)) continue;
        if (r.source == "codex") {
            if (responseLine_[i]) take(i, parsedLines_[*responseLine_[i]].account);
        } else if (isClaude(r.source)) {
            if (const auto* e = inForce(tl_[r.source], r.endMs)) take(i, e->key);
        } else {
            take(i, r.machine + ":" + r.source);
        }
    }

    obs_.clear();
    for (const auto& ln : parsedLines_) {
        for (const auto& w : ln.windows) obs_.push_back(observationOf(ln.account, "codex", w, ln.line.ts, ln.tLo, ln.plan, std::nullopt, std::string(kLocal)));
    }
    // QUOTAS 10.4 tLo of the Claude CLI: the earliest response of the account that started before t and ended at or after
    // t − λ (by start, with the running maximum of the ends).
    struct Started {
        std::vector<std::int64_t> starts, maxEnds;
    };
    std::map<std::string, Started> started;
    {
        std::map<std::string, std::vector<std::pair<std::int64_t, std::int64_t>>> spans;
        for (std::size_t i = 0; i < responses_.size(); ++i) {
            const auto& r = responses_[i];
            if (r.source == "claudeCode" && r.machine == kLocal && r.startMs && responseAccounts_[i]) spans[*responseAccounts_[i]].emplace_back(*r.startMs, r.endMs);
        }
        for (auto& [account, list] : spans) {
            std::stable_sort(list.begin(), list.end(), [](const auto& a, const auto& b) { return a.first < b.first; });
            auto& s = started[account];
            for (const auto& [start, end] : list) {
                s.starts.push_back(start);
                s.maxEnds.push_back(s.maxEnds.empty() ? end : std::max(s.maxEnds.back(), end));
            }
        }
    }
    for (const auto& s : snapshots_) {
        if (s.machine != kLocal) continue;
        const auto t = s.observedMs;
        std::string account;
        std::optional<std::string> plan, tier;
        if (hasTimeline(s.source)) {
            const auto* e = inForce(tl_[s.source], t);
            if (!e) continue;
            account = e->key;
            plan = e->plan;
            tier = e->tier;
        } else {
            account = std::string(kLocal) + ":" + s.source;
        }
        auto tLo = t;
        if (s.source == "claudeCode") {
            if (const auto it = started.find(account); it != started.end()) {
                const auto& st = it->second;
                const auto first = static_cast<std::size_t>(std::lower_bound(st.maxEnds.begin(), st.maxEnds.end(), t - kLagMs) - st.maxEnds.begin());
                if (first < st.starts.size() && st.starts[first] < t) tLo = std::min(t, st.starts[first]);
            }
        }
        for (const auto& w : s.windows) {
            if (w.resetsAtMs) obs_.push_back(observationOf(account, s.source, w, t, tLo, plan, tier, std::string(kLocal)));
        }
    }
    std::set<SeenKey> seen;
    for (const auto& o : obs_) seen.insert(seenKey(o));
    const auto& desktop = tl_["claudeDesktop"];
    for (auto o : stored_.observations) {
        if (o.machine != kLocal) continue;
        if (o.source == "claudeDesktop" && !desktop.empty()) {
            const auto* e = inForce(desktop, o.t);
            o.account = e->key;
            o.plan = e->plan;
            o.tier = e->tier;
        }
        if (seen.insert(seenKey(o)).second) obs_.push_back(std::move(o));
    }

    // Remote hosts: their timelines, observations and responses, after this computer's evidence is in.
    hostTl_.clear();
    for (const auto& s : snapshots_) {
        if (s.machine == kLocal) continue;
        const auto account = hostAccount(s.machine, s.source, s.account);
        hostTl_[{s.machine, s.source}].push_back({s.observedMs, account, s.plan, s.tier});
        for (const auto& w : s.windows) {
            if (!w.resetsAtMs) continue;
            auto o = observationOf(account, s.source, w, s.observedMs, s.observedMs, s.plan, s.tier, s.machine);
            o.hostKey = s.account;
            if (seen.insert(seenKey(o)).second) obs_.push_back(std::move(o));
        }
    }
    for (auto o : stored_.observations) {
        if (o.machine == kLocal) continue;
        o.account = hostAccount(o.machine, o.source, o.hostKey);
        if (seen.insert(seenKey(o)).second) obs_.push_back(std::move(o));
    }
    for (auto& [_, timeline] : hostTl_) sortUnique(timeline);
    for (std::size_t i = 0; i < responses_.size(); ++i) {
        const auto& r = responses_[i];
        if (r.machine == kLocal || !subscription(r)) continue;
        const auto it = hostTl_.find({r.machine, r.source});
        const auto* e = it == hostTl_.end() ? nullptr : inForce(it->second, r.endMs);
        take(i, e ? e->key : r.machine + ":" + r.source);
    }

    instancesAt();
    classEstimates();
    currentInputs();
}

void Engine::instancesAt() {
    insts_.clear();
    using GroupKey = std::tuple<std::string, std::string, std::optional<std::string>, std::optional<std::string>>;
    std::map<GroupKey, std::size_t> index;
    std::vector<std::pair<GroupKey, std::vector<Observation>>> groups;
    for (const auto& o : obs_) {
        if (o.t < nowMs_ - kKeepMs) continue;
        GroupKey key{o.account, o.window(), o.plan, o.tier};
        const auto [it, inserted] = index.try_emplace(key, groups.size());
        if (inserted) groups.emplace_back(key, std::vector<Observation>{});
        groups[it->second].second.push_back(o);
    }
    for (auto& [key, os] : groups) {
        const auto& [account, window, plan, tier] = key;
        const auto name = os.front().kind == "modelWeek" ? os.front().name : std::nullopt;
        const auto vendorPlan = plan ? std::optional<std::string>(vendorOf(os.front().source) + "/" + *plan) : std::nullopt;
        const auto cls = CapClass::of(account, vendorPlan, tier, window);
        for (auto& inst : estimate::instances(std::move(os))) {
            auto bounds = instanceBounds(segments(inst, *ledger_, account, name, kLagMs, nowMs_), inst.R);
            const bool closed = nowMs_ >= inst.R;
            insts_.push_back({account, window, plan, tier, cls, std::move(inst), bounds, name, closed});
        }
    }
}

void Engine::classEstimates() {
    estimates_.clear();
    std::map<CapClass, std::size_t> index;
    std::vector<std::pair<CapClass, std::vector<Interval>>> items;
    const auto slot = [&](const CapClass& cls) -> std::vector<Interval>& {
        const auto [it, inserted] = index.try_emplace(cls, items.size());
        if (inserted) items.emplace_back(cls, std::vector<Interval>{});
        return items[it->second].second;
    };
    std::set<std::tuple<CapClass, std::string, std::int64_t>> have;
    for (const auto& x : insts_) {
        if (!x.bounds) continue;
        slot(x.cls).push_back(*x.bounds);
        have.insert({x.cls, x.account, x.inst.R});
    }
    for (const auto& s : stored_.instances) {
        if (have.contains({s.cls, s.account, s.R})) continue;
        Interval i;
        i.R = s.R;
        i.lo = s.lo;
        i.hi = s.hi.value_or(kInfinity);
        i.dp = s.pMax;
        i.t = s.tLast;
        slot(s.cls).push_back(i);
    }
    for (auto& [cls, list] : items) {
        estimates_[cls] = estimate(pruneClosed(std::move(list)), nowMs_, halfLifeMs(minutesOfWindowId(cls.window)));
    }
}

void Engine::currentInputs() {
    current_.clear();
    std::map<std::pair<std::string, std::string>, std::size_t> index;
    std::vector<std::size_t> chosen;
    for (std::size_t i = 0; i < insts_.size(); ++i) {
        const auto& x = insts_[i];
        const auto& last = x.inst.obs.back();
        const auto [it, inserted] = index.try_emplace({x.account, x.window}, chosen.size());
        if (inserted) {
            chosen.push_back(i);
            continue;
        }
        const auto& held = insts_[chosen[it->second]].inst.obs.back();
        if (std::tie(last.t, last.tLo) > std::tie(held.t, held.tLo)) chosen[it->second] = i;
    }
    std::vector<const Observation*> byTime;
    for (const auto& o : obs_) byTime.push_back(&o);
    std::stable_sort(byTime.begin(), byTime.end(), [](const Observation* a, const Observation* b) { return a->t < b->t; });
    std::vector<std::tuple<std::tuple<int, WindowRank, std::string>, EntryInput>> rows;
    for (const auto i : chosen) {
        const auto& x = insts_[i];
        const auto& inst = x.inst;
        const auto& last = inst.obs.back();
        std::optional<std::int64_t> firstAfter;
        std::set<std::string> sourceSet{last.source};
        for (std::size_t k = 0; k < responses_.size(); ++k) {
            if (responseAccounts_[k] != x.account) continue;
            const auto& r = responses_[k];
            if (r.endMs >= inst.S) sourceSet.insert(r.source);
            if (inst.obs.front().kind == "fiveHour" && r.startMs && *r.startMs >= inst.R && r.endMs <= nowMs_) {
                firstAfter = firstAfter ? std::min(*firstAfter, *r.startMs) : *r.startMs;
            }
        }
        std::vector<std::string> sources(sourceSet.begin(), sourceSet.end());
        std::stable_sort(sources.begin(), sources.end(), [](const std::string& a, const std::string& b) { return sourceRank(a) < sourceRank(b); });
        std::vector<std::string> names;
        for (const auto* o : byTime) {
            if (o->account == x.account && o->name && std::find(names.begin(), names.end(), *o->name) == names.end()) names.push_back(*o->name);
        }
        EntryInput in;
        in.source = last.source;
        in.sources = sources;
        in.plan = x.plan;
        in.tier = x.tier;
        in.kind = last.kind;
        in.name = last.name;
        in.minutes = last.minutes;
        in.p = last.p;
        in.t = last.t;
        in.R = inst.R;
        in.S = inst.S;
        in.account = x.account;
        if (const auto e = estimates_.find(x.cls); e != estimates_.end()) in.estimate = e->second;
        in.firstAfterReset = firstAfter;
        rows.emplace_back(std::make_tuple(sourceRank(sources.front()), windowRank(last.kind, last.name, last.minutes, names), last.source), std::move(in));
    }
    std::stable_sort(rows.begin(), rows.end(), [](const auto& a, const auto& b) { return std::get<0>(a) < std::get<0>(b); });
    for (auto& [_, in] : rows) current_.push_back(std::move(in));
}

std::vector<Entry> Engine::entries(std::int64_t nowMs) const {
    std::vector<Entry> out;
    for (const auto& in : current_) {
        if (out.size() >= kMaxEntries) break;
        out.push_back(reportEntry(in, nowMs, *ledger_, priceVersion_));
    }
    return out;
}

std::optional<std::string> Engine::accountOf(const std::string& machine, const std::string& source) const {
    if (machine != kLocal) {
        const auto it = hostTl_.find({machine, source});
        const auto* e = it == hostTl_.end() ? nullptr : inForce(it->second, nowMs_);
        return e ? e->key : machine + ":" + source;
    }
    if (source == "codex") {
        const Line* newest = nullptr;
        for (const auto& ln : parsedLines_) {
            if (!newest || ln.line.ts > newest->line.ts) newest = &ln;
        }
        if (!newest) return std::nullopt;
        return newest->account;
    }
    if (isClaude(source)) {
        const auto it = tl_.find(source);
        const auto* e = it == tl_.end() ? nullptr : inForce(it->second, nowMs_);
        if (!e) return std::nullopt;
        return e->key;
    }
    return std::string(kLocal) + ":" + source;
}

}  // namespace agentspeed::quota::estimate
