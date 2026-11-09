#include "agentspeed/character/springs.hpp"

#include <algorithm>
#include <array>
#include <utility>

#include "agentspeed/character/numeric.hpp"

namespace agentspeed::character {

SecondOrder::SecondOrder(double f, double z, double r, double x0) {
    k1_ = z / (math::kPi * f);
    const double w = 2 * math::kPi * f;
    k2_ = 1 / (w * w);
    k3_ = r * z / (2 * math::kPi * f);
    snap(x0);
}

double SecondOrder::update(double dt, double x) {
    if (!(dt > 0)) return y_;
    const double xd = (x - xp_) / dt;
    xp_ = x;
    const double k2 = std::max(std::max(k2_, dt * dt / 2 + dt * k1_ / 2), dt * k1_);
    y_ += dt * yd_;
    yd_ += dt * (x + k3_ * xd - y_ - k1_ * yd_) / k2;
    return y_;
}

std::optional<SpringTuning> SpringTuning::of(std::string_view id) {
    using Entry = std::pair<std::string_view, std::optional<SpringTuning>>;
    static const std::array<Entry, 33> table = {{
        {"SkirtFlare", SpringTuning{2.0, 0.45, 0}},
        {"TailSwish", SpringTuning{2.4, 0.5, 0}}, {"Tail", std::nullopt}, {"WingFlap", SpringTuning{3.0, 0.5, 0}},
        {"EarTwitch", SpringTuning{7.0, 0.4, 0}},
        {"Var.", std::nullopt}, {"Mouth.", std::nullopt}, {"EyeOpen.", std::nullopt}, {"EyeBall", std::nullopt},
        {"Breath", std::nullopt}, {"Wind", std::nullopt},
        {"Spin", std::nullopt}, {"PosY", std::nullopt}, {"Squash", std::nullopt}, {"Hair", std::nullopt},
        {"Skirt", std::nullopt},
        {"Lift.", SpringTuning{6.0, 0.85, 0}}, {"Bend.", SpringTuning{6.0, 0.85, 0}},
        {"@", SpringTuning{3.2, 0.55, 0}},
        {"Finger.", SpringTuning{5.0, 0.55, 0}}, {"Spread.", SpringTuning{3.5, 0.6, 0}},
        {"Wrist.", SpringTuning{3.2, 0.55, 0}},
        {"Arm.", SpringTuning{2.6, 0.55, 0}}, {"Forearm.", SpringTuning{2.8, 0.5, 0}},
        {"Shoulder.", SpringTuning{3.0, 0.6, 0}},
        {"Angle", SpringTuning{2.2, 0.72, 0}}, {"BodyAngleZ", SpringTuning{1.8, 0.65, 0}},
        {"BodySway", SpringTuning{1.8, 0.65, 0}},
        {"Hip", SpringTuning{1.9, 0.62, 0}}, {"Lean", SpringTuning{2.0, 0.6, 0}}, {"PosX", SpringTuning{2.4, 0.8, 0}},
        {"Foot", SpringTuning{6.0, 0.7, 0}},
        {"", std::nullopt},
    }};
    for (const auto& [key, spec] : table) {
        if (key.empty()) break;
        const bool match = key == "@" ? id.find('@') != std::string_view::npos : id.starts_with(key);
        if (match) return spec;
    }
    return std::nullopt;
}

}  // namespace agentspeed::character
