#pragma once

#include <cstdint>
#include <functional>
#include <mutex>
#include <optional>
#include <vector>

#include "agentspeed/json.hpp"
#include "agentspeed/quota/window.hpp"

// QUOTAS 3: the account's limits that Codex writes into its rollouts with every token count. The Codex parser passes
// each `token_count` line's `payload.rate_limits` object (copied history excluded) to an observer; the collector keeps
// the newest snapshot across every Codex location.
namespace agentspeed::quota {

// The windows of one `rate_limits` object: `limit_id` `"codex"`, null or absent; `primary` then `secondary`, each an
// object with a numeric `used_percent` and `window_minutes` above 0; one window per kind (the first). `lineMs` is the
// line's timestamp, which an older Codex's `resets_in_seconds` counts from.
std::vector<Window> codexWindows(const json::Value& rateLimits, std::int64_t lineMs);

// Thread-safe: the monitor thread feeds it, any thread reads it.
class CodexCollector {
public:
    using Observer = std::function<void(const json::Value& rateLimits, std::int64_t timestampMs)>;

    // One line's `rate_limits`: a line with windows and a newer timestamp replaces the snapshot; one without replaces
    // nothing. Ignored while disabled.
    void observe(const json::Value& rateLimits, std::int64_t timestampMs);
    // For Monitor::setRateLimitObserver; the collector must outlive the monitor's parsers.
    Observer observer();

    std::optional<Snapshot> snapshot() const;
    // Counts every change of the snapshot, so a reader can tell it has news.
    std::uint64_t version() const;
    // QUOTAS 9.1: Codex disabled in Data sources (or its switch off) reads no quota: the snapshot goes and observations
    // are dropped until it is enabled again.
    void setEnabled(bool enabled);
    void clear();

private:
    mutable std::mutex mutex_;
    std::optional<Snapshot> snapshot_;
    std::uint64_t version_ = 0;
    bool enabled_ = true;
};

}  // namespace agentspeed::quota
