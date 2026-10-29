#include "task_eval.hpp"

#include <algorithm>
#include <cmath>
#include <tuple>

namespace agentspeed::tasks::detail {

namespace {

constexpr double kKBuild = 4.0, kKTest = 4.0, kKFull = 2.0;
constexpr int kRamp = 8;
constexpr std::size_t kLoopRepeats = 3;
constexpr std::size_t kStallWindow = 8;
constexpr double kStallShare = 0.25;

bool verifying(const Act& a) { return a.command() && (a.isClass("test") || a.isClass("build") || a.isClass("check")); }

RunResult runResult(const Act& a) {
    if (!a.outcome) return RunResult::None;
    const auto failed = a.outcome->failed;
    const auto& fc = a.failures.failed;
    const auto& pc = a.failures.passed;
    if (failed == true || (fc && *fc > 0)) return RunResult::Fail;
    if (failed == false || (fc && *fc == 0 && !(pc && *pc == 0))) return RunResult::Pass;
    return RunResult::None;
}

std::string checkId(const CommandInfo& command) {
    if (command.targeted || !command.suite) return "key:" + command.key;
    return "suite:" + *command.suite;
}

struct FileEdits {
    std::vector<std::optional<std::string>> written;
    std::optional<std::string> latest;
    std::set<std::optional<std::string>> current, superseded;
};

void applyEdit(std::map<std::string, FileEdits>& files, Act& act) {
    if (act.outcome && act.outcome->failed == true) return;
    const auto& hunks = act.event->hunks;
    if (!hunks || hunks->empty()) {
        act.effective = true;
        return;
    }
    bool changed = false, oscillating = false;
    for (const auto& h : *hunks) {
        auto& state = files[h.file];
        const auto& old = h.oldDigest;
        const auto& now = h.newDigest;
        if (!old) {
            if (state.latest && now == state.latest) continue;
            changed = true;
            if (std::find(state.written.begin(), state.written.end(), now) != state.written.end()) oscillating = true;
            state.written.push_back(now);
            state.latest = now;
            state.current.clear();
            state.superseded.clear();
        } else {
            if (old == now) continue;
            changed = true;
            if (state.current.contains(old) && state.superseded.contains(now)) oscillating = true;
            state.superseded.insert(old);
            state.current.erase(old);
            state.current.insert(now);
            state.latest.reset();
        }
    }
    act.effective = changed;
    act.oscillating = changed && oscillating;
}

// Positions covered by a block of 1–3 keys repeated at least 3 times back to back, first block excluded.
std::vector<bool> loopMarks(const std::vector<ActKey>& keys) {
    const auto n = keys.size();
    std::vector<bool> marked(n, false);
    const auto same = [&](std::size_t a, std::size_t b, std::size_t len) {
        for (std::size_t k = 0; k < len; ++k) {
            if (keys[a + k] != keys[b + k]) return false;
        }
        return true;
    };
    for (std::size_t i = 0; i < n; ++i) {
        for (std::size_t b = 1; b <= 3; ++b) {
            if (i + kLoopRepeats * b > n) continue;
            std::size_t repeats = 1;
            while (i + (repeats + 1) * b <= n && same(i + repeats * b, i, b)) ++repeats;
            if (repeats >= kLoopRepeats) {
                for (std::size_t p = i + b; p < i + repeats * b; ++p) marked[p] = true;
            }
        }
    }
    return marked;
}

std::vector<std::int64_t> stallEndsOf(const std::vector<const Act*>& counted) {
    std::vector<std::int64_t> ends;
    for (std::size_t end = kStallWindow; end <= counted.size(); ++end) {
        std::int64_t gained = 0, total = 0;
        for (std::size_t k = end - kStallWindow; k < end; ++k) {
            gained += counted[k]->weight * counted[k]->gain;
            total += counted[k]->weight;
        }
        if (static_cast<double>(gained) <= kStallShare * static_cast<double>(total)) ends.push_back(static_cast<std::int64_t>(end));
    }
    return ends;
}

}  // namespace

double seqSum(const std::vector<double>& values) {
    double total = 0;
    for (const double v : values) total = total + v;
    return total;
}

Evaluation::Evaluation(const Event* begin, const Event* end, std::optional<std::int64_t> now) {
    for (const auto* e = begin; e != end; ++e) {
        if (e->type != Event::Type::Action || (now && e->t > *now)) continue;
        Act a;
        a.index = acts.size();
        a.event = e;
        a.kind = e->kind;
        a.outcome = e->outcome;
        if (a.outcome && now && a.outcome->t && *a.outcome->t > *now) a.outcome.reset();
        if (a.outcome && e->failures) a.failures = *e->failures;
        const bool verifies = verifying(a);
        a.weight = a.kind == "other" ? 0 : verifies ? 2 : 1;
        a.result = verifies ? runResult(a) : RunResult::None;
        a.fingerprints.insert(a.failures.fingerprints.begin(), a.failures.fingerprints.end());
        if (a.result == RunResult::Fail) {
            a.errors = std::max<std::int64_t>({1, static_cast<std::int64_t>(a.failures.fingerprints.size()), a.failures.failed.value_or(0)});
        }
        if (a.result != RunResult::None) a.check = checkId(*a.command());
        acts.push_back(std::move(a));
    }
    editsAndGain();
    runsAndState();
    dimensions();
}

void Evaluation::editsAndGain() {
    std::map<std::string, FileEdits> files;
    std::map<ActKey, std::int64_t> lastSeen;
    using Signature = std::tuple<RunResult, std::set<std::string>, std::optional<std::int64_t>>;
    std::map<std::string, std::pair<std::int64_t, Signature>> lastRun;
    for (auto& a : acts) {
        a.block = edits;
        if (a.kind == "edit") {
            applyEdit(files, a);
            a.gain = a.effective && !a.oscillating ? 1 : 0;
            if (a.effective) ++edits;
        } else if (a.result != RunResult::None) {
            std::pair<std::int64_t, Signature> mark{edits, {a.result, a.fingerprints, a.failures.failed}};
            const auto it = lastRun.find(a.command()->key);
            a.gain = it == lastRun.end() || it->second != mark ? 1 : 0;
            lastRun[a.command()->key] = std::move(mark);
        } else if (a.weight > 0) {
            const auto it = lastSeen.find(a.key());
            a.gain = it == lastSeen.end() || it->second != edits ? 1 : 0;
        }
        if (a.kind != "edit" && a.weight > 0 && a.result == RunResult::None) lastSeen[a.key()] = edits;
        if (a.weight > 0) counted.push_back(&a);
    }
    std::int64_t effective = 0;
    for (const auto& a : acts) {
        if (!a.effective) continue;
        ++effective;
        if (a.oscillating) ++oscillations;
    }
    osc = effective ? static_cast<double>(oscillations) / static_cast<double>(effective) : 0.0;
    std::int64_t total = 0, gained = 0;
    for (const auto* a : counted) total += a->weight;
    for (const auto* a : counted) gained += a->weight * a->gain;
    ig = total ? static_cast<double>(gained) / static_cast<double>(total) : 0.0;
    std::vector<ActKey> keys;
    for (const auto* a : counted) keys.push_back(a->key());
    for (const bool m : loopMarks(keys)) loopActions += m ? 1 : 0;
    loop = counted.empty() ? 0.0 : static_cast<double>(loopActions) / static_cast<double>(counted.size());
    stallEnds = stallEndsOf(counted);
}

const std::vector<const Act*>& Evaluation::seqOf(const std::string& check) const {
    for (const auto& [c, s] : seq) {
        if (c == check) return s;
    }
    static const std::vector<const Act*> kNone;
    return kNone;
}

void Evaluation::runsAndState() {
    for (const auto& a : acts) {
        if (a.result != RunResult::None) runs.push_back(&a);
    }
    std::vector<std::pair<std::string, std::vector<const Act*>>> own;
    for (const auto* r : runs) {
        auto it = std::find_if(own.begin(), own.end(), [&](const auto& entry) { return entry.first == r->check; });
        if (it == own.end()) it = own.insert(own.end(), {r->check, {}});
        it->second.push_back(r);
    }
    for (const auto& [check, checkRuns] : own) {
        const auto* first = checkRuns.front();
        auto covered = checkRuns;
        if (first->targeted() && first->suite()) {
            for (const auto* r : runs) {
                if (r->index > first->index && r->result == RunResult::Pass && !r->targeted() && r->suite() == first->suite() &&
                    r->check != check) {
                    covered.push_back(r);
                }
            }
        }
        std::stable_sort(covered.begin(), covered.end(), [](const Act* a, const Act* b) { return a->index < b->index; });
        seq.emplace_back(check, std::move(covered));
    }
    for (const auto& [check, s] : seq) {
        bool open = false;
        for (std::size_t i = 0; i < s.size(); ++i) {
            const auto* r = s[i];
            if (r->check == check && r->result == RunResult::Fail) nextInSeq[r->index] = i + 1 < s.size() ? s[i + 1] : nullptr;
            if (r->result == RunResult::Fail) {
                if (!open) {
                    episodes.push_back({check, {}, false});
                    open = true;
                }
                episodes.back().fails.push_back(r);
            } else if (open) {
                episodes.back().recovered = true;
                open = false;
            }
        }
    }
    std::map<std::int64_t, std::vector<const Act*>> blocks;
    for (const auto* r : runs) blocks[r->block].push_back(r);
    if (const auto it = blocks.find(edits); it != blocks.end()) currentBlock = it->second;
    if (!blocks.empty()) latestBlock = blocks.rbegin()->second;
    if (!currentBlock.empty()) {
        state = blockFails(currentBlock) ? "failing" : "passing";
    } else if (!runs.empty()) {
        state = blockFails(latestBlock) ? "failing" : "pending";
    } else {
        state = edits ? "unverified" : "none";
    }
}

bool Evaluation::blockFails(const std::vector<const Act*>& block) const {
    std::set<std::size_t> members;
    std::set<std::string> checks;
    for (const auto* r : block) {
        members.insert(r->index);
        checks.insert(r->check);
    }
    for (const auto& check : checks) {
        const Act* last = nullptr;
        for (const auto* r : seqOf(check)) {
            if (members.contains(r->index)) last = r;
        }
        if (last && last->result == RunResult::Fail) return true;
    }
    return false;
}

void Evaluation::dimensions() {
    const auto& block = currentBlock;
    std::vector<const Act*> passingTests;
    for (const auto* r : block) {
        if (r->isClass("test") && r->result == RunResult::Pass) passingTests.push_back(r);
    }
    V = state == "passing" ? (passingTests.empty() ? kVBuild : kVTest) : 0.0;
    C = convergence();
    if (!episodes.empty()) {
        double sum = 0;
        for (const auto& e : episodes) sum = sum + (e.recovered ? 2.0 / static_cast<double>(e.fails.size() + 1) : 0.0);
        R = kRMax * sum / static_cast<double>(episodes.size());
    } else {
        R = std::any_of(runs.begin(), runs.end(), [](const Act* r) { return r->result == RunResult::Pass; }) ? kRMax : 0.0;
    }
    const auto n = counted.size();
    E = n ? kEMax * std::min(1.0, static_cast<double>(n) / kRamp) * ig * (1 - loop) * (1 - osc) : 0.0;
    double k = 0;
    if (std::any_of(block.begin(), block.end(), [](const Act* r) { return r->isClass("build") || r->isClass("check"); }) ||
        !passingTests.empty()) {
        k += kKBuild;
    }
    if (std::any_of(block.begin(), block.end(), [](const Act* r) { return r->isClass("test"); })) k += kKTest;
    for (const auto* r : passingTests) {
        const bool laterFail = std::any_of(block.begin(), block.end(), [&](const Act* f) {
            return f->index > r->index && f->isClass("test") && f->result == RunResult::Fail;
        });
        if (!r->targeted() && !laterFail) {
            k += kKFull;
            break;
        }
    }
    K = k;
    if (state != "none") score = V + C + R + E + K;
}

double Evaluation::convergence() const {
    if (runs.empty()) return 0.0;
    double weighted = 0, total = 0;
    for (const auto& [check, s] : seq) {
        std::int64_t peak = 0;
        for (const auto* r : s) peak = std::max(peak, r->errors);
        if (peak == 0) continue;
        std::size_t firstPeak = 0;
        while (s[firstPeak]->errors != peak) ++firstPeak;
        std::set<std::string> base;
        for (std::size_t i = 0; i <= firstPeak; ++i) base.insert(s[i]->fingerprints.begin(), s[i]->fingerprints.end());
        const auto* last = s.back();
        std::int64_t fresh = 0;
        if (last->result == RunResult::Fail) {
            for (const auto& f : last->fingerprints) fresh += base.contains(f) ? 0 : 1;
        }
        const auto p = static_cast<double>(peak);
        weighted += p * std::max(0.0, 1 - static_cast<double>(last->errors + fresh) / p);
        total += p;
    }
    return total == 0 ? kCMax : kCMax * weighted / total;
}

int fixLabel(const Act* next) { return !next || next->result == RunResult::Fail ? 1 : 0; }

TaskCounts Evaluation::counts() const {
    TaskCounts c;
    c.actions = static_cast<std::int64_t>(acts.size());
    c.counted = static_cast<std::int64_t>(counted.size());
    c.edits = edits;
    c.oscillations = oscillations;
    c.loopActions = loopActions;
    c.runs = static_cast<std::int64_t>(runs.size());
    for (const auto* r : runs) {
        if (r->result != RunResult::Fail) continue;
        ++c.fixAttempts;
        const auto it = nextInSeq.find(r->index);
        c.fixFailures += fixLabel(it == nextInSeq.end() ? nullptr : it->second);
    }
    c.failingRuns = c.fixAttempts;
    c.episodes = static_cast<std::int64_t>(episodes.size());
    for (const auto& e : episodes) {
        if (!e.recovered) continue;
        ++c.recovered;
        c.recoverAttempts += static_cast<std::int64_t>(e.fails.size());
    }
    return c;
}

std::optional<std::int64_t> Evaluation::firstPassMs() const {
    std::optional<std::int64_t> first;
    for (const auto* r : runs) {
        if (r->result == RunResult::Pass && r->outcome->t) first = first ? std::min(*first, *r->outcome->t) : *r->outcome->t;
    }
    return first;
}

const Act* Evaluation::openFailure() const {
    const Act* newest = nullptr;
    for (const auto& [check, s] : seq) {
        const auto* last = s.back();
        if (last->result == RunResult::Fail && last->check == check && (!newest || last->index > newest->index)) newest = last;
    }
    return newest;
}

std::optional<double> Evaluation::heldV() const {
    if (state != "pending") return std::nullopt;
    const bool tests = std::any_of(latestBlock.begin(), latestBlock.end(),
                                   [](const Act* r) { return r->isClass("test") && r->result == RunResult::Pass; });
    return tests ? kVTest : kVBuild;
}

std::optional<OpenFailure> Evaluation::openFailureFacts() const {
    if (state != "failing") return std::nullopt;
    const auto* run = openFailure();
    OpenFailure f;
    f.suite = run->suite();
    f.cls = run->runClass();
    f.key = run->command()->key;
    f.errors = run->errors;
    for (const auto* r : seqOf(run->check)) f.peak = std::max(f.peak, r->errors);
    f.counted = !run->fingerprints.empty() || (run->failures.failed && *run->failures.failed > 0);
    f.edited = run->block < edits;
    return f;
}

std::optional<LastPass> Evaluation::lastPassFacts() const {
    if (state != "passing" && state != "pending") return std::nullopt;
    const auto& block = state == "passing" ? currentBlock : latestBlock;
    const auto* run = block.back();
    LastPass p;
    p.suite = run->suite();
    p.cls = run->runClass();
    p.key = run->command()->key;
    p.buildOnly = !std::any_of(block.begin(), block.end(), [](const Act* r) { return r->isClass("test") && r->result == RunResult::Pass; });
    return p;
}

TaskResult Evaluation::result() const {
    TaskResult out;
    out.state = state;
    out.score = score;
    if (score) out.shown = roundHalfAway(*score);
    out.dims = dims();
    out.ig = ig;
    out.loop = loop;
    out.osc = osc;
    out.counts = counts();
    out.firstPassMs = firstPassMs();
    out.stallEnds = stallEnds;
    out.vHeld = heldV();
    out.openFailure = openFailureFacts();
    out.lastPass = lastPassFacts();
    return out;
}

double sizeFeature(std::size_t n) { return std::min(1.0, std::log2(1.0 + static_cast<double>(n)) / 9); }

Features snapshotFeatures(const Evaluation& ev) {
    return {{"passing", ev.state == "passing" ? 1.0 : 0.0},
            {"failing", ev.state == "failing" ? 1.0 : 0.0},
            {"conv", ev.C / kCMax},
            {"ig", ev.ig},
            {"loop", ev.loop},
            {"osc", ev.osc},
            {"size", sizeFeature(ev.counted.size())}};
}

Features fixFeatures(const Event* begin, std::size_t runEventIndex, std::optional<std::int64_t> now) {
    const Evaluation ev(begin, begin + runEventIndex + 1, now);
    const auto* run = &ev.acts.back();
    const auto& s = ev.seqOf(run->check);
    std::size_t attempts = 0;
    for (const auto& e : ev.episodes) {
        if (e.check != run->check) continue;
        const auto it = std::find(e.fails.begin(), e.fails.end(), run);
        if (it == e.fails.end()) continue;
        attempts = static_cast<std::size_t>(it - e.fails.begin()) + 1;
        break;
    }
    std::int64_t peak = 0;
    for (const auto* r : s) peak = std::max(peak, r->errors);
    return {{"attempts", std::min(1.0, static_cast<double>(attempts - 1) / 4)},
            {"errors", std::min(1.0, std::log2(1.0 + static_cast<double>(run->errors)) / 8)},
            {"trend", static_cast<double>(run->errors) / static_cast<double>(peak)},
            {"osc", ev.osc},
            {"ig", ev.ig}};
}

}  // namespace agentspeed::tasks::detail
