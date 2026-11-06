#pragma once

#include <cstdint>
#include <optional>
#include <vector>

#include "agentspeed/remote_messages.hpp"

// The host's clock offset δ = remote − local (REMOTE 6), from ping / clock pairs: δ = now_remote − (t_send + t_receive) / 2,
// the sample with the smallest round trip among the last 8. Until the first sample, `hello.now` gives δ.
namespace agentspeed::remote {

class ClockEstimator {
public:
    static constexpr std::size_t kSampleLimit = 8;

    struct Sample {
        double offsetMs = 0;
        std::int64_t roundTripMs = 0;
    };

    void noteHello(std::int64_t remoteNowMs, std::int64_t localMs);
    // A clock reply to a ping sent at `sendMs` and received at `receiveMs` (both local).
    void addSample(std::int64_t sendMs, std::int64_t receiveMs, std::int64_t remoteNowMs);
    // δ in whole milliseconds; nullopt before hello.
    std::optional<std::int64_t> offsetMs() const;
    const std::vector<Sample>& samples() const { return samples_; }

private:
    std::vector<Sample> samples_;
    std::optional<double> helloOffsetMs_;
};

// The message with every time of an agent, response or tool line converted to local time (t − δ); nullopt when a
// converted time leaves the years 2000–2100 (the line is then invalid). Durations are unchanged.
std::optional<RelayMessage> converted(const RelayMessage& message, std::int64_t offsetMs);

}  // namespace agentspeed::remote
