#pragma once

#include <algorithm>
#include <climits>
#include <cstdint>
#include <functional>
#include <optional>
#include <vector>

#include "agentspeed/report.hpp"

// The report scheduler of METRICS 11.5 on a monotonic millisecond clock. It decides when a window is discarded, when a
// report is built and when an attempt is sent; the owner builds the bodies, performs the requests and reports each
// outcome back with finish(). At most one request is in flight and the outbox holds one report.
namespace agentspeed::report {

enum class Mode { Active, Idle, Quiet };
enum class Activity { Active, Idle, Leaving };
// User events of a tick (METRICS 11.5 step 1). Stop turns reporting off or quits: like a pause, it sends a leaving report.
enum class Event { Pause, Resume, NetworkOff, NetworkOn, Stop };

const char* activityName(Activity activity);

struct Ticket {
    int seq = 0;
    Activity activity = Activity::Active;
    std::int64_t builtAt = 0, windowMs = 0;
    bool single = false;  // the leaving report of a user action: one attempt, no retry
    bool operator==(const Ticket&) const = default;
};

// The outcome of one attempt (METRICS 11.5, Deliver).
struct Outcome {
    enum class Kind {
        Delivered,    // 2xx, adopting the response's intervalSeconds when present
        Transient,    // a network error, a timeout, 3xx, or 5xx without Retry-After: retried once
        RateLimited,  // 429, or 503 with Retry-After (already clamped to 5 s – 1 h)
        Again,        // nonce_replayed, or signature_invalid with a pending key: attempted again at once, once
        Rejected,     // any other error: lost (the owner applies the reaction of REPORTING 8.3)
    } kind = Kind::Delivered;
    std::optional<int> intervalSeconds;
    int retryAfterSeconds = 0;
};

struct Action {
    enum class Kind {
        Discard,  // empty the window without a report; keepingTick for the gap rule (METRICS 11.1)
        Build,    // take the window and the instantaneous values into a report body
        Send,     // sign and send the ticket's body (a fresh timestamp and nonce)
        Delivered,
        Lost,
    } kind = Kind::Discard;
    Ticket ticket;
    bool keepingTick = false;
};
using Actions = std::vector<Action>;

class Scheduler {
public:
    static constexpr std::int64_t kHoldMs = 15'000, kQuietMs = 600'000, kGapMs = 15'000, kRetryMs = 2'000, kMaxBackoffMs = 300'000;

    // `jitter` is the random backoff factor in [0.8, 1.2]; the fixtures use 1.
    explicit Scheduler(const Limits& limits, bool paused = false, bool networkOff = false, std::function<double()> jitter = {});

    // I: the discovery interval, or the intervalSeconds of the latest success (until the next launch).
    std::int64_t intervalMs() const { return adoptedIntervalMs_.value_or(limitsIntervalMs_); }
    // H, never below I.
    std::int64_t heartbeatMs() const { return std::max(limitsHeartbeatMs_, intervalMs()); }
    std::int64_t maxAgeMs() const { return maxAgeMs_; }
    int seq() const { return seq_; }
    std::optional<std::int64_t> windowStart() const { return windowStart_; }
    Mode mode() const { return mode_; }
    int failures() const { return failures_; }
    bool paused() const { return paused_; }
    bool networkOff() const { return networkOff_; }
    bool stopped() const { return stopped_; }
    std::int64_t backoffUntil() const { return backoffUntil_; }
    std::int64_t rateLimitedUntil() const { return rateLimitedUntil_; }
    bool busy() const { return inflight_.has_value() || waiting_.has_value(); }
    // The moment the next report is due in the current mode (the preview's countdown); nullopt while quiet.
    std::optional<std::int64_t> nextDueAt() const;

    // New limits from a refreshed discovery document; a success's intervalSeconds still applies on top.
    void update(const Limits& limits);
    // A new run (METRICS 11.5, Run): seq restarts and the outbox is emptied; backoff and pauses stay.
    Actions startRun();
    // One engine tick. `blocked` covers the readiness the scheduler does not track (on, confirmed, registered, no
    // error that needs the user).
    Actions tick(std::int64_t t, bool active, bool blocked, const std::vector<Event>& events = {});
    // The outcome of the attempt a Send action asked for.
    Actions finish(const Ticket& ticket, const Outcome& outcome, std::int64_t t);

private:
    struct Slot {
        Ticket ticket;
        std::int64_t nextAt = 0;
        int attempts = 0;
        bool sending = false, againUsed = false;
    };

    Ticket buildTicket(std::int64_t t, Activity activity, bool single, Actions& actions);
    void deliver(std::int64_t t, Actions& actions);
    void send(Slot slot, Actions& actions);
    void fail(const Ticket& ticket, std::int64_t t, Actions& actions);
    void dropOutbox(Actions& actions);

    std::function<double()> jitter_;
    std::int64_t limitsIntervalMs_ = 0, limitsHeartbeatMs_ = 0, maxAgeMs_ = 0;
    std::optional<std::int64_t> adoptedIntervalMs_, lastActive_, previousTick_, windowStart_;
    bool blocked_ = false, paused_ = false, networkOff_ = false, stopped_ = false;
    int seq_ = 0, failures_ = 0;
    Mode mode_ = Mode::Quiet;
    std::int64_t backoffUntil_ = INT64_MIN, rateLimitedUntil_ = INT64_MIN;
    std::optional<Slot> inflight_;
    std::optional<Ticket> waiting_;
};

}  // namespace agentspeed::report
