#include "agentspeed/quota/cookies/stores.hpp"

#include <sys/stat.h>

#include <algorithm>
#include <charconv>
#include <cstdlib>
#include <filesystem>

#include "agentspeed/sqlite.hpp"

namespace agentspeed::quota::cookies {

namespace {

constexpr std::size_t kStoreCap = 128u << 20;
constexpr std::size_t kRowsPerSite = 256;

// The store and its `-wal` in a fresh 0700 folder, removed when this goes.
class PrivateCopy {
public:
    PrivateCopy(const VendorEnvironment& env, const std::string& store) {
        std::string pattern = fsx::join(env.temporaryFolder(), "AgentSpeed-cookies-XXXXXX");
        if (::mkdtemp(pattern.data()) == nullptr) return;
        folder_ = pattern;
        ::chmod(folder_.c_str(), 0700);
        const auto copy = fsx::join(folder_, "store.sqlite");
        auto& files = env.fs();
        if (!files.copy(store, copy, kStoreCap)) return;
        const auto wal = store + "-wal";
        if (const auto info = files.stat(wal); info && info->regular && !files.copy(wal, copy + "-wal", kStoreCap)) return;
        path_ = copy;
    }
    ~PrivateCopy() {
        if (folder_.empty()) return;
        std::error_code ignored;
        std::filesystem::remove_all(folder_, ignored);
    }
    PrivateCopy(const PrivateCopy&) = delete;
    PrivateCopy& operator=(const PrivateCopy&) = delete;
    const std::string& path() const { return path_; }

private:
    std::string folder_;
    std::string path_;
};

std::string likeEscaped(const std::string& site) {
    std::string out;
    for (const char c : site) {
        if (c == '\\' || c == '%' || c == '_') out += '\\';
        out += c;
    }
    return out;
}

void bindSite(sqlite::Statement& statement, const std::string& site) {
    statement.reset();
    statement.bind(1, std::string_view(site));
    const std::string dotted = "." + site;
    statement.bind(2, std::string_view(dotted));
    const std::string pattern = "%." + likeEscaped(site);
    statement.bind(3, std::string_view(pattern));
}

int metaVersion(const sqlite::Connection& db) {
    try {
        auto meta = db.prepare("SELECT value FROM meta WHERE key = 'version'");
        if (!meta.step()) return 0;
        if (meta.columnType(0) == 1) return static_cast<int>(meta.getInt64(0));
        const auto text = meta.getText(0);
        int value = 0;
        const auto [end, ec] = std::from_chars(text.data(), text.data() + text.size(), value);
        return ec == std::errc() ? value : 0;
    } catch (const sqlite::Error&) {
        return 0;
    }
}

std::int64_t chromeSeconds(std::int64_t chromeUs) {
    if (chromeUs <= 0) return 0;
    return std::max<std::int64_t>(1, (chromeUs - kWindowsEpochOffsetUs) / 1'000'000);
}

std::int64_t chromeMilliseconds(std::int64_t chromeUs) {
    if (chromeUs <= 0) return 0;
    return (chromeUs - kWindowsEpochOffsetUs) / 1000;
}

}  // namespace

std::optional<ChromeStoreRows> readChromeStore(const VendorEnvironment& env, const std::string& store,
                                               const std::vector<std::string>& sites) {
    PrivateCopy copy(env, store);
    if (copy.path().empty()) return std::nullopt;
    try {
        auto db = sqlite::Connection::open(copy.path(), sqlite::OpenMode::ReadWrite);
        ChromeStoreRows out;
        out.metaVersion = metaVersion(db);
        auto rows = db.prepare(
            "SELECT host_key, name, value, encrypted_value, expires_utc, last_access_utc FROM cookies "
            "WHERE host_key = ?1 OR host_key = ?2 OR host_key LIKE ?3 ESCAPE '\\' ORDER BY rowid");
        for (const auto& site : sites) {
            bindSite(rows, site);
            auto& cookies = out.sites[site];
            while (rows.step() && cookies.size() < kRowsPerSite) {
                Cookie cookie;
                cookie.host = std::string(rows.getText(0));
                cookie.name = std::string(rows.getText(1));
                cookie.value = std::string(rows.getText(2));
                cookie.encrypted = std::string(rows.getBlob(3));
                cookie.expiresS = chromeSeconds(rows.getInt64(4));
                cookie.lastAccessMs = chromeMilliseconds(rows.getInt64(5));
                cookies.push_back(std::move(cookie));
            }
        }
        return out;
    } catch (const sqlite::Error&) {
        return std::nullopt;
    }
}

std::optional<std::map<std::string, std::vector<Cookie>>> readFirefoxStore(const VendorEnvironment& env,
                                                                           const std::string& store,
                                                                           const std::vector<std::string>& sites) {
    PrivateCopy copy(env, store);
    if (copy.path().empty()) return std::nullopt;
    try {
        auto db = sqlite::Connection::open(copy.path(), sqlite::OpenMode::ReadWrite);
        std::map<std::string, std::vector<Cookie>> out;
        auto rows = db.prepare(
            "SELECT host, name, value, expiry, lastAccessed FROM moz_cookies "
            "WHERE host = ?1 OR host = ?2 OR host LIKE ?3 ESCAPE '\\' ORDER BY rowid");
        for (const auto& site : sites) {
            bindSite(rows, site);
            auto& cookies = out[site];
            while (rows.step() && cookies.size() < kRowsPerSite) {
                Cookie cookie;
                cookie.host = std::string(rows.getText(0));
                cookie.name = std::string(rows.getText(1));
                cookie.value = std::string(rows.getText(2));
                const auto expiry = rows.getInt64(3);
                cookie.expiresS = expiry > 100'000'000'000 ? expiry / 1000 : expiry;
                cookie.lastAccessMs = rows.getInt64(4) / 1000;
                cookies.push_back(std::move(cookie));
            }
        }
        return out;
    } catch (const sqlite::Error&) {
        return std::nullopt;
    }
}

}  // namespace agentspeed::quota::cookies
