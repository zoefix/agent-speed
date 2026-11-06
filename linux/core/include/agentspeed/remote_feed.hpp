#pragma once

#include <cstdint>
#include <map>
#include <mutex>
#include <optional>
#include <set>
#include <string>
#include <variant>
#include <vector>

#include "agentspeed/remote_messages.hpp"

// The live side of the remote hosts (REMOTE 7): what a monitor applies, in order, and the mirror of every host that
// lets a new monitor (a restart, METRICS 5) start from the received state without reconnecting.
namespace agentspeed::remote {

// A configured remote host as the live views name it.
struct HostInfo {
    std::string id;
    std::string name;  // the display name (REMOTE 2.1)
    bool operator==(const HostInfo&) const = default;
};

namespace feed {
// The configured hosts in settings order; empty: no remote host is configured.
struct Hosts {
    std::vector<HostInfo> hosts;
};
// The host left the settings: its agents and responses leave every live value.
struct RemoveHost {
    std::string id;
};
struct Status {
    std::string host;
    HostStatus status;
};
struct Report {
    std::string host;
    SourceReport report;
};
struct Agent {
    RemoteAgent agent;
    std::optional<std::string> parentId;  // prefixed
};
struct AgentRemoved {
    std::string id;
};
// The host's complete parent map, replacing the previous one.
struct Parents {
    std::string host;
    std::map<std::string, std::string> links;
};
// `live` is false for seeded lines (before `ready`) and for a replay: no events.
struct Response {
    RemoteResponse response;
    bool live = false;
};
struct Tool {
    RemoteTool tool;
};
// The host cannot be seen: every agent of it becomes idle without an in-flight start.
struct Idle {
    std::string host;
};
}  // namespace feed

using FeedChange = std::variant<feed::Hosts, feed::RemoveHost, feed::Status, feed::Report, feed::Agent, feed::AgentRemoved,
                                feed::Parents, feed::Response, feed::Tool, feed::Idle>;

// Everything received from one host that a new monitor needs: agents, parents, today's responses and the retention
// window, status and source report. It turns each event into the changes a monitor applies, so a live monitor and a
// replay see the same state.
class HostMirror {
public:
    static constexpr std::int64_t kPruneIntervalMs = 60'000;
    static constexpr std::int64_t kRetentionMs = 30 * 60'000;

    explicit HostMirror(std::string hostId) : hostId_(std::move(hostId)) {}

    std::vector<FeedChange> apply(const HostEvent& event, std::int64_t nowMs, std::int64_t todayStartMs);
    // The host's state as seeded changes, for a new monitor.
    std::vector<FeedChange> replay() const;

    const std::string& hostId() const { return hostId_; }
    int connection() const { return connection_; }
    bool ready() const { return ready_; }
    const std::optional<HostStatus>& status() const { return status_; }
    const std::map<std::string, RemoteAgent>& agents() const { return agents_; }
    const std::map<std::string, std::string>& parents() const { return parents_; }
    const std::map<std::string, RemoteResponse>& responses() const { return responses_; }
    void setResponseLimit(std::size_t limit) { responseLimit_ = limit; }

    // Connected or seeding: the relay runs and its agents are current.
    static bool isLive(const Status& status);

private:
    std::vector<FeedChange> applyMessage(const RelayMessage& message, bool seeded, std::int64_t nowMs, std::int64_t todayStartMs);
    bool idleAgents();
    void store(const RemoteResponse& response, std::int64_t nowMs, std::int64_t todayStartMs);

    std::string hostId_;
    int connection_ = 0;
    std::optional<HostStatus> status_;
    bool ready_ = false;
    std::map<std::string, RemoteAgent> agents_;
    std::map<std::string, std::string> parents_;
    std::map<std::string, RemoteResponse> responses_;
    std::optional<SourceReport> report_;
    std::set<std::string> sentAgents_;
    bool sentParents_ = false;
    std::optional<std::int64_t> lastPruneMs_;
    std::size_t responseLimit_ = limits::kResponses;
};

// Every host's mirror and the changes the current monitor has not taken yet. Thread-safe: events arrive on the hub's
// thread, hosts from the app and `take` / `attach` from the monitor's thread.
class Feed {
public:
    // The configured hosts in settings order with their display names; a host that left is forgotten.
    void setHosts(std::vector<HostInfo> hosts);
    // One event of the hub; events of hosts that are not configured are dropped.
    void receive(const HostEvent& event, std::int64_t nowMs, std::int64_t todayStartMs);
    // The changes since the last `take` or `attach`, in order.
    std::vector<FeedChange> take();
    // A new monitor: the current state as seeded changes (the hosts first); pending changes are dropped.
    std::vector<FeedChange> attach();

    bool hasHosts() const;
    std::vector<HostInfo> hosts() const;
    // A copy of one host's mirror (tests, diagnostics).
    std::optional<HostMirror> mirror(const std::string& hostId) const;

private:
    std::vector<FeedChange> replayLocked();

    mutable std::mutex mutex_;
    std::vector<HostInfo> hosts_;
    std::map<std::string, HostMirror> mirrors_;
    std::vector<FeedChange> pending_;
    // Too many changes waited for a monitor that did not take them: the next take replays every host.
    bool overflowed_ = false;
};

}  // namespace agentspeed::remote
