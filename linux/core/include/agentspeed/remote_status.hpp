#pragma once

#include <cstdint>
#include <optional>
#include <string>
#include <string_view>

// A remote host's connection status (REMOTE 4.5) and how a finished `ssh` command maps to one.
namespace agentspeed::remote {

enum class StatusCode : std::uint8_t {
    Connecting,
    Installing,
    Seeding,
    Connected,
    Unreachable,
    AuthFailed,
    HostKeyUnknown,
    UnsupportedSystem,
    CannotVerify,
    UploadFailed,
    NotExecutable,
    VersionMismatch,
    ProtocolError,
    Exited,
    SshMissing,
    InvalidDestination,
    NetworkOff,
    Disabled,
};

struct Status {
    StatusCode code = StatusCode::Connecting;
    // Installing: the share of the relay's bytes written; seeding: the relay's progress (0–1).
    double fraction = 0;
    // UnsupportedSystem: `uname -s -m` as printed (printable ASCII, at most 64 characters).
    std::string system;
    // VersionMismatch: the relay's `hello.protocol`.
    std::int64_t relayProtocol = 0;
    // Exited: the exit status of `ssh` (128 + the signal number for a signal).
    int exitStatus = 0;

    static Status of(StatusCode code) {
        Status s;
        s.code = code;
        return s;
    }
    static Status installing(double fraction);
    static Status seeding(double fraction);
    static Status unsupportedSystem(std::string system);
    static Status versionMismatch(std::int64_t relayProtocol);
    static Status exited(int exitStatus);

    // The code the log keeps (never stderr, REMOTE 4.1).
    std::string_view name() const;
    // Connecting, installing or seeding: the UI shows progress.
    bool inProgress() const;
    bool isSeeding() const { return code == StatusCode::Seeding; }
    // Errors that need the user (REMOTE 4.4): retried every 5 minutes, at once on Retry or an edit.
    bool needsUser() const;
    // Failures retried with the normal back-off.
    bool retrying() const;
    bool operator==(const Status&) const = default;
};

std::string_view statusName(StatusCode code);

// What the UI shows for one host: the status and its timing facts (DESIGN 13 status line and tooltip).
struct HostStatus {
    static constexpr std::int64_t kSkewThresholdMs = 5000;

    Status status;
    // When the next attempt starts (Unix ms); nullopt when none is planned.
    std::optional<std::int64_t> retryAtMs;
    std::optional<std::int64_t> lastErrorAtMs;
    // δ = remote − local in milliseconds (REMOTE 6); nullopt before `hello`.
    std::optional<std::int64_t> clockOffsetMs;
    // The relay's `hello` facts of the current connection.
    std::optional<std::string> relayVersion, relayArch;
    // REMOTE 8.3: the host's last history batch exceeded the bounds (set from the history store, not the connection).
    bool historyTooLarge = false;

    // `remote.clock.skewed`: |δ| ≥ 5 s.
    bool clockSkewed() const;
    bool operator==(const HostStatus&) const = default;
};

// Maps how a command ended to a status (REMOTE 4.5). stderr is inspected here and nowhere else.
namespace classify {
inline constexpr std::size_t kMaxStderrBytes = 64 * 1024;
enum class Stage : std::uint8_t { Probe, Upload, Run };
// `stderrText`: the last (at most) 64 KiB the command wrote.
Status ended(int exitStatus, std::string_view stderrText, Stage stage);
}  // namespace classify

}  // namespace agentspeed::remote
