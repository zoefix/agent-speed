#include "agentspeed/quota/estimate/window_feed.hpp"

namespace agentspeed::quota::estimate {

bool feedsByWindow(std::string_view source) { return source == sourceId(SourceKind::ClaudeCode); }

std::vector<SnapshotEvent> WindowFeed::take(const SnapshotEvent& snapshot) {
    std::map<std::int64_t, std::vector<Window>> groups;
    for (const auto& w : snapshot.windows) {
        const auto t = w.observedMs.value_or(snapshot.observedMs);
        const Key key{snapshot.source, snapshot.machine, w.kind, w.name.value_or(""), w.minutes};
        const auto [it, inserted] = last_.try_emplace(key, t);
        if (!inserted) {
            if (t <= it->second) continue;
            it->second = t;
        }
        auto plain = w;
        plain.observedMs.reset();
        groups[t].push_back(std::move(plain));
    }
    std::vector<SnapshotEvent> out;
    for (auto& [t, windows] : groups) {
        SnapshotEvent event = snapshot;
        event.observedMs = t;
        event.windows = std::move(windows);
        out.push_back(std::move(event));
    }
    return out;
}

}  // namespace agentspeed::quota::estimate
