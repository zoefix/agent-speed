#pragma once

#include <optional>

#include "agentspeed/character/curve.hpp"

namespace agentspeed::character {

// Turn side-on, swap the illustration, turn back (spec/CHARACTERS.md, Pose swap): `Spin` runs 0 → 0.25 (edge-on)
// over 0.2 s, the frame that crosses 0.2 s shows the new pose at exactly 0.75 (also edge-on), then `Spin` runs on to 1
// over 0.32 s with a small squash, lift and skirt flare, and returns to 0.
class PoseSwitch {
public:
    static constexpr double kOutDuration = 0.2;
    static constexpr double kInDuration = 0.32;

    // The values the swap writes this frame: `spin` replaces, squash and lift add, the flare takes the larger value.
    struct Output {
        double spin, squash, posY, skirtFlare;
    };
    struct Step {
        std::optional<int> swap;
        std::optional<Output> output;
        bool swapped = false;
    };

    PoseSwitch();

    bool isRunning() const { return t_.has_value(); }
    std::optional<double> t() const { return t_; }
    std::optional<int> target() const { return target_; }

    // Starts a swap toward `target`; ignored while one runs.
    void request(int target);
    // Starts on the edge-on instant, for a character that takes over from another one.
    void enterEdgeOn();
    // Advances one frame. `swap` is the target on the edge-on frame that swaps.
    Step update(double dt);

private:
    std::optional<double> t_;
    std::optional<int> target_;
    Curve hop_, lift_, flare_;
};

}  // namespace agentspeed::character
