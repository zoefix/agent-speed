#include "fields.hpp"

// REMOTE 11.1: the relay's quota lines, validated by 5.6. A source a host does not read is ignored, like an unknown
// type; anything else that does not fit makes the line invalid.
namespace agentspeed::remote::detail {

std::size_t codePoints(std::string_view text);

namespace {

constexpr std::size_t kKeyBytes = 64;

std::optional<SourceKind> hostSource(const Fields& f) {
    const auto key = f.string("source", kKeyBytes);
    const auto source = sourceFromId(key);
    if (!source || !quotas::hostReads(*source)) return std::nullopt;
    return source;
}

quota::Window window(const Fields& w) {
    quota::Window out;
    const auto kind = quota::kindFromId(w.string("kind", 16));
    if (!kind) fail(LineError::Kind::OutOfRange, w.name("kind"));
    out.kind = *kind;
    if (auto name = w.optionalString("name", quotas::kNameCharacters * 4, TextRule::Model)) {
        if (codePoints(*name) > quotas::kNameCharacters) fail(LineError::Kind::TooLong, w.name("name"));
        if (!name->empty()) out.name = std::move(*name);
    }
    out.minutes = w.optionalInteger("minutes");
    if (out.minutes && (*out.minutes < 1 || *out.minutes > quotas::kMaxMinutes)) fail(LineError::Kind::OutOfRange, w.name("minutes"));
    if (out.kind == quota::Kind::Other && !out.minutes) fail(LineError::Kind::Missing, w.name("minutes"));
    out.usedPercent = w.number("usedPercent");
    if (out.usedPercent < 0 || out.usedPercent > quotas::kMaxUsedPercent) fail(LineError::Kind::OutOfRange, w.name("usedPercent"));
    out.resetsAtMs = w.optionalTime("resetsAtMs");
    return out;
}

HostQuota::Status status(const Fields& q) {
    const auto text = q.string("status", 32);
    for (const auto s : {HostQuota::Status::Ok, HostQuota::Status::NoCredential, HostQuota::Status::Expired, HostQuota::Status::Denied,
                         HostQuota::Status::Failed, HostQuota::Status::NetworkOff, HostQuota::Status::NoCertificates,
                         HostQuota::Status::NotRunning}) {
        if (hostQuotaStatusId(s) == text) return s;
    }
    fail(LineError::Kind::OutOfRange, q.name("status"));
}

}  // namespace

std::optional<RelayMessage> quotaLine(const Fields& f, const std::string& hostId) {
    const auto q = f.object("q");
    const auto source = hostSource(q);
    HostQuota out;
    out.hostId = hostId;
    out.snapshot.observedMs = q.time("observedMs");
    const auto items = q.array("windows", quotas::kWindows);
    for (std::size_t i = 0; i < items.size(); ++i) {
        out.snapshot.windows.push_back(window(Fields::of(items[i], q.name("windows.") + std::to_string(i))));
    }
    out.running = q.boolean("running");
    out.signedIn = q.boolean("signedIn");
    out.status = status(q);
    if (!source) return std::nullopt;
    out.snapshot.source = *source;
    return out;
}

std::optional<RelayMessage> quotaRemovedLine(const Fields& f, const std::string& hostId) {
    const auto source = hostSource(f);
    if (!source) return std::nullopt;
    return HostQuotaRemoved{hostId, *source};
}

RelayMessage quotaBridgeLine(const Fields& f, const std::string& hostId) {
    const auto text = f.string("state", 16);
    HostQuotaBridge out;
    out.hostId = hostId;
    bool known = false;
    for (const auto s : {HostQuotaBridge::State::Off, HostQuotaBridge::State::Waiting, HostQuotaBridge::State::Ok, HostQuotaBridge::State::Replaced,
                         HostQuotaBridge::State::Unreadable, HostQuotaBridge::State::Failed}) {
        if (hostBridgeStateId(s) == text) {
            out.state = s;
            known = true;
        }
    }
    if (!known) fail(LineError::Kind::OutOfRange, "state");
    out.settingsPath = f.string("settingsPath", limits::kCwdBytes, TextRule::Model);
    return out;
}

}  // namespace agentspeed::remote::detail
