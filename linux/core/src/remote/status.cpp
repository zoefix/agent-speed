#include <algorithm>
#include <array>

#include "agentspeed/remote_status.hpp"

namespace agentspeed::remote {

Status Status::installing(double fraction) {
    auto s = of(StatusCode::Installing);
    s.fraction = fraction;
    return s;
}

Status Status::seeding(double fraction) {
    auto s = of(StatusCode::Seeding);
    s.fraction = fraction;
    return s;
}

Status Status::unsupportedSystem(std::string system) {
    auto s = of(StatusCode::UnsupportedSystem);
    s.system = std::move(system);
    return s;
}

Status Status::versionMismatch(std::int64_t relayProtocol) {
    auto s = of(StatusCode::VersionMismatch);
    s.relayProtocol = relayProtocol;
    return s;
}

Status Status::exited(int exitStatus) {
    auto s = of(StatusCode::Exited);
    s.exitStatus = exitStatus;
    return s;
}

std::string_view statusName(StatusCode code) {
    switch (code) {
        case StatusCode::Connecting: return "connecting";
        case StatusCode::Installing: return "installing";
        case StatusCode::Seeding: return "seeding";
        case StatusCode::Connected: return "connected";
        case StatusCode::Unreachable: return "unreachable";
        case StatusCode::AuthFailed: return "authFailed";
        case StatusCode::HostKeyUnknown: return "hostKeyUnknown";
        case StatusCode::UnsupportedSystem: return "unsupportedSystem";
        case StatusCode::CannotVerify: return "cannotVerify";
        case StatusCode::UploadFailed: return "uploadFailed";
        case StatusCode::NotExecutable: return "notExecutable";
        case StatusCode::VersionMismatch: return "versionMismatch";
        case StatusCode::ProtocolError: return "protocolError";
        case StatusCode::Exited: return "exited";
        case StatusCode::SshMissing: return "sshMissing";
        case StatusCode::InvalidDestination: return "invalidDestination";
        case StatusCode::NetworkOff: return "networkOff";
        case StatusCode::Disabled: return "disabled";
    }
    return "exited";
}

std::string_view Status::name() const { return statusName(code); }

bool Status::inProgress() const {
    return code == StatusCode::Connecting || code == StatusCode::Installing || code == StatusCode::Seeding;
}

bool Status::needsUser() const {
    switch (code) {
        case StatusCode::AuthFailed:
        case StatusCode::HostKeyUnknown:
        case StatusCode::UnsupportedSystem:
        case StatusCode::NotExecutable:
        case StatusCode::CannotVerify:
        case StatusCode::InvalidDestination:
        case StatusCode::SshMissing:
        case StatusCode::UploadFailed:
        case StatusCode::VersionMismatch: return true;
        default: return false;
    }
}

bool Status::retrying() const {
    return code == StatusCode::Unreachable || code == StatusCode::ProtocolError || code == StatusCode::Exited;
}

bool HostStatus::clockSkewed() const {
    if (!clockOffsetMs) return false;
    const auto magnitude = *clockOffsetMs < 0 ? -*clockOffsetMs : *clockOffsetMs;
    return magnitude >= kSkewThresholdMs;
}

namespace classify {

namespace {

bool contains(std::string_view text, std::string_view marker) { return text.find(marker) != std::string_view::npos; }

constexpr std::array<std::string_view, 7> kUnreachable = {"Could not resolve", "timed out",      "Connection refused",
                                                         "Connection reset",  "No route",       "Network is unreachable",
                                                         "Connection closed"};

bool blank(std::string_view text) {
    return std::all_of(text.begin(), text.end(), [](char c) { return c == ' ' || c == '\t' || c == '\r' || c == '\n'; });
}

// The upload script's exit statuses (REMOTE 4.2).
constexpr int kNoHashToolStatus = 5;

}  // namespace

Status ended(int exitStatus, std::string_view stderrText, Stage stage) {
    const auto text = stderrText.size() > kMaxStderrBytes ? stderrText.substr(stderrText.size() - kMaxStderrBytes) : stderrText;
    if (contains(text, "Host key verification failed")) return Status::of(StatusCode::HostKeyUnknown);
    if (exitStatus == 255) {
        // "ssh: connect to host h port 22: Permission denied" is a local firewall, not a refused login: every connect
        // failure is unreachable.
        if (contains(text, "connect to host ")) return Status::of(StatusCode::Unreachable);
        if (contains(text, "Permission denied")) return Status::of(StatusCode::AuthFailed);
        if (std::any_of(kUnreachable.begin(), kUnreachable.end(), [&](std::string_view m) { return contains(text, m); })) {
            return Status::of(StatusCode::Unreachable);
        }
        // LogLevel=ERROR hides "Connection to … closed by remote host"; the relay itself never exits with 255.
        if (blank(text)) return Status::of(StatusCode::Unreachable);
        return Status::exited(exitStatus);
    }
    switch (stage) {
        case Stage::Run:
            if (exitStatus == 126 || contains(text, "Permission denied") || contains(text, "cannot execute")) {
                return Status::of(StatusCode::NotExecutable);
            }
            break;
        case Stage::Upload:
            if (exitStatus == kNoHashToolStatus) return Status::of(StatusCode::CannotVerify);
            if (exitStatus != 0) return Status::of(StatusCode::UploadFailed);
            break;
        case Stage::Probe: break;
    }
    return Status::exited(exitStatus);
}

}  // namespace classify

}  // namespace agentspeed::remote
