#include "orb/readout.hpp"

#include <algorithm>
#include <cmath>

namespace orb {

namespace {

constexpr double kPi = 3.14159265358979323846;
constexpr double kTitleSlide = 0.35;
constexpr double kPeakPill = 1.8;

// DESIGN 2 layer 6 (macOS OrbReadoutLayout.led), fractions of S below the center.
constexpr double kValueSize = 0.24, kValueBaseline = 0.012, kUnitSize = 0.072, kUnitGap = 0.104;
constexpr double kTitleSize = 0.062, kTitleGap = 0.09, kDots = 0.3785, kPill = -0.232;

bool latin(const QString& text) {
    for (QChar c : text) {
        if (c.unicode() > 0x024F) return false;
    }
    return true;
}

}  // namespace

void buildReadout(Scene& scene, const Frame& f, const ReadoutContent& c, const MeasureText& measure) {
    const double s = f.s;
    const double cx = f.center.x(), cy = f.center.y();
    const bool idle = f.idle();

    // Value: tabular bold 0.24 S; from boost a heat glow, from overdrive a lean and a wider soft glow.
    TextDraw value;
    value.text = c.value;
    value.pixelSize = kValueSize * s;
    value.weight = 700;
    value.baselineCenter = QPointF(cx + c.jitter.x(), cy + kValueBaseline * s + c.jitter.y());
    value.color = idle ? f.tokens.idleValueText : f.tokens.valueText;
    const Rgba heat = f.tokens.heat(std::max(f.heat, 0.2));
    value.glowRadius = 0.035 * s * led::glowScale(f.overdrive);
    value.glowColor = heat;
    value.glowStrength = c.valueGlow * (f.dark ? 0.75 : 0.35);
    value.shear = std::tan(led::leanDegrees(f.heat) * kPi / 180) * c.skew;
    if (c.skew > 0.01 && measure) {
        const double width = measure(value);
        const double sigma = 2.4 * 0.0175 * s * led::glowScale(f.overdrive);
        const double alpha = (f.dark ? 0.45 : 0.2) * 0.42 * c.skew;
        scene.add(Instance::roundedRect(QPointF(cx, value.baselineCenter.y() - 0.36 * value.pixelSize),
                                        std::max(1.0, width / 2 - 0.08 * value.pixelSize), 0.3 * value.pixelSize, 0,
                                        0.28 * value.pixelSize, heat.withAlpha(alpha), 1, Instance::glowSigma(sigma), 1),
                  Blend::Over);
    }
    scene.add(value);

    TextDraw unit;
    unit.text = c.unit;
    unit.pixelSize = kUnitSize * s;
    unit.weight = 500;
    unit.tracking = 0.5 * f.unit;
    unit.baselineCenter = QPointF(cx, cy + (kValueBaseline + kUnitGap) * s);
    unit.color = f.tokens.secondaryText;
    scene.add(unit);

    // Title below the unit: slides up on change; crossfades to the hover line.
    const double titleY = cy + (kValueBaseline + kUnitGap + kTitleGap) * s;
    const double since = f.time - c.titleChangedAt;
    const double slide = since >= 0 && since < kTitleSlide ? led::easeOut(since / kTitleSlide) : 1;
    const double rise = 0.05 * s;
    auto title = [&](const QString& text, double y, double alpha, Rgba color) {
        if (text.isEmpty() || alpha <= 0.01) return;
        TextDraw t;
        t.text = latin(text) ? text.toUpper() : text;
        t.pixelSize = kTitleSize * s;
        t.weight = 600;
        t.tracking = latin(text) ? 1.6 * f.unit : 0.6 * f.unit;
        t.baselineCenter = QPointF(cx, y);
        t.color = color.withAlpha(color.a * alpha);
        scene.add(t);
    };
    const Rgba titleColor = idle ? f.tokens.secondaryText : f.tokens.heat(std::max(c.titleHeat, 0.1));
    const double show = 1 - c.hover;
    title(c.title, titleY + rise * (1 - slide), show * slide, titleColor);
    if (slide < 1) title(c.previousTitle, titleY - rise * slide, show * (1 - slide), titleColor);
    if (c.hover > 0.01 && !c.info.isEmpty()) {
        TextDraw info;
        info.text = c.info;
        info.pixelSize = kTitleSize * s;
        info.weight = 500;
        info.baselineCenter = QPointF(cx, titleY);
        info.color = f.tokens.secondaryText.withAlpha(f.tokens.secondaryText.a * c.hover);
        scene.add(info);
    }

    // Agent dots in the gauge gap: one per generating agent, max 6 (then 5 and +n), gently pulsing.
    if (!idle && !c.agents.empty()) {
        const int count = static_cast<int>(c.agents.size());
        const int overflow = count > 6 ? count - 5 : 0;
        const int dots = overflow > 0 ? 5 : count;
        const double d = 4 * f.unit, spacing = 3.2 * f.unit, more = 0.075 * s;
        const double total = dots * d + std::max(0, dots - 1) * spacing + (overflow > 0 ? spacing + more : 0);
        const double y = cy + kDots * s;
        double x = cx - total / 2;
        for (int i = 0; i < dots; ++i) {
            const Rgba tint = Rgba::from(c.agents[static_cast<std::size_t>(i)]);
            const Rgba color = f.dark ? tint : tint.darkened(0.12);
            const double pulse =
                f.effects.breathing ? 0.62 + 0.38 * (0.5 + 0.5 * std::sin(2 * kPi * (f.time * 0.9 + i * 0.19))) : 1;
            scene.add(Instance::circle(QPointF(x + d / 2, y), d / 2, color.withAlpha(pulse), 1, f.dark ? d * 0.7 : 0, 0.45),
                      Blend::Over);
            x += d + spacing;
        }
        if (overflow > 0) {
            TextDraw plus;
            plus.text = QStringLiteral("+%1").arg(overflow);
            plus.pixelSize = 0.06 * s;
            plus.weight = 600;
            plus.baselineCenter = QPointF(x + more / 2, y + 0.02 * s);
            plus.color = f.tokens.secondaryText;
            scene.add(plus);
        }
    }

    // New peak: a gold pill springs in above the value for 1.8 s.
    const double t = f.peakAge;
    if (f.effects.celebrate && t >= 0 && t < kPeakPill && !c.peakLabel.isEmpty()) {
        const double in = std::min(1.0, t / 0.35);
        const double spring = 1 + 0.12 * std::sin(in * kPi) * (1 - in);
        const double out = t > kPeakPill - 0.3 ? (kPeakPill - t) / 0.3 : 1;
        const double alpha = std::clamp(std::min(in * 2, out), 0.0, 1.0);
        TextDraw label;
        label.text = c.peakLabel;
        label.pixelSize = 0.058 * s * spring;
        label.weight = 700;
        label.tracking = 0.8 * f.unit;
        const double y = cy + kPill * s;
        const double width = measure ? measure(label) : label.pixelSize * static_cast<double>(label.text.size()) * 0.6;
        const Rgba gold = f.dark ? kGold : kGold.darkened(0.25);
        scene.add(Instance::roundedRect(QPointF(cx, y), width / 2 + 0.035 * s, 0.045 * s * spring, 0, 0.045 * s * spring,
                                        gold.withAlpha((f.dark ? 0.16 : 0.18) * alpha), 1, 0.02 * s, 0.35 * alpha),
                  Blend::Over);
        label.baselineCenter = QPointF(cx, y + 0.36 * label.pixelSize);
        label.color = gold.withAlpha(alpha);
        scene.add(label);
    }
}

}  // namespace orb
