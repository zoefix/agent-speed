#include "agentspeed/quota/cli_cache.hpp"

#include <string.h>

#include <cmath>

#include "agentspeed/json.hpp"
#include "agentspeed/quota/desktop_entry.hpp"

namespace agentspeed::quota::cli {

namespace {

// REMOTE 5.6: the years 2000–2100, checked on the number before its fraction is dropped.
constexpr double kEarliestMs = 946'684'800'000.0;
constexpr double kAfterLatestMs = 4'133'980'800'000.0;

void wipe(std::optional<std::string>& text) {
    if (text && !text->empty()) ::explicit_bzero(text->data(), text->size());
}

// Both uuids present (non-empty strings) and different: the cache belongs to another account.
bool otherAccount(const json::Value& root, const json::Value& cache) {
    const auto account = root["oauthAccount"];
    auto signedIn = account.isObject() ? account["accountUuid"].nonEmptyString() : std::nullopt;
    auto cached = cache["accountUuid"].nonEmptyString();
    const bool other = signedIn && cached && *signedIn != *cached;
    wipe(signedIn);
    wipe(cached);
    return other;
}

}  // namespace

CacheRead readCache(std::string_view bytes) {
    CacheRead out;
    if (bytes.size() > CacheReader::kFileLimit) {
        out.parsed = true;
        return out;
    }
    json::Document doc;
    if (!doc.parse(bytes)) return out;
    out.parsed = true;
    const auto root = doc.root();
    if (!root.isObject()) return out;
    const auto cache = root["cachedUsageUtilization"];
    if (!cache.isObject()) return out;
    const auto fetched = cache["fetchedAtMs"].finiteNumber();
    const auto body = cache["utilization"];
    if (!fetched || !(*fetched >= kEarliestMs && *fetched < kAfterLatestMs) || !body.isObject()) return out;
    if (otherAccount(root, cache)) return out;
    Snapshot part;
    part.source = SourceKind::ClaudeCode;
    part.observedMs = static_cast<std::int64_t>(std::trunc(*fetched));
    part.windows = desktop::usageWindows(body);
    out.part = std::move(part);
    return out;
}

bool CacheReader::clear() {
    const bool had = snapshot_.has_value();
    snapshot_.reset();
    return had;
}

bool CacheReader::refresh(std::int64_t nowMs, bool enabled) {
    if (!enabled) {
        stamp_.reset();
        readMs_.reset();
        return clear();
    }
    const auto stamp = files::stampOf(file_, true);
    if (!stamp) {
        stamp_.reset();
        return clear();
    }
    if (stamp_ == stamp || (readMs_ && nowMs - *readMs_ < kRereadAfterMs)) return false;
    readMs_ = nowMs;
    stamp_ = stamp;
    ++reads_;
    auto read = files::readFile(file_, kFileLimit, true);
    CacheRead result;
    result.parsed = true;
    if (read.status == files::ReadStatus::Ok) {
        result = readCache(read.bytes);
    } else if (read.status != files::ReadStatus::TooLarge) {
        stamp_.reset();
    }
    // The file holds the account's details: nothing of it outlives the read.
    if (!read.bytes.empty()) ::explicit_bzero(read.bytes.data(), read.bytes.size());
    // Claude Code rewrites the file often: one that does not parse keeps the value and is read again at the next check.
    if (!result.parsed) {
        stamp_.reset();
        return false;
    }
    const bool changed = result.part != snapshot_;
    snapshot_ = std::move(result.part);
    return changed;
}

}  // namespace agentspeed::quota::cli
