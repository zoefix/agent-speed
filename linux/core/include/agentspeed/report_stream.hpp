#pragma once

#include <cstdint>
#include <functional>
#include <optional>
#include <string>
#include <string_view>
#include <utility>
#include <vector>

#include "agentspeed/report.hpp"

// The WebSocket transport of live reporting (METRICS 11.9, REPORTING-WEBSOCKET; spec/tools/report_websocket.py):
// service messages, refused handshakes, latency percentiles and the stream scheduler.
namespace agentspeed::report::stream {

constexpr std::string_view kPath = "/v1/reports/ws";
constexpr std::string_view kSubprotocol = "agent-speed-report.v1";

// A service message as the client sees it; nullopt when it is invalid (the connection then counts as close 1002).
struct Message {
    enum class Type { Ready, Ack, Error, Other } type = Type::Other;
    StreamSettings limits;                         // ready
    std::string runId;                             // ack, error (empty: run null)
    std::int64_t seq = 0;                          // ack, error (0: run null)
    std::string status, bodySha256;                // ack
    std::optional<std::int64_t> rank;              // ack
    int httpStatus = 0;                            // error
    std::string code;                              // error
    std::optional<std::int64_t> retryAfterSeconds;  // error
    std::optional<std::int64_t> serverTime;         // error
    bool fatal = false;                             // error
};
std::optional<Message> parseMessage(std::string_view data, std::size_t maxBytes = 65'536);

// A handshake answered without 101: the v1 error code (empty for a plain failure) and the wait.
struct Rejection {
    std::string code;
    std::optional<std::int64_t> retryAfterSeconds;
};
Rejection classifyRejection(int status, std::optional<std::string_view> body, std::optional<std::string_view> retryAfter);
// Nearest rank: the smallest sample with at least p % of the samples at or below it.
std::optional<std::int64_t> percentile(std::vector<std::int64_t> samples, int p);

enum class Connection { Closed, Connecting, Opening, Open, Closing };
const char* connectionName(Connection connection);

struct Action {
    enum class Kind { Connect, Abort, Close, Build, Write, Delivered, Dropped, Saturated, React, Stop, Backoff, Wait, Wake } kind;
    std::int64_t t = 0;
    int seq = 0;
    std::string text;  // activity (build), status (delivered), why (dropped), name (react), reason (stop)
    std::int64_t windowMs = 0, value = 0;  // value: the observation age of a build (−1 none), or the time of backoff/wait/wake
    std::string log() const;  // `<t> <action>` as in the vectors
};

struct Counters {
    std::int64_t built = 0, accepted = 0, duplicate = 0, rejected = 0, stale = 0, saturated = 0, unknown = 0, unmatched = 0;
};

// METRICS 11.9 on a monotonic millisecond clock. The owner performs every action in order: connect, abort, close,
// build (a body of the window), write (send it), and acts on the reactions; writes are instant.
class StreamScheduler {
public:
    static constexpr std::int64_t kConnectMs = 10'000, kClosingMs = 1000, kStaleMs = 1000, kStableMs = 60'000;
    static constexpr std::size_t kLatencySamples = 512;

    StreamScheduler(const StreamSettings& settings, std::int64_t heartbeatMs, bool available = true, std::function<double()> jitter = {});

    // One tick: an engine tick, a snapshot after a read, or a wake-up the scheduler asked for. `events`: pause,
    // resume, networkOff, networkOn, off, quit.
    std::vector<Action> tick(std::int64_t t, bool active, bool changed, std::optional<std::int64_t> observed, const std::vector<std::string>& events = {});
    std::vector<Action> opened(std::int64_t t);
    std::vector<Action> ready(std::int64_t t, const StreamSettings& limits);
    std::vector<Action> settingsChanged(std::int64_t t, const std::optional<StreamSettings>& settings);
    std::vector<Action> retry(std::int64_t t);
    std::vector<Action> ack(std::int64_t t, int seq, const std::string& status, bool matches);
    std::vector<Action> error(std::int64_t t, std::optional<int> seq, const std::string& code, int status, std::optional<std::int64_t> retryAfter,
                              bool fatal);
    std::vector<Action> rejected(std::int64_t t, int status, std::optional<std::string> code, std::optional<std::int64_t> retryAfter);
    std::vector<Action> closed(std::int64_t t, int code);
    // The owner's readiness of 11.5 (on, confirmed, registered, no error that needs the user).
    void setReporting(bool on) { reporting_ = on; }
    void setHeartbeat(std::int64_t ms) { heartbeatMs_ = ms; }

    Connection connection() const { return conn_; }
    int seq() const { return seq_; }
    int failures() const { return failures_; }
    const std::optional<std::string>& stop() const { return stop_; }
    std::optional<std::int64_t> lastDelivered() const { return lastDelivered_; }
    std::optional<std::int64_t> wakeAt() const { return wakeAt_; }
    void clearWake() { wakeAt_.reset(); }
    const Counters& counters() const { return counters_; }
    const std::vector<std::int64_t>& latency() const { return latency_; }
    const StreamSettings& limits() const { return limits_; }
    const StreamSettings& settings() const { return settings_; }
    std::int64_t rateLimitedUntil() const { return rateLimitedUntil_; }
    std::int64_t reconnectAt() const { return reconnectAt_; }
    bool awaitingSettings() const { return waitingSettings_; }
    std::size_t awaiting() const { return awaiting_.size(); }
    std::string mode() const { return mode_; }
    // A new run (seq_conflict or a new run of the owner).
    void newRun(std::optional<std::int64_t> t);

private:
    struct Report {
        int seq = 0;
        std::optional<std::int64_t> observed;
        std::int64_t written = 0;
    };
    void say(std::int64_t t, Action::Kind kind, int seq = 0, std::string text = {}, std::int64_t windowMs = 0, std::int64_t value = 0);
    void discard(std::int64_t t);
    void end(std::int64_t t, std::optional<Action::Kind> action);
    void failure(std::int64_t t);
    void wait(std::int64_t t, std::optional<std::int64_t> seconds);
    void halt(std::int64_t t, const std::string& reason);
    void react(std::int64_t t, const std::string& name) { say(t, Action::Kind::React, 0, name); }
    bool readyToSend() const { return reporting_ && network_ && !paused_ && !stop_ && available_; }
    void build(std::int64_t t, const std::string& activity);
    void leave(std::int64_t t);
    bool reactTo(std::int64_t t, const std::string& code, std::optional<int> status, std::optional<std::int64_t> retryAfter);
    std::vector<Action> take() { return std::exchange(actions_, {}); }

    std::function<double()> jitter_;
    StreamSettings settings_, limits_;
    std::int64_t heartbeatMs_;
    bool available_;
    std::vector<Action> actions_;
    Counters counters_;
    int rejectStrikes_ = 0, nonceStrikes_ = 0, signatureStrikes_ = 0, runConflicts_ = 0, oversize_ = 0;
    std::vector<std::int64_t> latency_;
    bool reporting_ = true, network_ = true, paused_ = false;
    std::string mode_ = "quiet";
    std::optional<std::int64_t> lastActive_, prevTick_;
    Connection conn_ = Connection::Closed;
    std::optional<std::int64_t> deadline_, readyAt_;
    std::vector<Report> awaiting_;
    int failures_ = 0;
    std::int64_t reconnectAt_ = 0, rateLimitedUntil_ = 0;
    std::optional<std::string> stop_;
    bool mismatched_ = false, waitingSettings_ = false;
    std::optional<std::int64_t> lastDelivered_, wakeAt_;
    int seq_ = 0;
    std::optional<std::int64_t> windowStart_, observed_, lastBuild_;
    bool dirty_ = false;
    std::optional<std::string> lastActivity_;
};

}  // namespace agentspeed::report::stream
