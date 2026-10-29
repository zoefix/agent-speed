#include "orb/needle_style.hpp"

#include <algorithm>
#include <cmath>

#include "orb/shared_layers.hpp"

namespace orb {

namespace {

constexpr double kPi = 3.14159265358979323846;
const double kRedZone = std::pow(0.75, 0.6);  // the redline-tier threshold
constexpr double kZeroStop = -0.012, kPegStop = 1.012;
constexpr double kZeroPin = -0.034, kPegPin = 1.034;
constexpr double kContact = 1.024;  // where peg sparks start
constexpr double kLampThresholdsBase[] = {0.70, 0.735, 0.77, 0.805};
constexpr double kLampAngles[] = {-110, -100, -90, -80, -70};
constexpr int kGraduations = 41;
constexpr double kWakeThreshold = 12, kFastTurn = 90;
constexpr double kTremble[] = {11.3, 17.9, 26.1};
constexpr double kLampHz = 3, kPeakSweep = 1.0, kTipFlare = 0.35;
constexpr double kSpringHz = 1.5, kDamping = 0.55, kBounce = -0.32, kMaxStep = 1.0 / 480;
constexpr double kHub = 0.235;
// While the two-line readout fits, the unit leaves the hub for the dial face (DESIGN 10).
constexpr double kDialUnitBaseline = -0.254, kDialUnitSize = 0.046;
const Rgba kRedDark = Rgba::hex(0xF43F5E), kRedLight = Rgba::hex(0xE11D48);
const Rgba kIdleNeedle = Rgba::hex(0x9EA8BA), kLampLight = Rgba::hex(0xF59E0B);

double angleOf(double n) { return 135 + 270 * n; }
double lampThreshold(int i) { return i < 4 ? kLampThresholdsBase[i] : kRedZone; }
double clampStop(double n) { return std::clamp(n, kZeroStop, kPegStop); }

double smoothstep(double e0, double e1, double x) {
    const double t = std::clamp((x - e0) / (e1 - e0), 0.0, 1.0);
    return t * t * (3 - 2 * t);
}

Rgba redColor(const Frame& f) { return f.dark ? kRedDark : kRedLight; }

double glowFactor(const Frame& f) { return f.effects.level == 3 ? 1.25 : 1; }

}  // namespace

const ReadoutLayout& NeedleStyle::readoutLayout() const {
    static const ReadoutLayout layout = [] {
        ReadoutLayout l;
        l.valueSize = 0.205;
        l.valueBaseline = 0.035;
        l.valueMaxWidth = 0.40;
        l.unitSize = 0.062;
        l.unitGap = 0.08;
        l.title = ReadoutLayout::Title::Fixed;
        l.titlePlace = 0.338;
        l.titleSize = 0.062;
        l.titleMaxWidth = 0.46;
        l.infoScale = 0.85;
        l.infoMaxWidth = 0.42;
        l.dots = 0.405;
        l.pill = -0.175;
        return l;
    }();
    return layout;
}

void NeedleStyle::pulse(double time, double strength, double) {
    flareStart_ = time;
    flareStrength_ = strength;
}

double NeedleStyle::flare(double time) const {
    const double age = time - flareStart_;
    return age >= 0 && age < kTipFlare ? (1 - age / kTipFlare) * flareStrength_ : 0;
}

bool NeedleStyle::sweeping(const Frame& f) const { return f.effects.celebrate && f.peakAge >= 0 && f.peakAge < kPeakSweep; }

// The spring's target: h_raw (0 at idle), plus 0.02 + 0.05 o_raw while o_raw > 0 so it presses into the peg. Off: the
// smoothed h directly.
double NeedleStyle::target(const Frame& f) const {
    if (f.idle()) return 0;
    if (f.effects.level >= 1) return f.rawHeat + (f.rawOverdrive > 0 ? 0.02 + 0.05 * f.rawOverdrive : 0);
    return clampStop(f.heat + (f.overdrive > 0 ? 0.02 + 0.05 * f.overdrive : 0));
}

void NeedleStyle::step(const Frame& f) {
    const int level = f.effects.level;
    const bool active = !f.idle();
    springy_ = level >= 1;
    const double goal = target(f);
    if (!initialized_ || f.resumed) {
        position_ = clampStop(goal);
        velocity_ = 0;
        initialized_ = true;
        sparks_.clear();
        sparkCarry_ = 0;
    }
    const double dt = f.dt;
    if (dt <= 0) return;
    const double previous = position_;
    if (springy_) {
        // Underdamped, 1.5 Hz, ζ 0.55, in sub-steps of at most 1/480 s; a stop reverses the velocity × −0.32.
        const double omega = 2 * kPi * kSpringHz;
        const int count = std::max(1, static_cast<int>(std::ceil(dt / kMaxStep)));
        const double h = dt / count;
        for (int i = 0; i < count; ++i) {
            const double acceleration = -omega * omega * (position_ - goal) - 2 * kDamping * omega * velocity_;
            velocity_ += acceleration * h;
            position_ += velocity_ * h;
            if (position_ > kPegStop) {
                position_ = kPegStop;
                if (velocity_ > 0) velocity_ *= kBounce;
            }
            if (position_ < kZeroStop) {
                position_ = kZeroStop;
                if (velocity_ < 0) velocity_ *= kBounce;
            }
        }
    } else {
        position_ = clampStop(goal);
        velocity_ = 0;
    }
    angularSpeed_ = (position_ - previous) / dt * 270;

    for (int i = 0; i < 3; ++i) phases_[i] = std::fmod(phases_[i] + 2 * kPi * kTremble[i] * dt, 2 * kPi);
    const double amplitude =
        level >= 2 && active ? smoothstep(0.55, 1, f.heat) * (0.3 + 1.0 * f.overdrive) * (level == 3 ? 1.4 : 1) : 0;
    tremble_ = amplitude * (0.5 * std::sin(phases_[0]) + 0.3 * std::sin(phases_[1] + 1.3) + 0.2 * std::sin(phases_[2] + 2.1));
    redPhase_ = std::fmod(redPhase_ + 2 * kPi * (2 + f.overdrive) * dt, 2 * kPi);
    lampPhase_ = std::fmod(lampPhase_ + 2 * kPi * kLampHz * dt, 2 * kPi);

    const bool pinned = springy_ && active && position_ >= 1.0105 && goal > kPegStop;
    const double rate = level >= 2 && pinned ? (25 + 70 * f.overdrive) * (level == 3 ? 1.6 : 1) : 0;
    if (rate > 0 || !sparks_.empty()) stepSparks(dt, rate, level == 3 ? 80 : 48);
}

void NeedleStyle::stepSparks(double dt, double rate, int pool) {
    const double drag = std::exp(-2 * dt);
    for (std::size_t i = 0; i < sparks_.size();) {
        auto& p = sparks_[i];
        p.age += dt;
        if (p.age >= p.life) {
            p = sparks_.back();
            sparks_.pop_back();
            continue;
        }
        p.vy += 1.1 * dt;
        p.vx *= drag;
        p.vy *= drag;
        p.x += p.vx * dt;
        p.y += p.vy * dt;
        ++i;
    }
    sparkCarry_ += rate * dt;
    const double a = angleOf(kContact) * kPi / 180, radius = 0.405;
    while (sparkCarry_ >= 1) {
        sparkCarry_ -= 1;
        if (static_cast<int>(sparks_.size()) >= pool) continue;
        // Along the clockwise tangent ±30°, 0.35–0.9 S/s, plus 0.2 S/s outward; 0.22–0.55 s.
        const double direction = a + kPi / 2 + (random_.next() * 2 - 1) * kPi / 6;
        const double speed = 0.35 + 0.55 * random_.next();
        sparks_.push_back({radius * std::cos(a), radius * std::sin(a), std::cos(direction) * speed + std::cos(a) * 0.2,
                           std::sin(direction) * speed + std::sin(a) * 0.2, 0, 0.22 + 0.33 * random_.next(),
                           0.8 + 0.8 * random_.next()});
    }
}

bool NeedleStyle::isBusy(const Frame& f) const {
    const double goal = target(f);
    const bool settled = std::abs(position_ - clampStop(goal)) < 0.0008 && std::abs(velocity_) < 0.004;
    return (springy_ && !settled) || !sparks_.empty() || flare(f.time) > 0 || sweeping(f);
}

int NeedleStyle::ambientFps(const Frame& f) const {
    if (f.idle()) return 0;
    if (f.tier >= Tier::Overdrive || std::abs(angularSpeed_) > kFastTurn) return 60;
    return 30;
}

// The needle as drawn: spring plus tremble, swung to the peg and back by a new peak.
double NeedleStyle::displayed(const Frame& f) const {
    double n = clampStop(position_ + tremble_ / 270);
    if (sweeping(f)) n += (kPegStop - n) * (smoothstep(0, 0.38, f.peakAge) - smoothstep(0.5, 1.0, f.peakAge));
    return clampStop(n);
}

void NeedleStyle::build(Scene& scene, const Frame& f) {
    const double n = displayed(f);
    const bool active = !f.idle();
    const bool thumbnail = f.s < 64;
    addDial(scene, f);
    addRedZone(scene, f, active);
    if (f.linesFit && !thumbnail) {
        // The unit as the legend of a real tachometer: on the dial face above the hub, under the needle.
        TextDraw unit;
        unit.text = f.unitText;
        unit.pixelSize = std::max(7.0, kDialUnitSize * f.s);
        unit.weight = 500;
        unit.tracking = 0.4 * f.unit;
        unit.baselineCenter = QPointF(f.center.x(), f.center.y() + kDialUnitBaseline * f.s);
        unit.color = f.tokens.secondaryText.opacity(0.75);
        scene.addUnder(unit);
    }
    addGraduations(scene, f, n, active);
    if (!thumbnail) {
        if (active) addTrace(scene, f, n);
        addPins(scene, f);
        if (!f.small) addLamps(scene, f, n, active);
        if (active) addWake(scene, f, n);
    }
    addNeedle(scene, f, n, active);
    if (!thumbnail) addSparks(scene, f);
    addHub(scene, f);
}

void NeedleStyle::buildReadout(Scene& scene, const Frame& f, const ReadoutContent& content, const MeasureText& measure) {
    ReadoutPass pass;
    pass.linesYieldToPill = false;  // the lines sit in the hub, away from the pill
    if (!f.linesFit) {
        orb::buildReadout(scene, f, content, measure, readoutLayout(), linePlacement(), pass);
        return;
    }
    ReadoutLayout hub = readoutLayout();
    hub.unitSize = 0;
    orb::buildReadout(scene, f, content, measure, hub, linePlacement(), pass);
}

void NeedleStyle::addDial(Scene& scene, const Frame& f) const {
    const Rgba color = f.dark ? Rgba::white(0.028f) : Rgba::black(0.035f);
    scene.add(Instance::bandArc(f.center, 0.41 * f.s, 0.075 * f.s, angleOf(-0.004), 270 * 1.008, color, color), Blend::Over);
}

void NeedleStyle::addRedZone(Scene& scene, const Frame& f, bool active) const {
    const Rgba red = redColor(f);
    const bool inRedline = active && f.tier == Tier::Redline;
    double alpha = f.dark ? 0.62 : 0.8;
    if (inRedline && f.effects.level >= 1) alpha = 0.8 + 0.2 * (0.5 + 0.5 * std::sin(redPhase_));
    const double start = angleOf(kRedZone), sweep = 270 * (1 - kRedZone);
    const double radius = 0.4655 * f.s, width = 0.017 * f.s;
    if (inRedline && f.effects.level >= 2 && f.s >= 64) {
        const double sigma = 0.025 * (0.6 + 0.4 * f.overdrive) * (f.effects.level == 3 ? 1.3 : 1) * f.s;
        const Rgba glow = red.opacity(0.9 * f.redlineAmount * shared::strokeGlow(width, sigma));
        scene.add(Instance::bandArc(f.center, radius, width, start, sweep, glow, glow,
                                    Instance::Band{.core = 0, .glow = Instance::glowSigma(sigma), .glowAlpha = 1}),
                  f.glowBlend());
    }
    const Rgba color = red.opacity(alpha);
    scene.add(Instance::bandArc(f.center, radius, width, start, sweep, color, color), Blend::Over);
}

void NeedleStyle::addGraduations(Scene& scene, const Frame& f, double n, bool active) const {
    const bool dark = f.dark;
    const bool thumbnail = f.s < 64;
    const Rgba red = redColor(f);
    const double outer = 0.445 * f.s, sigma = 0.015 * f.s;
    for (int k = 0; k < kGraduations; ++k) {
        const bool major = k % 5 == 0;
        if (!major && (f.small || thumbnail)) continue;
        const double p = k / 40.0;
        const bool lit = active && p <= n + 1e-6;
        Rgba color;
        double alpha;
        if (lit) {
            color = f.labelColor(p);
            if (p > n - 1.0 / 40) color = dark ? color.mixLinear(kWhiteHot, 0.75) : color.mixLinear(kAmberHot, 0.25);
            if (f.flash > 0) color = color.mix(Rgba::white(), 0.25 * f.flash);
            alpha = 0.95;
        } else if (p >= kRedZone - 1e-6) {
            color = red;
            alpha = major ? 0.85 : 0.6;
        } else {
            color = dark ? Rgba::white() : Rgba::black();
            alpha = (major ? 0.5 : 0.24) * (dark ? 1 : 0.85);
        }
        if (f.signOfLife) {
            const double d = p - (*f.signOfLife * 1.3 - 0.15);
            alpha = std::min(1.0, alpha + (dark ? 0.35 : 0.25) * std::exp(-d * d * 90));
        }
        const double angle = angleOf(p);
        const double length = (major ? 0.062 : 0.03) * f.s, width = (major ? 0.014 : 0.0075) * f.s;
        const bool glows = lit && major && dark && f.effects.level >= 1 && !thumbnail;
        scene.add(Instance::capsule(f.point(angle, outer - length), f.point(angle, outer), width, color.opacity(alpha),
                                    color.opacity(alpha), 1, glows ? Instance::glowSigma(sigma) : 0,
                                    glows ? 0.8 * glowFactor(f) * shared::strokeGlow(width, sigma) : 0),
                  Blend::Over);
    }
}

void NeedleStyle::addTrace(Scene& scene, const Frame& f, double n) const {
    if (n <= 0.002) return;
    const double radius = 0.366 * f.s, width = 0.011 * f.s;
    const double flash = 1 + 0.25 * f.flash;
    if (f.dark) {
        scene.add(Instance::paletteArc(f.center, radius, width, angleOf(0), 270 * n, 0, n, std::min(1.0, 0.7 * flash),
                                       Instance::Band{.startAlpha = 0.3}),
                  Blend::Additive);
        return;
    }
    // Light: the label color of each position, in short runs blended between their ends.
    const int runs = std::max(2, static_cast<int>(std::ceil(n * 16)));
    for (int i = 0; i < runs; ++i) {
        const double p0 = n * i / runs, p1 = n * (i + 1) / runs;
        const double a0 = std::min(1.0, 0.85 * (0.3 + 0.7 * p0 / n) * flash);
        const double a1 = std::min(1.0, 0.85 * (0.3 + 0.7 * p1 / n) * flash);
        scene.add(Instance::bandArc(f.center, radius, width, angleOf(p0), 270 * (p1 - p0), f.labelColor(p1).opacity(a1),
                                    f.labelColor(p0).opacity(a0)),
                  Blend::Over);
    }
}

void NeedleStyle::addPins(Scene& scene, const Frame& f) const {
    const Rgba color = f.dark ? Rgba::white(0.32f) : Rgba::black(0.28f);
    for (const double n : {kZeroPin, kPegPin}) scene.add(Instance::circle(f.point(angleOf(n), 0.405 * f.s), 0.011 * f.s, color), Blend::Over);
}

void NeedleStyle::addLamps(Scene& scene, const Frame& f, double n, bool active) const {
    const bool dark = f.dark;
    const bool flashing = active && f.tier == Tier::Redline && f.effects.level >= 1;
    const Rgba housing = dark ? Rgba::white(0.08f) : Rgba::black(0.09f);
    const Rgba flashColor = dark ? heatColor(0.97).mixLinear(kWhiteHot, 0.55) : kLampLight;
    // 3 Hz, 28 → 100 % through smoothstep(−0.35, 0.35, sin φ): the product's only blinking element.
    const double flash = 0.28 + 0.72 * smoothstep(-0.35, 0.35, std::sin(lampPhase_));
    for (int i = 0; i < 5; ++i) {
        const double threshold = lampThreshold(i);
        const QPointF center = f.point(kLampAngles[i], 0.33 * f.s);
        scene.add(Instance::circle(center, 0.019 * f.s, housing), Blend::Over);
        double brightness = active && n >= threshold ? 1.0 : 0.0;
        Rgba color = dark ? heatColor(threshold) : heatColor(threshold).darkened(0.15);
        if (flashing) {
            color = flashColor;
            brightness = flash;
        }
        if (brightness <= 0.001) continue;
        const double radius = 0.019 * 0.86 * f.s, sigma = 0.02 * f.s;
        scene.add(Instance::circle(center, radius, color.opacity(brightness), 1, dark ? Instance::glowSigma(sigma) : 0,
                                   dark ? 0.9 * glowFactor(f) * shared::dotGlow(radius, sigma) : 0),
                  Blend::Over);
    }
}

void NeedleStyle::addWake(Scene& scene, const Frame& f, double n) const {
    if (f.effects.level < 1) return;
    const double speed = std::abs(angularSpeed_);
    if (speed <= kWakeThreshold) return;
    const double length = std::min(55.0, 4 + 0.085 * speed);
    const Rgba color = f.labelColor(f.heat);
    const Rgba bright = color.opacity(0.32), clear = color.opacity(0);
    const double head = angleOf(n);
    const double radius = (0.235 + 0.43) / 2 * f.s, width = (0.43 - 0.235) * f.s;
    if (angularSpeed_ > 0) {
        scene.add(Instance::bandArc(f.center, radius, width, head - length, length, bright, clear), f.glowBlend());
    } else {
        scene.add(Instance::bandArc(f.center, radius, width, head, length, clear, bright), f.glowBlend());
    }
}

void NeedleStyle::addNeedle(Scene& scene, const Frame& f, double n, bool active) const {
    const double s = f.s;
    const bool dark = f.dark;
    const double h = f.heat;
    const double angle = angleOf(n);
    const double tipFlare = active && s >= 64 ? flare(f.time) : 0;
    const double tip = 0.44 * s, tail = 0.09 * s;
    const double pivotHalf = 0.031 * s, tipHalf = 0.006 * s, tailHalf = 0.9 * pivotHalf;
    Rgba fill;
    if (!active) {
        fill = kIdleNeedle.opacity(0.75);
    } else if (dark) {
        fill = heatColor(std::max(h, 0.04)).mixLinear(kWhiteHot, 0.05 + 0.45 * f.overdrive);
    } else {
        fill = f.labelColor(std::max(h, 0.05));
    }
    const auto blade = [&](Rgba color, QPointF offset, double core, double glow, double glowAlpha) {
        return Instance::blade(f.center + offset, angle, tip, tail, pivotHalf, tipHalf, tailHalf, color, core, glow, glowAlpha);
    };
    if (dark) {
        const double sigma = (0.0275 + 0.0175 * tipFlare) * s * glowFactor(f);
        const double alpha = std::min(1.0, 0.45 + 0.35 * h + 0.6 * tipFlare) * shared::strokeGlow(pivotHalf + tipHalf, sigma);
        scene.add(blade(heatColor(std::max(0.1, h)).opacity(alpha), QPointF(), 0, Instance::glowSigma(sigma), 1), Blend::Additive);
    } else {
        scene.add(blade(Rgba::black(0.22f), QPointF(0.006 * s, 0.01 * s), 1, Instance::glowSigma(0.007 * s), 1), Blend::Over);
    }
    scene.add(blade(fill, QPointF(), 1, 0, 0), Blend::Over);

    const double ux = std::cos(angle * kPi / 180), uy = std::sin(angle * kPi / 180);
    const QPointF spineStart(f.center.x() + ux * 0.9 * kHub * s, f.center.y() + uy * 0.9 * kHub * s);
    const QPointF tipPoint(f.center.x() + ux * tip, f.center.y() + uy * tip);
    const QPointF spineEnd(tipPoint.x() - ux * tipHalf, tipPoint.y() - uy * tipHalf);
    const Rgba spine = dark ? kWhiteHot.opacity(std::min(1.0, 0.75 + 0.25 * tipFlare)) : Rgba::white(0.55f);
    scene.add(Instance::capsule(spineStart, spineEnd, std::max(0.6, 0.0045 * s), spine, spine), Blend::Over);
    if (tipFlare > 0.01) {
        const Rgba hot = dark ? kWhiteHot : kAmberHot;
        scene.add(Instance::circle(tipPoint, 0.06 * s, hot.opacity(0.7 * tipFlare)).radialGradient(hot.opacity(0), QPointF(), 0.06 * s),
                  f.glowBlend());
    }
}

void NeedleStyle::addSparks(Scene& scene, const Frame& f) const {
    if (sparks_.empty() || f.effects.level < 2) return;
    const double s = f.s;
    const Rgba start = f.dark ? kWhiteHot : kAmberHot;
    const Rgba end = f.dark ? heatColor(0.95) : f.labelColor(0.95);
    for (const auto& spark : sparks_) {
        const double u = spark.age / spark.life;
        const QPointF head(f.center.x() + spark.x * s, f.center.y() + spark.y * s);
        const QPointF tail(head.x() - spark.vx * 0.025 * s, head.y() - spark.vy * 0.025 * s);
        const Rgba color = start.mix(end, u).opacity((1 - u) * f.readoutZoneFade(head));
        scene.add(Instance::capsule(tail, head, spark.width * (1 - 0.5 * u) * f.unit, color, color.opacity(0.35)), f.glowBlend());
    }
}

void NeedleStyle::addHub(Scene& scene, const Frame& f) const {
    const double s = f.s;
    const bool dark = f.dark;
    const double radius = kHub * s;
    const QPointF center = f.center;
    const bool thumbnail = s < 64;
    if (!thumbnail) {
        scene.add(Instance::circle(QPointF(center.x(), center.y() + 0.012 * s), radius * 0.96, Rgba::black(dark ? 0.55f : 0.16f), 1,
                                   Instance::glowSigma(0.025 * s), 1),
                  Blend::Over);
    }
    const Rgba top = dark ? Rgba::hex(0x262C3C, 0.95f) : Rgba::hex(0xFFFFFF, 0.98f);
    const Rgba bottom = dark ? Rgba::hex(0x0C0E14, 0.95f) : Rgba::hex(0xECEFF5, 0.98f);
    scene.add(Instance::circle(center, radius, top).linearGradient(bottom, 90, radius), Blend::Over);
    if (thumbnail) return;
    // Inner highlight just inside the bezel.
    const Rgba highlightTop = dark ? Rgba::white(0.2f) : Rgba::white();
    const Rgba highlightBottom = dark ? Rgba::white(0.03f) : Rgba::black(0.1f);
    scene.add(Instance::ring(center, radius - 0.004 * s, std::max(1.0, 0.008 * s), highlightTop).linearGradient(highlightBottom, 90, radius),
              Blend::Over);
    // Chrome bezel with a 135° gradient (three stops), drawn as four quarter arcs between the stops.
    const Rgba first = dark ? Rgba::white(0.42f) : Rgba::white();
    const Rgba middle = dark ? Rgba::white(0.10f) : Rgba::hex(0xA0A8B6, 0.55f);
    const Rgba last = dark ? Rgba::white(0.22f) : Rgba::hex(0x78808E, 0.6f);
    const double width = std::max(1.2, 0.013 * s);
    const auto quarter = [&](double start, Rgba from, Rgba to) {
        scene.add(Instance::bandArc(center, radius, width, start, 90, to, from), Blend::Over);
    };
    quarter(225, first, middle);
    quarter(315, middle, last);
    quarter(45, last, middle);
    quarter(135, middle, first);
}

}  // namespace orb
