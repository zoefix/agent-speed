#pragma once

#include <optional>
#include <string_view>
#include <utility>
#include <vector>

#include "agentspeed/character/param_space.hpp"

namespace agentspeed::character {

// The effect tiers of METRICS 6 as the character's speed bands.
enum class Band { Idle = 0, Cruise = 1, Boost = 2, Overdrive = 3, Redline = 4 };

constexpr std::array<Band, 5> kBands = {Band::Idle, Band::Cruise, Band::Boost, Band::Overdrive, Band::Redline};

// The band of a speed level 0...13.
inline Band bandOf(int level) {
    if (level >= 12) return Band::Redline;
    if (level >= 9) return Band::Overdrive;
    if (level >= 5) return Band::Boost;
    if (level >= 1) return Band::Cruise;
    return Band::Idle;
}

inline int order(Band b) { return static_cast<int>(b); }
inline bool operator<(Band a, Band b) { return order(a) < order(b); }
inline bool operator<=(Band a, Band b) { return order(a) <= order(b); }
inline bool operator>(Band a, Band b) { return order(a) > order(b); }

const char* name(Band b);
std::optional<Band> bandNamed(std::string_view n);
double bpm(Band b);

// The beat clock: its tempo eases toward the band's, scaled by the character's tempo.
struct Beat {
    double tempo = 1;
    double bpm = 56;
    double phase = 0;

    explicit Beat(double t = 1) : tempo(t), bpm(character::bpm(Band::Idle) * t) {}
    void update(double dt, Band band);
};

// Body grooves per band on the beat clock: posture, weight shifts, bounce and arm swing (motions/rhythm.py).
namespace groove {
using Out = std::vector<std::pair<int, double>>;
double up(double b);
double hit(double b);
void fill(Band band, double b, double t, const Slots& s, const Slots::SideSlots& o, const Slots::SideSlots& k,
          bool rest, Out& out);
}  // namespace groove

}  // namespace agentspeed::character
