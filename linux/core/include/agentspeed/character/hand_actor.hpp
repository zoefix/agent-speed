#pragma once

#include <array>
#include <optional>
#include <string>
#include <vector>

#include "agentspeed/character/motion.hpp"
#include "agentspeed/character/param_space.hpp"
#include "agentspeed/character/rhythm.hpp"
#include "agentspeed/character/rng.hpp"

namespace agentspeed::character {

// Hand acting (motions/hands.py): a slow ripple across the fingers, a drifting wrist and spread, fidgets now and then
// (taps, regrips and lifts on the object hand; flexes, fans, counting and drumming on the free hand), and the wrist
// trailing the arm.
struct HandActor {
    static constexpr int kWristSlot = 5;
    static constexpr int kSpreadSlot = 6;

    Side side;
    bool holds;
    double phase;
    std::vector<std::string> freeSets;
    std::optional<std::string> mode;
    bool modeAction = false;
    std::optional<Motion> action;
    double t = 0;
    double next = 0;
    std::optional<double> previousArm;
    double trail = 0;
    std::array<double, 5> fingers{};
    double spread = 0;
    double wrist = 0;

    HandActor(Side side, bool holds, double phase, Rng& rng);

    static const std::vector<std::string>& sets();
    static MotionSpec make(const std::string& name, double beat);
    static Motion bind(const MotionSpec& spec);
    // Which hands a pose's hand set plays on: "both", "holder" or "free".
    static const char* modeSides(const std::string& name);

    bool modeOn() const;
    // The pose's hand set: played back to back on the hands it belongs to, from the next action on.
    void setMode(const std::optional<std::string>& mode);
    Motion choose(Band band, double beat, Rng& rng);
    static double busyness(Band band);
    // Advances one frame; the offsets land in `fingers`, `spread` and `wrist`.
    void update(double dt, double time, Band band, double beat, double arm, Rng& rng);
};

}  // namespace agentspeed::character
