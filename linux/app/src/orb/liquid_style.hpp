#pragma once

#include <cmath>
#include <optional>
#include <vector>

#include "orb/style.hpp"

namespace orb {

// `liquid` — the liquid energy orb (DESIGN 2a.3): a glass sphere of glowing liquid whose level rises with speed, its
// surface waving, luminous motes streaming up, boiling, splashing and steaming at the redline. The volume is one
// fragment shader pass; particles, meniscus and glass are drawn around it.
class LiquidStyle : public Style {
public:
    StyleKind kind() const override { return StyleKind::Liquid; }
    const ReadoutLayout& readoutLayout() const override;
    double signOfLifeDuration() const override { return 1.2; }

    void pulse(double time, double strength, double heat) override;
    void celebrate(double time) override { pendingPeak_ = time; }
    void step(const Frame& frame) override;
    void build(Scene& scene, const Frame& frame) override;
    void buildReadout(Scene& scene, const Frame& frame, const ReadoutContent& content, const MeasureText& measure) override;
    bool isBusy(const Frame& frame) const override;
    int ambientFps(const Frame& frame) const override;

private:
    // An underdamped spring in sub-steps of at most 1/480 s.
    struct Oscillator {
        double omega, damping, value = 0, velocity = 0;
        void step(double target, double dt);
        bool settled(double target, double tolerance = 0.0005) const {
            return std::abs(value - target) < tolerance && std::abs(velocity) < tolerance * 4;
        }
    };
    // A critically damped spring settling in `settle` seconds, stepped exactly.
    struct Critical {
        double omega, value = 0, velocity = 0;
        void step(double target, double dt);
        bool settled(double target, double tolerance = 0.0002) const {
            return std::abs(value - target) < tolerance && std::abs(velocity) < tolerance * omega;
        }
    };
    // Points from the disc center, y down.
    struct Mote {
        double x, y, vy, size, phase;
        bool gold;
    };
    struct Pop {
        double x, y, age, size;
    };
    struct Splash {
        double x, y, vx, vy, age, life, size;
    };
    struct Steam {
        double x, y, vy, age, life, size, phase;  // y: height above the mean surface
    };
    struct Drop {
        double x, y, vy, heat, strength;
    };
    struct Ripple {
        double x, start, amplitude;
    };
    struct Targets {
        double level = 0.08, amplitude = 0, chop = 0;
    };

    double currentLevel() const;
    double surfaceOffset(double x, double s, double time) const;
    void snap(const Targets& targets);
    void stepParticles(const Frame& frame, double dt, double s, double h, double o, int level, bool boiling);
    void spawnMote(double s, double h, double o, std::optional<double> x, std::optional<double> y);
    void spawnGold(double s);
    void spawnSplash(double x, double y, int count, double strength, double s);
    void addMotes(Scene& scene, const Frame& frame, double h, Rgba liquid) const;
    void addSurfaceParticles(Scene& scene, const Frame& frame, Rgba liquid) const;
    void addGlass(Scene& scene, const Frame& frame, double h, bool active, Rgba liquid, double glowScale) const;

    Oscillator level_{2 * 3.14159265358979323846 * 0.75, 0.62, 0.08};
    Critical levelEased_{6.2 / 0.9, 0.08};
    Oscillator surge_{2 * 3.14159265358979323846 * 0.9, 0.45};
    Critical amplitude_{6.2 / 0.8};
    Critical chop_{6.2 / 0.6};
    double phases_[4] = {0, 0, 0, 0};
    Targets targets_;
    std::vector<Mote> motes_;
    std::vector<Pop> pops_;
    std::vector<Splash> splashes_;
    std::vector<Steam> steam_;
    std::vector<Drop> drops_;
    std::vector<Ripple> ripples_;
    double moteCarry_ = 0, steamCarry_ = 0;
    led::Random random_{0x5EED1A9D0B0B7777ULL};
    std::optional<double> pendingPeak_;
    double goldStart_ = -1e9;
    int pendingDrops_ = 0;
    double pendingStrength_ = 0, pendingHeat_ = 0;
    bool hadSignOfLife_ = false;
    bool allowsDrops_ = true;
};

}  // namespace orb
