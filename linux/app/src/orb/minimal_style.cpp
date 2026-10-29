#include "orb/minimal_style.hpp"

#include <algorithm>
#include <cmath>

#include "orb/shared_layers.hpp"

namespace orb {

namespace {

constexpr double kPi = 3.14159265358979323846;
constexpr double kRadius = 0.425;
constexpr double kBlink = 0.4, kTint = 0.7;
// Shadow of the second lap's head onto the lap below, radians behind / ahead of the head.
constexpr double kShadowBehind = 0.02, kShadowAhead = 0.07;
const Rgba kGoldDark = Rgba::hex(0xFACC15), kGoldLight = Rgba::hex(0xC99700);

double angle(double fraction) { return -90 + 360 * fraction; }

}  // namespace

const ReadoutLayout& MinimalStyle::readoutLayout() const {
    static const ReadoutLayout layout = [] {
        ReadoutLayout l;
        l.valueSize = 0.30;
        l.valueBaseline = 0.07;
        l.valueMaxWidth = 0.66;
        l.unitSize = 0;
        l.title = ReadoutLayout::Title::BelowValue;
        l.titlePlace = 0.098;
        l.titleSize = 0.068;
        l.titleMaxWidth = 0.62;
        l.dots = std::nullopt;
        l.pill = -0.25;
        l.titleCrossfades = true;
        l.valueGlow = false;
        l.valueMotion = false;
        return l;
    }();
    return layout;
}

void MinimalStyle::pulse(double time, double, double) { blinkStart_ = time; }

void MinimalStyle::step(const Frame& f) {
    if (f.dt <= 0) return;
    breathPhase_ = std::fmod(breathPhase_ + 2 * kPi * f.dt, 2 * kPi);
}

bool MinimalStyle::breathes(const Frame& f) const { return f.tier == Tier::Redline && f.effects.level >= 1; }

double MinimalStyle::blink(const Frame& f) const {
    if (f.effects.level < 1) return 0;
    const double age = f.time - blinkStart_;
    return age >= 0 && age < kBlink ? 1 - age / kBlink : 0;
}

double MinimalStyle::tint(const Frame& f) const {
    if (!f.effects.celebrate || f.peakAge < 0 || f.peakAge >= kTint) return 0;
    return std::sin(kPi * f.peakAge / kTint);
}

bool MinimalStyle::isBusy(const Frame& f) const { return blink(f) > 0 || tint(f) > 0; }

// Static at idle; in the redline tier the glow breathes at 15 fps.
int MinimalStyle::ambientFps(const Frame& f) const { return breathes(f) ? 15 : 0; }

void MinimalStyle::build(Scene& scene, const Frame& f) {
    const double s = f.s;
    const bool dark = f.dark;
    const double width = std::max(2.5, 0.034 * s);
    const double radius = kRadius * s;
    const QPointF center = f.center;

    scene.add(Instance::ring(center, radius, width, f.tokens.unlitSegment.opacity(1.15)), Blend::Over);
    if (f.idle()) return;
    const double progress = std::clamp(f.heat + f.overdrive, 0.0, 2.0);
    if (progress <= 0.002) return;
    const double first = std::min(1.0, progress), second = std::clamp(progress - 1, 0.0, 1.0);

    const double h = f.heat;
    const double gold = tint(f);
    const Rgba goldColor = dark ? kGoldDark : kGoldLight;
    const Rgba head = f.labelColor(std::max(h, 0.04)).mix(goldColor, gold);
    const Rgba tail = f.labelColor(std::max(h, 0.04)).darkened(dark ? 0.28 : 0.1).mix(goldColor, gold);
    const double blinking = blink(f);
    const bool redline = f.tier == Tier::Redline;
    const double breath = redline ? (breathes(f) ? 0.5 + 0.5 * std::sin(breathPhase_) : 1) : 0;
    const bool glows = dark && f.tier >= Tier::Boost && f.effects.level >= 1;
    const double sigma = (0.0125 + 0.0125 * breath + 0.01 * blinking) * (f.effects.level == 3 ? 1.3 : 1) * s;
    const double glowAlpha = std::min(1.0, 0.35 + 0.35 * breath + 0.3 * blinking) * shared::strokeGlow(width, sigma);

    const auto lap = [&](double sweep, Rgba from) {
        if (glows) {
            const Rgba glow = head.opacity(glowAlpha);
            scene.add(Instance::bandArc(center, radius, width, angle(0), 360 * sweep, glow, glow,
                                        Instance::Band{.roundCaps = true, .core = 0, .glow = Instance::glowSigma(sigma), .glowAlpha = 1}),
                      Blend::Over);
        }
        scene.add(Instance::bandArc(center, radius, width, angle(0), 360 * sweep, head, from, Instance::Band{.roundCaps = true}),
                  Blend::Over);
    };
    lap(first, tail);
    if (second > 0.0005) {
        // The second lap runs over the first; its head casts a soft shadow onto the lap below.
        const double behind = kShadowBehind * 180 / kPi, ahead = kShadowAhead * 180 / kPi;
        const Rgba shadow = Rgba::black(dark ? 0.55f : 0.22f);
        scene.add(Instance::bandArc(center, radius, width, angle(second) - behind, behind + ahead, shadow, shadow,
                                    Instance::Band{.core = 0, .glow = Instance::glowSigma(0.35 * width), .glowAlpha = 1}),
                  Blend::Over);
        lap(second, head);
    }

    const double dot = std::max(blinking, redline && f.effects.level >= 1 ? 0.6 * breath : 0.0);
    if (dot > 0.01) {
        const double at = second > 0 ? second : first;
        const Rgba color = dark ? head.mix(kWhiteHot, 0.6) : head;
        scene.add(Instance::circle(f.point(angle(at), radius), width * 0.32, color.opacity(std::min(1.0, dot))), Blend::Over);
    }
}

}  // namespace orb
