#pragma once

#include <optional>
#include <string_view>

namespace agentspeed::character {

struct SpringTuning {
    double frequency = 0;
    double damping = 0;
    double response = 0;
    bool operator==(const SpringTuning&) const = default;

    // The spring a parameter rides on, or nullopt when it stays raw.
    static std::optional<SpringTuning> of(std::string_view id);
};

// A second-order follower: y + k1 y' + k2 y'' = x + k3 x', k1 = z / (pi f), k2 = 1 / (2 pi f)^2,
// k3 = r z / (2 pi f); k2 is raised when needed to keep the semi-implicit Euler step stable.
class SecondOrder {
public:
    SecondOrder() : SecondOrder(1, 1, 0) {}
    SecondOrder(double f, double z, double r, double x0 = 0);
    explicit SecondOrder(const SpringTuning& tuning, double x0 = 0)
        : SecondOrder(tuning.frequency, tuning.damping, tuning.response, x0) {}

    void snap(double x) {
        xp_ = x;
        y_ = x;
        yd_ = 0;
    }
    double update(double dt, double x);
    double value() const { return y_; }

private:
    double k1_ = 0, k2_ = 0, k3_ = 0;
    double xp_ = 0, y_ = 0, yd_ = 0;
};

}  // namespace agentspeed::character
