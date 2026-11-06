#pragma once

#include <cstdint>
#include <functional>
#include <map>
#include <optional>
#include <set>
#include <string>
#include <tuple>
#include <variant>
#include <vector>

#include "agentspeed/history_store.hpp"
#include "agentspeed/perf_history.hpp"
#include "agentspeed/remote_messages.hpp"

// The remote hosts' daily history in the app (REMOTE 8.2, 8.3): the batches of `historyDay` / `historyPerf` lines, the
// sync rules, the stored rows and their merge with the local history.
namespace agentspeed::remote {

// A host's `historyDay` lines, each with the `historyPerf` lines right after it, since its last `historyDone`. Kept
// in memory until it commits, except that a large batch moves its complete days to the staging tables.
class HistoryBatch {
public:
    // Records accepted per day and source (the relay's list limit).
    static constexpr std::size_t kPerfLimit = limits::kHistoryList;

    HistoryBatch(std::string host, int connection) : host_(std::move(host)), connection_(connection) {}

    // A day and source sent again replaces its earlier entry.
    void add(const HistoryDay& line);
    // A record belongs to the day sent right before it; any other is ignored.
    void add(const HistoryPerf& line);
    // Removes and returns every day but the last, which may still get records; they count as staged.
    std::vector<history::RemoteDay> takeCompleted();
    std::vector<history::RemoteDay> takeDays() { return std::move(days_); }

    bool staged() const { return !staged_.empty(); }
    bool exceeds(const history::RemoteLimits& limits) const;
    const std::string& host() const { return host_; }
    int connection() const { return connection_; }
    const std::vector<history::RemoteDay>& days() const { return days_; }
    // The JSON text held, for the memory bound.
    std::size_t byteCount() const { return byteCount_; }
    // The distinct days of the batch, staged ones included, and the records they keep.
    std::int64_t dayCount() const { return dayCount_; }
    std::int64_t perfCount() const { return perfCount_; }

private:
    std::string host_;
    int connection_ = 0;
    std::vector<history::RemoteDay> days_;
    std::set<history::DayKey> keys_;
    std::size_t byteCount_ = 0;
    std::map<history::DayKey, std::int64_t> staged_;
    std::int64_t dayCount_ = 0, perfCount_ = 0;
};

// The sync rules of REMOTE 8.2 and 8.3 over the hosts' events, without storage.
class HistorySyncState {
public:
    static constexpr std::size_t kFlushBytes = 32u << 20;

    struct None {};
    // Answer `historyDue` with the host's stored digests.
    struct Request {
        std::string host;
    };
    // Move these complete days of a large batch to the staging tables; `first`: the batch's first ones.
    struct Stage {
        std::string host;
        std::vector<history::RemoteDay> days;
        bool first = false;
    };
    // `historyDone` of `connection`: commit the staged days (when `staged`) and these.
    struct Commit {
        std::string host;
        int connection = 0;
        std::vector<history::RemoteDay> days;
        bool staged = false;
    };
    // The batch exceeds the bounds: it is discarded with its staged rows, and the rest of it ignored.
    struct Overflow {
        std::string host;
    };
    // `historyDone` of a batch that exceeded the bounds.
    struct TooLarge {
        std::string host;
    };
    using Action = std::variant<None, Request, Stage, Commit, Overflow, TooLarge>;

    explicit HistorySyncState(history::RemoteLimits limits = {}, std::size_t flushBytes = kFlushBytes)
        : limits_(limits), flushBytes_(flushBytes) {}

    Action apply(const HostEvent& event);
    // Forgets a host's batch (its history was deleted).
    void drop(const std::string& host);
    // Forgets every batch (Clear history); the connection states stay.
    void dropBatches();

    const history::RemoteLimits& limits() const { return limits_; }
    // Hosts whose last status is `connected`, with that connection.
    const std::map<std::string, int>& connected() const { return connected_; }
    const std::map<std::string, HistoryBatch>& batches() const { return batches_; }

private:
    Action applyMessage(const RelayMessage& message, const std::string& host, int connection);
    Action keep(HistoryBatch batch, const std::string& host, int connection);

    history::RemoteLimits limits_;
    std::size_t flushBytes_;
    std::map<std::string, HistoryBatch> batches_;
    std::map<std::string, int> overflowed_;
    std::map<std::string, int> connected_;
};

// The stored days of the remote hosts in the form the history publishes. A row whose text no longer decodes is left
// out with its digest, so the next sync sends it again.
class HistoryRows {
public:
    using Peak = std::function<std::optional<double>(const std::string& day)>;

    HistoryRows() = default;
    // Throws when the store cannot be read.
    static HistoryRows load(const history::Store& store);

