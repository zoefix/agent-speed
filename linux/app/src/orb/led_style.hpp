#pragma once

#include <vector>

#include "orb/style.hpp"

namespace orb {

// `led` — the LED gauge (DESIGN 2 layers 1–8) with the v1.4 effects of DESIGN 2.1: silk core, gauge energy flow, tip
// flare, comets, corona and warp streaks, sparks and embers, shockwaves with a wake, and the new-peak flash, ring,
// rays and burst. Electric arcs and digit lightning are not ported yet.
class LedStyle : public Style {
public:
    StyleKind kind() const override { return StyleKind::Led; }
    bool sharedAura() const override { return false; }
    bool sharedWave() const override { return false; }
    bool sharedRays() const override { return false; }
    void pulse(double time, double strength, double heat) override;
    void celebrate(double time) override;
    void step(const Frame& frame) override;
    void build(Scene& scene, const Frame& frame) override;
    bool isBusy(const Frame& frame) const override;
    int ambientFps(const Frame& frame) const override;

private:
    struct Pulse {
        double start, strength, heat;
    };

    double pulseAge(const Pulse& p, double time) const { return (time - p.start) / led::kPulseDuration; }
    void fieldUniforms(Scene& scene, const Frame& frame) const;
    void addAura(Scene& scene, const Frame& frame) const;
    void addCorona(Scene& scene, const Frame& frame) const;
    void addSilk(Scene& scene, const Frame& frame) const;
    void addSegments(Scene& scene, const Frame& frame) const;
    void addTicks(Scene& scene, const Frame& frame) const;
    void addTipFlare(Scene& scene, const Frame& frame) const;
    void addComets(Scene& scene, const Frame& frame) const;
    void addPulses(Scene& scene, const Frame& frame) const;
    void addPeak(Scene& scene, const Frame& frame) const;
    void addParticles(Scene& scene, const Frame& frame) const;
    double flow(int index, const Frame& frame) const;

    led::Phases phases_;
    led::Particles particles_;
    std::vector<Pulse> pulses_;
    double cometVisibility_ = 0;
    double cometTarget_ = 0;
    bool pendingBurst_ = false;
};

}  // namespace orb
