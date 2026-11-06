#pragma once

#include <map>
#include <memory>
#include <optional>
#include <set>
#include <stdexcept>
#include <string>
#include <vector>

#include "agentspeed/history.hpp"
#include "agentspeed/sqlite.hpp"
#include "agentspeed/perf_history.hpp"
#include "agentspeed/traffic.hpp"

// The persistent daily history (METRICS 8): per-file identities, owned key hashes and per-day contributions, plus the
// peaks observed live, the price bindings (9.7) and the table copies they reference.
namespace agentspeed::history {

// REMOTE 8.3: one day and source of a remote host's history as its relay sent it, with the exact texts the store keeps
// (`remote_days.data`, `remote_perf.data`) so the digest stays verifiable.
struct RemotePerf {
    std::string model;  // "" when unknown
    std::string json;
    perf::Record record;
    std::size_t byteCount() const { return model.size() + json.size(); }
};
struct RemoteDay {
    std::string day;
    SourceKind source = SourceKind::Codex;
    DayAggregate aggregate;
    std::string aggregateJson;
    std::string digest;
    // By model, each model once.
    std::vector<RemotePerf> perf;
    DayKey key() const { return {day, source}; }
    std::size_t byteCount() const;
};
struct RemoteDayRow {
    std::string host, day, source, data, digest;
    bool operator==(const RemoteDayRow&) const = default;
};
struct RemotePerfRow {
    std::string host, day, source, model, data;
    bool operator==(const RemotePerfRow&) const = default;
};
// The bounds of one host's stored rows (REMOTE 8.3 Bounds).
struct RemoteLimits {
    std::int64_t days = 100'000;
    std::int64_t perf = 1'000'000;
};
// A committed batch would leave the host with more rows than the bounds allow; nothing was changed.
class RemoteTooLarge : public std::runtime_error {
public:
    RemoteTooLarge() : std::runtime_error("remote history too large") {}
};

struct StoredFile {
    std::int64_t id = 0;
    std::string path;
    std::string family;
    std::int64_t size = 0;
    std::int64_t modified = 0;
    int version = 0;
    Contributions days;
    // METRICS 6.11: the performance records of the file's agents per day, source and raw model, current version only.
    std::vector<perf::DayRecord> perf;
    bool hasUnpriced() const;
};

class Store {
public:
    static constexpr int kSchema = 1;

    // Creates the store when missing; a store of another schema starts over.
    static std::unique_ptr<Store> open(const std::string& path);

    std::map<std::string, StoredFile> loadFiles() const;
    // Inserts or replaces each file with its owned key hashes and contributions; assigns new ids.
    void save(std::vector<std::pair<StoredFile*, std::vector<std::uint64_t>>>& files);
    // Settles the key hashes of every stored file except `except` (their files are analysed again).
    void loadKeys(KeyLedger& ledger, const std::set<std::int64_t>& except) const;
    void loadKeys(KeyLedger& ledger, std::int64_t fileId) const;

    std::map<std::string, double> peaks() const;
    void savePeaks(const std::map<std::string, double>& peaks);
    std::map<std::string, int> priceBindings() const;
    std::map<int, std::string> priceTables() const;
    void savePriceBindings(const std::map<std::string, int>& bindings, const std::function<std::string(int)>& tableData);

    // METRICS 10.9: today's traffic rows (upserted) and every stored row; `trafficSince` is the first day saved.
    void saveTraffic(const std::vector<traffic::HistoryRow>& rows);
    std::vector<traffic::HistoryRow> trafficRows() const;

    std::optional<std::string> meta(const std::string& key) const;
    void setMeta(const std::string& key, const std::string& value);
    // "Clear history": every table emptied, the remote side tables included.
    void clear();

    // REMOTE 8.3: the side tables of the remote hosts' history (created by the app, no schema change; the relay's own
    // store never has them) and the connection's temporary staging tables of a large batch.
    void ensureRemoteTables();
    // id → name (nullopt when stored without one).
    std::map<std::string, std::optional<std::string>> remoteHostNames() const;
    // id → the zone its rows were computed in, for every host stored with one.
    std::map<std::string, std::string> remoteHostZones() const;
    std::vector<RemoteDayRow> remoteDayRows() const;
    std::vector<RemotePerfRow> remotePerfRows() const;
    // Moves complete days of a large batch to the staging tables; `first` clears what a dropped batch left there.
    void stageRemote(const std::vector<RemoteDay>& days, const std::string& host, bool first);
    void clearRemoteStaging(const std::string& host);
    // One transaction: the staged days (when `staged`), then `days`, each replacing its row and its records; with
    // `replace`, the host's other rows go first. The host keeps `name` (or its stored name) and takes `zone`; a host left
    // without rows is removed from `remote_hosts`. Beyond `limits` throws RemoteTooLarge and nothing changes. The
    // staging rows go either way.
    void commitRemote(const std::vector<RemoteDay>& days, const std::string& host, const std::optional<std::string>& name,
                      const std::string& zone, bool replace, bool staged, const RemoteLimits& limits);
    // Sets a stored host's name; a host without rows stays absent.
    void renameRemoteHost(const std::string& id, const std::string& name);
    // Every row of a host (REMOTE 2.4).
    void deleteRemoteHost(const std::string& id);
    // The connection (tests, diagnostics).
    const sqlite::Connection& connection() const { return db_; }

private:
    explicit Store(sqlite::Connection connection) : db_(std::move(connection)) {}
    void createSchema();

    sqlite::Connection db_;
};

// Compact JSON of one contribution and back (the `data` column).
std::string encode(const DayAggregate& day);
std::optional<DayAggregate> decode(std::string_view text);

}  // namespace agentspeed::history