    bool empty() const { return days_.empty() && perf_.empty(); }
    // "<day>/<source>" → digest of every stored day of `host`.
    std::map<std::string, std::string> digests(const std::string& host) const;
    // The days of `sources` (nullptr: all), each with the day's live peak; no price version (the line carries none).
    std::vector<history::DailyUsage> usage(const std::set<SourceKind>* sources = nullptr, const Peak& peak = {}) const;
    // The records of `sources` (nullptr: all), by host, then day, source and model.
    std::vector<perf::DayRecord> perfRecords(const std::set<SourceKind>* sources = nullptr) const;
    // The committed days of a batch: each replaces its row and records; with `replace`, the host's other rows go.
    void store(const std::vector<history::RemoteDay>& batch, const std::string& host, const std::optional<std::string>& name,
               const std::string& zone, bool replace = false);
    // The hosts with stored rows.
    std::set<std::string> hosts() const;
    void rename(const std::string& host, const std::string& name) { names_[host] = name; }
    void remove(const std::string& host);

    // Host id → its last name, for every host with a stored name.
    const std::map<std::string, std::string>& names() const { return names_; }
    // Host id → the zone its rows were computed in; absent for rows stored before zones.
    const std::map<std::string, std::string>& zones() const { return zones_; }

private:
    using Key = std::tuple<std::string, std::string, SourceKind>;  // host, day, source
    struct Day {
        history::DailyUsage usage;
        std::string digest;
    };
    std::map<std::string, std::string> names_, zones_;
    std::map<Key, Day> days_;
    std::map<Key, std::map<std::string, perf::Record>> perf_;
};

// What a ledger call asks of the history worker.
struct HistoryEffect {
    enum class Kind : std::uint8_t { None, Changed, TooLargeChanged, Request };
    Kind kind = Kind::None;
    // Request: send `history` with these digests to this host.
    std::string host;
    std::map<std::string, std::string> digests;

    static HistoryEffect none() { return {}; }
    static HistoryEffect changed() { return {Kind::Changed, {}, {}}; }
    static HistoryEffect tooLargeChanged() { return {Kind::TooLargeChanged, {}, {}}; }
    static HistoryEffect request(std::string host, std::map<std::string, std::string> digests) {
        return {Kind::Request, std::move(host), std::move(digests)};
    }
    bool operator==(const HistoryEffect&) const = default;
};

// The remote hosts' daily history inside the history worker: the sync state of every host and the stored rows,
// written through the worker's store. Confined to the worker's thread.
class HistoryLedger {
public:
    using Zone = std::function<std::string()>;

    // `zone`: the app's time zone id as the relay gets it (REMOTE 5.3); default `currentZone().id`.
    explicit HistoryLedger(history::RemoteLimits limits = {}, std::size_t flushBytes = HistorySyncState::kFlushBytes, Zone zone = {});

    // Reads the rows of the store (none when they cannot be read) and gives stored hosts their settings names.
    void load(history::Store& store);
    // Clear history: the store was emptied, with every batch in progress.
    void reset();
    HistoryEffect receive(const HostEvent& event, history::Store& store);
    // The settings' hosts with their display names; stored hosts get the new names, removed ones keep theirs.
    HistoryEffect setHosts(std::map<std::string, std::string> names, history::Store* store);
    // Deletes a host's rows and batch (REMOTE 2.4).
    HistoryEffect deleteHost(const std::string& id, history::Store& store);

    // Hosts whose relay is connected, in id order.
    std::vector<std::string> connectedHosts() const;
    const HistoryRows& rows() const { return rows_; }
    // Hosts whose last batch exceeded the bounds (`historyTooLarge`) until a sync succeeds.
    const std::set<std::string>& tooLarge() const { return tooLarge_; }
    // Bumped whenever the rows change.
    std::uint64_t version() const { return version_; }

private:
    HistoryEffect commit(std::vector<history::RemoteDay> days, const std::string& host, int connection, bool staged,
                         history::Store& store);
    HistoryEffect succeeded(const std::string& host);
    bool applyNames(history::Store& store);

    HistorySyncState sync_;
    HistoryRows rows_;
    std::map<std::string, std::string> names_;
    // The zone each host's last request ran in, with its connection.
    std::map<std::string, std::pair<int, std::string>> requested_;
    Zone zone_;
    std::uint64_t version_ = 0;
    std::set<std::string> tooLarge_;
    // Hosts whose current batch could not be staged: it commits nothing, so the next sync sends its days again.
    std::set<std::string> unstaged_;
};

// REMOTE 8.3: local and remote days sorted by day, source id, then host (this computer first, then by id).
std::vector<history::DailyUsage> mergeDays(std::vector<history::DailyUsage> local, std::vector<history::DailyUsage> remote);
// Every host's records pooled per day, source and model (METRICS 6.11), local ones added first.
std::vector<perf::DayRecord> mergePerf(const std::vector<perf::DayRecord>& local, const std::vector<perf::DayRecord>& remote);

}  // namespace agentspeed::remote
