#include "orb/led_style.hpp"

#include <algorithm>
#include <cmath>


namespace orb {

namespace {

constexpr double kPi = 3.14159265358979323846;
constexpr int kSegments = led::kSegments;
constexpr double kStart = 135, kSweep = 270, kGap = 2.2;
const Rgba kRingGold{1, 0.78f, 0.3f, 1};
const Rgba kSparkGold{1, 0.82f, 0.35f, 1};

double segmentAngle(int i) { return kStart + kSweep / kSegments * (i + 0.5); }
double angleFor(double fraction) { return kStart + kSweep * fraction; }

double hash01(int a, int b) {
    std::uint32_t h = static_cast<std::uint32_t>(a) * 374761393u + static_cast<std::uint32_t>(b) * 668265263u;
    h = (h ^ (h >> 13)) * 1274126177u;
    h ^= h >> 16;
    return static_cast<double>(h & 0xFFFFFF) / static_cast<double>(0x1000000);
}

struct Geometry {
    double gaugeRadius, segmentLength, segmentWidth, segmentCorner, tickRadius;
    explicit Geometry(double s) {
        gaugeRadius = 0.41 * s;
        segmentLength = 0.045 * s;
        segmentWidth = gaugeRadius * (kSweep / kSegments - kGap) * kPi / 180;
        segmentCorner = std::min(segmentWidth, segmentLength) * 0.24;
        tickRadius = 0.455 * s;
    }
};

}  // namespace

QPointF Frame::point(double degrees, double radius) const {
    const double a = degrees * kPi / 180;
    return {center.x() + radius * std::cos(a), center.y() + radius * std::sin(a)};
}

void LedStyle::pulse(double time, double strength, double heat) {
    if (pulses_.size() >= 4) pulses_.erase(pulses_.begin());
    pulses_.push_back({time, strength, heat});
}

void LedStyle::celebrate(double) { pendingBurst_ = true; }

void LedStyle::step(const Frame& f) {
    const auto& e = f.effects;
    const double h = f.heat, o = f.overdrive, s = f.s;
    const bool comets = e.maxComets > 0 && !f.idle();
    if (f.resumed) {
        particles_.clear();
        pulses_.clear();
        pendingBurst_ = false;
        cometTarget_ = cometVisibility_ = comets ? 1 : 0;
    }
    if (f.dt <= 0) return;
    led::advance(phases_, h, o, f.dt, e.silkRate, e.maxComets > 0);
    cometTarget_ = comets ? 1 : 0;
    cometVisibility_ += (cometTarget_ - cometVisibility_) * (1 - std::exp(-f.dt / 0.28));
    if (std::abs(cometTarget_ - cometVisibility_) < 0.0005) cometVisibility_ = cometTarget_;
    while (!pulses_.empty() && pulseAge(pulses_.front(), f.time) >= 1) pulses_.erase(pulses_.begin());
    if (pendingBurst_) {
        pendingBurst_ = false;
        if (e.particles) particles_.burst(s, static_cast<int>(std::lround(led::Particles::kBurstCount * e.countScale)));
    }
    const double sparkRate = e.particles ? led::Particles::sparkRate(h, e.lvl, e.countScale) : 0;
    const double emberRate =
        e.particles ? led::Particles::emberRate(o, f.tier == Tier::Redline, e.lvl, e.countScale) : 0;
    if (sparkRate > 0 || emberRate > 0 || !particles_.empty()) {
        particles_.step(f.dt, f.time, s, sparkRate, emberRate, f.displayHeat, o);
    }
}

bool LedStyle::isBusy(const Frame&) const {
    return !particles_.empty() || !pulses_.empty() || pendingBurst_ || cometVisibility_ != cometTarget_;
}

int LedStyle::ambientFps(const Frame& f) const {
    if (f.idle()) return isBusy(f) ? 30 : 0;
    return f.heat >= 0.45 || f.tier >= Tier::Overdrive ? 60 : 30;
}

void LedStyle::build(Scene& scene, const Frame& f) {
    addAura(scene, f);
    addCorona(scene, f);
    addSilk(scene, f);
    addSegments(scene, f);
    addTicks(scene, f);
    addTipFlare(scene, f);
    addComets(scene, f);
    addPulses(scene, f);
    addPeak(scene, f);
    addParticles(scene, f);
}

void LedStyle::fieldUniforms(Scene& scene, const Frame& f) const {
    const auto& e = f.effects;
    const double h = f.heat, o = f.overdrive;
    auto& u = scene.ledField;
    u.frame = {static_cast<float>(f.center.x()), static_cast<float>(f.center.y()), static_cast<float>(f.s), 1.0f};
    u.heat = {static_cast<float>(h), static_cast<float>(o), static_cast<float>(led::activity(h)), static_cast<float>(e.lvl)};
    const double peak = !e.celebrate || f.peakAge < 0 ? 99 : std::min(f.peakAge, 99.0);
    u.phases = {static_cast<float>(phases_.silk), static_cast<float>(phases_.light), static_cast<float>(phases_.clock),
                static_cast<float>(peak)};
    u.flags = {f.dark ? 1.0f : 0.0f, static_cast<float>(f.flash),
               static_cast<float>(e.silkRipples ? led::ripple(o) : 0.0),
               static_cast<float>(led::silkBrightness(h, o, e.lvl))};
    const double corona = e.corona ? led::coronaAmount(h, o, e.lvl) : 0;
    const double warp = e.warp ? led::warpAmount(h, e.lvl) : 0;
    u.corona = {static_cast<float>(phases_.corona), static_cast<float>(phases_.warp), static_cast<float>(corona),
                static_cast<float>(warp)};
    u.text = {0, 1, 0, 0};
    for (auto& w : u.waves) w = {-1, 0, 0, 0};
    if (e.silkWaves) {
        std::size_t slot = 0;
        for (const auto& p : pulses_) {
            const double age = pulseAge(p, f.time);
            if (age < 0 || age >= 1 || slot >= 4) continue;
            u.waves[slot++] = {static_cast<float>(age), static_cast<float>(p.strength), 0, 0};
        }
    }
}

void LedStyle::addAura(Scene& scene, const Frame& f) const {
    const double h = f.heat;
    const double alpha =
        (f.tokens.auraOpacity * h * f.breath * (1 + 0.7 * f.auraFlare) + (f.dark ? 0.05 : 0.03) * f.auraFlare) * 0.8;
    if (alpha <= 0.004) return;
    Rgba color = f.tokens.heat(std::max(h, 0.12)).mix(kWhiteHot, 0.25 * f.overdrive);
    scene.add(Instance::radial(f.center, 0.5 * f.s, f.reach * f.s, color, std::min(1.0, alpha)), f.glowBlend());
}

void LedStyle::addCorona(Scene& scene, const Frame& f) const {
    const auto& e = f.effects;
    const double corona = e.corona ? led::coronaAmount(f.heat, f.overdrive, e.lvl) : 0;
    const double warp = e.warp ? led::warpAmount(f.heat, e.lvl) : 0;
    if (corona <= 0.001 && warp <= 0.001) return;
    fieldUniforms(scene, f);
    if (corona > 0.001) led::coronaBody(phases_.corona, scene.coronaBody);
    scene.addLedField(1, f.glowBlend());
}

void LedStyle::addSilk(Scene& scene, const Frame& f) const {
    fieldUniforms(scene, f);
    scene.addLedField(0, Blend::Over);
}

double LedStyle::flow(int index, const Frame& f) const {
    if (!f.effects.flow) return 0;
    return led::flow(index, phases_.flow, f.heat);
}

void LedStyle::addSegments(Scene& scene, const Frame& f) const {
    const Geometry g(f.s);
    const double lit = f.displayHeat * kSegments;
    const double bloom = f.effects.bloom;
    const double glowRadius = 0.02 * f.s * bloom;
    auto light = [&](int i, double& brightness, double& whiteness) {
        brightness = std::clamp(lit - i, 0.0, 1.0);
        whiteness = 0;
        if (brightness <= 0) return;
        const double distance = lit - (i + 1);
        whiteness = distance < 0 ? 1 : std::clamp(1 - distance, 0.0, 1.0);
    };
    auto segmentColor = [&](int i) {
        const double h = std::max(0.07, static_cast<double>(i) / (kSegments - 1));
        return f.dark ? heatColor(h) : heatColor(h).darkened(0.06);
    };
    auto shimmer = [&](int i) {
        if (!f.effects.shimmer || f.redlineAmount <= 0.001) return 1.0;
        const int tick = static_cast<int>(std::floor(f.time * 12));
        const double random = 0.75 + 0.25 * hash01(i, tick);
        return 1 - (1 - random) * f.redlineAmount;
    };
    auto segment = [&](int i, Rgba color, double core = 1, double glow = 0, double glowAlpha = 0) {
        const double angle = segmentAngle(i);
        return Instance::roundedRect(f.point(angle, g.gaugeRadius), g.segmentLength / 2, g.segmentWidth / 2,
                                     angle * kPi / 180, g.segmentCorner, color, core, glow, glowAlpha);
    };

    // Bloom under the lit segments, white-hot at the leading edge.
    for (int i = 0; i < kSegments; ++i) {
        double brightness, whiteness;
        light(i, brightness, whiteness);
        if (brightness <= 0.01) continue;
        const double fl = flow(i, f);
        Rgba color = segmentColor(i);
        if (fl > 0) color = color.mix(f.dark ? kWhiteHot : color.scaled(1.15), 0.75 * fl);
        if (whiteness > 0) color = color.mixLinear(f.tokens.leadingSegment(i / 35.0, f.overdrive), whiteness * 0.8);
        const double base = (f.dark ? 0.3 : 0.2) + 0.45 * whiteness;
        const double alpha = brightness * base * shimmer(i) * (1 + 0.5 * f.flash) * (1 + 0.9 * fl) * 1.3;
        scene.add(segment(i, color.withAlpha(std::min(1.0, alpha * bloom)), 0, glowRadius * (1 + 0.7 * whiteness), 0.6),
                  f.glowBlend());
    }
    if (f.redlineAmount > 0.01) {
        const double pulse = 0.55 + 0.45 * std::sin(2 * kPi * f.breathPhase);
        const Rgba color = highlight(f.heat, f.overdrive, f.dark);
        const double end = angleFor(f.displayHeat);
        const double alpha = std::min(1.0, 0.5 * 1.3 * f.redlineAmount * pulse * bloom);
        scene.add(Instance::arc(f.center, 0.447 * f.s, 0.012 * f.s, kStart, end - kStart, color.withAlpha(alpha),
                                color.withAlpha(alpha), 0.02 * f.s, 0.8),
                  f.glowBlend());
    }

    // Crisp segments.
    const Rgba unlit = f.tokens.unlitSegment;
    for (int i = 0; i < kSegments; ++i) {
        double brightness, whiteness;
        light(i, brightness, whiteness);
        if (brightness < 1) {
            double boost = 0;
            if (f.signOfLife) {
                const double u = *f.signOfLife;
                const double d = static_cast<double>(i) / (kSegments - 1) - (u * 1.3 - 0.15);
                boost = (f.dark ? 0.16 : 0.1) * std::exp(-d * d * 90);
            }
            scene.add(segment(i, boost > 0 ? unlit.withAlpha(unlit.a + boost) : unlit), Blend::Over);
        }
        if (brightness <= 0) continue;
        const double fl = flow(i, f);
        Rgba color = segmentColor(i).scaled(shimmer(i));
        if (fl > 0) color = color.mix(f.dark ? kWhiteHot : color.scaled(1.15), 0.75 * fl);
        if (whiteness > 0) color = color.mixLinear(f.tokens.leadingSegment(i / 35.0, f.overdrive), whiteness);
        if (f.flash > 0) color = color.mix(Rgba::white(), 0.25 * f.flash);
        scene.add(segment(i, color.withAlpha(std::min(1.0, brightness * (1 + 0.9 * fl)))), Blend::Over);
    }
}

void LedStyle::addTicks(Scene& scene, const Frame& f) const {
    const Geometry g(f.s);
    const Rgba idle = f.dark ? Rgba::white(0.26f) : Rgba::black(0.22f);
    const double redline = std::max(f.redline, 1.0);
    const double enters[] = {0.5, 0.15 * redline, 0.375 * redline, 0.75 * redline};
    for (double enter : enters) {
        const double fraction = std::min(1.0, std::pow(std::max(enter / redline, 0.0), 0.6));
        const bool passed = f.displayHeat >= fraction;
        const Rgba color = passed ? f.tokens.heat(std::max(fraction, 0.1)) : idle;
        scene.add(Instance::circle(f.point(angleFor(fraction), g.tickRadius), 0.0085 * f.s, color, 1,
                                   passed && f.dark ? 1.5 * f.unit : 0, 0.5),
                  Blend::Over);
    }
}

void LedStyle::addTipFlare(Scene& scene, const Frame& f) const {
    if (!f.effects.tipFlare) return;
    const double amount = led::flareAmount(f.heat, f.overdrive, f.effects.lvl);
    if (amount <= 0.002) return;
    const Geometry g(f.s);
    const double angle = angleFor(std::min(f.displayHeat, 1.0));
    const Rgba color = f.dark ? kWhiteHot.withAlpha(0.9 * amount) : heatColor(f.heat).inked().withAlpha(0.4 * amount);
    scene.add(Instance::flare(f.point(angle, g.gaugeRadius), (angle + 90) * kPi / 180, led::flareTangential(f.overdrive) * f.s,
                              led::flareRadial(f.overdrive) * f.s, 0.0032 * f.s, 0.0028 * f.s, color),
              f.glowBlend());
}

void LedStyle::addComets(Scene& scene, const Frame& f) const {
    if (cometVisibility_ <= 0.01 || f.heat <= 0.01) return;
    const double h = f.heat, s = f.s;
    const int count = led::cometCount(h, f.effects.maxComets);
    const double tail = led::cometTail(h);
    const Rgba heat = heatColor(h);
    const double radius = 0.455 * s;
    for (int i = 0; i < count; ++i) {
        const double head = 360 * phases_.comet + 180 * i;
        const double opacity = cometVisibility_ * (0.25 + 0.6 * h) * (i == 1 ? 0.7 : 1) * (f.dark ? 1 : 0.6);
        const Rgba headColor = f.dark ? kWhiteHot : heat.inked();
        const Rgba tailColor = f.dark ? heat : heat.inked();
        Instance band = Instance::arc(f.center, radius, 0, head - tail, tail, headColor.withAlpha(opacity),
                                      tailColor.withAlpha(opacity), (0.0035 + 0.0045 * h) * s, 1);
        band.extra[0] = 0;
        scene.add(band, f.glowBlend());
        scene.add(Instance::circle(f.point(head, radius), 0, headColor.withAlpha(opacity), 0, (0.01 + 0.006 * h) * s, 0.9)
                      .exponentialGlow(),
                  f.glowBlend());
    }
}

void LedStyle::addPulses(Scene& scene, const Frame& f) const {
    const double s = f.s;
    for (const auto& p : pulses_) {
        const double age = pulseAge(p, f.time);
        if (age < 0 || age >= 1) continue;
        const Rgba heat = heatColor(p.heat);
        const double width = ((0.005 + 0.005 * p.strength) * (1 - age) + 0.001) * s;
        const Rgba shock = f.dark ? heat.mix(kWhiteHot, 0.3) : heat.inked();
        scene.add(Instance::ring(f.center, led::shockRadius(age, f.reach) * s, 0,
                                 shock.withAlpha((1 - age) * 0.7 * p.strength * (f.dark ? 1 : 0.6)), 0, width, 1),
                  f.glowBlend());
        const Rgba wake = f.dark ? heat : heat.inked();
        scene.add(Instance::ring(f.center, led::wakeRadius(age, f.reach) * s, 0, wake.withAlpha((1 - age) * 0.3 * p.strength),
                                 0, width * 3.5, 1),
                  f.glowBlend());
    }
}

void LedStyle::addPeak(Scene& scene, const Frame& f) const {
    const double t = f.peakAge;
    if (!f.effects.celebrate || t < 0 || t >= 1.4) return;
    const double s = f.s;
    const double flash = led::peakFlash(t);
    if (flash > 0.0075) {
        const Rgba color = f.dark ? kWhiteHot.withAlpha(flash) : Rgba::hex(0xFFB021).withAlpha(0.35 / 0.75 * flash);
        scene.add(Instance::softDisc(f.center, 0.15 * s, 0.6 * s, color), f.glowBlend());
    }
    const double ring = led::peakRingRadius(t, f.reach);
    scene.add(Instance::ring(f.center, ring * s, 0, kRingGold.withAlpha(1 - t / 1.4), 0, (0.012 + 0.02 * t) * s, 1),
              f.glowBlend());
    if (t >= 1.3) return;
    // 24 gold rays turning at 0.4 rad/s, each 0 → 75 % at 30 % of its length → 0.
    const double k = t / 1.3;
    const double r0 = 0.5 * s;
    const double r1 = led::peakRayReach(t, f.reach) * s;
    const double peak = 0.75 * (1 - k);
    const double width = s * 0.014 * (1 - 0.5 * k);
    const Rgba start{1, 0.85f, 0.35f, 0};
    const Rgba middle{1, 0.8f, 0.3f, static_cast<float>(peak)};
    const Rgba end{1, 0.7f, 0.2f, 0};
    const double rm = r0 + 0.3 * (r1 - r0);
    for (int i = 0; i < 24; ++i) {
        const double angle = t * 0.4 + i / 24.0 * 2 * kPi;
        const QPointF dir(std::cos(angle), std::sin(angle));
        auto piece = [&](double from, double to, Rgba a, Rgba b) {
            const double length = to - from;
            if (length <= 0.1) return;
            const double mid = (from + to) / 2;
            const QPointF c(f.center.x() + dir.x() * mid, f.center.y() + dir.y() * mid);
            scene.add(Instance::roundedRect(c, length / 2, width / 2, angle, 0, a).linearGradient(b, angle * 180 / kPi, length / 2),
                      f.glowBlend());
        };
        piece(r0, rm, start, middle);
        piece(rm, r1, middle, end);
    }
}

void LedStyle::addParticles(Scene& scene, const Frame& f) const {
    if (particles_.empty()) return;
    const double s = f.s;
    const bool dark = f.dark;
    for (const auto& p : particles_.items()) {
        const double life = 1 - p.age / p.life;
        const double radius = std::sqrt(p.x * p.x + p.y * p.y) / s;
        const QPointF position(f.center.x() + p.x, f.center.y() + p.y);
        const double fade = led::particleFade(radius);
        if (fade <= 0.002) continue;
        Rgba color;
        if (p.kind == led::Particle::Kind::Gold) {
            color = kSparkGold;
        } else if (dark) {
            color = heatColor(p.heat).mix(kWhiteHot, std::min(1.0, (1 - life) * 0.3 + 0.4));
        } else {
            color = p.heat > 0.92 ? kAmberHot : heatColor(p.heat).scaled(0.9);
        }
        const double alpha = std::min(1.0, life * 1.4) * (p.kind == led::Particle::Kind::Ember ? 0.8 : 1) * fade;
        if (p.streak) {
            const double speed = std::sqrt(p.vx * p.vx + p.vy * p.vy);
            const double fadeV = std::min(1.0, speed / (0.12 * s));
            const double length = std::max(0.02, std::min(0.06, 0.06 * fadeV));
            const double a = alpha * (0.25 + 0.75 * fadeV);
            const QPointF tail(position.x() - p.vx * length, position.y() - p.vy * length);
            scene.add(Instance::capsule(tail, position, p.size * (dark ? 0.75 : 1), color.withAlpha(a), color.withAlpha(0)),
                      f.glowBlend());
            if (dark) {
                scene.add(Instance::circle(position, p.size * 0.55, color.mix(Rgba::white(), 0.6).withAlpha(a)), f.glowBlend());
            }
        } else {
            const double flicker = 0.55 + 0.45 * std::sin(p.age * 18 + p.seed);
            const double r = p.size * (dark ? 0.8 : 1) * flicker;
            const Rgba tint = dark ? color.mix(Rgba::white(), 0.25) : color;
            scene.add(Instance::circle(position, 0, tint.withAlpha(alpha), 0, 0.854 * r, 1), f.glowBlend());
        }
    }
}

}  // namespace orb
