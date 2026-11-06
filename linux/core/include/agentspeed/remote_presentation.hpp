#pragma once

#include <cstdint>
#include <optional>
#include <string>
#include <string_view>
#include <vector>

#include "agentspeed/remote_host.hpp"
#include "agentspeed/remote_status.hpp"

// What a remote host's status says to the user (DESIGN 13 status line, dot, tooltip and words; REMOTE 4.5): string
// keys of `remote.*` with their arguments, so the app only fills them in its language.
namespace agentspeed::remote::ui {

// The status dot: connected green, a breathing grey dot while connecting, installing or seeding, orange while retrying,
// red when the user must act, hollow while off.
enum class Dot : std::uint8_t { Connected, Progress, Retrying, Attention, Idle };
Dot dotOf(StatusCode code);
std::string_view dotName(Dot dot);
// The status a code name (`statusName`) stands for.
std::optional<StatusCode> statusFromName(std::string_view name);

// A `strings.json` key and its arguments in order (`%@` and `%d` alike, as text).
struct Phrase {
    std::string key;
    std::vector<std::string> args;
    bool operator==(const Phrase&) const = default;
};

// The status line: the sentence, wrapped in `remote.status.retryIn` while a retry is counted down, or in
// `remote.status.retryingNow` once its deadline passed.
struct Line {
    Phrase sentence;
    std::optional<std::int64_t> retryInSeconds;
    bool retryingNow = false;
    bool operator==(const Line&) const = default;
};

// The status the settings show for a configured host: off, network off and an invalid destination at once, else the
// connection's last status (connecting before the first one). `historyTooLarge` comes from the history store.
HostStatus effective(const Host& host, bool networkEnabled, const std::optional<HostStatus>& live, bool historyTooLarge);

// The plain sentence of a status, without a countdown. `destination` names the host for `hostKeyUnknown`.
Phrase sentence(const Status& status, std::string_view destination);
// The status line; `sessions` are today's main sessions on the host (the connected line's agent count).
Line line(const HostStatus& status, std::string_view destination, int sessions, std::int64_t nowMs);
// While connected with |δ| ≥ 5 s (REMOTE 6): seconds below two minutes, else whole minutes.
std::optional<Phrase> clockNote(const HostStatus& status);
// While the host's history cannot be synced (REMOTE 8.3), whatever the connection does.
std::optional<Phrase> historyNote(const HostStatus& status);

// The facts of the status's tooltip: the helper's version while connected or seeding, else the last error time and, for
// `exited`, the exit status.
struct TooltipFacts {
    std::optional<std::string> relayVersion, relayArch;
    std::optional<std::int64_t> lastErrorAtMs;
    std::optional<int> exitStatus;
    bool empty() const { return !relayVersion && !lastErrorAtMs && !exitStatus; }
    bool operator==(const TooltipFacts&) const = default;
};
TooltipFacts tooltip(const HostStatus& status);

// The one word of a host that is not connected (dashboard, pickers).
Phrase word(StatusCode code);
// Retry now is offered while the host retries or needs the user, except for an invalid destination (only an edit helps).
bool canRetry(StatusCode code);
// The status line counts down to the next attempt.
bool countsDown(StatusCode code);

// The editor's message for a refused field (REMOTE 2.1); `folder` for the relay folder.
Phrase fieldError(FieldIssue issue, bool folder);
// The name's message when it is longer than 32 characters after trimming; nullopt otherwise.
std::optional<Phrase> nameError(std::string_view name);

}  // namespace agentspeed::remote::ui
