#pragma once

#include <vector>

#include "orb/frame.hpp"

namespace orb {

// `led` — the LED gauge (DESIGN 2 layers 1–8) with the v1.4 effects of DESIGN 2.1: silk core, gauge energy flow, tip
// flare, comets, corona and warp streaks, sparks and embers, shockwaves with a wake, and the new-peak flash, ring,
// rays and burst. Electric arcs and digit lightning are not ported yet.
class LedStyle {
public:
    void pulse(double time, double strength, double heat);
    void celebrate();
    void step(const Frame& frame);
    void build(Scene& scene, const Frame& frame);
    bool isBusy() const;
    // 0: static, 30 or 60 frames per second at rest in this state (DESIGN 2a, Frame policy).
    int ambientFps(const Frame& frame) const;
    static constexpr double kSignOfLifeDuration = 0.9;

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
