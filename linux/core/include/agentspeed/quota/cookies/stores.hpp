#pragma once

#include <map>
#include <optional>
#include <string>
#include <vector>

#include "agentspeed/quota/cookies/header.hpp"
#include "agentspeed/quota/routes/environment.hpp"

// QUOTAS 9.10: a browser's cookie database, copied with its `-wal` into a private temporary folder (0700), read there
// and removed right after; the browser's own files are never opened by SQLite. Only the given sites' rows are read.
namespace agentspeed::quota::cookies {

// Microseconds from 1601-01-01 to 1970-01-01.
constexpr std::int64_t kWindowsEpochOffsetUs = 11'644'473'600'000'000;

struct ChromeStoreRows {
    int metaVersion = 0;
    std::map<std::string, std::vector<Cookie>> sites;
};
// A Chromium `Cookies` database: each site's rows (host_key, name, value, encrypted_value, expires_utc,
// last_access_utc) with the `meta` version.
std::optional<ChromeStoreRows> readChromeStore(const VendorEnvironment& env, const std::string& store,
                                               const std::vector<std::string>& sites);
// Firefox's `cookies.sqlite` (`moz_cookies`: host, name, value, expiry, lastAccessed).
std::optional<std::map<std::string, std::vector<Cookie>>> readFirefoxStore(const VendorEnvironment& env,
                                                                           const std::string& store,
                                                                           const std::vector<std::string>& sites);

}  // namespace agentspeed::quota::cookies
