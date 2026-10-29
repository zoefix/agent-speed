#pragma once

#include <QPointF>
#include <optional>

#include "orb/color.hpp"
#include "orb/gauge.hpp"
#include "orb/led_math.hpp"
#include "orb/scene.hpp"

namespace orb {

// Everything a style needs to draw one frame (the macOS OrbFrame, trimmed to the `led` style).
struct Frame {
    double time = 0;
    double dt = 0;
    double s = 148;
    QPointF center;
    double unit = 1;  // s / 148
    bool dark = true;
    Tokens tokens;
    led::Effects effects;
    double heat = 0;         // smoothed heat
    double displayHeat = 0;  // the gauge fill (sweeps after a new peak)
    double overdrive = 0;
    Tier tier = Tier::Idle;
    double redline = 2000;
    double redlineAmount = 0;  // envelope of the redline tier
    double activity = 0;       // envelope of any non-idle tier
    double breathPhase = 0;
    double breath = 1;
    double flash = 0;  // effect-tier flash, 1 → 0 over 0.4 s
    double auraFlare = 0;
    std::optional<double> signOfLife;  // progress of the idle sweep
    double peakAge = -1;               // seconds since a new peak, < 0: none
    double reach = led::kOuterReach;
    bool resumed = false;
    bool small = false;

    bool idle() const { return tier == Tier::Idle; }
    Blend glowBlend() const { return dark ? Blend::Additive : Blend::Over; }
    QPointF point(double degrees, double radius) const;
};

}  // namespace orb
