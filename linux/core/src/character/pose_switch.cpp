#include "agentspeed/character/pose_switch.hpp"

#include <algorithm>
#include <cmath>

namespace agentspeed::character {

PoseSwitch::PoseSwitch()
    : hop_({{0, 0}, {0.08, -0.25}, {0.18, 0.35}, {0.36, 0.1}, {0.46, -0.3}, {0.52, 0}}),
      lift_({{0, 0}, {0.12, 0}, {0.3, 0.07}, {0.46, 0}, {0.52, 0}}),
      flare_({{0, 0}, {0.2, 0.7}, {0.4, 0.5}, {0.7, 0}}) {}

void PoseSwitch::request(int target) {
    if (!t_) {
        t_ = 0;
        target_ = target;
    }
}

void PoseSwitch::enterEdgeOn() {
    t_ = kOutDuration - 1e-9;
    target_.reset();
}

PoseSwitch::Step PoseSwitch::update(double dt) {
    Step out;
    if (!t_) return out;
    const double before = *t_;
    double now = before + dt;
    double spin;
    if (now < kOutDuration) {
        const double k = now / kOutDuration;
        spin = 0.25 * k * k;
    } else {
        if (before < kOutDuration) {
            out.swap = target_;
            out.swapped = true;
            now = kOutDuration;
        }
        const double k = std::min(1.0, (now - kOutDuration) / kInDuration);
        spin = 0.75 + 0.25 * (1 - std::pow(1 - k, 3));
    }
    out.output = Output{spin, hop_(now), lift_(now), flare_(now)};
    if (now >= kOutDuration + kInDuration) {
        out.output->spin = 0;
        t_.reset();
    } else {
        t_ = now;
    }
    return out;
}

}  // namespace agentspeed::character
