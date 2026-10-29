#pragma once

#include <optional>
#include <vector>

#include "orb/style.hpp"

namespace orb {

// `reactor` — the reactor energy ring (DESIGN 2a.4): a dark core window carries the readout; around it three
// counter-rotating rotors spin up with speed, lengthen until rotor A closes into a solid ring beyond the redline, and
// throw lightning at the redline. Every completed response kicks the rotors; a new peak locks them at 12 o'clock.
class ReactorStyle : public Style {
public:
    StyleKind kind() const override { return StyleKind::Reactor; }
    const ReadoutLayout& readoutLayout() const override;
    const LinePlacement& linePlacement() const override { return LinePlacement::reactor; }

    void pulse(double time, double strength, double heat) override;
    void celebrate(double time) override;
    void step(const Frame& frame) override;
    void build(Scene& scene, const Frame& frame) override;
    bool isBusy(const Frame& frame) const override;
    int ambientFps(const Frame& frame) const override;

private:
    struct Bolt {
        double start, life, from, to, inner, outer;  // radians and diameters
        int seed;
    };
    struct Spark {
        double x, y, vx, vy, age, life, size;
    };
    struct Palette {
        Rgba heat, hot, unlit;
        double tailFloor, glow;
    };

    double lockGold(const Frame& frame) const;
    double boltFlash(const Frame& frame) const;
    int stepLightning(double time, double dt, double rate, int maxBolts, int sparkPool, double overdrive);
    void emitBolt(double time, double overdrive, int sparkPool);
    static QPointF boltPoint(const Bolt& bolt, int index, double time);
    std::pair<Rgba, Rgba> arcColors(const Frame& frame, const Palette& palette, bool active, double gold, double& alpha) const;
    void addStator(Scene& scene, const Frame& frame, const Palette& palette) const;
    void addCorona(Scene& scene, const Frame& frame, const Palette& palette, double heat) const;
    void addRotorA(Scene& scene, const Frame& frame, const Palette& palette, double heat, bool active, double gold) const;
    void addRotorB(Scene& scene, const Frame& frame, const Palette& palette, double heat, bool active, double gold) const;
    void addRotorC(Scene& scene, const Frame& frame, const Palette& palette, double heat, bool active, double gold) const;
    void addFlareRing(Scene& scene, const Frame& frame) const;
    void addLightning(Scene& scene, const Frame& frame) const;
    void addCore(Scene& scene, const Frame& frame, const Palette& palette, double heat, bool active, double gold) const;

    // Rotor angles in degrees: the head of rotor A's first arc, rotor B's first coil, rotor C's first arc.
    double angleA_ = 17.2, angleB_ = 63.0, angleC_ = 114.6;
    double bandPhase_ = 0, breathPhase_ = 0;
    double kick_ = 0;
    std::vector<Bolt> bolts_;
    std::vector<Spark> sparks_;
    double boltCarry_ = 0;
    led::Random random_{0x0B077EA5E1EC7A1CULL};
    double lockStart_ = -1e9;
    double lockFrom_[3] = {0, 0, 0}, lockTo_[3] = {0, 0, 0};
    std::optional<double> pendingLock_;
    struct PendingPulse {
        double time, strength, heat;
    };
    std::optional<PendingPulse> pendingPulse_;
    double flareStart_ = -1e9, flareHeat_ = 0;
    double lastFlash_ = -1e9;
};

}  // namespace orb
