#include "agentspeed/history_store.hpp"

#include <array>
#include <filesystem>

#include "agentspeed/json.hpp"

namespace agentspeed::history {

namespace {

std::string encodeBins(const std::array<std::int64_t, perf::kBins>& bins) {
    std::string out = "[";
    for (std::size_t k = 0; k < bins.size(); ++k) {
        if (k) out += ",";
        out += std::to_string(bins[k]);
    }
    return out + "]";
}

std::array<std::int64_t, perf::kBins> decodeBins(std::string_view text) {
    std::array<std::int64_t, perf::kBins> bins{};
    json::Document doc;
    if (!doc.parse(text) || !doc.root().isArray()) return bins;
    std::size_t k = 0;
    for (const auto& cell : doc.root().elements()) {
        if (k >= bins.size()) break;
        bins[k++] = cell.truncated().value_or(0);
    }
    return bins;
}


void appendString(std::string& out, std::string_view text) {
    out += '"';
    for (const unsigned char c : text) {
        switch (c) {
            case '"': out += "\\\""; break;
            case '\\': out += "\\\\"; break;
            case '\n': out += "\\n"; break;
            case '\r': out += "\\r"; break;
            case '\t': out += "\\t"; break;
            default:
                if (c < 0x20) {
                    char buffer[8];
                    std::snprintf(buffer, sizeof buffer, "\\u%04x", c);
                    out += buffer;
                } else {
                    out += static_cast<char>(c);
                }
        }
    }
    out += '"';
}

void appendOptional(std::string& out, const std::optional<std::int64_t>& value) {
    out += value ? std::to_string(*value) : std::string("null");
}

std::optional<std::int64_t> optionalOf(const json::Value& v) {
    if (!v.valid() || v.isNull()) return std::nullopt;
    return v.truncated();
}

std::string packHashes(const std::vector<std::uint64_t>& hashes) {
    std::string out(hashes.size() * 8, '\0');
    for (std::size_t i = 0; i < hashes.size(); ++i) {
        for (int b = 0; b < 8; ++b) out[i * 8 + static_cast<std::size_t>(b)] = static_cast<char>((hashes[i] >> (8 * b)) & 0xFF);
    }
    return out;
}

std::vector<std::uint64_t> unpackHashes(std::string_view blob) {
    std::vector<std::uint64_t> out(blob.size() / 8);
    for (std::size_t i = 0; i < out.size(); ++i) {
        std::uint64_t v = 0;
        for (int b = 7; b >= 0; --b) v = (v << 8) | static_cast<unsigned char>(blob[i * 8 + static_cast<std::size_t>(b)]);
        out[i] = v;
    }
    return out;
}

}  // namespace

bool StoredFile::hasUnpriced() const {
    for (const auto& [key, day] : days) {
        if (day.unpricedRequests.value_or(0) > 0) return true;
    }
    return false;
}

std::string encode(const DayAggregate& d) {
    std::string out = "{";
    const auto field = [&](const char* name, std::int64_t value) {
        out += '"';
        out += name;
        out += "\":" + std::to_string(value) + ",";
    };
    field("requests", d.requests);
    field("input", d.input);
    field("cachedInput", d.cachedInput);
    field("cacheWriteInput", d.cacheWriteInput);
    field("output", d.output);
    field("reasoning", d.reasoning);
    field("measuredOutput", d.measuredOutput);
    field("measuredMs", d.measuredMs);
    field("unfinalized", d.unfinalized);
    field("agents", d.agents);
    out += "\"costMicroUsd\":";
    appendOptional(out, d.costMicroUsd);
    out += ",\"unpricedRequests\":";
    appendOptional(out, d.unpricedRequests);
    out += ",\"models\":{";
    bool first = true;
    for (const auto& [name, m] : d.models) {
        if (!first) out += ',';
        first = false;
        appendString(out, name);
        out += ":[" + std::to_string(m.requests) + "," + std::to_string(m.output) + "," + std::to_string(m.measuredOutput) + "," +
               std::to_string(m.measuredMs) + ",";
        appendOptional(out, m.costMicroUsd);
        out += ',';
        appendOptional(out, m.unpricedRequests);
        out += ']';
    }
    out += "},\"unpriced\":[";
    first = true;
    for (const auto& [fact, n] : d.unpriced) {
        if (!first) out += ',';
        first = false;
        out += '[';
        appendString(out, fact.model);
        out += ',';
        if (fact.provider) {
            appendString(out, *fact.provider);
        } else {
            out += "null";
        }
        out += fact.fast ? ",true," : ",false,";
        out += fact.noDate ? "true," : "false,";
        out += std::to_string(n) + "]";
    }
    out += "]}";
    return out;
}

std::optional<DayAggregate> decode(std::string_view text) {
    json::Document document;
    if (!document.parse(text) || !document.root().isObject()) return std::nullopt;
    const auto r = document.root();
    DayAggregate d;
    d.requests = r["requests"].truncated().value_or(0);
    d.input = r["input"].truncated().value_or(0);
    d.cachedInput = r["cachedInput"].truncated().value_or(0);
    d.cacheWriteInput = r["cacheWriteInput"].truncated().value_or(0);
    d.output = r["output"].truncated().value_or(0);
    d.reasoning = r["reasoning"].truncated().value_or(0);
    d.measuredOutput = r["measuredOutput"].truncated().value_or(0);
    d.measuredMs = r["measuredMs"].truncated().value_or(0);
    d.unfinalized = r["unfinalized"].truncated().value_or(0);
    d.agents = static_cast<int>(r["agents"].truncated().value_or(0));
    d.costMicroUsd = optionalOf(r["costMicroUsd"]);
    d.unpricedRequests = optionalOf(r["unpricedRequests"]);
    for (const auto& [name, m] : r["models"].members()) {
        ModelTotals t;
        t.requests = m.at(0).truncated().value_or(0);
        t.output = m.at(1).truncated().value_or(0);
        t.measuredOutput = m.at(2).truncated().value_or(0);
        t.measuredMs = m.at(3).truncated().value_or(0);
        t.costMicroUsd = optionalOf(m.at(4));
        t.unpricedRequests = optionalOf(m.at(5));
        d.models[name] = t;
    }
    for (const auto& f : r["unpriced"].elements()) {
        UnpricedFact fact;
        fact.model = f.at(0).string().value_or("");
        fact.provider = f.at(1).string();
        fact.fast = f.at(2).isTrue();
        fact.noDate = f.at(3).isTrue();
        d.unpriced[fact] = f.at(4).truncated().value_or(0);
    }
    return d;
}

std::unique_ptr<Store> Store::open(const std::string& path) {
    std::error_code ec;
    std::filesystem::create_directories(std::filesystem::path(path).parent_path(), ec);
    auto store = std::unique_ptr<Store>(new Store(sqlite::Connection::open(path, sqlite::OpenMode::ReadWrite, 5000)));
    store->db_.execute("PRAGMA journal_mode=WAL");
    store->db_.execute("PRAGMA synchronous=NORMAL");
    store->db_.execute("CREATE TABLE IF NOT EXISTS meta(key TEXT PRIMARY KEY, value TEXT)");
    const auto schema = store->meta("schema");
    if (schema && *schema != std::to_string(kSchema)) {
        for (const char* table : {"files", "contributions", "peaks", "bindings", "price_tables"}) {
            store->db_.execute(std::string("DROP TABLE IF EXISTS ") + table);
        }
        store->db_.execute("DELETE FROM meta");
    }
    store->createSchema();
    store->setMeta("schema", std::to_string(kSchema));
    return store;
}

void Store::createSchema() {
    db_.execute(
        "CREATE TABLE IF NOT EXISTS files(id INTEGER PRIMARY KEY, path TEXT NOT NULL UNIQUE, family TEXT NOT NULL, size INTEGER NOT NULL,"
        " modified INTEGER NOT NULL, version INTEGER NOT NULL, keys BLOB)");
    db_.execute(
        "CREATE TABLE IF NOT EXISTS contributions(file_id INTEGER NOT NULL, day TEXT NOT NULL, source TEXT NOT NULL, data TEXT NOT NULL,"
        " PRIMARY KEY(file_id, day, source))");
    db_.execute("CREATE TABLE IF NOT EXISTS peaks(day TEXT PRIMARY KEY, peak REAL NOT NULL)");
    db_.execute("CREATE TABLE IF NOT EXISTS bindings(day TEXT PRIMARY KEY, version INTEGER NOT NULL)");
    db_.execute("CREATE TABLE IF NOT EXISTS price_tables(version INTEGER PRIMARY KEY, data BLOB NOT NULL)");
    // METRICS 6.11 Store: the retired task score's records go; the performance records live in a side table.
    db_.execute("DROP TABLE IF EXISTS task_records");
    db_.execute(
        "CREATE TABLE IF NOT EXISTS perf_records(file_id INTEGER NOT NULL, day TEXT NOT NULL, source TEXT NOT NULL, model TEXT NOT NULL,"
        " n INTEGER NOT NULL, output_sum INTEGER NOT NULL, duration_ms_sum INTEGER NOT NULL, latency_bins TEXT NOT NULL,"
        " reason_n INTEGER NOT NULL, reason_output INTEGER NOT NULL, reason_duration_ms_sum INTEGER NOT NULL, cache_n INTEGER NOT NULL,"
        " cache_input INTEGER NOT NULL, cache_cached INTEGER NOT NULL, tool_n INTEGER NOT NULL, tool_ln_sum REAL NOT NULL,"
        " tool_failed INTEGER NOT NULL, active_ms INTEGER NOT NULL, version INTEGER NOT NULL,"
        " PRIMARY KEY (file_id, day, source, model)) WITHOUT ROWID");
    db_.execute(
        "CREATE TABLE IF NOT EXISTS day_traffic(day TEXT, key TEXT, rx INTEGER, tx INTEGER, rx_tools INTEGER, tx_tools INTEGER,"
        " sampled_ms INTEGER, PRIMARY KEY (day, key)) WITHOUT ROWID");
}

void Store::saveTraffic(const std::vector<traffic::HistoryRow>& rows) {
    if (rows.empty()) return;
    db_.execute("BEGIN IMMEDIATE");
    try {
        auto upsert = db_.prepare(
            "INSERT INTO day_traffic(day, key, rx, tx, rx_tools, tx_tools, sampled_ms) VALUES(?1, ?2, ?3, ?4, ?5, ?6, ?7)"
            " ON CONFLICT(day, key) DO UPDATE SET rx=excluded.rx, tx=excluded.tx, rx_tools=excluded.rx_tools,"
            " tx_tools=excluded.tx_tools, sampled_ms=excluded.sampled_ms");
        std::string first;
        for (const auto& r : rows) {
            upsert.reset();
            upsert.bind(1, std::string_view(r.day)).bind(2, std::string_view(r.key));
            upsert.bind(3, static_cast<std::int64_t>(r.rx)).bind(4, static_cast<std::int64_t>(r.tx));
            upsert.bind(5, static_cast<std::int64_t>(r.rxTools)).bind(6, static_cast<std::int64_t>(r.txTools)).bind(7, r.sampledMs);
            upsert.step();
            if (first.empty() || r.day < first) first = r.day;
        }
        if (const auto since = meta("trafficSince"); !since || first < *since) setMeta("trafficSince", first);
        db_.execute("COMMIT");
    } catch (...) {
        db_.execute("ROLLBACK");
        throw;
    }
}

std::vector<traffic::HistoryRow> Store::trafficRows() const {
    std::vector<traffic::HistoryRow> out;
    auto rows = db_.prepare("SELECT day, key, rx, tx, rx_tools, tx_tools, sampled_ms FROM day_traffic ORDER BY day, key");
    while (rows.step()) {
        out.push_back({rows.getString(0).value_or(""), rows.getString(1).value_or(""), static_cast<std::uint64_t>(rows.getInt64(2)),
                       static_cast<std::uint64_t>(rows.getInt64(3)), static_cast<std::uint64_t>(rows.getInt64(4)),
                       static_cast<std::uint64_t>(rows.getInt64(5)), rows.getInt64(6)});
    }
    return out;
}

std::map<std::string, StoredFile> Store::loadFiles() const {
    std::map<std::string, StoredFile> files;
    std::map<std::int64_t, std::string> byId;
    auto rows = db_.prepare("SELECT id, path, family, size, modified, version FROM files");
    while (rows.step()) {
        StoredFile f;
        f.id = rows.getInt64(0);
        f.path = rows.getString(1).value_or("");
        f.family = rows.getString(2).value_or("");
        f.size = rows.getInt64(3);
        f.modified = rows.getInt64(4);
        f.version = static_cast<int>(rows.getInt64(5));
        byId[f.id] = f.path;
        files[f.path] = std::move(f);
    }
    auto days = db_.prepare("SELECT file_id, day, source, data FROM contributions");
    while (days.step()) {
        const auto it = byId.find(days.getInt64(0));
        if (it == byId.end()) continue;
        const auto source = sourceFromId(days.getText(2));
        const auto data = decode(days.getText(3));
        if (!source || !data) continue;
        files[it->second].days[DayKey{days.getString(1).value_or(""), *source}] = *data;
    }
    auto records = db_.prepare(
        "SELECT r.file_id, r.day, r.source, r.model, r.n, r.output_sum, r.duration_ms_sum, r.latency_bins, r.reason_n, r.reason_output,"
        " r.reason_duration_ms_sum, r.cache_n, r.cache_input, r.cache_cached, r.tool_n, r.tool_ln_sum, r.tool_failed, r.active_ms"
        " FROM perf_records r JOIN files f ON f.id = r.file_id AND f.version = r.version");
    while (records.step()) {
        const auto it = byId.find(records.getInt64(0));
        const auto source = sourceFromId(records.getText(2));
        if (it == byId.end() || !source) continue;
        perf::DayRecord row;
        row.day = records.getString(1).value_or("");
        row.source = *source;
        row.model = records.getString(3).value_or("");
        auto& r = row.record;
        r.n = records.getInt64(4);
        r.outputSum = records.getInt64(5);
        r.durationMsSum = records.getInt64(6);
        r.latencyBins = decodeBins(records.getText(7));
        r.reasonN = records.getInt64(8);
        r.reasonOutput = records.getInt64(9);
        r.reasonDurationMsSum = records.getInt64(10);
        r.cacheN = records.getInt64(11);
        r.cacheInput = records.getInt64(12);
        r.cacheCached = records.getInt64(13);
        r.toolN = records.getInt64(14);
        r.toolLnSum = records.getDouble(15);
        r.toolFailed = records.getInt64(16);
        r.activeMs = records.getInt64(17);
        files[it->second].perf.push_back(std::move(row));
    }
    return files;
}

void Store::save(std::vector<std::pair<StoredFile*, std::vector<std::uint64_t>>>& files) {
    db_.execute("BEGIN IMMEDIATE");
    try {
        auto upsert = db_.prepare(
            "INSERT INTO files(path, family, size, modified, version, keys) VALUES(?1, ?2, ?3, ?4, ?5, ?6)"
            " ON CONFLICT(path) DO UPDATE SET family=excluded.family, size=excluded.size, modified=excluded.modified,"
            " version=excluded.version, keys=excluded.keys RETURNING id");
        auto clear = db_.prepare("DELETE FROM contributions WHERE file_id=?1");
        auto insert = db_.prepare("INSERT INTO contributions(file_id, day, source, data) VALUES(?1, ?2, ?3, ?4)");
        auto clearRecords = db_.prepare("DELETE FROM perf_records WHERE file_id=?1");
        auto insertRecord = db_.prepare(
            "INSERT INTO perf_records(file_id, day, source, model, n, output_sum, duration_ms_sum, latency_bins, reason_n, reason_output,"
            " reason_duration_ms_sum, cache_n, cache_input, cache_cached, tool_n, tool_ln_sum, tool_failed, active_ms, version)"
            " VALUES(?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8, ?9, ?10, ?11, ?12, ?13, ?14, ?15, ?16, ?17, ?18, ?19)"
            " ON CONFLICT(file_id, day, source, model) DO NOTHING");
        for (auto& [file, hashes] : files) {
            upsert.reset();
            upsert.bind(1, std::string_view(file->path)).bind(2, std::string_view(file->family)).bind(3, file->size).bind(4, file->modified);
            upsert.bind(5, static_cast<std::int64_t>(file->version)).bindBlob(6, packHashes(hashes));
            if (upsert.step()) file->id = upsert.getInt64(0);
            while (upsert.step()) {
            }
            clear.reset();
            clear.bind(1, file->id);
            clear.step();
            for (const auto& [key, day] : file->days) {
                insert.reset();
                insert.bind(1, file->id).bind(2, std::string_view(key.day)).bind(3, sourceId(key.source)).bind(4, std::string_view(encode(day)));
                insert.step();
            }
            clearRecords.reset();
            clearRecords.bind(1, file->id);
            clearRecords.step();
            for (const auto& row : file->perf) {
                const auto& r = row.record;
                const auto bins = encodeBins(r.latencyBins);
                insertRecord.reset();
                insertRecord.bind(1, file->id).bind(2, std::string_view(row.day)).bind(3, sourceId(row.source)).bind(4, std::string_view(row.model));
                insertRecord.bind(5, r.n).bind(6, r.outputSum).bind(7, r.durationMsSum).bind(8, std::string_view(bins));
                insertRecord.bind(9, r.reasonN).bind(10, r.reasonOutput).bind(11, r.reasonDurationMsSum);
                insertRecord.bind(12, r.cacheN).bind(13, r.cacheInput).bind(14, r.cacheCached);
                insertRecord.bind(15, r.toolN).bind(16, r.toolLnSum).bind(17, r.toolFailed).bind(18, r.activeMs);
                insertRecord.bind(19, static_cast<std::int64_t>(file->version));
                insertRecord.step();
            }
        }
        db_.execute("COMMIT");
    } catch (...) {
        db_.execute("ROLLBACK");
        throw;
    }
}

void Store::loadKeys(KeyLedger& ledger, const std::set<std::int64_t>& except) const {
    auto rows = db_.prepare("SELECT id, keys FROM files WHERE keys IS NOT NULL");
    while (rows.step()) {
        if (except.contains(rows.getInt64(0))) continue;
        ledger.settle(unpackHashes(rows.getBlob(1)));
    }
}

void Store::loadKeys(KeyLedger& ledger, std::int64_t fileId) const {
    auto rows = db_.prepare("SELECT keys FROM files WHERE id=?1 AND keys IS NOT NULL");
    rows.bind(1, fileId);
    while (rows.step()) ledger.settle(unpackHashes(rows.getBlob(0)));
}

std::map<std::string, double> Store::peaks() const {
    std::map<std::string, double> out;
    auto rows = db_.prepare("SELECT day, peak FROM peaks");
    while (rows.step()) out[rows.getString(0).value_or("")] = rows.getDouble(1);
    return out;
}

void Store::savePeaks(const std::map<std::string, double>& peaks) {
    auto upsert = db_.prepare("INSERT INTO peaks(day, peak) VALUES(?1, ?2) ON CONFLICT(day) DO UPDATE SET peak=max(peak, excluded.peak)");
    for (const auto& [day, peak] : peaks) {
        upsert.reset();
        upsert.bind(1, std::string_view(day)).bind(2, peak);
        upsert.step();
    }
}

std::map<std::string, int> Store::priceBindings() const {
    std::map<std::string, int> out;
    auto rows = db_.prepare("SELECT day, version FROM bindings");
    while (rows.step()) out[rows.getString(0).value_or("")] = static_cast<int>(rows.getInt64(1));
    return out;
}

std::map<int, std::string> Store::priceTables() const {
    std::map<int, std::string> out;
    auto rows = db_.prepare("SELECT version, data FROM price_tables");
    while (rows.step()) out[static_cast<int>(rows.getInt64(0))] = std::string(rows.getBlob(1));
    return out;
}

void Store::savePriceBindings(const std::map<std::string, int>& bindings, const std::function<std::string(int)>& tableData) {
    auto bind = db_.prepare("INSERT INTO bindings(day, version) VALUES(?1, ?2) ON CONFLICT(day) DO UPDATE SET version=excluded.version");
    auto table = db_.prepare("INSERT OR IGNORE INTO price_tables(version, data) VALUES(?1, ?2)");
    std::set<int> versions;
    for (const auto& [day, version] : bindings) {
        bind.reset();
        bind.bind(1, std::string_view(day)).bind(2, static_cast<std::int64_t>(version));
        bind.step();
        versions.insert(version);
    }
    for (const int version : versions) {
        const auto data = tableData(version);
        if (data.empty()) continue;
        table.reset();
        table.bind(1, static_cast<std::int64_t>(version)).bindBlob(2, data);
        table.step();
    }
}

std::optional<std::string> Store::meta(const std::string& key) const {
    auto rows = db_.prepare("SELECT value FROM meta WHERE key=?1");
    rows.bind(1, std::string_view(key));
    if (rows.step()) return rows.getString(0);
    return std::nullopt;
}

void Store::setMeta(const std::string& key, const std::string& value) {
    auto rows = db_.prepare("INSERT INTO meta(key, value) VALUES(?1, ?2) ON CONFLICT(key) DO UPDATE SET value=excluded.value");
    rows.bind(1, std::string_view(key)).bind(2, std::string_view(value));
    rows.step();
}

void Store::clear() {
    for (const char* table : {"files", "contributions", "peaks", "bindings", "price_tables", "perf_records", "day_traffic"}) {
        db_.execute(std::string("DELETE FROM ") + table);
    }
    db_.execute("DELETE FROM meta WHERE key<>'schema'");
}

}  // namespace agentspeed::history
