#pragma once

#include <array>
#include <optional>
#include <string>
#include <string_view>
#include <unordered_map>
#include <vector>

#include "agentspeed/character/rig.hpp"
#include "agentspeed/character/springs.hpp"

namespace agentspeed::character {

// An image side of the character (the reference's `left` / `right`).
enum class Side { Left, Right };

inline Side other(Side s) { return s == Side::Left ? Side::Right : Side::Left; }
inline const char* name(Side s) { return s == Side::Left ? "left" : "right"; }
inline std::optional<Side> sideNamed(std::string_view n) {
    if (n == "left") return Side::Left;
    if (n == "right") return Side::Right;
    return std::nullopt;
}

// The parameters the director writes: the rig's own, then every name a motion may write, each at a fixed slot.
// Values live in dense arrays indexed by slot; names are resolved once, never per frame.
class ParamSpace {
public:
    explicit ParamSpace(const std::vector<Param>& params);

    int count() const { return static_cast<int>(names_.size()); }
    std::optional<int> slot(const std::string& name) const;
    int slotOrMinus(const std::string& name) const;
    bool isParam(int slot) const { return params_[static_cast<std::size_t>(slot)].has_value(); }
    bool isParam(const std::string& name) const;

    const std::vector<std::string>& names() const { return names_; }
    const std::vector<std::optional<Param>>& params() const { return params_; }
    const std::vector<std::optional<SpringTuning>>& springs() const { return springs_; }

    // Every parameter a motion of the reference can write.
    static const std::vector<std::string>& vocabulary();

private:
    void add(const std::string& name, std::optional<Param> param);

    std::vector<std::string> names_;
    std::vector<std::optional<Param>> params_;
    std::vector<std::optional<SpringTuning>> springs_;
    std::unordered_map<std::string, int> index_;
};

// The slots the motions write, resolved once from a `ParamSpace`.
struct Slots {
    struct SideSlots {
        int shoulder, arm, forearm, lift, bend, wrist, spread;
        std::array<int, 5> fingers;
    };

    int angleX, angleY, angleZ, bodyAngleZ, bodySway, breath, eyeBallX, eyeBallY, wind;
    int posX, posY, squash, lean, spin, hip, skirtFlare, footLift, footPoint;
    int tailSwish, wingFlap;
    std::array<int, 2> eyeOpen;
    SideSlots left, right;

    explicit Slots(const ParamSpace& space);
    const SideSlots& side(Side s) const { return s == Side::Left ? left : right; }
};

}  // namespace agentspeed::character
