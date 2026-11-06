#pragma once

#include <cstdint>
#include <map>
#include <optional>
#include <string>
#include <string_view>
#include <variant>
#include <vector>

#include "agentspeed/actions.hpp"
#include "agentspeed/history.hpp"
#include "agentspeed/model.hpp"
#include "agentspeed/modes.hpp"
#include "agentspeed/perf_history.hpp"
#include "agentspeed/remote_status.hpp"

// The relay's lines as the app reads them (REMOTE 5.2, 5.4), already validated (5.6). Agent ids are prefixed with
// `ssh://<host id>/` (REMOTE 2.2); times are the host's until the connection converts them (REMOTE 6).
namespace agentspeed::remote {

// The host id of this computer (REMOTE 2.2); never a remote host's id.
inline constexpr std::string_view kLocalHost = "local";

inline constexpr std::int64_t kProtocol = 1;

// REMOTE 5.4, 5.6: per-line and per-host bounds.
namespace limits {
inline constexpr std::size_t kAgents = 4096;
inline constexpr std::size_t kResponses = 200'000;
inline constexpr std::size_t kActionsPerAgent = 64;
inline constexpr std::size_t kFactsPerModeList = 20;
inline constexpr int kInvalidLines = 100;
inline constexpr std::size_t kAgentIdBytes = 4096;
inline constexpr std::size_t kKeyBytes = 512;
inline constexpr std::size_t kProjectBytes = 256;
inline constexpr std::size_t kCwdBytes = 4096;
inline constexpr std::size_t kModelBytes = 256;
inline constexpr std::size_t kProviderBytes = 64;
inline constexpr std::size_t kToolBytes = 128;
inline constexpr std::size_t kErrorDetailCharacters = 200;
inline constexpr std::size_t kHistoryList = 4096;
inline constexpr std::size_t kRelayLineBytes = 1u << 20;
inline constexpr std::size_t kAppLineBytes = 4u << 20;
}  // namespace limits

// `ssh://<host id>/`.
std::string agentPrefix(std::string_view hostId);

struct Hello {
    std::int64_t protocol = 0;
    // Empty when a relay of another protocol sent none.
    std::string version, arch;
    std::int64_t nowMs = 0;
    bool operator==(const Hello&) const = default;
};

struct Progress {
    double value = 0;
    bool operator==(const Progress&) const = default;
};

// An agent's complete state (REMOTE 5.4).
struct RemoteAgent {
    std::string hostId;
    // `ssh://<host id>/<relay id>`.
    std::string id;
    // As the relay sent it (a path on the host): display fallbacks only.
    std::string relayId;
    SourceKind source = SourceKind::Codex;
    std::optional<std::string> project, cwd, model;
    bool subagent = false;
    Phase phase = Phase::Idle;
    std::optional<std::int64_t> inFlightStartMs, lastLineMs;
    ExecutionModes modes, sessionModes, configuredModes;
    // At most 64, oldest first.
    std::vector<work::Action> actions;
    bool operator==(const RemoteAgent&) const;
};

struct AgentRemoved {
    std::string id;  // prefixed
    bool operator==(const AgentRemoved&) const = default;
};

// The complete parent map, child → parent, both prefixed.
struct Parents {
    std::map<std::string, std::string> links;
    bool operator==(const Parents&) const = default;
};

// A `response` line: the record (agent id prefixed, no cost) with its host and the relay's project name.
struct RemoteResponse {
    std::string hostId;
    ParsedResponse record;
    bool completion = false;
    std::string project;
    bool operator==(const RemoteResponse&) const = default;
};

// A `tool` line (METRICS 11.10 Counting); `action.tool` and `action.digest` are empty.
struct RemoteTool {
    std::string hostId;
    std::string agentId;  // prefixed
    SourceKind source = SourceKind::Codex;
    work::Action action;
    bool operator==(const RemoteTool&) const;
};

struct Ready {
    bool operator==(const Ready&) const = default;
};

struct RemoteSourceState {
    SourceKind source = SourceKind::Codex;
    bool available = false;
    int sessions = 0;
    bool unsupported = false;
    ExecutionModes configuredModes;
    bool operator==(const RemoteSourceState&) const = default;
};

// A `status` line: sources this app knows, in the relay's order.
struct SourceReport {
    std::vector<RemoteSourceState> sources;
    std::int64_t malformed = 0, unreadable = 0;
    bool partialHistory = false;
    bool operator==(const SourceReport&) const = default;
};

struct ClockReply {
    std::int64_t id = 0, nowMs = 0;
    bool operator==(const ClockReply&) const = default;
};

// A `historyDay` line. `aggregateJson` is the aggregate's exact text on the line (stored and covered by the digest).
struct HistoryDay {
    std::string day;
    SourceKind source = SourceKind::Codex;
    history::DayAggregate aggregate;
    std::string aggregateJson;
    std::string digest;  // 16 lowercase hex digits
    // `"<day>/<source>"`, the key of the `history` request's digests.
    std::string digestKey() const;
    bool operator==(const HistoryDay&) const;
};

// A `historyPerf` line; `recordJson` is the record's exact text.
struct HistoryPerf {
    std::string day;
    SourceKind source = SourceKind::Codex;
    std::string model;  // "" when unknown
    perf::Record record;
    std::string recordJson;
    bool operator==(const HistoryPerf&) const = default;
};

struct HistoryDone {
    std::int64_t days = 0, analyzed = 0;
    bool operator==(const HistoryDone&) const = default;
};

struct HistoryProgress {
    double value = 0;
    bool operator==(const HistoryProgress&) const = default;
};

struct RelayError {
    enum class Code : std::uint8_t { BadStart, HistoryFailed, Internal, Other };
    Code code = Code::Internal;
    std::string codeText;
    // At most 200 characters of plain text; displayed only, never logged.
    std::string detail;
    // Every code but historyFailed ends the relay.
    bool fatal() const { return code != Code::HistoryFailed; }
    bool operator==(const RelayError&) const = default;
};

using RelayMessage = std::variant<Hello, Progress, RemoteAgent, AgentRemoved, Parents, RemoteResponse, RemoteTool, Ready,
                                  SourceReport, ClockReply, HistoryDay, HistoryPerf, HistoryDone, HistoryProgress, RelayError>;

// What a host's connection reports, in order.
struct HostEvent {
    // A validated relay line, times converted to local time. `seeded`: before `ready` (apply without events).
    struct Message {
        RelayMessage message;
        bool seeded = false;
    };
    // The history schedule (REMOTE 8.2) wants a sync: answer with the digests held for the host.
    struct HistoryDue {};

    std::string hostId;
    // Counts the host's connection attempts (unique across connections); 0 for a status the hub reports itself.
    int connection = 0;
    std::variant<HostStatus, Message, HistoryDue> kind;
};

}  // namespace agentspeed::remote
