#include <algorithm>
#include <climits>
#include <set>

#include "agentspeed/quota/cli_cache.hpp"

// QUOTAS 5.6 Joining the routes.
namespace agentspeed::quota::cli {

namespace {

constexpr std::int64_t kSameWindowGapMs = 60'000;

using Identity = std::pair<Kind, std::optional<std::string>>;
Identity identityOf(const Window& w) { return {w.kind, w.name}; }

// `later − earlier`, saturated.
std::int64_t gap(std::int64_t later, std::int64_t earlier) {
    std::int64_t d = 0;
    if (!__builtin_sub_overflow(later, earlier, &d)) return d;
    return later > earlier ? INT64_MAX : INT64_MIN;
}

// Resets within 60 s of each other, or both unknown.
bool sameWindow(const Window& a, const Window& b) {
    if (!a.resetsAtMs || !b.resetsAtMs) return !a.resetsAtMs && !b.resetsAtMs;
    const auto d = gap(*b.resetsAtMs, *a.resetsAtMs);
    return d >= -kSameWindowGapMs && d <= kSameWindowGapMs;
}

// `next` resets more than 60 s after `stored` (an unknown reset is earlier than any time).
bool newerPeriod(const Window& stored, const Window& next) {
    return next.resetsAtMs && (!stored.resetsAtMs || gap(*next.resetsAtMs, *stored.resetsAtMs) > kSameWindowGapMs);
}

Window timed(Window w, std::int64_t observedMs) {
    w.observedMs = observedMs;
    return w;
}

}  // namespace

std::optional<Snapshot> joined(const std::optional<Snapshot>& bridge, const std::optional<Snapshot>& cache) {
    if (!bridge || !cache) return bridge ? bridge : cache;
    const auto tb = bridge->observedMs;
    const auto tc = cache->observedMs;
    std::vector<Window> windows;
    std::set<Identity> taken;
    for (const auto& w : bridge->windows) {
        const auto id = identityOf(w);
        const auto match = std::find_if(cache->windows.rbegin(), cache->windows.rend(), [&](const Window& c) { return identityOf(c) == id; });
        if (match == cache->windows.rend()) {
            windows.push_back(timed(w, tb));
            continue;
        }
        taken.insert(id);
        const auto& c = *match;
        if (sameWindow(w, c)) {
            // Usage only grows: the higher value holds at the later time too.
            windows.push_back(timed(c.usedPercent > w.usedPercent ? c : w, std::max(tb, tc)));
        } else if (newerPeriod(w, c)) {
            windows.push_back(timed(c, tc));
        } else {
            windows.push_back(timed(w, tb));
        }
    }
    for (const auto& c : cache->windows) {
        if (!taken.contains(identityOf(c))) windows.push_back(timed(c, tc));
    }
    Snapshot out;
    out.source = SourceKind::ClaudeCode;
    out.observedMs = std::max(tb, tc);
    if (!windows.empty()) {
        out.observedMs = *windows.front().observedMs;
        for (const auto& w : windows) out.observedMs = std::max(out.observedMs, *w.observedMs);
    }
    out.windows = ordered(windows);
    return out;
}

}  // namespace agentspeed::quota::cli
