#pragma once

#include <cstdint>
#include <string>
#include <utility>
#include <vector>

#include "agentspeed/character/hud.hpp"

namespace orb {

// The speed's lightning (DESIGN 2a.6): when digits change each changed digit crackles (an arc of 9 points around its
// ellipse with a jagged radius, at most three digits) and, when more than the last digit changed, an arc of 12 points
// jumps over them; bolts take a new shape 30 times a second, flicker and fade over 0.2 s (units only) or 0.3 s.
class CharacterLightning {
public:
    struct Strike {
        int low, high;
        double start, life, power;
        std::uint64_t seed;
        bool bridge;
    };
    enum class Strength { Off, FlashOnly, Soft, Standard, Max };

    CharacterLightning();
    void update(double dt, const std::vector<agentspeed::character::Odometer::Digit>& digits, double heat, Strength strength);
    bool isActive() const { return !strikes_.empty(); }
    const std::vector<Strike>& strikes() const { return strikes_; }
    // The flash of the digit `fromRight` places from the right, 0…1.
    double flash(int fromRight) const;
    double envelope(const Strike& strike) const;
    int boltCount(const Strike& strike, Strength strength) const;
    // Writes bolt `index` of a strike into `path` (x, y offsets from the digits' center); false when there is none.
    bool bolt(const Strike& strike, int index, Strength strength, int digitCount, double advance, double cap);
    const std::vector<std::pair<double, double>>& path() const { return path_; }

private:
    std::vector<Strike> strikes_;
    double time_ = 0;
    std::vector<std::pair<double, double>> path_;
    std::string lastText_;
    std::uint64_t count_ = 0;
};

}  // namespace orb
