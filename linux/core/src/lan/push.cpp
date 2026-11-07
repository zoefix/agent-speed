#include <algorithm>
#include <array>

#include "agentspeed/lan_push.hpp"

namespace agentspeed::lan {

PushRule pushRule(Receiver receiver) {
    switch (receiver) {
    case Receiver::Stream: return {500, 5000};
    case Receiver::Udp: return {500, 1000};
    case Receiver::Http: return {1000, 10'000};
    }
    return {};
}

bool PushScheduler::due(std::int64_t nowMs, bool changed) {
    if (!last_) return true;
    held_ = held_ || changed;
    const auto since = nowMs - *last_ + kTickSlackMs;
    return (held_ && since >= rule_.minimumMs) || since >= rule_.heartbeatMs;
}

void PushScheduler::sent(std::int64_t nowMs) {
    last_ = nowMs;
    held_ = false;
}

void PushScheduler::restart() {
    last_.reset();
    held_ = false;
}

std::int64_t httpBackoffMs(int failures) {
    static constexpr std::array<std::int64_t, 5> kSteps = {2000, 4000, 8000, 16'000, 30'000};
    if (failures <= 0) return 0;
    return kSteps[static_cast<std::size_t>(std::min(failures, static_cast<int>(kSteps.size()))) - 1];
}

std::string_view statusName(TargetStatus status) {
    switch (status) {
    case TargetStatus::Off: return "off";
    case TargetStatus::NetworkOff: return "networkOff";
    case TargetStatus::Waiting: return "waiting";
    case TargetStatus::Ok: return "ok";
    case TargetStatus::NotFound: return "notFound";
    case TargetStatus::NotLocal: return "notLocal";
    case TargetStatus::Unreachable: return "unreachable";
    case TargetStatus::HttpStatus: return "httpStatus";
    case TargetStatus::Failed: return "failed";
    }
    return "off";
}

}  // namespace agentspeed::lan
