#include "agentspeed/remote_presentation.hpp"

#include <algorithm>
#include <cmath>
#include <cstdlib>

namespace agentspeed::remote::ui {

namespace {

std::string number(std::int64_t value) { return std::to_string(value); }

// Unicode code points of UTF-8 text (continuation bytes are not counted).
std::size_t codePointCount(std::string_view text) {
    std::size_t n = 0;
    for (const unsigned char byte : text) n += (byte & 0xC0) != 0x80 ? 1 : 0;
    return n;
}

std::string_view trimmed(std::string_view text) {
    const auto space = [](char c) { return c == ' ' || c == '\t' || c == '\n' || c == '\r' || c == '\f' || c == '\v'; };
    while (!text.empty() && space(text.front())) text.remove_prefix(1);
    while (!text.empty() && space(text.back())) text.remove_suffix(1);
    return text;
}

}  // namespace

Dot dotOf(StatusCode code) {
    switch (code) {
        case StatusCode::Connected: return Dot::Connected;
        case StatusCode::Connecting:
        case StatusCode::Installing:
        case StatusCode::Seeding: return Dot::Progress;
        case StatusCode::NetworkOff:
        case StatusCode::Disabled: return Dot::Idle;
        default: return Status::of(code).needsUser() ? Dot::Attention : Dot::Retrying;
    }
}

std::string_view dotName(Dot dot) {
    switch (dot) {
        case Dot::Connected: return "connected";
        case Dot::Progress: return "progress";
        case Dot::Retrying: return "retrying";
        case Dot::Attention: return "attention";
        case Dot::Idle: return "idle";
    }
    return "idle";
}

std::optional<StatusCode> statusFromName(std::string_view name) {
    for (auto value = static_cast<int>(StatusCode::Connecting); value <= static_cast<int>(StatusCode::Disabled); ++value) {
        const auto code = static_cast<StatusCode>(value);
        if (statusName(code) == name) return code;
    }
    return std::nullopt;
}

HostStatus effective(const Host& host, bool networkEnabled, const std::optional<HostStatus>& live, bool historyTooLarge) {
    HostStatus out;
    if (!networkEnabled) {
        out.status = Status::of(StatusCode::NetworkOff);
    } else if (!host.enabled) {
        out.status = Status::of(StatusCode::Disabled);
    } else if (!host.connectable()) {
        out.status = Status::of(StatusCode::InvalidDestination);
    } else if (live && live->status.code != StatusCode::Disabled && live->status.code != StatusCode::NetworkOff) {
        out = *live;
    } else {
        out.status = Status::of(StatusCode::Connecting);
    }
    out.historyTooLarge = historyTooLarge || (live && live->historyTooLarge);
    return out;
}

Phrase sentence(const Status& status, std::string_view destination) {
    switch (status.code) {
        case StatusCode::Connecting: return {"remote.status.connecting", {}};
        case StatusCode::Installing: return {"remote.status.installing", {}};
        case StatusCode::Seeding: {
            const double fraction = std::clamp(status.fraction, 0.0, 1.0);
            return {"remote.status.seeding", {number(static_cast<std::int64_t>(std::floor(fraction * 100)))}};
        }
        case StatusCode::Connected: return {"remote.status.connected", {}};
        case StatusCode::Unreachable: return {"remote.status.unreachable", {}};
        case StatusCode::AuthFailed: return {"remote.status.authFailed", {}};
        case StatusCode::HostKeyUnknown: return {"remote.status.hostKeyUnknown", {std::string(destination)}};
        case StatusCode::UnsupportedSystem: return {"remote.status.unsupportedSystem", {status.system}};
        case StatusCode::CannotVerify: return {"remote.status.cannotVerify", {}};
        case StatusCode::UploadFailed: return {"remote.status.uploadFailed", {}};
        case StatusCode::NotExecutable: return {"remote.status.notExecutable", {}};
        case StatusCode::VersionMismatch: return {"remote.status.versionMismatch", {}};
        case StatusCode::ProtocolError: return {"remote.status.protocolError", {}};
        case StatusCode::Exited: return {"remote.status.exited", {}};
        case StatusCode::SshMissing: return {"remote.status.sshMissing", {}};
        case StatusCode::InvalidDestination: return {"remote.status.invalidDestination", {}};
        case StatusCode::NetworkOff: return {"remote.status.networkOff", {}};
        case StatusCode::Disabled: return {"remote.status.disabled", {}};
    }
    return {"remote.status.connecting", {}};
}

Line line(const HostStatus& status, std::string_view destination, int sessions, std::int64_t nowMs) {
    Line out;
    if (status.status.code == StatusCode::Connected && sessions > 0) {
        out.sentence = {sessions == 1 ? "remote.status.connected.one" : "remote.status.connected.agents", {number(sessions)}};
        return out;
    }
    out.sentence = sentence(status.status, destination);
    if (countsDown(status.status.code) && status.retryAtMs) {
        // Whole seconds, rounded up, so the line never reads 0 s before the attempt starts.
        const auto remaining = *status.retryAtMs - nowMs;
        if (remaining > 0) {
            out.retryInSeconds = (remaining + 999) / 1000;
        } else {
            out.retryingNow = true;
        }
    }
    return out;
}

std::optional<Phrase> clockNote(const HostStatus& status) {
    if (status.status.code != StatusCode::Connected || !status.clockSkewed() || !status.clockOffsetMs) return std::nullopt;
    const auto seconds = static_cast<std::int64_t>(std::llround(std::abs(static_cast<double>(*status.clockOffsetMs)) / 1000.0));
    if (seconds < 120) return Phrase{"remote.clock.skewed", {number(seconds)}};
    return Phrase{"remote.clock.skewedMinutes", {number(static_cast<std::int64_t>(std::llround(static_cast<double>(seconds) / 60.0)))}};
}

std::optional<Phrase> historyNote(const HostStatus& status) {
    if (!status.historyTooLarge) return std::nullopt;
    return Phrase{"remote.status.historyTooLarge", {}};
}

TooltipFacts tooltip(const HostStatus& status) {
    TooltipFacts out;
    switch (status.status.code) {
        case StatusCode::Connected:
        case StatusCode::Seeding:
            if (status.relayVersion && status.relayArch) {
                out.relayVersion = status.relayVersion;
                out.relayArch = status.relayArch;
            }
            break;
        case StatusCode::Connecting:
        case StatusCode::Installing:
        case StatusCode::Disabled:
        case StatusCode::NetworkOff: break;
        default:
            out.lastErrorAtMs = status.lastErrorAtMs;
            if (status.status.code == StatusCode::Exited) out.exitStatus = status.status.exitStatus;
    }
    return out;
}

Phrase word(StatusCode code) {
    switch (code) {
        case StatusCode::Connected: return {"remote.status.connected", {}};
        case StatusCode::Disabled: return {"remote.status.disabled", {}};
        case StatusCode::NetworkOff: return {"remote.word.offline", {}};
        default:
            switch (dotOf(code)) {
                case Dot::Progress: return {"remote.word.connecting", {}};
                case Dot::Retrying: return {"remote.word.retrying", {}};
                default: return {"remote.word.attention", {}};
            }
    }
}

bool canRetry(StatusCode code) {
    const auto dot = dotOf(code);
    return (dot == Dot::Retrying || dot == Dot::Attention) && code != StatusCode::InvalidDestination;
}

bool countsDown(StatusCode code) { return Status::of(code).retrying(); }

Phrase fieldError(FieldIssue issue, bool folder) {
    switch (issue) {
        case FieldIssue::Empty: return {"remote.error.empty", {}};
        case FieldIssue::TooLong:
            return {"remote.error.tooLong", {number(static_cast<std::int64_t>(folder ? validation::kFolderLimit : validation::kDestinationLimit))}};
        case FieldIssue::LeadingDash: return {"remote.error.leadingDash", {}};
        case FieldIssue::ForbiddenCharacter: return {folder ? "remote.error.folderCharacter" : "remote.error.forbiddenCharacter", {}};
        case FieldIssue::NotAbsolute: return {"remote.error.notAbsolute", {}};
        case FieldIssue::UnsafeComponent: return {"remote.error.unsafeComponent", {}};
    }
    return {"remote.error.empty", {}};
}

std::optional<Phrase> nameError(std::string_view name) {
    if (codePointCount(trimmed(name)) <= validation::kNameLimit) return std::nullopt;
    return Phrase{"remote.error.tooLong", {number(static_cast<std::int64_t>(validation::kNameLimit))}};
}

}  // namespace agentspeed::remote::ui
