#pragma once

#include <map>
#include <optional>
#include <set>
#include <string>
#include <utility>
#include <vector>

#include "agentspeed/character/rhythm.hpp"
#include "agentspeed/json.hpp"

namespace agentspeed::character {

// A character's motion personality (motions/profile.py, spec/CHARACTERS.md Motion profile): the manifest's `motion`
// block. Every key is optional; the defaults are Claude's behavior.
struct MotionProfile {
    double tempo = 1, energy = 1, sway = 1, bounce = 1, gap = 1;
    std::string idle = "read";  // the idle groove: `read` (looking down at a book) or `rest`
    bool seated = false;
    bool talk = true;
    std::vector<std::string> signature;
    std::vector<std::string> exclude;
    std::vector<std::string> hands;  // fidget sets of the free hand
    std::map<std::string, std::string> poseHands;  // a hand set per pose band
    std::optional<std::string> particles;

    bool operator==(const MotionProfile&) const = default;

    // Tolerant: unknown or ill-typed keys take their defaults.
    static MotionProfile decode(const json::Value& v);

    // The bands an action fits in, lowest and highest.
    static const std::map<std::string, std::pair<Band, Band>>& actionBands();
    static const std::set<std::string>& seatedSkip();
    static const std::vector<std::string>& seatedFreeze();
    static const std::set<std::string>& twoHanded();
    static const std::set<std::string>& atMouth();

    bool allows(const std::string& action) const;
    // The band's pool: the base actions it allows, then each signature action that fits the band three times in all.
    std::vector<std::string> pool(const std::vector<std::string>& base, Band band) const;
    // How a groove's value for `name` scales: bounce, sway or energy (motions/rhythm.py, groove_scale).
    double grooveScale(const std::string& name) const;
    // The pose band's hand set, if any.
    std::optional<std::string> poseHand(Band band) const;
};

}  // namespace agentspeed::character
