#include "agentspeed/remote_clock.hpp"

#include <algorithm>
#include <cmath>

#include "agentspeed/remote_protocol.hpp"

namespace agentspeed::remote {

void ClockEstimator::noteHello(std::int64_t remoteNowMs, std::int64_t localMs) {
    helloOffsetMs_ = static_cast<double>(remoteNowMs) - static_cast<double>(localMs);
}

void ClockEstimator::addSample(std::int64_t sendMs, std::int64_t receiveMs, std::int64_t remoteNowMs) {
    if (receiveMs < sendMs) return;
    const double offset = static_cast<double>(remoteNowMs) - (static_cast<double>(sendMs) + static_cast<double>(receiveMs)) / 2;
    samples_.push_back({offset, receiveMs - sendMs});
    if (samples_.size() > kSampleLimit) samples_.erase(samples_.begin(), samples_.end() - kSampleLimit);
}

std::optional<std::int64_t> ClockEstimator::offsetMs() const {
    std::optional<double> offset = helloOffsetMs_;
    if (!samples_.empty()) {
        offset = std::min_element(samples_.begin(), samples_.end(), [](const Sample& a, const Sample& b) {
                     return a.roundTripMs < b.roundTripMs;
                 })->offsetMs;
    }
    if (!offset) return std::nullopt;
    return static_cast<std::int64_t>(std::llround(*offset));
}

namespace {

std::optional<std::int64_t> shift(std::int64_t ms, std::int64_t delta) {
    std::int64_t local = 0;
    if (__builtin_sub_overflow(ms, delta, &local) || !isTime(local)) return std::nullopt;
    return local;
}

// nullopt stays nullopt; a value that leaves the range fails.
bool shiftOptional(std::optional<std::int64_t>& ms, std::int64_t delta) {
    if (!ms) return true;
    const auto local = shift(*ms, delta);
    if (!local) return false;
    ms = local;
    return true;
}

}  // namespace

std::optional<RelayMessage> converted(const RelayMessage& message, std::int64_t delta) {
    if (const auto* agent = std::get_if<RemoteAgent>(&message)) {
        auto a = *agent;
        if (!shiftOptional(a.inFlightStartMs, delta) || !shiftOptional(a.lastLineMs, delta)) return std::nullopt;
        for (auto& action : a.actions) {
            const auto call = shift(action.callMs, delta);
            if (!call || !shiftOptional(action.resultMs, delta)) return std::nullopt;
            action.callMs = *call;
        }
        return a;
    }
    if (const auto* response = std::get_if<RemoteResponse>(&message)) {
        auto r = *response;
        const auto end = shift(r.record.endMs, delta);
        if (!end || !shiftOptional(r.record.startMs, delta) || !shiftOptional(r.record.firstMs, delta)) return std::nullopt;
        r.record.endMs = *end;
        return r;
    }
    if (const auto* tool = std::get_if<RemoteTool>(&message)) {
        auto t = *tool;
        const auto call = shift(t.action.callMs, delta);
        if (!call || !shiftOptional(t.action.resultMs, delta)) return std::nullopt;
        t.action.callMs = *call;
        return t;
    }
    return message;
}

}  // namespace agentspeed::remote
