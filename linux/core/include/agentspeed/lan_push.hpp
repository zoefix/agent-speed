#pragma once

#include <cstdint>
#include <optional>
#include <string_view>

// When a push receiver sends (LAN 4.7) and how an HTTP target backs off (5.3); what a target's row shows (5.5).
namespace agentspeed::lan {

enum class Receiver { Stream, Udp, Http };

struct PushRule {
    std::int64_t minimumMs = 500, heartbeatMs = 5000;
};
PushRule pushRule(Receiver receiver);

class PushScheduler {
public:
    // Ticks come every 500 ms with timer jitter: an interval counts as passed within a fifth of a tick, so "every
    // tick" stays every tick.
    static constexpr std::int64_t kTickSlackMs = 100;

    explicit PushScheduler(PushRule rule) : rule_(rule) {}
    // A tick with a new frame: true when the receiver should send it now. A receiver that cannot send (busy) leaves the
    // state as it is, so it sends the latest frame at the first tick it can.
    bool due(std::int64_t nowMs, bool changed);
    void sent(std::int64_t nowMs);
    // Starts over: the next tick sends at once.
    void restart();
    bool started() const { return last_.has_value(); }

private:
    PushRule rule_;
    std::optional<std::int64_t> last_;
    bool held_ = false;
};

// 2, 4, 8, 16 then 30 s after the first, second, … failure; 0 without one.
std::int64_t httpBackoffMs(int failures);

enum class TargetStatus { Off, NetworkOff, Waiting, Ok, NotFound, NotLocal, Unreachable, HttpStatus, Failed };
std::string_view statusName(TargetStatus status);

}  // namespace agentspeed::lan
