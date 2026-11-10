#pragma once

#include <cstddef>
#include <cstdint>
#include <optional>
#include <string>
#include <string_view>
#include <utility>
#include <vector>

#include "agentspeed/json.hpp"
#include "agentspeed/quota/window.hpp"

// QUOTAS 4.2 and 4.3: one file of Claude Desktop's Chromium *simple cache* and the usage response it may hold. Every
// offset is checked against the file; anything unexpected gives nothing (the Desktop rows then show no quota, never a
// wrong number).
namespace agentspeed::quota::desktop {

constexpr std::size_t kHeaderSize = 24;
constexpr std::size_t kEofSize = 24;
constexpr std::size_t kKeyHashSize = 32;
// Larger files are not a usage response; a larger decoded body is refused.
constexpr std::size_t kFileLimit = 1 << 20;
constexpr std::size_t kBodyLimit = 1 << 20;

// `<16 hex digits>_0`: the first 8 bytes of the key's SHA-1 as a little-endian number in lowercase hex.
std::string entryFileName(std::string_view key);
bool isEntryFileName(std::string_view name);

// The key's length from the first 24 bytes when the magic and the version (5) match.
std::optional<std::uint32_t> keyLength(std::string_view header);
// The URL of a key: the text after its last space (partitioned caches), else without a leading `<digits>/<digits>/`.
std::string urlOfKey(std::string_view key);
// `https://claude.ai/api/organizations/<uuid>/usage`, optionally followed by `?` and any query.
bool isUsageUrl(std::string_view url);
inline bool isUsageKey(std::string_view key) { return isUsageUrl(urlOfKey(key)); }

// The parts of an entry file: views into its bytes.
struct CacheEntry {
    std::string key;
    std::string_view body;          // stream 1
    std::string_view responseInfo;  // stream 0
};
std::optional<CacheEntry> parseCacheEntry(std::string_view bytes);

// Stream 0: NUL-separated lines from the first `HTTP/`, then `name:value` lines up to an empty one.
struct ResponseInfo {
    std::optional<int> status;
    // Lower-case names, trimmed values; a repeated name keeps its first value.
    std::vector<std::pair<std::string, std::string>> headers;
    std::optional<std::string> header(std::string_view name) const;
};
std::optional<ResponseInfo> parseResponseInfo(std::string_view stream);

enum class Coding : std::uint8_t { Identity, Zstd, Gzip, Brotli };
// `content-encoding`: zstd, gzip, br, identity or absent (also empty); nullopt for any other value.
std::optional<Coding> codingOf(const std::optional<std::string>& header);
// The decoded body; nullopt when it is damaged, truncated or larger than `limit`.
std::optional<std::string> decodeBody(std::string_view body, Coding coding, std::size_t limit = kBodyLimit);

// QUOTAS 4.3: `five_hour`, `seven_day`, every scoped weekly limit by model name, then `seven_day_opus` and
// `seven_day_sonnet` unless a scoped limit gave that name.
std::vector<Window> usageWindows(const json::Value& body);

// A usage response: its URL, when the response was made (`date`, else the file's modification time) and its windows.
struct UsageEntry {
    std::string url;
    std::int64_t observedMs = 0;
    std::vector<Window> windows;
    bool operator==(const UsageEntry&) const = default;
};
// The usage entry of a whole cache file: a usage URL, status 200, a known coding and a JSON object body of ≤ 1 MiB.
std::optional<UsageEntry> parseUsageEntry(std::string_view bytes, std::int64_t mtimeMs);

// One usage entry found in the folder.
struct FoundEntry {
    std::string file;
    UsageEntry entry;
    std::int64_t mtimeNs = 0;
};
// QUOTAS 4.4 Choosing: the newest `observedMs` wins, ties go to the newer file modification time, then the first
// listed. A newer entry without windows still wins (the account lost its limits). Null for none.
const FoundEntry* newestEntry(const std::vector<FoundEntry>& entries);

}  // namespace agentspeed::quota::desktop
