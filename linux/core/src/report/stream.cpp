#include <algorithm>
#include <array>
#include <cmath>
#include <random>
#include <set>

#include "agentspeed/report_stream.hpp"

namespace agentspeed::report::stream {

namespace {

constexpr std::array<std::int64_t, 7> kBackoffMs = {500, 1000, 2000, 4000, 8000, 16'000, 30'000};
constexpr std::int64_t kMaxSeq = 9'007'199'254'740'991;
constexpr std::int64_t kGapMs = 15'000, kHoldMs = 15'000, kQuietMs = 600'000;

bool validUtf8(std::string_view s) {
    for (std::size_t i = 0; i < s.size();) {
        const auto c = static_cast<unsigned char>(s[i]);
        if (c < 0x80) {
            ++i;
            continue;
        }
        std::size_t extra = 0;
        std::uint32_t cp = 0;
        if ((c & 0xE0) == 0xC0) extra = 1, cp = c & 0x1F;
        else if ((c & 0xF0) == 0xE0) extra = 2, cp = c & 0x0F;
        else if ((c & 0xF8) == 0xF0) extra = 3, cp = c & 0x07;
        else return false;
        if (i + extra >= s.size()) return false;
        for (std::size_t k = 1; k <= extra; ++k) {
            const auto next = static_cast<unsigned char>(s[i + k]);
            if ((next & 0xC0) != 0x80) return false;
            cp = (cp << 6) | (next & 0x3F);
        }
        if ((extra == 1 && cp < 0x80) || (extra == 2 && cp < 0x800) || (extra == 3 && (cp < 0x10000 || cp > 0x10FFFF)) || (cp >= 0xD800 && cp <= 0xDFFF))
            return false;
        i += extra + 1;
    }
    return true;
}

// Escapes \uD800–\uDFFF that are not a high surrogate followed by a low one.
bool loneSurrogate(std::string_view raw) {
    const auto unit = [&](std::size_t at) -> int {
        if (at + 6 > raw.size() || raw[at] != '\\' || raw[at + 1] != 'u') return -1;
        int value = 0;
        for (std::size_t k = 2; k < 6; ++k) {
            const char c = raw[at + k];
            const int d = c >= '0' && c <= '9' ? c - '0' : c >= 'a' && c <= 'f' ? c - 'a' + 10 : c >= 'A' && c <= 'F' ? c - 'A' + 10 : -1;
            if (d < 0) return -1;
            value = value * 16 + d;
        }
        return value;
    };
    for (std::size_t i = 0; i < raw.size(); ++i) {
        if (raw[i] != '\\') continue;
        if (i + 1 < raw.size() && raw[i + 1] != 'u') {
            ++i;
            continue;
        }
        const int u = unit(i);
        if (u < 0) continue;
        if (u >= 0xDC00 && u <= 0xDFFF) return true;
        if (u >= 0xD800 && u <= 0xDBFF) {
            const int low = unit(i + 6);
            if (low < 0xDC00 || low > 0xDFFF) return true;
            i += 11;
            continue;
        }
        i += 5;
    }
    return false;
}

bool strictTree(const json::Value& value) {
    if (value.isString()) return !loneSurrogate(value.raw());
    if (value.isArray()) {
        for (const auto& item : value.elements())
            if (!strictTree(item)) return false;
        return true;
    }
    if (value.isObject()) {
        std::set<std::string> names;
        for (const auto& [name, item] : value.members()) {
            if (!names.insert(name).second || !strictTree(item)) return false;
        }
        return true;
    }
    return true;
}

bool isInteger(const json::Value& v) { return v.isNumber() && v.isIntegerLiteral(); }

std::optional<std::int64_t> integer(const json::Value& v, std::int64_t low, std::int64_t high) {
    if (!isInteger(v)) return std::nullopt;
    const auto n = v.truncated();
    if (!n || *n < low || *n > high) return std::nullopt;
    return n;
}

// YYYY-MM-DDTHH:MM:SS.sssZ
bool instant(const json::Value& v) {
    const auto text = v.string();
    if (!text || text->size() != 24) return false;
    static constexpr std::string_view kShape = "dddd-dd-ddTdd:dd:dd.dddZ";
    for (std::size_t i = 0; i < 24; ++i) {
        const char c = (*text)[i];
        if (kShape[i] == 'd' ? !(c >= '0' && c <= '9') : c != kShape[i]) return false;
    }
    return true;
}

bool codeShape(std::string_view s) {
    return !s.empty() && s.size() <= 48 && s[0] >= 'a' && s[0] <= 'z' &&
           std::all_of(s.begin(), s.end(), [](char c) { return (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '_'; });
}

bool sha(std::string_view s) {
    return s.size() == 64 && std::all_of(s.begin(), s.end(), [](char c) { return (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'); });
}

std::optional<std::pair<std::string, std::int64_t>> runOf(const json::Value& v) {
    if (!v.isObject()) return std::nullopt;
    const auto id = v["id"].string();
    const auto seq = integer(v["seq"], 1, kMaxSeq);
    if (!id || id->empty() || id->size() > 64 || !seq) return std::nullopt;
    return std::pair{*id, *seq};
}

std::optional<json::Value> strictObject(json::Document& doc, std::string_view data, std::size_t maxBytes) {
    if (data.size() > maxBytes || !validUtf8(data) || !doc.parse(data, 32) || !doc.root().isObject() || !strictTree(doc.root())) return std::nullopt;
    return doc.root();
}

}  // namespace

std::optional<Message> parseMessage(std::string_view data, std::size_t maxBytes) {
    json::Document doc;
    const auto parsed = strictObject(doc, data, maxBytes);
    if (!parsed) return std::nullopt;
    const auto& m = *parsed;
    const auto type = m["type"].string();
    if (!type) return std::nullopt;
    Message out;
    if (*type == "ready") {
        if (!m["protocol"].equals("agent-speed-report")) return std::nullopt;
        for (const auto* key : {"version", "transportVersion"})
            if (!isInteger(m[key]) || m[key].truncated() != 1) return std::nullopt;
        if (!instant(m["serverTime"])) return std::nullopt;
        out.type = Message::Type::Ready;
        const std::array<std::pair<const char*, int*>, 6> fields = {
            std::pair{"minIntervalMs", &out.limits.minIntervalMs},         std::pair{"maxInFlight", &out.limits.maxInFlight},
            std::pair{"ackTimeoutMs", &out.limits.ackTimeoutMs},           std::pair{"maxMessageBytes", &out.limits.maxMessageBytes},
            std::pair{"pingIntervalSeconds", &out.limits.pingIntervalSeconds}, std::pair{"pongTimeoutSeconds", &out.limits.pongTimeoutSeconds}};
        for (const auto& [key, target] : fields) {
            const auto value = m[key];
            if (!isInteger(value)) return std::nullopt;
            *target = static_cast<int>(std::clamp<std::int64_t>(*value.truncated(), INT32_MIN, INT32_MAX));
        }
        return out;
    }
    if (*type == "ack") {
        const auto run = runOf(m["run"]);
        const auto status = m["status"].string();
        const auto digest = m["bodySha256"].string();
        if (!run || !status || (*status != "accepted" && *status != "duplicate") || !digest || !sha(*digest)) return std::nullopt;
        if (!instant(m["receivedAt"]) || !instant(m["acceptedAt"]) || !integer(m["processingMs"], 0, 900'000)) return std::nullopt;
        const auto rank = m["rank"];
        if (rank.valid() && !rank.isNull() && !integer(rank, 1, 100'000'000)) return std::nullopt;
        out.type = Message::Type::Ack;
        out.runId = run->first;
        out.seq = run->second;
        out.status = *status;
        out.bodySha256 = *digest;
        if (rank.valid() && !rank.isNull()) out.rank = rank.truncated();
        return out;
    }
    if (*type == "error") {
        const auto run = m["run"];
        if (!run.valid()) return std::nullopt;
        if (!run.isNull()) {
            const auto r = runOf(run);
            if (!r) return std::nullopt;
            out.runId = r->first;
            out.seq = r->second;
        }
        const auto status = integer(m["httpStatus"], 400, 599);
        const auto error = m["error"];
        if (!status || !error.isObject()) return std::nullopt;
        const auto code = error["code"].string();
        if (!code || !codeShape(*code)) return std::nullopt;
        const auto retry = error["retryAfterSeconds"];
        if (retry.valid() && !retry.isNull() && !integer(retry, 1, 86'400)) return std::nullopt;
        const auto serverTime = error["serverTime"];
        if (serverTime.valid() && !serverTime.isNull() && !integer(serverTime, 0, INT64_MAX)) return std::nullopt;
        if (!m["fatal"].isBool()) return std::nullopt;
        out.type = Message::Type::Error;
        out.httpStatus = static_cast<int>(*status);
        out.code = *code;
        if (retry.valid() && !retry.isNull()) out.retryAfterSeconds = retry.truncated();
        if (serverTime.valid() && !serverTime.isNull()) out.serverTime = serverTime.truncated();
        out.fatal = m["fatal"].isTrue();
        return out;
    }
    return out;
}

Rejection classifyRejection(int status, std::optional<std::string_view> body, std::optional<std::string_view> retryAfter) {
    std::optional<std::int64_t> header;
    if (retryAfter) {
        auto text = *retryAfter;
        while (!text.empty() && std::isspace(static_cast<unsigned char>(text.front()))) text.remove_prefix(1);
        while (!text.empty() && std::isspace(static_cast<unsigned char>(text.back()))) text.remove_suffix(1);
        if (!text.empty() && text.size() <= 9 && std::all_of(text.begin(), text.end(), [](char c) { return c >= '0' && c <= '9'; }))
            header = std::stoll(std::string(text));
    }
    Rejection out;
    std::optional<std::int64_t> wait;
    if (body && !body->empty()) {
        json::Document doc;
        if (const auto parsed = strictObject(doc, *body, 65'536)) {
            const auto error = (*parsed)["error"];
            if (const auto code = error.isObject() ? error["code"].string() : std::nullopt; code && codeShape(*code)) {
                out.code = *code;
                const auto given = error["retryAfterSeconds"];
                if (isInteger(given) && *given.truncated() >= 1) wait = given.truncated();
            }
        }
    }
    if (out.code.empty()) {
        switch (status) {
        case 401: out.code = "signature_invalid"; break;
        case 403: out.code = "forbidden"; break;
        case 404: out.code = "unknown_device"; break;
        case 410: out.code = "gone"; break;
        case 413: out.code = "payload_too_large"; break;
        case 429: out.code = "rate_limited"; break;
        default:
            if (status == 503 && header) out.code = "rate_limited";
            else if (status >= 400 && status < 500) out.code = "bad_request";
        }
    }
    out.retryAfterSeconds = wait ? wait : header;
    return out;
}

std::optional<std::int64_t> percentile(std::vector<std::int64_t> samples, int p) {
    if (samples.empty()) return std::nullopt;
    std::sort(samples.begin(), samples.end());
    const auto rank = std::max<std::int64_t>(1, static_cast<std::int64_t>(std::ceil(p * static_cast<double>(samples.size()) / 100.0)));
    return samples[static_cast<std::size_t>(rank - 1)];
}

const char* connectionName(Connection connection) {
    switch (connection) {
    case Connection::Closed: return "closed";
    case Connection::Connecting: return "connecting";
    case Connection::Opening: return "opening";
    case Connection::Open: return "open";
    case Connection::Closing: return "closing";
    }
    return "";
}

std::string Action::log() const {
    const auto at = std::to_string(t) + " ";
    switch (kind) {
    case Kind::Connect: return at + "connect";
    case Kind::Abort: return at + "abort";
    case Kind::Close: return at + "close 1000";
    case Kind::Build: return at + "build " + std::to_string(seq) + " " + text + " " + std::to_string(windowMs) + " " + (value < 0 ? "-" : std::to_string(value));
    case Kind::Write: return at + "write " + std::to_string(seq);
    case Kind::Delivered: return at + "delivered " + std::to_string(seq) + " " + text;
    case Kind::Dropped: return at + "dropped " + std::to_string(seq) + " " + text;
    case Kind::Saturated: return at + "saturated";
    case Kind::React: return at + "react " + text;
    case Kind::Stop: return at + "stop " + text;
    case Kind::Backoff: return at + "backoff " + std::to_string(value);
    case Kind::Wait: return at + "wait " + std::to_string(value);
    case Kind::Wake: return at + "wake " + std::to_string(value);
    }
    return at;
}

StreamScheduler::StreamScheduler(const StreamSettings& settings, std::int64_t heartbeatMs, bool available, std::function<double()> jitter)
    : jitter_(std::move(jitter)), settings_(settings), limits_(settings), heartbeatMs_(heartbeatMs), available_(available) {
    if (!jitter_) {
        jitter_ = [] {
            static thread_local std::mt19937_64 engine{std::random_device{}()};
            return std::uniform_real_distribution<double>(0.8, 1.2)(engine);
        };
    }
    newRun(std::nullopt);
}

void StreamScheduler::say(std::int64_t t, Action::Kind kind, int seq, std::string text, std::int64_t windowMs, std::int64_t value) {
    actions_.push_back({kind, t, seq, std::move(text), windowMs, value});
}

void StreamScheduler::newRun(std::optional<std::int64_t> t) {
    seq_ = 0;
    windowStart_ = t;
    observed_.reset();
    dirty_ = false;
    lastBuild_.reset();
    lastActivity_.reset();
}

void StreamScheduler::discard(std::int64_t t) {
    windowStart_ = t;
    observed_.reset();
}

void StreamScheduler::end(std::int64_t t, std::optional<Action::Kind> action) {
    if (action) say(t, *action);
    for (const auto& report : awaiting_) {
        say(t, Action::Kind::Dropped, report.seq, "unknown");
        ++counters_.unknown;
    }
    awaiting_.clear();
    conn_ = Connection::Closed;
    deadline_.reset();
    readyAt_.reset();
}

void StreamScheduler::failure(std::int64_t t) {
    ++failures_;
    const auto base = kBackoffMs[static_cast<std::size_t>(std::min<int>(failures_, static_cast<int>(kBackoffMs.size())) - 1)];
    reconnectAt_ = t + static_cast<std::int64_t>(std::llround(static_cast<double>(base) * jitter_()));
    say(t, Action::Kind::Backoff, 0, {}, 0, reconnectAt_);
}

void StreamScheduler::wait(std::int64_t t, std::optional<std::int64_t> seconds) {
    const auto s = seconds ? std::clamp<std::int64_t>(*seconds, 5, 3600) : 60;
    rateLimitedUntil_ = std::max(rateLimitedUntil_, t + s * 1000);
    say(t, Action::Kind::Wait, 0, {}, 0, rateLimitedUntil_);
}

void StreamScheduler::halt(std::int64_t t, const std::string& reason) {
    stop_ = reason;
    say(t, Action::Kind::Stop, 0, reason);
    if (conn_ != Connection::Closed) end(t, Action::Kind::Abort);
}

void StreamScheduler::build(std::int64_t t, const std::string& activity) {
    ++seq_;
    const auto age = observed_ ? std::optional<std::int64_t>(t - *observed_) : std::nullopt;
    say(t, Action::Kind::Build, seq_, activity, t - windowStart_.value_or(t), age.value_or(-1));
    ++counters_.built;
    const Report report{seq_, observed_, t};
    windowStart_ = t;
    observed_.reset();
    dirty_ = false;
    lastBuild_ = t;
    lastActivity_ = activity;
    if (age && *age > kStaleMs) {
        say(t, Action::Kind::Dropped, report.seq, "stale");
        ++counters_.stale;
        return;
    }
    awaiting_.push_back(report);
    say(t, Action::Kind::Write, report.seq);
}

void StreamScheduler::leave(std::int64_t t) {
    if (static_cast<int>(awaiting_.size()) < limits_.maxInFlight) build(t, "leaving");
    conn_ = Connection::Closing;
    deadline_ = t + kClosingMs;
    if (awaiting_.empty()) end(t, Action::Kind::Close);
}

std::vector<Action> StreamScheduler::tick(std::int64_t t, bool active, bool changed, std::optional<std::int64_t> observed, const std::vector<std::string>& events) {
    bool userLeave = false;
    for (const auto& event : events) {
        if (event == "pause") {
            userLeave |= !paused_;
            paused_ = true;
        } else if (event == "resume") {
            paused_ = false;
        } else if (event == "networkOff") {
            network_ = false;
            if (conn_ != Connection::Closed) end(t, Action::Kind::Abort);
        } else if (event == "networkOn") {
            network_ = true;
        } else if (event == "off" || event == "quit") {
            userLeave |= reporting_;
            reporting_ = false;
        }
    }
    if (prevTick_ && t - *prevTick_ > kGapMs) {
        discard(t);
        if (conn_ != Connection::Closed) end(t, Action::Kind::Abort);
    }
    prevTick_ = t;
    if (!windowStart_) windowStart_ = t;
    const auto previous = mode_;
    if (active) lastActive_ = t;
    const auto since = lastActive_ ? std::optional<std::int64_t>(t - *lastActive_) : std::nullopt;
    mode_ = !since || *since >= kQuietMs ? "quiet" : *since < kHoldMs ? "active" : "idle";
    if (changed || !lastActivity_ || mode_ != *lastActivity_) dirty_ = true;
    if (changed && observed) observed_ = observed_ ? std::min(*observed_, *observed) : *observed;
    if (conn_ == Connection::Closing && deadline_ && t >= *deadline_) end(t, Action::Kind::Abort);
    if (userLeave && conn_ == Connection::Open && previous != "quiet" && t >= rateLimitedUntil_) leave(t);
    if (!readyToSend()) {
        if (conn_ == Connection::Open) {
            if (!awaiting_.empty()) {
                conn_ = Connection::Closing;
                deadline_ = t + kClosingMs;
            } else {
                end(t, Action::Kind::Close);
            }
        } else if (conn_ == Connection::Connecting || conn_ == Connection::Opening) {
            end(t, Action::Kind::Abort);
        }
        discard(t);
        return take();
    }
    if (mode_ == "quiet") {
        if (previous != "quiet" && conn_ == Connection::Open) leave(t);
        else if (conn_ == Connection::Connecting || conn_ == Connection::Opening) end(t, Action::Kind::Abort);
        discard(t);
        return take();
    }
    if (conn_ == Connection::Closed) {
        if (t >= reconnectAt_ && t >= rateLimitedUntil_ && !waitingSettings_) {
            conn_ = Connection::Connecting;
            deadline_ = t + kConnectMs;
            say(t, Action::Kind::Connect);
        }
        discard(t);
        return take();
    }
    if (conn_ == Connection::Connecting || conn_ == Connection::Opening) {
        if (deadline_ && t >= *deadline_) {
            end(t, Action::Kind::Abort);
            failure(t);
        }
        discard(t);
        return take();
    }
    if (conn_ == Connection::Closing) {
        discard(t);
        return take();
    }
    if (std::any_of(awaiting_.begin(), awaiting_.end(), [&](const Report& r) { return t > r.written + limits_.ackTimeoutMs; })) {
        end(t, Action::Kind::Abort);
        failure(t);
        discard(t);
        return take();
    }
    bool due = dirty_ && (!lastBuild_ || t - *lastBuild_ >= limits_.minIntervalMs);
    const auto heartbeatFrom = lastBuild_ ? *lastBuild_ : readyAt_.value_or(t);
    due = due || t - heartbeatFrom >= heartbeatMs_;
    if (due) {
        if (static_cast<int>(awaiting_.size()) < limits_.maxInFlight) {
            build(t, mode_);
        } else {
            discard(t);
            dirty_ = false;
            ++counters_.saturated;
            say(t, Action::Kind::Saturated);
        }
    } else if (dirty_ && lastBuild_) {
        const auto at = *lastBuild_ + limits_.minIntervalMs;
        if (wakeAt_ != at) {
            wakeAt_ = at;
            say(t, Action::Kind::Wake, 0, {}, 0, at);
        }
    }
    return take();
}

std::vector<Action> StreamScheduler::opened(std::int64_t t) {
    if (conn_ == Connection::Connecting) {
        conn_ = Connection::Opening;
        deadline_ = t + kConnectMs;
    }
    return take();
}

namespace {
bool sameLimits(const StreamSettings& a, const StreamSettings& b) {
    return a.minIntervalMs == b.minIntervalMs && a.maxInFlight == b.maxInFlight && a.ackTimeoutMs == b.ackTimeoutMs &&
           a.maxMessageBytes == b.maxMessageBytes && a.pingIntervalSeconds == b.pingIntervalSeconds && a.pongTimeoutSeconds == b.pongTimeoutSeconds;
}
}  // namespace

std::vector<Action> StreamScheduler::ready(std::int64_t t, const StreamSettings& limits) {
    if (conn_ != Connection::Opening) return take();
    if (sameLimits(limits, settings_)) {
        conn_ = Connection::Open;
        deadline_.reset();
        readyAt_ = t;
        mismatched_ = false;
        limits_ = limits;
        discard(t);
        dirty_ = true;
        lastBuild_.reset();
        return take();
    }
    end(t, Action::Kind::Close);
    if (mismatched_) {
        halt(t, "mismatch");
    } else {
        mismatched_ = true;
        waitingSettings_ = true;
        react(t, "refreshDiscovery");
    }
    return take();
}

std::vector<Action> StreamScheduler::settingsChanged(std::int64_t t, const std::optional<StreamSettings>& settings) {
    if (!settings) {
        if (waitingSettings_) {
            waitingSettings_ = false;
            halt(t, "mismatch");
        } else {
            available_ = false;
        }
        return take();
    }
    settings_ = *settings;
    available_ = true;
    waitingSettings_ = false;
    return take();
}

std::vector<Action> StreamScheduler::retry(std::int64_t t) {
    stop_.reset();
    mismatched_ = waitingSettings_ = false;
    rejectStrikes_ = nonceStrikes_ = signatureStrikes_ = runConflicts_ = oversize_ = 0;
    failures_ = 0;
    reconnectAt_ = t;
    return take();
}

std::vector<Action> StreamScheduler::ack(std::int64_t t, int seq, const std::string& status, bool matches) {
    if (conn_ != Connection::Open && conn_ != Connection::Closing) return take();
    const auto it = matches ? std::find_if(awaiting_.begin(), awaiting_.end(), [&](const Report& r) { return r.seq == seq; }) : awaiting_.end();
    if (it == awaiting_.end()) {
        ++counters_.unmatched;
        return take();
    }
    const auto report = *it;
    awaiting_.erase(it);
    say(t, Action::Kind::Delivered, seq, status);
    if (status == "duplicate") ++counters_.duplicate;
    else ++counters_.accepted;
    lastDelivered_ = t;
    if (report.observed) {
        latency_.push_back(t - *report.observed);
        if (latency_.size() > kLatencySamples) latency_.erase(latency_.begin());
    }
    rejectStrikes_ = nonceStrikes_ = signatureStrikes_ = runConflicts_ = oversize_ = 0;
    if (readyAt_ && t - *readyAt_ >= kStableMs) failures_ = 0;
    if (conn_ == Connection::Closing && awaiting_.empty()) end(t, Action::Kind::Close);
    return take();
}

std::vector<Action> StreamScheduler::error(std::int64_t t, std::optional<int> seq, const std::string& code, int status,
                                           std::optional<std::int64_t> retryAfter, bool fatal) {
    if (conn_ != Connection::Open && conn_ != Connection::Closing) return take();
    if (seq) {
        const auto it = std::find_if(awaiting_.begin(), awaiting_.end(), [&](const Report& r) { return r.seq == *seq; });
        if (it != awaiting_.end()) {
            awaiting_.erase(it);
            say(t, Action::Kind::Dropped, *seq, "rejected");
            ++counters_.rejected;
        }
    }
    reactTo(t, code, status, retryAfter);
    if (fatal && conn_ != Connection::Closed) {
        end(t, Action::Kind::Abort);
        failure(t);
    } else if (conn_ == Connection::Closing && awaiting_.empty()) {
        end(t, Action::Kind::Close);
    }
    return take();
}

std::vector<Action> StreamScheduler::rejected(std::int64_t t, int status, std::optional<std::string> code, std::optional<std::int64_t> retryAfter) {
    if (conn_ != Connection::Connecting) return take();
    end(t, std::nullopt);
    if (!code) {
        const auto header = retryAfter ? std::optional<std::string>(std::to_string(*retryAfter)) : std::nullopt;
        const auto c = classifyRejection(status, std::nullopt, header ? std::optional<std::string_view>(*header) : std::nullopt).code;
        if (!c.empty()) code = c;
    }
    if (!code || !reactTo(t, *code, status, retryAfter)) failure(t);
    return take();
}

std::vector<Action> StreamScheduler::closed(std::int64_t t, int code) {
    if (conn_ == Connection::Closed) return take();
    const auto was = conn_;
    end(t, std::nullopt);
    if (was == Connection::Closing) return take();
    switch (code) {
    case 4001: halt(t, "apiKey"); break;
    case 4003: halt(t, "forbidden"); break;
    case 4004:
        react(t, "reRegister");
        failure(t);
        break;
    case 4009: halt(t, "replaced"); break;
    case 4410: halt(t, "gone"); break;
    case 4429:
    case 4503: wait(t, std::nullopt); break;
    case 1002:
    case 1003:
    case 1007: halt(t, "incompatible"); break;
    case 1009:
        react(t, "refreshDiscovery");
        if (++oversize_ >= 3) halt(t, "incompatible");
        else failure(t);
        break;
    default: failure(t);
    }
    return take();
}

bool StreamScheduler::reactTo(std::int64_t t, const std::string& code, std::optional<int> status, std::optional<std::int64_t> retryAfter) {
    if (code == "timestamp_out_of_window") {
        react(t, "correctClock");
        return false;
    }
    if (code == "nonce_replayed") {
        if (++nonceStrikes_ >= 3) {
            halt(t, "nonce");
            return true;
        }
        return false;
    }
    if (code == "signature_invalid") {
        react(t, "recheckRegistration");
        if (++signatureStrikes_ >= 3) {
            halt(t, "keyMismatch");
            return true;
        }
        if (conn_ != Connection::Closed) end(t, Action::Kind::Close);
        failure(t);
        return true;
    }
    if (code == "api_key_required" || code == "api_key_invalid") {
        halt(t, "apiKey");
        return true;
    }
    if (code == "forbidden") {
        halt(t, "forbidden");
        return true;
    }
    if (code == "unknown_device") {
        react(t, "reRegister");
        if (conn_ != Connection::Closed) end(t, Action::Kind::Abort);
        failure(t);
        return true;
    }
    if (code == "username_mismatch") {
        react(t, "rename");
        return false;
    }
    if (code == "seq_conflict") {
        if (conn_ != Connection::Closed) end(t, Action::Kind::Abort);
        react(t, "newRun");
        newRun(t);
        reconnectAt_ = t;
        return true;
    }
    if (code == "run_conflict") {
        if (conn_ != Connection::Closed) end(t, Action::Kind::Close);
        if (++runConflicts_ >= 2) halt(t, "clientState");
        else reconnectAt_ = t;
        return true;
    }
    if (code == "version_unsupported") {
        react(t, "refreshDiscovery");
        halt(t, "upgrade");
        return true;
    }
    if (code == "payload_too_large") {
        react(t, "refreshDiscovery");
        if (++oversize_ >= 3) {
            halt(t, "incompatible");
            return true;
        }
        return false;
    }
    if (code == "report_stale") return false;
    if (code == "rate_limited" || (status == 503 && retryAfter)) {
        if (conn_ != Connection::Closed) end(t, Action::Kind::Abort);
        wait(t, retryAfter);
        return true;
    }
    if (code == "gone") {
        halt(t, "gone");
        return true;
    }
    if (status && *status >= 400 && *status < 500) {
        if (++rejectStrikes_ >= 3) {
            halt(t, "rejected");
            return true;
        }
        return false;
    }
    if (conn_ != Connection::Closed) end(t, Action::Kind::Abort);
    failure(t);
    return true;
}

}  // namespace agentspeed::report::stream
