#include "agentspeed/character/hud.hpp"

#include <algorithm>
#include <array>
#include <cmath>

#include "agentspeed/character/numeric.hpp"

namespace agentspeed::character {

constexpr double kPi = math::kPi;

double Orbit::eccentricAnomaly(double mean, double e) {
    double E = e < 0.8 ? mean : kPi;
    for (int i = 0; i < 6; ++i) E -= (E - e * std::sin(E) - mean) / (1 - e * std::cos(E));
    return E;
}

Orbit::Position Orbit::at(double t, double cx, double cy) const {
    const double s = seed;
    const double ax = a * (1 + 0.07 * std::sin(0.11 * t + s) + 0.04 * std::sin(0.27 * t + 2.3 * s));
    const double mean = 2 * kPi * t / period + phase;
    const double E = eccentricAnomaly(math::mod(mean, 2 * kPi), e);
    const double px = ax * (std::cos(E) - e);
    const double pz = ax * std::sqrt(1 - e * e) * std::sin(E);
    const double w = omega + precess * t;
    const double x = px * std::cos(w) - pz * std::sin(w);
    const double z = px * std::sin(w) + pz * std::cos(w);
    const double bob = 0.035 * a * std::sin(0.37 * t + 1.7 * s) + 0.02 * a * std::sin(0.91 * t + s);
    const double y = cy + height + z * tilt + bob;
    return {cx + x, y, std::max(-1.0, std::min(1.0, z / (a * (1 + e))))};
}

double Orbit::spin(double t) const {
    return 0.055 * std::sin(2 * kPi * t / 13.0 + seed) + 0.03 * std::sin(2 * kPi * t / 5.3 + 2 * seed);
}

Follow::Follow(double f, double z, double bobAmplitude, double bobPeriod, double phase)
    : x_(f, z, 0), y_(f, z, 0), amplitude_(bobAmplitude), period_(bobPeriod), phase_(phase) {}

void Follow::snap(double tx, double ty) {
    x_.snap(tx);
    y_.snap(ty);
    placed = true;
}

std::pair<double, double> Follow::update(double dt, double t, double tx, double ty) {
    if (!placed) snap(tx, ty);
    return {x_.update(dt, tx) + 0.35 * amplitude_ * std::sin(2 * kPi * t / (period_ * 1.7) + phase_),
            y_.update(dt, ty) + amplitude_ * std::sin(2 * kPi * t / period_ + phase_)};
}

const std::string& Odometer::update(double dt, double target) {
    value_ += (target - value_) * std::min(1.0, dt * 7.0);
    since_ += dt;
    if (since_ >= kHold) {
        held_ = static_cast<long long>(math::roundHalfEven(value_));
        since_ = 0;
    }
    text_ = std::to_string(held_);
    const std::size_t count = text_.size();
    while (digits_.size() < count) digits_.insert(digits_.begin(), Digit{0, 0, 1});
    while (digits_.size() > count) digits_.erase(digits_.begin());
    for (std::size_t i = 0; i < count; ++i) {
        auto& d = digits_[i];
        if (d.current != text_[i]) {
            d.previous = d.current;
            d.current = text_[i];
            d.t = 0;
        }
        d.t = std::min(1.0, d.t + dt / 0.12);
    }
    return text_;
}

Rgb heat_color::rgb(double hIn) {
    static const std::array<std::pair<double, Rgb>, 4> stops = {{
        {0.0, {61, 220, 132}}, {0.35, {255, 213, 74}}, {0.7, {255, 138, 61}}, {1.0, {255, 59, 59}},
    }};
    const double h = std::min(std::max(hIn, 0.0), 1.0);
    for (std::size_t i = 0; i + 1 < stops.size(); ++i) {
        const auto& [h0, c0] = stops[i];
        const auto& [h1, c1] = stops[i + 1];
        if (h <= h1) {
            const double k = (h - h0) / (h1 - h0);
            return {std::trunc(c0.r + (c1.r - c0.r) * k), std::trunc(c0.g + (c1.g - c0.g) * k),
                    std::trunc(c0.b + (c1.b - c0.b) * k)};
        }
    }
    return stops.back().second;
}

CharacterHud::CharacterHud(std::uint64_t seed, double unit) : rng_(seed), peakFollow_(2.0, 0.6, 3.0 * unit, 2.9, 0.7) {
    back_.reserve(512);
    front_.reserve(512);
    pulses_.reserve(16);
    embers_.reserve(256);
}

void CharacterHud::pulse(double strength) { pulses_.push_back({0, strength}); }

void CharacterHud::celebratePeak() {
    peakAge_ = 0;
    peakFollow_.placed = false;
}

void CharacterHud::update(double dt, double time, double speed, double heat, double intensity, const std::string& title) {
    comet_ = math::mod(comet_ + dt / (6.0 - 5.3 * heat), 1.0);
    orbitPhase_ += dt * (0.25 + 0.9 * heat);
    if (!title_ || title != *title_) {
        previousTitle_ = title_;
        title_ = title;
        titleProgress_ = 0;
    }
    titleProgress_ = std::min(1.0, titleProgress_ + dt / 0.35);
    for (auto& p : pulses_) p.t += dt;
    pulses_.erase(std::remove_if(pulses_.begin(), pulses_.end(), [](const Pulse& p) { return p.t >= 0.9; }), pulses_.end());
    if (peakAge_) {
        const double age = *peakAge_;
        if (age + dt > 2.4) peakAge_.reset(); else peakAge_ = age + dt;
    }
    speed_ = speed;
    heat_ = heat;
    intensity_ = intensity;
    time_ = time;
    odometer_.update(dt, speed);
}

HudDot CharacterHud::dot(double x, double y, double rx, double ry, Rgb c, double a, double soft, bool additive) const {
    return {static_cast<float>(x), static_cast<float>(y), static_cast<float>(rx), static_cast<float>(ry),
            static_cast<float>(c.r / 255), static_cast<float>(c.g / 255), static_cast<float>(c.b / 255),
            static_cast<float>(a), static_cast<float>(soft), additive};
}

void CharacterHud::layout(double dt, const HudLayout& l, const std::vector<Rgb>& agents, double digitAdvance,
                          double peakX, double peakY) {
    const double u = l.unit;
    speedWidth_ = width_.update(dt, digitAdvance * static_cast<double>(odometer_.digits().size()));
    if (peakAge_) {
        const double age = *peakAge_;
        const auto [px, py] = peakFollow_.update(dt, time_, peakX, peakY);
        const double pop = age > 0 ? 1 + 0.25 * std::exp(-6 * age) * std::sin(14 * age) : 1;
        const double grow = std::min(1.0, 0.6 + age * 3);
        peak_ = HudBody{px, py, pop * grow, std::min(1.0, age / 0.15) * std::min(1.0, (2.4 - age) / 0.4)};
    } else {
        peak_.reset();
    }
    back_.clear();
    front_.clear();
    const Ellipse& g = l.ground;
    updateEmbers(dt, g, u);
    const Rgb c = heat_color::rgb(heat_);
    back_.push_back(dot(g.cx, g.cy - g.ry * 0.1, g.rx * 0.55, g.ry * 0.9, {0.02 * 255, 0.02 * 255, 0.04 * 255}, 0.55, 1,
                        false));
    back_.push_back(dot(g.cx, g.cy, g.rx * 1.2, g.ry * 1.8, c, 0.12 + 0.3 * heat_, 1));
    ring(g, u);
    pulseRows(g, u);
    satellites(l.waist, u, agents);
    emberRows(u);
    if (peakAge_ && peak_) {
        const double age = *peakAge_;
        for (int i = 0; i < 24; ++i) {
            const double ang = 2 * kPi * static_cast<double>(i) / 24;
            const double dist = (40 + 160 * std::min(1.0, age / 0.6)) * u;
            front_.push_back(dot(peak_->x + dist * std::cos(ang), peak_->y + dist * 0.5 * std::sin(ang), 2.2 * u, 2.2 * u,
                                 {255, 200, 80}, std::max(0.0, 0.8 - age), 0.6));
        }
    }
}

void CharacterHud::ring(const Ellipse& g, double u) {
    constexpr int n = 96;
    const double lit = heat_ * n;
    for (int i = 0; i < n; ++i) {
        const double a = kPi / 2 + 2 * kPi * static_cast<double>(i) / n;
        const double s = std::sin(a);
        const double x = g.cx + g.rx * std::cos(a), y = g.cy + g.ry * s;
        const double depth = 0.5 + 0.5 * (s + 1) / 2;
        const double size = (2.2 + 1.6 * (s + 1) / 2) * u;
        const double cm = math::mod(static_cast<double>(i) / n - comet_ + 0.5, 1.0) - 0.5;
        const double comet = std::exp(-((cm * 18) * (cm * 18)));
        if (static_cast<double>(i) < lit) {
            const bool leading = i == static_cast<int>(lit);
            const Rgb col = leading ? heat_color::kWhiteHot : heat_color::rgb(static_cast<double>(i) / n);
            const double k = leading ? 1.0 : std::min(1.0, lit - static_cast<double>(i));
            put(dot(x, y, size * 3.2, size * 1.7, col, (0.16 + 0.3 * comet) * depth * k, 1), s >= 0);
            put(dot(x, y, size, size * 0.62, col, (0.75 + 0.25 * comet) * depth * k, 0.35), s >= 0);
        } else {
            put(dot(x, y, size * 0.75, size * 0.47, {255, 255, 255}, (0.16 + 0.3 * comet * (0.4 + heat_)) * depth, 0.4),
                s >= 0);
        }
    }
}

void CharacterHud::pulseRows(const Ellipse& g, double u) {
    const Rgb c = heat_color::rgb(heat_);
    for (const auto& p : pulses_) {
        const double k = p.t / 0.9;
        const double ease = 1 - std::pow(1 - k, 3);
        const double grow = 1 + 0.42 * ease;
        for (int i = 0; i < 96; ++i) {
            const double a = 2 * kPi * static_cast<double>(i) / 96;
            const double s = std::sin(a);
            put(dot(g.cx + g.rx * grow * std::cos(a), g.cy + g.ry * grow * s, 1.6 * u, 1.0 * u, c, 0.55 * (1 - k) * p.k,
                    0.5),
                s >= 0);
        }
    }
}

void CharacterHud::satellites(const Ellipse& w, double u, const std::vector<Rgb>& agents) {
    const auto count = static_cast<double>(std::max<std::size_t>(1, agents.size()));
    for (std::size_t k = 0; k < agents.size(); ++k) {
        const Rgb color = agents[k];
        const double base = orbitPhase_ + 2 * kPi * static_cast<double>(k) / count;
        for (int j = 0; j < 6; ++j) {
            const double a = base - static_cast<double>(j) * 0.07;
            const double s = std::sin(a);
            const double x = w.cx + w.rx * std::cos(a);
            const double y = w.cy + w.ry * s + -0.14 * w.rx * std::cos(a);
            const double depth = 0.6 + 0.4 * (s + 1) / 2;
            if (j == 0) {
                put(dot(x, y, 18 * u * depth, 18 * u * depth, color, 0.45 * depth, 1), s >= 0);
                const Rgb core{color.r * 0.35 + 0.65 * 255, color.g * 0.35 + 0.65 * 255, color.b * 0.35 + 0.65 * 255};
                put(dot(x, y, 4.5 * u * depth, 4.5 * u * depth, core, depth, 0.3), s >= 0);
            } else {
                const double r = (3.6 - 0.45 * static_cast<double>(j)) * u * depth;
                put(dot(x, y, r, r, color, (0.55 - 0.08 * static_cast<double>(j)) * depth, 0.6), s >= 0);
            }
        }
    }
}

void CharacterHud::updateEmbers(double dt, const Ellipse& g, double u) {
    const double rate = 70 * std::max(0.0, (heat_ - 0.6) / 0.4) * intensity_ * emberRate;
    const int n = static_cast<int>(rate * dt + rng_.random());
    for (int i = 0; i < n; ++i) {
        const double a = rng_.uniform(0, 2 * kPi);
        const double vy = -rng_.uniform(60, 160);
        const double life = rng_.uniform(0.7, 1.5);
        const double seed = rng_.uniform(0, 6.28);
        if (embers_.size() < embers_.capacity()) {
            embers_.push_back({g.cx + g.rx * std::cos(a), g.cy + g.ry * std::sin(a), vy, 0, life, seed, std::sin(a) > 0});
        }
    }
    std::size_t kept = 0;
    for (auto& e : embers_) {
        e.age += dt;
        if (e.age < e.life) {
            e.y += e.vy * u * dt;
            e.x += 18 * u * std::sin(e.age * 6 + e.seed) * dt;
            embers_[kept++] = e;
        }
    }
    embers_.resize(kept);
}

void CharacterHud::emberRows(double u) {
    for (const auto& e : embers_) {
        const double k = e.age / e.life;
        put(dot(e.x, e.y, 1.8 * u, 1.8 * u, heat_color::rgb(0.7 + 0.3 * (1 - k)), 0.8 * (1 - k), 0.6), e.front);
    }
}

}  // namespace agentspeed::character
