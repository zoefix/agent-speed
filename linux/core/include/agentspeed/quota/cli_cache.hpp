#pragma once

#include <cstddef>
#include <cstdint>
#include <optional>
#include <string>
#include <string_view>
#include <utility>

#include "agentspeed/quota/files.hpp"
#include "agentspeed/quota/window.hpp"

// QUOTAS 5.6: Claude Code's own usage cache, the second route of the `claudeCode` snapshot. Claude Code keeps the last
// claude.ai usage response it fetched in its global config file (`claudeAccountFile()`):
//
//   {"cachedUsageUtilization": {"fetchedAtMs": 1794635153747, "accountUuid": "<uuid>", "utilization": {<Desktop's body>}},
//    "oauthAccount": {"accountUuid": "<uuid>", …}, …}
//
// Only those two keys are looked at; the uuids are compared in memory and never kept. Shared by the app and the relay.
namespace agentspeed::quota::cli {

// One read of the file's bytes: the cache part (`observedMs` = fetchedAtMs, the windows of QUOTAS 4.3), or none;
// `parsed` is false when the bytes are not JSON (the previous value then stays).
struct CacheRead {
    bool parsed = false;
    std::optional<Snapshot> part;
};
CacheRead readCache(std::string_view bytes);

// The file with QUOTAS 6's cadence: at most once a minute, and again when its size or modification time changes; a file
// that does not parse is read again at the next check. The bytes are wiped after each read. Not thread-safe.
class CacheReader {
public:
    static constexpr std::int64_t kRereadAfterMs = 60'000;
    static constexpr std::size_t kFileLimit = 64u << 20;

    explicit CacheReader(std::string file) : file_(std::move(file)) {}

    // `enabled` false (Claude Code disabled in Data sources, or its quota switch off): nothing is read, no cache.
    // Returns true when the cache part changed.
    bool refresh(std::int64_t nowMs, bool enabled);
    const std::optional<Snapshot>& snapshot() const { return snapshot_; }
    int reads() const { return reads_; }

private:
    bool clear();

    std::string file_;
    std::optional<files::Stamp> stamp_;
    std::optional<std::int64_t> readMs_;
    std::optional<Snapshot> snapshot_;
    int reads_ = 0;
};

// QUOTAS 5.6 Joining the routes: the bridge's snapshot (5.5 Reader) and the cache part, window by window (kind and
// name) with the helper's rule, the bridge's value being the stored one. Each joined window carries its own
// `observedMs`, and the snapshot's is the newest of them. With one part, that part unchanged.
std::optional<Snapshot> joined(const std::optional<Snapshot>& bridge, const std::optional<Snapshot>& cache);

}  // namespace agentspeed::quota::cli
