#include "agentspeed/work_signals.hpp"

#include <algorithm>
#include <set>
#include <vector>

namespace agentspeed::work {

namespace {

constexpr std::int64_t kRecentMs = 600'000;
constexpr std::int64_t kFreshMs = 120'000;
constexpr std::int64_t kLoopRepeats = 3;
constexpr std::size_t kNoveltyMin = 8;
constexpr std::size_t kRecoveryWindow = 5;

using Key = std::pair<std::string, std::string>;
Key keyOf(const Action& a) { return {a.tool, a.digest}; }

// How many times the newest block of `size` actions repeats back to back, and where the run starts.
std::pair<std::int64_t, std::size_t> repeatRun(const std::vector<const Action*>& actions, std::size_t size) {
    if (actions.size() < size) return {0, actions.size()};
    const auto same = [&](std::size_t a, std::size_t b) {
        for (std::size_t k = 0; k < size; ++k) {
            if (keyOf(*actions[a + k]) != keyOf(*actions[b + k])) return false;
        }
        return true;
    };
    std::int64_t count = 1;
    std::size_t end = actions.size() - size;
    while (end >= size && same(end - size, actions.size() - size)) {
        ++count;
        end -= size;
    }
    return {count, end};
}

}  // namespace

Signals signalsOf(const std::deque<Action>& actions, std::int64_t now) {
    std::vector<const Action*> recent;
    for (const auto& a : actions) {
        if (!a.seeded && a.callMs >= now - kRecentMs) recent.push_back(&a);
    }
    std::int64_t looping = 0, repeating = 0;
    if (!recent.empty() && now - recent.back()->callMs < kFreshMs) {
        for (std::size_t size = 1; size <= 3; ++size) {
            const auto [count, start] = repeatRun(recent, size);
            if (count < kLoopRepeats) continue;
            const bool failure = std::any_of(recent.begin() + static_cast<std::ptrdiff_t>(start), recent.end(),
                                             [](const Action* a) { return a->failed == true; });
            (failure ? looping : repeating) = std::max(failure ? looping : repeating, count);
        }
    }
    Signals out;
    for (std::size_t i = 0; i < recent.size(); ++i) {
        if (recent[i]->failed != true) continue;
        ++out.failed;
        const auto last = std::min(recent.size(), i + 1 + kRecoveryWindow);
        for (std::size_t k = i + 1; k < last; ++k) {
            const auto* b = recent[k];
            if (b->tool == recent[i]->tool && b->failed == false && b->callMs - recent[i]->callMs <= kRecentMs) {
                ++out.recovered;
                break;
            }
        }
    }
    if (recent.size() >= kNoveltyMin) {
        std::set<Key> distinct;
        for (const auto* a : recent) distinct.insert(keyOf(*a));
        out.novelty = static_cast<double>(distinct.size()) / static_cast<double>(recent.size());
    }
    if (looping) out.looping = looping;
    if (!looping && repeating) out.repeating = repeating;
    return out;
}

}  // namespace agentspeed::work
