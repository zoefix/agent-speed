#include <algorithm>
#include <map>
#include <set>

#include "agentspeed/quota/estimate/engine.hpp"

// QUOTAS 10.1 Codex history: token count lines, the response each follows, the chains of window instances and their
// accounts, and the plan of every line.
namespace agentspeed::quota::estimate {

void Engine::codex() {
    parsedLines_.clear();
    responseLine_.assign(responses_.size(), std::nullopt);
    std::map<std::string, std::vector<std::size_t>> byFile;
    for (std::size_t i = 0; i < responses_.size(); ++i) {
        const auto& r = responses_[i];
        if (r.source == "codex" && r.machine == kLocal) byFile[r.file].push_back(i);
    }
    for (auto& [_, list] : byFile) {
        std::stable_sort(list.begin(), list.end(), [&](std::size_t a, std::size_t b) { return responses_[a].endMs < responses_[b].endMs; });
    }
    std::vector<std::size_t> order(lines_.size());
    for (std::size_t i = 0; i < order.size(); ++i) order[i] = i;
    std::stable_sort(order.begin(), order.end(), [&](std::size_t a, std::size_t b) {
        return lines_[a].file != lines_[b].file ? lines_[a].file < lines_[b].file : lines_[a].ts < lines_[b].ts;
    });
    for (const auto i : order) {
        Line ln;
        ln.line = lines_[i];
        for (const auto& w : ln.line.windows) {
            if (w.resetsAtMs) ln.windows.push_back(w);
        }
        if (!ln.windows.empty()) parsedLines_.push_back(std::move(ln));
    }
    const auto idOf = [](const Window& w) { return windowId(kindId(w.kind), w.name, w.minutes.value_or(defaultMinutes(w.kind).value_or(0))); };
    std::map<std::string, std::set<std::int64_t>> clusters;
    for (const auto& ln : parsedLines_) {
        for (const auto& w : ln.windows) clusters[idOf(w)].insert(*w.resetsAtMs);
    }
    std::map<std::pair<std::string, std::int64_t>, std::string> tagOf;
    for (const auto& [wid, resets] : clusters) {
        std::optional<std::int64_t> first;
        for (const auto r : resets) {
            if (!first || r - *first > kInstanceJoinMs) first = r;
            tagOf[{wid, r}] = wid + "@" + std::to_string(*first);
        }
    }
    Chains chains;
    for (auto& ln : parsedLines_) {
        for (const auto& w : ln.windows) ln.tags.push_back(tagOf.at({idOf(w), *w.resetsAtMs}));
        chains.link(ln.tags);
    }
    std::vector<bool> used(responses_.size(), false);
    for (std::size_t li = 0; li < parsedLines_.size(); ++li) {
        auto& ln = parsedLines_[li];
        const auto ts = ln.line.ts;
        ln.tLo = ts;
        const auto found = byFile.find(ln.line.file);
        if (found == byFile.end()) continue;
        const auto& list = found->second;
        auto it = std::lower_bound(list.begin(), list.end(), ts - kLineMatchMs, [&](std::size_t r, std::int64_t v) { return responses_[r].endMs < v; });
        for (; it != list.end() && responses_[*it].endMs <= ts; ++it) {
            if (used[*it]) continue;
            used[*it] = true;
            responseLine_[*it] = li;
            const auto& start = responses_[*it].startMs;
            ln.tLo = std::min(ts, start && *start ? *start : ts);
            break;
        }
    }
    std::vector<std::size_t> byTs(parsedLines_.size());
    for (std::size_t i = 0; i < byTs.size(); ++i) byTs[i] = i;
    std::stable_sort(byTs.begin(), byTs.end(), [&](std::size_t a, std::size_t b) { return parsedLines_[a].line.ts < parsedLines_[b].line.ts; });
    const auto& codexTl = tl_["codex"];
    std::map<std::string, std::string> keyed;
    for (const auto i : byTs) {
        const auto& ln = parsedLines_[i];
        const auto* e = inForce(codexTl, ln.line.ts);
        if (e && ln.line.ts >= codexTl.front().at) keyed[chains.find(ln.tags.front())] = e->key;
    }
    std::map<std::string, std::string> smallestTag;
    std::set<std::string> tags;
    for (const auto& [_, tag] : tagOf) tags.insert(tag);
    for (const auto& tag : tags) smallestTag.try_emplace(chains.find(tag), tag);
    for (auto& ln : parsedLines_) {
        const auto root = chains.find(ln.tags.front());
        const auto key = keyed.find(root);
        ln.account = key != keyed.end() && !key->second.empty() ? key->second : accountKey(salt_, "codex-chain", smallestTag.at(root));
    }
    std::map<std::string, std::vector<std::pair<std::int64_t, std::string>>> plans;
    for (const auto i : byTs) {
        auto& ln = parsedLines_[i];
        ln.plan = ln.line.plan;
        if (ln.plan) plans[ln.account].emplace_back(ln.line.ts, *ln.plan);
    }
    for (auto& ln : parsedLines_) {
        if (ln.plan) continue;
        const auto known = plans.find(ln.account);
        if (known == plans.end() || known->second.empty()) continue;
        std::optional<std::string> before;
        for (const auto& [t, p] : known->second) {
            if (t <= ln.line.ts) before = p;
        }
        ln.plan = before ? before : known->second.front().second;
    }
}

}  // namespace agentspeed::quota::estimate
