#pragma once

#include <map>
#include <optional>
#include <string>
#include <vector>

#include "agentspeed/character/motion.hpp"
#include "agentspeed/character/param_space.hpp"

namespace agentspeed::character {

// Head gestures with the body joining in (motions/gestures.py). Arm gestures, two-arm combos and face gestures need
// pose or expression variants; packs without them leave those out (spec/CHARACTERS.md, Motion).
namespace gestures {
const std::vector<std::string>& head();
const std::vector<std::string>& face();
const std::vector<std::string>& arm();
const std::vector<std::string>& combos();
MotionSpec headGesture(const std::string& name);
MotionSpec faceGesture(const std::string& name, double duration = 2.4);
}  // namespace gestures

// Whole-body moves (motions/moves.py) and the moves of the motion profiles. `out` is the free arm, `book` the arm
// holding an object, `orb` is -1 or 1 toward the orb.
namespace moves {
const std::vector<std::string>& names();
std::optional<MotionSpec> make(const std::string& name, Side out, Side book);
MotionSpec hop(Side out, Side book, double height = 0.3, double air = 0.36, const std::string& name = "hop",
               const std::string& expression = "happy", bool arms = true);
MotionSpec jump(Side out, Side book, bool arms = true);
MotionSpec sidestep(Side out, Side book, double dx);
MotionSpec peek(Side out, Side book, double orb);
MotionSpec pounce(Side out, Side book, double orb);

// Profile moves and the rig parameters each needs (any one of them).
const std::map<std::string, std::vector<std::string>>& profile();
std::optional<MotionSpec> profileMove(const std::string& name, Side out, Side book);
}  // namespace moves

}  // namespace agentspeed::character
