#include "agentspeed/character/param_space.hpp"

namespace agentspeed::character {

ParamSpace::ParamSpace(const std::vector<Param>& params) {
    for (const auto& p : params) {
        if (!index_.count(p.id)) add(p.id, p);
    }
    for (const auto& n : vocabulary()) {
        if (!index_.count(n)) add(n, std::nullopt);
    }
}

void ParamSpace::add(const std::string& name, std::optional<Param> param) {
    index_[name] = static_cast<int>(names_.size());
    names_.push_back(name);
    params_.push_back(std::move(param));
    springs_.push_back(SpringTuning::of(name));
}

std::optional<int> ParamSpace::slot(const std::string& name) const {
    auto it = index_.find(name);
    if (it == index_.end()) return std::nullopt;
    return it->second;
}

int ParamSpace::slotOrMinus(const std::string& name) const { return slot(name).value_or(-1); }

bool ParamSpace::isParam(const std::string& name) const {
    auto s = slot(name);
    return s && params_[static_cast<std::size_t>(*s)].has_value();
}

const std::vector<std::string>& ParamSpace::vocabulary() {
    static const std::vector<std::string> names = [] {
        std::vector<std::string> v = {"AngleX", "AngleY", "AngleZ", "BodyAngleZ", "BodySway", "Breath", "EyeOpen.left",
                                      "EyeOpen.right", "EyeBallX", "EyeBallY", "Wind", "PosX", "PosY", "Squash", "Lean",
                                      "Spin", "Hip", "SkirtFlare", "FootLift", "FootPoint", "TailSwish", "WingFlap",
                                      "EarTwitch.left", "EarTwitch.right"};
        for (const char* s : {"left", "right"}) {
            const std::string x = s;
            for (const char* p : {"Shoulder.", "Arm.", "Forearm.", "Lift.", "Bend.", "Wrist.", "Spread."}) {
                v.push_back(std::string(p) + x);
            }
            for (int i = 0; i < 5; ++i) v.push_back("Finger." + x + "." + std::to_string(i));
        }
        return v;
    }();
    return names;
}

Slots::Slots(const ParamSpace& space) {
    auto s = [&](const std::string& n) { return *space.slot(n); };
    angleX = s("AngleX");
    angleY = s("AngleY");
    angleZ = s("AngleZ");
    bodyAngleZ = s("BodyAngleZ");
    bodySway = s("BodySway");
    breath = s("Breath");
    eyeBallX = s("EyeBallX");
    eyeBallY = s("EyeBallY");
    wind = s("Wind");
    posX = s("PosX");
    posY = s("PosY");
    squash = s("Squash");
    lean = s("Lean");
    spin = s("Spin");
    hip = s("Hip");
    skirtFlare = s("SkirtFlare");
    footLift = s("FootLift");
    footPoint = s("FootPoint");
    tailSwish = s("TailSwish");
    wingFlap = s("WingFlap");
    eyeOpen = {s("EyeOpen.left"), s("EyeOpen.right")};
    auto side = [&](const std::string& x) {
        SideSlots o{};
        o.shoulder = s("Shoulder." + x);
        o.arm = s("Arm." + x);
        o.forearm = s("Forearm." + x);
        o.lift = s("Lift." + x);
        o.bend = s("Bend." + x);
        o.wrist = s("Wrist." + x);
        o.spread = s("Spread." + x);
        for (int i = 0; i < 5; ++i) o.fingers[static_cast<std::size_t>(i)] = s("Finger." + x + "." + std::to_string(i));
        return o;
    };
    left = side("left");
    right = side("right");
}

}  // namespace agentspeed::character
