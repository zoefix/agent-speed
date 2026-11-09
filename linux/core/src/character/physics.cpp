#include "agentspeed/character/physics.hpp"

#include <algorithm>
#include <cmath>

#include "agentspeed/character/numeric.hpp"

namespace agentspeed::character {

Physics::Physics(const std::vector<RigDocument::Chain>& specs, double windSign, const SlotOf& slot)
    : windSign_(windSign) {
    for (const auto& s : specs) {
        Chain c;
        c.anchor = s.anchor;
        c.segments = s.segments;
        c.stiffness = s.stiffness;
        c.damping = s.damping;
        c.wind = s.wind;
        for (const auto& i : s.inputs) c.inputs.push_back({slot(i.param).value_or(-1), i.x.value_or(0), i.y.value_or(0)});
        for (const auto& o : s.outputs) {
            c.outputs.push_back({slot(o.param).value_or(-1), static_cast<int>(o.segment), o.degrees});
        }
        const std::size_t n = s.segments.size() + 1;
        c.px.assign(n, 0);
        c.py.assign(n, 0);
        c.qx.assign(n, 0);
        c.qy.assign(n, 0);
        chains_.push_back(std::move(c));
    }
    windSlot_ = slot("Wind").value_or(-1);
}

std::pair<double, double> Physics::Chain::anchorAt(const std::vector<double>& values) const {
    double sx = 0, sy = 0;
    for (const auto& i : inputs) sx += (i.slot >= 0 ? values[static_cast<std::size_t>(i.slot)] : 0.0) * i.x;
    for (const auto& i : inputs) sy += (i.slot >= 0 ? values[static_cast<std::size_t>(i.slot)] : 0.0) * i.y;
    return {anchor[0] + sx, anchor[1] + sy};
}

void Physics::Chain::reset(const std::vector<double>& values) {
    const auto [ax, ay] = anchorAt(values);
    px[0] = ax;
    py[0] = ay;
    for (std::size_t i = 1; i < px.size(); ++i) {
        px[i] = px[i - 1];
        py[i] = py[i - 1] + segments[i - 1];
    }
    qx = px;
    qy = py;
    time = 0;
}

void Physics::Chain::step(double dt, const std::vector<double>& values, double windValue, double windSign) {
    const int steps = std::max(1, static_cast<int>(std::nearbyint(dt / kSubstep)));
    const double h = dt / static_cast<double>(steps);
    const double k = stiffness;
    const double keep = std::max(0.0, 1 - damping * h);
    for (int s = 0; s < steps; ++s) {
        time += h;
        const double gust = 1 + 0.35 * std::sin(time * 7.3) + 0.2 * std::sin(time * 13.1 + 1.7);
        const double forceX = windSign * windValue * wind * kGravity * gust;
        const auto [ax, ay] = anchorAt(values);
        px[0] = ax;
        py[0] = ay;
        double rx = ax, ry = ay;
        for (std::size_t i = 1; i < px.size(); ++i) {
            ry = ry + segments[i - 1];
            rx = rx + 0;
            const double vx = (px[i] - qx[i]) * keep, vy = (py[i] - qy[i]) * keep;
            const double accX = forceX + k * (rx - px[i]);
            const double accY = kGravity + k * (ry - py[i]);
            qx[i] = px[i];
            qy[i] = py[i];
            px[i] = px[i] + vx + accX * h * h;
            py[i] = py[i] + vy + accY * h * h;
        }
        for (int pass = 0; pass < 2; ++pass) {
            for (std::size_t i = 1; i < px.size(); ++i) {
                const double dx = px[i] - px[i - 1], dy = py[i] - py[i - 1];
                const double dist = std::max(std::hypot(dx, dy), 1e-6);
                const double r = segments[i - 1] / dist;
                px[i] = px[i - 1] + dx * r;
                py[i] = py[i - 1] + dy * r;
            }
        }
    }
}

void Physics::Chain::write(std::vector<double>& values) const {
    double parent = 0;
    for (const auto& o : outputs) {
        const auto i = static_cast<std::size_t>(o.segment);
        const double angle = std::atan2(px[i + 1] - px[i], py[i + 1] - py[i]) * (180.0 / math::kPi);
        if (o.slot >= 0) values[static_cast<std::size_t>(o.slot)] = std::tanh((angle - parent) / o.degrees);
        parent = angle;
    }
}

void Physics::reset(const std::vector<double>& values) {
    for (auto& c : chains_) c.reset(values);
}

void Physics::step(double dt, const std::vector<double>& values) {
    const double wind = windSlot_ >= 0 ? values[static_cast<std::size_t>(windSlot_)] : 0.0;
    for (auto& c : chains_) c.step(dt, values, wind, windSign_);
}

void Physics::write(std::vector<double>& values) const {
    for (const auto& c : chains_) c.write(values);
}

}  // namespace agentspeed::character
