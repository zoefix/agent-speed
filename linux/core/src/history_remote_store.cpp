#include <set>

#include "agentspeed/history_store.hpp"

// REMOTE 8.3: the remote hosts' rows in side tables of the history store. `data` is the aggregate's or the record's
// exact JSON text as the relay sent it. A large batch waits in the connection's temporary staging tables until it
// commits, so nothing of it reaches the store before its `historyDone`.
namespace agentspeed::history {

namespace {

constexpr const char* kRemoteTables =
    "CREATE TABLE IF NOT EXISTS remote_hosts (id TEXT PRIMARY KEY, name TEXT, zone TEXT);"
    "CREATE TABLE IF NOT EXISTS remote_days (host TEXT, day TEXT, source TEXT, data TEXT, digest TEXT,"
    " PRIMARY KEY (host, day, source));"
    "CREATE TABLE IF NOT EXISTS remote_perf (host TEXT, day TEXT, source TEXT, model TEXT, data TEXT,"
    " PRIMARY KEY (host, day, source, model));"
    "CREATE TEMP TABLE IF NOT EXISTS remote_stage_days (host TEXT, day TEXT, source TEXT, data TEXT, digest TEXT,"
    " PRIMARY KEY (host, day, source));"
    "CREATE TEMP TABLE IF NOT EXISTS remote_stage_perf (host TEXT, day TEXT, source TEXT, model TEXT, data TEXT,"
    " PRIMARY KEY (host, day, source, model));";

// BEGIN IMMEDIATE … COMMIT, rolled back when the scope ends without `commit`.
class Transaction {
public:
    explicit Transaction(const sqlite::Connection& db) : db_(db) { db_.execute("BEGIN IMMEDIATE"); }
    ~Transaction() {
        if (open_) {
            try {
                db_.execute("ROLLBACK");
            } catch (...) {
            }
        }
    }
    Transaction(const Transaction&) = delete;
    Transaction& operator=(const Transaction&) = delete;
    void commit() {
        db_.execute("COMMIT");
        open_ = false;
    }

private:
    const sqlite::Connection& db_;
    bool open_ = true;
};

void runForHost(const sqlite::Connection& db, const std::string& sql, const std::string& host) {
    auto statement = db.prepare(sql);
    statement.bind(1, std::string_view(host));
    statement.step();
}

std::int64_t countForHost(const sqlite::Connection& db, const char* table, const std::string& host) {
    auto statement = db.prepare(std::string("SELECT count(*) FROM ") + table + " WHERE host = ?1");
    statement.bind(1, std::string_view(host));
    return statement.step() ? statement.getInt64(0) : 0;
}

// Each day replaces its row and its performance records in the given tables.
void write(const sqlite::Connection& db, const std::vector<RemoteDay>& batch, const std::string& host, const char* days, const char* perf) {
    if (batch.empty()) return;
    auto upsertDay = db.prepare(std::string("INSERT OR REPLACE INTO ") + days + " (host, day, source, data, digest) VALUES (?1, ?2, ?3, ?4, ?5)");
    auto clearPerf = db.prepare(std::string("DELETE FROM ") + perf + " WHERE host = ?1 AND day = ?2 AND source = ?3");
    auto insertPerf = db.prepare(std::string("INSERT OR REPLACE INTO ") + perf + " (host, day, source, model, data) VALUES (?1, ?2, ?3, ?4, ?5)");
    for (const auto& day : batch) {
        const auto source = sourceId(day.source);
        upsertDay.reset();
        upsertDay.bind(1, std::string_view(host)).bind(2, std::string_view(day.day)).bind(3, source);
        upsertDay.bind(4, std::string_view(day.aggregateJson)).bind(5, std::string_view(day.digest));
        upsertDay.step();
        clearPerf.reset();
        clearPerf.bind(1, std::string_view(host)).bind(2, std::string_view(day.day)).bind(3, source);
        clearPerf.step();
        for (const auto& record : day.perf) {
            insertPerf.reset();
            insertPerf.bind(1, std::string_view(host)).bind(2, std::string_view(day.day)).bind(3, source);
            insertPerf.bind(4, std::string_view(record.model)).bind(5, std::string_view(record.json));
            insertPerf.step();
        }
    }
}

}  // namespace

std::size_t RemoteDay::byteCount() const {
    std::size_t total = aggregateJson.size();
    for (const auto& record : perf) total += record.byteCount();
    return total;
}

void Store::ensureRemoteTables() {
    db_.execute(kRemoteTables);
    std::set<std::string> columns;
    auto info = db_.prepare("PRAGMA table_info(remote_hosts)");
    while (info.step()) {
        if (const auto name = info.getString(1)) columns.insert(*name);
    }
    if (!columns.contains("zone")) db_.execute("ALTER TABLE remote_hosts ADD COLUMN zone TEXT");
}

std::map<std::string, std::optional<std::string>> Store::remoteHostNames() const {
    std::map<std::string, std::optional<std::string>> names;
    auto rows = db_.prepare("SELECT id, name FROM remote_hosts");
    while (rows.step()) {
        if (const auto id = rows.getString(0)) names[*id] = rows.getString(1);
    }
    return names;
}

std::map<std::string, std::string> Store::remoteHostZones() const {
    std::map<std::string, std::string> zones;
    auto rows = db_.prepare("SELECT id, zone FROM remote_hosts WHERE zone IS NOT NULL");
    while (rows.step()) {
        const auto id = rows.getString(0), zone = rows.getString(1);
        if (id && zone) zones[*id] = *zone;
    }
    return zones;
}

std::vector<RemoteDayRow> Store::remoteDayRows() const {
    std::vector<RemoteDayRow> out;
    auto rows = db_.prepare("SELECT host, day, source, data, digest FROM remote_days ORDER BY host, day, source");
    while (rows.step()) {
        const auto host = rows.getString(0), day = rows.getString(1), source = rows.getString(2), data = rows.getString(3),
                   digest = rows.getString(4);
        if (host && day && source && data && digest) out.push_back({*host, *day, *source, *data, *digest});
    }
    return out;
}

std::vector<RemotePerfRow> Store::remotePerfRows() const {
    std::vector<RemotePerfRow> out;
    auto rows = db_.prepare("SELECT host, day, source, model, data FROM remote_perf ORDER BY host, day, source, model");
    while (rows.step()) {
        const auto host = rows.getString(0), day = rows.getString(1), source = rows.getString(2), model = rows.getString(3),
                   data = rows.getString(4);
        if (host && day && source && model && data) out.push_back({*host, *day, *source, *model, *data});
    }
    return out;
}

void Store::stageRemote(const std::vector<RemoteDay>& days, const std::string& host, bool first) {
    Transaction transaction(db_);
    if (first) {
        runForHost(db_, "DELETE FROM temp.remote_stage_perf WHERE host = ?1", host);
        runForHost(db_, "DELETE FROM temp.remote_stage_days WHERE host = ?1", host);
    }
    write(db_, days, host, "temp.remote_stage_days", "temp.remote_stage_perf");
    transaction.commit();
}

void Store::clearRemoteStaging(const std::string& host) {
    runForHost(db_, "DELETE FROM temp.remote_stage_perf WHERE host = ?1", host);
    runForHost(db_, "DELETE FROM temp.remote_stage_days WHERE host = ?1", host);
}

void Store::commitRemote(const std::vector<RemoteDay>& days, const std::string& host, const std::optional<std::string>& name,
                         const std::string& zone, bool replace, bool staged, const RemoteLimits& limits) {
    struct ClearStaging {
        Store& store;
        const std::string& host;
        ~ClearStaging() {
            try {
                store.clearRemoteStaging(host);
            } catch (...) {
            }
        }
    } clearStaging{*this, host};
    Transaction transaction(db_);
    if (replace) {
        runForHost(db_, "DELETE FROM remote_perf WHERE host = ?1", host);
        runForHost(db_, "DELETE FROM remote_days WHERE host = ?1", host);
    }
    if (staged) {
        runForHost(db_,
                   "DELETE FROM remote_perf WHERE host = ?1 AND (day, source) IN"
                   " (SELECT day, source FROM temp.remote_stage_days WHERE host = ?1)",
                   host);
        runForHost(db_, "INSERT OR REPLACE INTO remote_days SELECT * FROM temp.remote_stage_days WHERE host = ?1", host);
        runForHost(db_, "INSERT OR REPLACE INTO remote_perf SELECT * FROM temp.remote_stage_perf WHERE host = ?1", host);
    }
    write(db_, days, host, "remote_days", "remote_perf");
    const auto dayRows = countForHost(db_, "remote_days", host);
    const auto perfRows = countForHost(db_, "remote_perf", host);
    if (dayRows > limits.days || perfRows > limits.perf) throw RemoteTooLarge();
    if (dayRows == 0 && perfRows == 0) {
        runForHost(db_, "DELETE FROM remote_hosts WHERE id = ?1", host);
    } else {
        auto upsert = db_.prepare(
            "INSERT INTO remote_hosts (id, name, zone) VALUES (?1, ?2, ?3)"
            " ON CONFLICT(id) DO UPDATE SET name = coalesce(excluded.name, remote_hosts.name), zone = excluded.zone");
        upsert.bind(1, std::string_view(host));
        if (name) {
            upsert.bind(2, std::string_view(*name));
        } else {
            upsert.bindNull(2);
        }
        upsert.bind(3, std::string_view(zone));
        upsert.step();
    }
    transaction.commit();
}

void Store::renameRemoteHost(const std::string& id, const std::string& name) {
    auto statement = db_.prepare("UPDATE remote_hosts SET name = ?2 WHERE id = ?1");
    statement.bind(1, std::string_view(id)).bind(2, std::string_view(name));
    statement.step();
}

void Store::deleteRemoteHost(const std::string& id) {
    clearRemoteStaging(id);
    Transaction transaction(db_);
    runForHost(db_, "DELETE FROM remote_perf WHERE host = ?1", id);
    runForHost(db_, "DELETE FROM remote_days WHERE host = ?1", id);
    runForHost(db_, "DELETE FROM remote_hosts WHERE id = ?1", id);
    transaction.commit();
}

}  // namespace agentspeed::history
