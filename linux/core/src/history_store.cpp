#include "agentspeed/history_store.hpp"

#include <filesystem>

#include "agentspeed/json.hpp"

namespace agentspeed::history {

namespace {

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
    for (const char* table : {"files", "contributions", "peaks", "bindings", "price_tables"}) db_.execute(std::string("DELETE FROM ") + table);
    db_.execute("DELETE FROM meta WHERE key<>'schema'");
}

}  // namespace agentspeed::history
