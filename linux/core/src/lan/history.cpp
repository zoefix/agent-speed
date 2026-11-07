#include "agentspeed/lan_frame.hpp"

namespace agentspeed::lan {

const std::vector<double>& History::tick(std::int64_t nowMs, double value) {
    const double sample = roundTokS(value);
    if (!s0_ || nowMs - lastSampleMs_ >= kRestartMs) {
        s0_ = nowMs;
        next_ = 1;
        lastSampleMs_ = nowMs;
        samples_.assign(1, sample);
        return samples_;
    }
    while (nowMs >= *s0_ + next_ * kStepMs) {
        samples_.push_back(sample);
        ++next_;
        lastSampleMs_ = nowMs;
    }
    if (samples_.size() > kLength) samples_.erase(samples_.begin(), samples_.end() - static_cast<std::ptrdiff_t>(kLength));
    return samples_;
}

void History::reset() {
    s0_.reset();
    next_ = 0;
    lastSampleMs_ = 0;
    samples_.clear();
}

}  // namespace agentspeed::lan
