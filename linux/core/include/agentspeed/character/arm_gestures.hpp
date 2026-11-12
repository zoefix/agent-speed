#pragma once

#include <map>
#include <optional>
#include <string>

#include "agentspeed/character/motion.hpp"
#include "agentspeed/character/rig.hpp"

// Arm gestures that swap in a pose variant, and combos of one variant per arm (motions/gestures.py): the old arm turns
// to meet the new one, they cross-fade, the new arm settles into its pose, holds with a flourish, and the swap runs
// backwards. The shoulder rises with the arm, the weight shifts to the other leg and the head follows the hand. Only a
// living pack hands the director the variants they need (spec/CHARACTERS.md, Behavior).
namespace agentspeed::character::arm_gestures {

struct ArmSpec {
    double hold;
    std::string flourish;
    std::optional<std::string> expression;
    bool tiptoe;
};

struct ComboSpec {
    std::string book, free;
    double hold;
    std::optional<std::string> expression;
};

const std::map<std::string, ArmSpec>& arms();
const std::map<std::string, ComboSpec>& combos();

// The gesture of an arm variant (`kind: arm`); nullopt when the variant lacks the parameters it drives.
std::optional<MotionSpec> armGesture(const RigDocument::Variant& meta, double hold, const std::string& flourish,
                                     const std::optional<std::string>& expression, bool tiptoe = false);
// Two arm gestures at once: the object arm's, then 0.1 s later the free arm's, which carries the expression.
std::optional<MotionSpec> combo(const std::string& name, const RigDocument::Variant& book, const RigDocument::Variant& free,
                                double hold, const std::optional<std::string>& expression);

}  // namespace agentspeed::character::arm_gestures
