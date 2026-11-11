#include "agentspeed/quota/estimate/estimator.hpp"

#include <algorithm>
#include <tuple>

#include "agentspeed/json.hpp"
#include "agentspeed/quota/codex.hpp"
#include "agentspeed/quota/estimate/store_file.hpp"

namespace agentspeed::quota::estimate {

namespace {

std::string storeFile(const std::string& folder) { return folder + "/" + std::string(kStoreFileName); }

}  // namespace

Response responseOf(const history::EstimateResponse& e, const std::string& machine) {
    Response r;
    r.source = std::string(sourceId(e.source));
    r.machine = machine;
    r.file = e.agentId;
    r.startMs = e.startMs;
    r.endMs = e.endMs;
    r.model = e.model;
    r.cost = e.costMicroUsd;
    r.tokens = e.tokens;
    r.requests = e.requests;
    r.provider = e.provider;
    r.cloudRoute = e.cloudRoute;
    return r;
}

std::optional<CodexLine> codexLineOf(const std::string& agentId, std::int64_t ts, const json::Value& rateLimits) {
    CodexLine line;
    line.file = agentId;
    line.ts = ts;
    line.windows = codexWindows(rateLimits, ts);
    std::erase_if(line.windows, [](const Window& w) { return !w.resetsAtMs; });
    if (line.windows.empty()) return std::nullopt;
    line.plan = cleanPlan(rateLimits["plan_type"]);
    return line;
}

std::optional<CodexLine> codexLineOf(const std::string& agentId, std::int64_t ts, std::string_view rateLimits) {
    json::Document doc;
    if (!doc.parse(rateLimits) || !doc.root().isObject()) return std::nullopt;
    return codexLineOf(agentId, ts, doc.root());
}

Estimator::Estimator(std::string folder) : folder_(std::move(folder)) {
    salt_ = loadOrCreateSalt(folder_);
    if (!salt_) return;
    if (auto stored = readStore(storeFile(folder_)); stored && stored->version == kStoreVersion) stored_ = std::move(*stored);
    // The timeline's newest keys: a launch that reads the same sign-in adds no entry.
    std::optional<std::int64_t> codexAt, claudeAt;
    for (const auto& k : stored_.keys) {
        if (k.source == "codex" && (!codexAt || k.entry.at >= *codexAt)) {
            codexAt = k.entry.at;
            lastCodexKey_ = k.entry.key;
        } else if (k.source == "claudeCode" && (!claudeAt || k.entry.at >= *claudeAt)) {
            claudeAt = k.entry.at;
            lastClaude_ = SignedInAccount{k.entry.key, k.orgKey.value_or(""), k.entry.plan, k.entry.tier};
        }
    }
}

std::string Estimator::saltText() const { return salt_ ? saltHex(*salt_) : std::string(); }

std::string Estimator::shownKey(const std::string& machine, const std::string& source) {
    return machine == kLocal ? source : source + "@" + machine;
}

void Estimator::setHistory(const history::EstimateFile& spend, std::int64_t cutMs) {
    std::vector<Response> responses;
    responses.reserve(spend.responses.size());
    for (const auto& r : spend.responses) {
        if (r.endMs < cutMs) responses.push_back(responseOf(r));
    }
    std::vector<CodexLine> lines;
    for (const auto& l : spend.lines) {
        if (l.ts >= cutMs) continue;
        if (auto line = codexLineOf(l.agentId, l.ts, std::string_view(l.rateLimits))) lines.push_back(std::move(*line));
    }
    const std::lock_guard lock(mutex_);
    history_ = std::move(responses);
    historyLines_ = std::move(lines);
    cutMs_ = cutMs;
}

void Estimator::putLive(std::vector<std::pair<std::string, Response>> responses) {
    const std::lock_guard lock(mutex_);
    for (auto& [key, r] : responses) live_[key] = std::move(r);
}

void Estimator::addCodexLine(CodexLine line) {
    const std::lock_guard lock(mutex_);
    if (codexEnabled_) liveLines_.push_back(std::move(line));
}

void Estimator::setCodexEnabled(bool enabled) {
    const std::lock_guard lock(mutex_);
    codexEnabled_ = enabled;
}

void Estimator::setCodexAccount(std::int64_t at, const std::optional<SignedInAccount>& account) {
    if (!account || account->key.empty()) return;
    const std::lock_guard lock(mutex_);
    if (lastCodexKey_ == account->key) return;
    lastCodexKey_ = account->key;
    codexKeys_.emplace_back(at, account->key);
}

void Estimator::setClaudeAccount(std::int64_t at, const std::optional<SignedInAccount>& account) {
    if (!account || account->key.empty()) return;
    const std::lock_guard lock(mutex_);
    if (lastClaude_ == account) return;
    lastClaude_ = account;
    profiles_.push_back({at, account->key, account->orgKey, account->plan, account->tier});
}

void Estimator::addSnapshot(SnapshotEvent snapshot) {
    const std::lock_guard lock(mutex_);
    const auto id = std::make_pair(snapshot.machine, snapshot.source);
    const auto same = [&](const SnapshotEvent& a) {
        return a.windows == snapshot.windows && a.orgKey == snapshot.orgKey && a.account == snapshot.account && a.plan == snapshot.plan &&
               a.tier == snapshot.tier;
    };
    if (const auto it = lastSnapshot_.find(id); it != lastSnapshot_.end() && it->second.index < snapshots_.size()) {
        auto& last = snapshots_[it->second.index];
        if (last.observedMs == snapshot.observedMs && same(last)) return;
        if (same(last) && it->second.repeated) {
            last.observedMs = std::max(last.observedMs, snapshot.observedMs);
            return;
        }
        const bool repeated = same(last);
        snapshots_.push_back(std::move(snapshot));
        it->second = {snapshots_.size() - 1, repeated};
        return;
    }
    snapshots_.push_back(std::move(snapshot));
    lastSnapshot_[id] = {snapshots_.size() - 1, false};
}

void Estimator::setPriceVersion(int version) {
    const std::lock_guard lock(mutex_);
    priceVersion_ = version;
}

void Estimator::prune(std::int64_t nowMs) {
    const auto keep = nowMs - kKeepMs;
    std::erase_if(live_, [&](const auto& item) { return item.second.endMs < keep; });
    std::erase_if(liveLines_, [&](const CodexLine& l) { return l.ts < keep; });
    std::erase_if(history_, [&](const Response& r) { return r.endMs < keep; });
    std::erase_if(historyLines_, [&](const CodexLine& l) { return l.ts < keep; });
    if (std::any_of(snapshots_.begin(), snapshots_.end(), [&](const SnapshotEvent& s) { return s.observedMs < keep; })) {
        std::erase_if(snapshots_, [&](const SnapshotEvent& s) { return s.observedMs < keep; });
        lastSnapshot_.clear();
        for (std::size_t i = 0; i < snapshots_.size(); ++i) lastSnapshot_[{snapshots_[i].machine, snapshots_[i].source}] = {i, false};
    }
}

Estimator::Shown Estimator::build(std::int64_t nowMs, const std::set<std::pair<std::string, std::string>>& shown) {
    if (!salt_) return {};
    std::shared_ptr<Engine> engine;
    {
        const std::lock_guard lock(mutex_);
        prune(nowMs);
        engine = std::make_shared<Engine>(*salt_, priceVersion_);
        for (const auto& r : history_) engine->add(r);
        for (const auto& [_, r] : live_) {
            if (r.machine != kLocal || r.endMs >= cutMs_) engine->add(r);
        }
        for (const auto& l : historyLines_) engine->add(l);
        for (const auto& l : liveLines_) {
            if (l.ts >= cutMs_) engine->add(l);
        }
        for (const auto& [at, key] : codexKeys_) engine->addCodexKey(at, key);
        for (const auto& p : profiles_) engine->add(p);
        for (const auto& s : snapshots_) engine->add(s);
        engine->load(stored_);
    }
    engine->build(nowMs);
    Shown out;
    std::set<std::string> accounts;
    for (const auto& [machine, source] : shown) {
        const auto account = engine->accountOf(machine, source);
        if (!account) continue;
        accounts.insert(*account);
        auto& list = out[shownKey(machine, source)];
        for (const auto& in : engine->current()) {
            if (in.account == *account) list.push_back(reportEntry(in, nowMs, engine->ledger(), engine->priceVersion()));
        }
        if (list.empty()) out.erase(shownKey(machine, source));
    }
    const std::lock_guard lock(mutex_);
    built_ = std::move(engine);
    shownAccounts_ = std::move(accounts);
    return out;
}

std::vector<Entry> Estimator::entries(std::int64_t nowMs) const {
    std::shared_ptr<const Engine> engine;
    std::set<std::string> accounts;
    {
        const std::lock_guard lock(mutex_);
        engine = built_;
        accounts = shownAccounts_;
    }
    std::vector<Entry> out;
    if (!engine) return out;
    // REPORTING-EXTENSIONS 11.4: (sources, window) once per report; of two accounts (a host's whose resets contradict
    // this computer's), the newest vendor value's.
    using Identity = std::tuple<std::vector<std::string>, std::string, std::optional<std::string>, std::int64_t>;
    std::map<Identity, const EntryInput*> newest;
    for (const auto& in : engine->current()) {
        if (!accounts.contains(in.account)) continue;
        auto& held = newest[{in.sources, in.kind, in.name, in.minutes}];
        if (!held || in.t > held->t) held = &in;
    }
    for (const auto& in : engine->current()) {
        if (out.size() >= kMaxEntries) break;
        const auto held = newest.find({in.sources, in.kind, in.name, in.minutes});
        if (held != newest.end() && held->second == &in) out.push_back(reportEntry(in, nowMs, engine->ledger(), engine->priceVersion()));
    }
    return out;
}

void Estimator::save(std::int64_t nowMs, bool force) {
    std::shared_ptr<const Engine> engine;
    {
        const std::lock_guard lock(mutex_);
        if (!salt_ || !built_) return;
        if (!force && savedMs_ && nowMs - *savedMs_ < kMinute) return;
        savedMs_ = nowMs;
        engine = built_;
    }
    auto store = engine->store();
    if (!writeStore(storeFile(folder_), store)) return;
    const std::lock_guard lock(mutex_);
    stored_ = std::move(store);
}

}  // namespace agentspeed::quota::estimate
