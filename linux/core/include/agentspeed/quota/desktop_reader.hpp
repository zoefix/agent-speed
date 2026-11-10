#pragma once

#include <cstdint>
#include <map>
#include <optional>
#include <string>
#include <utility>
#include <vector>

#include "agentspeed/quota/desktop_entry.hpp"
#include "agentspeed/quota/files.hpp"
#include "agentspeed/quota/window.hpp"

// QUOTAS 4.4: the account's limits from the usage responses Claude Desktop caches, read-only. The reader remembers the
// usage entries it found (candidates) and checks them with one stat each every 60 s; a search lists the folder and reads
// the first bytes of the newest `_0` files until it meets a usage entry. Not thread-safe: one thread drives it.
namespace agentspeed::quota::desktop {

class Reader {
public:
    static constexpr std::int64_t kCheckIntervalMs = 60'000;
    static constexpr std::int64_t kSearchIntervalMs = 30 * 60'000;
    // While Desktop runs, a newest candidate older than this asks for a search: Desktop may have changed its query or
    // organization.
    static constexpr std::int64_t kStaleCandidateMs = 20 * 60'000;
    static constexpr std::int64_t kMaxEntryAgeMs = 7LL * 24 * 3'600'000;
    static constexpr int kMaxHeaderReads = 4000;
    static constexpr std::uint32_t kMaxKeyBytes = 8192;

    // `folder`: QUOTAS 4.1's cache folder (desktopCacheFolder()).
    explicit Reader(std::string folder) : folder_(std::move(folder)) {}

    struct Tick {
        std::int64_t nowMs = 0;
        // Claude Desktop enabled in Data sources and `quotaClaudeDesktop` on. False: nothing is listed, stat'ed or read,
        // everything is forgotten, and the next enabled tick searches as at start.
        bool enabled = true;
        // Claude Desktop's main process runs (QUOTAS 6), and it appeared since the last tick.
        bool desktopRunning = false;
        bool desktopStarted = false;
    };
    // Runs what is due: a search (at start while the folder exists, when Desktop starts, at most every 30 min while it
    // runs with a stale newest candidate), then the candidate check every 60 s (and after a search). Call it as often
    // as convenient, e.g. every few seconds; it does nothing until something is due. True when the snapshot changed.
    bool tick(const Tick& tick);

    // The newest observation among the candidates (ties: the newer file, then the file name); its windows may be empty.
    const std::optional<Snapshot>& snapshot() const { return snapshot_; }
    // The file the snapshot came from.
    const std::optional<std::string>& snapshotFile() const { return snapshotFile_; }
    std::vector<std::string> candidates() const;
    const std::string& folder() const { return folder_; }

    // For tests and diagnostics.
    int lastSearchReads() const { return lastSearchReads_; }
    int searches() const { return searches_; }
    int checks() const { return checks_; }

private:
    struct Candidate {
        std::optional<files::Stamp> stamp;
        std::optional<UsageEntry> entry;
    };
    struct Listed {
        std::string name;
        std::int64_t mtimeMs = 0;
    };

    bool searchDue(const Tick& tick) const;
    // False when the folder could not be listed.
    bool search(std::int64_t nowMs);
    void check();
    void choose();
    std::optional<std::vector<Listed>> listEntries() const;
    bool isUsageHeader(const std::string& name) const;
    void forget();

    std::string folder_;
    std::map<std::string, Candidate> candidates_;
    std::optional<Snapshot> snapshot_;
    std::optional<std::string> snapshotFile_;
    bool searchWanted_ = true;
    std::optional<std::int64_t> searchedMs_;
    std::optional<std::int64_t> checkedMs_;
    // A wanted search whose folder was missing is tried again no sooner than the next check.
    std::optional<std::int64_t> missingFolderMs_;
    int lastSearchReads_ = 0;
    int searches_ = 0;
    int checks_ = 0;
};

}  // namespace agentspeed::quota::desktop
