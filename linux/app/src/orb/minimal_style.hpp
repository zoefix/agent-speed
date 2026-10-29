#pragma once

#include "orb/style.hpp"

namespace orb {

// `minimal` — one thin progress ring (DESIGN 2a.5): the number, the ring and the title; almost nothing moves. The ring
// fills to the redline and runs a second lap beyond it, the way an activity ring passes 100 %.
class MinimalStyle : public Style {
public:
    StyleKind kind() const override { return StyleKind::Minimal; }
    const ReadoutLayout& readoutLayout() const override;
    const LinePlacement& linePlacement() const override { return LinePlacement::minimal; }
    bool sharedAura() const override { return false; }
    bool sharedWave() const override { return false; }
    bool sharedRays() const override { return false; }
    double signOfLifeDuration() const override { return 0; }

    void pulse(double time, double strength, double heat) override;
    void celebrate(double) override {}
    void step(const Frame& frame) override;
    void build(Scene& scene, const Frame& frame) override;
    bool isBusy(const Frame& frame) const override;
    int ambientFps(const Frame& frame) const override;

private:
    bool breathes(const Frame& frame) const;
    double blink(const Frame& frame) const;
    double tint(const Frame& frame) const;

    double breathPhase_ = 0;
    double blinkStart_ = -1e9;
};

}  // namespace orb
