#pragma once

#include <QPointF>
#include <QString>
#include <cmath>
#include <optional>

#include "orb/color.hpp"
#include "orb/gauge.hpp"
#include "orb/led_math.hpp"
#include "orb/scene.hpp"

namespace orb {

// Everything a style needs to draw one frame (the macOS OrbFrame).
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
    double rawHeat = 0, rawOverdrive = 0;  // unsmoothed, stepping with the engine tick (DESIGN 2a conventions)
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
    double alpha = 1;         // this style's share of a style crossfade
    double readoutShift = 0;  // how far the readout stack sits below its resting place (DESIGN 10)
    bool linesFit = false;    // the two-line readout is on and fits at this size
    QString unitText;         // the value's unit (`needle` prints it on the dial while the lines show)

    bool idle() const { return tier == Tier::Idle; }
    Blend glowBlend() const { return dark ? Blend::Additive : Blend::Over; }
    QPointF point(double degrees, double radius) const;
    // The label color of DESIGN 2a: heat on dark, darkened on light.
    Rgba labelColor(double h) const { return heatColor(h, dark); }
    // *hot* of DESIGN 2a: mix(heat(h), #FFF7D6, 0.55 + 0.45 o) on dark, mix(label, #FFB020, 0.3) on light.
    Rgba hot(double h) const {
        return dark ? heatColor(h).mixLinear(kWhiteHot, 0.55 + 0.45 * overdrive) : labelColor(h).mixLinear(kAmberHot, 0.3);
    }
    // Particles fade to 35 % inside the readout zone, an ellipse 0.30 x 0.19 S around c + (0, 0.07 S).
    double readoutZoneFade(QPointF p) const {
        const double qx = (p.x() - center.x()) / (0.30 * s);
        const double qy = (p.y() - center.y() - readoutShift - 0.07 * s) / (0.19 * s);
        return 1 - 0.65 * std::exp(-(qx * qx + qy * qy) * 1.6);
    }
};

}  // namespace orb
