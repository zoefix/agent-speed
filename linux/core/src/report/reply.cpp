#include <unicode/uchar.h>

#include <algorithm>
#include <array>
#include <cctype>
#include <ctime>

#include "agentspeed/report_service.hpp"
#include "unicode.hpp"

namespace agentspeed::report {

namespace {

bool isDeviceId(std::string_view s) {
    if (s.size() != 36) return false;
    for (std::size_t i = 0; i < 36; ++i) {
        const bool dash = i == 8 || i == 13 || i == 18 || i == 23;
        if (dash ? s[i] != '-' : !((s[i] >= '0' && s[i] <= '9') || (s[i] >= 'a' && s[i] <= 'f'))) return false;
    }
    return s[14] == '4' && std::string_view("89ab").find(s[19]) != std::string_view::npos;
}

std::optional<std::string> boundedName(const std::optional<std::string>& text) {
    if (!text) return std::nullopt;
    const auto count = detail::points(*text).size();
    return count >= 1 && count <= 32 ? text : std::nullopt;
}

std::optional<std::int64_t> integer(const json::Value& value) {
    if (!value.isNumber() || !value.isIntegerLiteral()) return std::nullopt;
    return value.truncated();
}

// English day and month names, independent of the C locale the Qt app sets.
int monthOf(std::string_view name) {
    static constexpr std::array<std::string_view, 12> kMonths = {"jan", "feb", "mar", "apr", "may", "jun", "jul", "aug", "sep", "oct", "nov", "dec"};
    std::string lowered;
    for (const char c : name.substr(0, 3)) lowered += static_cast<char>(std::tolower(static_cast<unsigned char>(c)));
    for (std::size_t i = 0; i < kMonths.size(); ++i)
        if (kMonths[i] == lowered) return static_cast<int>(i);
    return -1;
}

std::vector<std::string_view> fields(std::string_view text) {
    std::vector<std::string_view> out;
    std::size_t i = 0;
    while (i < text.size()) {
        while (i < text.size() && (text[i] == ' ' || text[i] == ',' || text[i] == '-')) ++i;
        const auto start = i;
        while (i < text.size() && text[i] != ' ' && text[i] != ',' && text[i] != '-') ++i;
        if (i > start) out.push_back(text.substr(start, i - start));
    }
    return out;
}

bool digits(std::string_view s) { return !s.empty() && std::all_of(s.begin(), s.end(), [](char c) { return c >= '0' && c <= '9'; }); }

// IMF-fixdate, RFC 850 and asctime (RFC 9110 5.6.7).
std::optional<std::int64_t> httpDate(std::string_view text) {
    const auto f = fields(text);
    std::tm tm{};
    std::string_view time;
    if (f.size() >= 5 && digits(f[1]) && monthOf(f[2]) >= 0 && digits(f[3])) {  // Sun, 06 Nov 1994 08:49:37 GMT / Sunday, 06-Nov-94 ...
        tm.tm_mday = std::stoi(std::string(f[1]));
        tm.tm_mon = monthOf(f[2]);
        int year = std::stoi(std::string(f[3]));
        if (f[3].size() == 2) year += year < 70 ? 2000 : 1900;
        tm.tm_year = year - 1900;
        time = f[4];
    } else if (f.size() >= 5 && monthOf(f[1]) >= 0 && digits(f[2]) && digits(f[4])) {  // Sun Nov  6 08:49:37 1994
        tm.tm_mon = monthOf(f[1]);
        tm.tm_mday = std::stoi(std::string(f[2]));
        time = f[3];
        tm.tm_year = std::stoi(std::string(f[4])) - 1900;
    } else {
        return std::nullopt;
    }
    if (time.size() != 8 || time[2] != ':' || time[5] != ':' || !digits(time.substr(0, 2)) || !digits(time.substr(3, 2)) || !digits(time.substr(6, 2)))
        return std::nullopt;
    tm.tm_hour = std::stoi(std::string(time.substr(0, 2)));
    tm.tm_min = std::stoi(std::string(time.substr(3, 2)));
    tm.tm_sec = std::stoi(std::string(time.substr(6, 2)));
    return static_cast<std::int64_t>(timegm(&tm));
}

}  // namespace

bool isErrorCode(std::string_view text) {
    return !text.empty() && text.size() <= 48 && text[0] >= 'a' && text[0] <= 'z' &&
           std::all_of(text.begin(), text.end(), [](char c) { return (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '_'; });
}

std::string sanitized(std::string_view text, std::size_t cap) {
    std::u32string kept;
    for (const auto c : detail::points(text)) {
        const auto type = u_charType(static_cast<UChar32>(c));
        if (type == U_CONTROL_CHAR || type == U_FORMAT_CHAR) continue;
        kept += c;
        if (kept.size() >= cap) break;
    }
    return detail::utf8(kept);
}

std::optional<int> retryAfter(std::string_view value, std::int64_t nowSeconds) {
    while (!value.empty() && std::isspace(static_cast<unsigned char>(value.front()))) value.remove_prefix(1);
    while (!value.empty() && std::isspace(static_cast<unsigned char>(value.back()))) value.remove_suffix(1);
    if (digits(value)) return value.size() > 9 ? INT32_MAX : std::stoi(std::string(value));
    if (const auto date = httpDate(value)) return static_cast<int>(std::clamp<std::int64_t>(*date - nowSeconds, 0, INT32_MAX));
    return std::nullopt;
}

Reply Reply::parse(int status, const std::map<std::string, std::string>& headers, std::string_view body, std::int64_t nowSeconds) {
    Reply reply;
    reply.status = status;
    if (const auto it = headers.find("retry-after"); it != headers.end()) reply.retryAfterSeconds = retryAfter(it->second, nowSeconds);
    json::Document doc;
    if (body.empty() || !doc.parse(body) || !doc.root().isObject()) return reply;
    const auto root = doc.root();
    if (reply.success()) {
        static constexpr std::array<std::string_view, 8> kResults = {"created", "exists", "updated", "unchanged", "rotated", "deleted", "accepted", "duplicate"};
        if (const auto result = root["status"].string(); result && std::find(kResults.begin(), kResults.end(), *result) != kResults.end()) reply.result = result;
        if (const auto id = root["deviceId"].string(); id && isDeviceId(*id)) reply.deviceId = id;
        reply.username = boundedName(root["username"].string());
        if (const auto link = root["profileUrl"].string()) reply.profileUrl = httpsLink(*link);
        reply.transferred = root["transferred"].isTrue();
        if (const auto seconds = integer(root["intervalSeconds"])) reply.intervalSeconds = static_cast<int>(std::clamp<std::int64_t>(*seconds, 5, 300));
        if (const auto rank = integer(root["rank"]); rank && *rank >= 1 && *rank <= 100'000'000) reply.rank = static_cast<int>(*rank);
        return reply;
    }
    const auto error = root["error"];
    if (!error.isObject()) return reply;
    if (const auto code = error["code"].string(); code && isErrorCode(*code)) reply.code = code;
    if (const auto reason = error["reason"].string()) reply.reason = sanitized(*reason, 32);
    if (const auto field = error["field"].string()) reply.field = sanitized(*field, 200);
    reply.errorUsername = boundedName(error["username"].string());
    if (const auto serverTime = integer(error["serverTime"]); serverTime && *serverTime >= 0) reply.serverTime = serverTime;
    std::size_t taken = 0;
    for (const auto& v : error["supported"].elements()) {
        if (++taken > 16) break;
        if (const auto n = integer(v)) reply.supported.push_back(static_cast<int>(std::clamp<std::int64_t>(*n, INT32_MIN, INT32_MAX)));
    }
    if (const auto message = error["message"].string()) reply.logMessage = sanitized(*message, 200);
    if (!reply.retryAfterSeconds) {
        if (const auto after = integer(error["retryAfterSeconds"]); after && *after >= 0) reply.retryAfterSeconds = static_cast<int>(std::min<std::int64_t>(*after, 86'400));
    }
    return reply;
}

std::string statusCode(int status) {
    switch (status) {
    case 404:
    case 405: return "not_found";
    case 410: return "gone";
    case 413: return "payload_too_large";
    case 403: return "forbidden";
    default: return "bad_request";
    }
}

std::pair<Outcome, Reaction> classify(const Reply* reply, bool hasPendingKey) {
    const auto transient = Outcome{Outcome::Kind::Transient, std::nullopt, 0};
    if (!reply) return {transient, {}};
    if (reply->success()) return {Outcome{Outcome::Kind::Delivered, reply->intervalSeconds, 0}, {}};
    const auto retry = reply->retryAfterSeconds ? std::optional<int>(std::clamp(*reply->retryAfterSeconds, 5, 3600)) : std::nullopt;
    if (reply->status >= 300 && reply->status <= 399) return {transient, {}};
    if (reply->status == 429 || reply->code == "rate_limited") return {Outcome{Outcome::Kind::RateLimited, std::nullopt, retry.value_or(60)}, {}};
    if (reply->status >= 500) {
        if (reply->status == 503 && retry) return {Outcome{Outcome::Kind::RateLimited, std::nullopt, *retry}, {}};
        return {transient, {}};
    }
    const Outcome rejected{Outcome::Kind::Rejected, std::nullopt, 0};
    const auto needs = [&](Problem problem) {
        Reaction r;
        r.kind = Reaction::Kind::NeedsUser;
        r.problem = problem;
        return std::pair{rejected, r};
    };
    const auto react = [&](Reaction::Kind kind) {
        Reaction r;
        r.kind = kind;
        return r;
    };
    const auto code = reply->code.value_or(statusCode(reply->status));
    if (code == "timestamp_out_of_window") {
        auto r = react(Reaction::Kind::CorrectClock);
        r.serverTime = reply->serverTime;
        return {rejected, r};
    }
    if (code == "nonce_replayed") return {Outcome{Outcome::Kind::Again, std::nullopt, 0}, {}};
    if (code == "signature_invalid") {
        if (hasPendingKey) return {Outcome{Outcome::Kind::Again, std::nullopt, 0}, react(Reaction::Kind::UsePendingKey)};
        return {rejected, react(Reaction::Kind::RecheckRegistration)};
    }
    if (code == "api_key_required" || code == "api_key_invalid") return needs(Problem::ApiKey);
    if (code == "forbidden") return needs(Problem::Forbidden);
    if (code == "unknown_device") return {rejected, react(Reaction::Kind::ReRegister)};
    if (code == "not_found" || code == "method_not_allowed") {
        auto r = react(Reaction::Kind::Strike);
        r.misconfigured = true;
        return {rejected, r};
    }
    if (code == "username_taken") return needs(Problem::UsernameTaken);
    if (code == "username_mismatch") {
        auto r = react(Reaction::Kind::Rename);
        r.registered = reply->errorUsername;
        return {rejected, r};
    }
    if (code == "device_conflict") return needs(Problem::KeyMismatch);
    if (code == "seq_conflict") return {rejected, react(Reaction::Kind::NewRun)};
    if (code == "gone") return needs(Problem::Gone);
    if (code == "version_unsupported" || code == "payload_too_large") {
        auto r = react(Reaction::Kind::RefreshDiscovery);
        r.upgrade = code == "version_unsupported";
        return {rejected, r};
    }
    if (code == "report_stale" || code == "length_required") return {rejected, {}};
    return {rejected, react(Reaction::Kind::Strike)};
}

}  // namespace agentspeed::report
