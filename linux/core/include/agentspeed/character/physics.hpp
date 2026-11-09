#pragma once

#include <functional>
#include <optional>
#include <string>
#include <vector>

#include "agentspeed/character/rig.hpp"

namespace agentspeed::character {

// Pendulum chains for hair and skirt (physics.py): anchors follow body parameters, wind pushes, outputs are angles.
// Verlet in fixed 1/240 s substeps, a spring toward the hanging rest shape, damping, two passes of length
// constraints; each output is tanh(segment angle relative to the previous segment / degrees).
class Physics {
public:
    using SlotOf = std::function<std::optional<int>(const std::string&)>;

    static constexpr double kGravity = 1400.0;
    static constexpr double kSubstep = 1.0 / 240;

    // `slot` maps a parameter name to its index in the value array the chains read and write (nullopt: absent,
    // reads 0).
    Physics(const std::vector<RigDocument::Chain>& specs, double windSign, const SlotOf& slot);

    void reset(const std::vector<double>& values);
    // Steps every chain with the current values (read only).
    void step(double dt, const std::vector<double>& values);
    // Writes the chains' output parameters.
    void write(std::vector<double>& values) const;

    double windSign() const { return windSign_; }

private:
    struct Input {
        int slot;  // -1 absent
        double x, y;
    };
    struct Output {
        int slot;
        int segment;
        double degrees;
    };
    struct Chain {
        std::vector<double> anchor, segments;
        double stiffness, damping, wind;
        std::vector<Input> inputs;
        std::vector<Output> outputs;
        std::vector<double> px, py, qx, qy;
        double time = 0;

        std::pair<double, double> anchorAt(const std::vector<double>& values) const;
        void reset(const std::vector<double>& values);
        void step(double dt, const std::vector<double>& values, double wind, double windSign);
        void write(std::vector<double>& values) const;
    };

    std::vector<Chain> chains_;
    int windSlot_ = -1;
    double windSign_ = 1;
};

}  // namespace agentspeed::character
