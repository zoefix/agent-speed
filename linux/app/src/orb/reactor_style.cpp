#include "orb/reactor_style.hpp"

#include <algorithm>
#include <cmath>

#include "orb/shared_layers.hpp"

namespace orb {

namespace {

constexpr double kPi = 3.14159265358979323846;
constexpr double kCore = 0.30;
constexpr double kStator = 0.468, kStatorWidth = 0.006, kNotchInner = 0.456, kNotchOuter = 0.474, kNotchWidth = 0.005;
constexpr double kRotorA = 0.425, kRotorAWidth = 0.024, kRotorB = 0.383, kRotorBWidth = 0.018, kRotorC = 0.338,
                 kRotorCWidth = 0.009;
constexpr double kCoreRim = 0.009;
constexpr int kArcsA = 3, kArcsC = 2;
constexpr double kKick = 0.25, kKickTau = 0.5;
constexpr double kLockSwing = 0.42, kLockEnd = 0.57, kLockOvershoot = 1.2;
constexpr double kCoronaFlare = 0.35, kFlareRing = 0.32, kFlash = 0.12, kFlashInterval = 1.0 / 3;
constexpr int kBoltSegments = 8, kSparksPerBolt = 6, kBoltCapacity = 4, kSparkCapacity = 64;
constexpr double kBoltRefresh = 0.05, kBoltJitter = 0.048;
const Rgba kLockGold = Rgba::hex(0xFACC15);

Rgba slate() { return heatColor(0); }

int coilCount(bool small) { return small ? 12 : 24; }
double coilLength(bool small) { return small ? 13 : 7.5; }

double wrap(double angle) {
    const double w = std::fmod(angle, 360.0);
    return w < 0 ? w + 360 : w;
}

// The nearest pose ahead in the direction of travel with an arc head at 12 o'clock (−90°).
double lockTarget(double angle, double symmetry, bool clockwise) {
    const double steps = (angle + 90) / symmetry;
    return (clockwise ? std::ceil(steps) : std::floor(steps)) * symmetry - 90;
}

double easeOutBack(double u, double k) {
    const double x = u - 1;
    return 1 + (k + 1) * x * x * x + k * x * x;
}

double hash01(int a, int b) {
    std::uint64_t x = static_cast<std::uint64_t>(static_cast<std::int64_t>(a) * 73856093 ^ static_cast<std::int64_t>(b) * 19349663);
    x ^= x >> 33;
    x *= 0xff51afd7ed558ccdULL;
    x ^= x >> 33;
    x *= 0xc4ceb9fe1a85ec53ULL;
    x ^= x >> 33;
    return static_cast<double>(x % 10000) / 10000;
}

}  // namespace

const ReadoutLayout& ReactorStyle::readoutLayout() const {
    static const ReadoutLayout layout = [] {
        ReadoutLayout l;
        l.valueSize = 0.205;
        l.valueBaseline = 0.03;
        l.valueMaxWidth = 0.46;
        l.unitSize = 0.062;
        l.unitGap = 0.08;
        l.titlePlace = 0.074;
        l.titleSize = 0.058;
        l.titleMaxWidth = 0.50;
        l.infoMaxWidth = 0.52;
        l.dots = 0.252;
        l.pill = -0.172;
        return l;
    }();
    return layout;
}

void ReactorStyle::pulse(double time, double strength, double heat) { pendingPulse_ = PendingPulse{time, strength, heat}; }

void ReactorStyle::celebrate(double time) { pendingLock_ = time; }

void ReactorStyle::step(const Frame& f) {
    if (f.resumed) {
        kick_ = 0;
        bolts_.clear();
        sparks_.clear();
        boltCarry_ = 0;
        lockStart_ = -1e9;
        pendingLock_.reset();
        pendingPulse_.reset();
        flareStart_ = -1e9;
    }
    const double dt = f.dt;
    const int level = f.effects.level;
    const bool still = level == 0;
    const bool active = !f.idle();
    const double h = f.heat * f.activity, o = f.overdrive;
    const int coils = coilCount(f.small);

    if (pendingPulse_) {
        if (!still) {
            kick_ += kKick * pendingPulse_->strength;
            flareStart_ = pendingPulse_->time;
            flareHeat_ = pendingPulse_->heat;
        }
        pendingPulse_.reset();
    }
    if (pendingLock_) {
        lockStart_ = *pendingLock_;
        pendingLock_.reset();
        lockFrom_[0] = angleA_;
        lockFrom_[1] = angleB_;
        lockFrom_[2] = angleC_;
        lockTo_[0] = lockTarget(angleA_, 360.0 / kArcsA, true);
        lockTo_[1] = lockTarget(angleB_, 360.0 / coils, false);
        lockTo_[2] = lockTarget(angleC_, 360.0 / kArcsC, true);
    }
    if (dt <= 0) return;
    kick_ *= std::exp(-dt / kKickTau);
    if (kick_ < 0.0005) kick_ = 0;
    if (!still) breathPhase_ = std::fmod(breathPhase_ + 2 * kPi * (0.3 + 1.7 * h) * dt, 2 * kPi);

    const double lockAge = f.time - lockStart_;
    if (lockAge >= 0 && lockAge < kLockEnd) {
        const double p = easeOutBack(std::clamp(lockAge / kLockSwing, 0.0, 1.0), kLockOvershoot);
        angleA_ = lockFrom_[0] + (lockTo_[0] - lockFrom_[0]) * p;
        angleB_ = lockFrom_[1] + (lockTo_[1] - lockFrom_[1]) * p;
        angleC_ = lockFrom_[2] + (lockTo_[2] - lockFrom_[2]) * p;
        if (lockAge + dt >= kLockEnd) {
            angleA_ = wrap(angleA_);
            angleB_ = wrap(angleB_);
            angleC_ = wrap(angleC_);
        }
    } else if (!still && active) {
        // rev/s: A 0.03 + 0.45 h^1.3 + 0.35 o + k; B −(0.02 + 0.30 h^1.3 + 0.25 o + 0.6 k); C 0.05 + 1.1 h^1.3 + 0.7 o + 1.2 k.
        const double p = std::pow(std::max(0.0, h), 1.3);
        angleA_ = wrap(angleA_ + 360 * (0.03 + 0.45 * p + 0.35 * o + kick_) * dt);
        angleB_ = wrap(angleB_ - 360 * (0.02 + 0.30 * p + 0.25 * o + 0.6 * kick_) * dt);
        angleC_ = wrap(angleC_ + 360 * (0.05 + 1.1 * p + 0.7 * o + 1.2 * kick_) * dt);
    }
    if (!still && active) bandPhase_ = std::fmod(bandPhase_ + 2 * kPi * (0.15 + 1.4 * h + 0.6 * o) * dt, 2 * kPi);

    const bool max = level == 3;
    const bool boiling = active && level >= 2 && f.tier == Tier::Redline;
    const double rate = boiling ? (1.5 + 6 * o) * (max ? 1.6 : 1) : 0;
    if (rate > 0 || !bolts_.empty() || !sparks_.empty()) {
        const int started = stepLightning(f.time, dt, rate, max ? 4 : 3, max ? 64 : 40, o);
        if (started > 0 && f.time - lastFlash_ >= kFlashInterval - 1e-9) lastFlash_ = f.time;
    }
}

int ReactorStyle::stepLightning(double time, double dt, double rate, int maxBolts, int sparkPool, double overdrive) {
    std::erase_if(bolts_, [time](const Bolt& b) { return time - b.start >= b.life; });
    const double drag = std::exp(-3.0 * dt);
    for (std::size_t i = 0; i < sparks_.size();) {
        auto& p = sparks_[i];
        p.age += dt;
        if (p.age >= p.life) {
            p = sparks_.back();
            sparks_.pop_back();
            continue;
        }
        p.vx *= drag;
        p.vy *= drag;
        p.x += p.vx * dt;
        p.y += p.vy * dt;
        ++i;
    }
    if (rate <= 0) {
        boltCarry_ = 0;
        return 0;
    }
    boltCarry_ += rate * dt;
    int started = 0;
    while (boltCarry_ >= 1) {
        boltCarry_ -= 1;
        if (static_cast<int>(bolts_.size()) >= std::min(maxBolts, kBoltCapacity)) continue;
        emitBolt(time, overdrive, std::min(sparkPool, kSparkCapacity));
        ++started;
    }
    return started;
}

// From the core rim to rotor A within ±29°, or — when o > 0.3, 40 % of bolts — outward from r 0.437 S into the aura.
void ReactorStyle::emitBolt(double time, double overdrive, int sparkPool) {
    const double theta = random_.next() * 2 * kPi;
    const bool outward = overdrive > 0.3 && random_.next() < 0.4;
    const double spread = 29 * kPi / 180;
    Bolt bolt;
    bolt.start = time;
    bolt.life = 0.09 + 0.09 * random_.next();
    bolt.from = theta;
    bolt.to = theta + (random_.next() * 2 - 1) * spread * (outward ? 0.6 : 1);
    bolt.inner = outward ? 0.437 : 0.306;
    bolt.outer = outward ? 0.56 + 0.10 * random_.next() : kRotorA;
    bolt.seed = static_cast<int>(random_.next() * 1000000);
    bolts_.push_back(bolt);
    for (int i = 0; i < kSparksPerBolt && static_cast<int>(sparks_.size()) < sparkPool; ++i) {
        const double speed = 0.2 + 0.4 * random_.next();
        const double direction = bolt.to + (random_.next() * 2 - 1) * 1.2;
        sparks_.push_back({bolt.outer * std::cos(bolt.to), bolt.outer * std::sin(bolt.to), std::cos(direction) * speed,
                           std::sin(direction) * speed, 0, 0.15 + 0.25 * random_.next(), 0.8 + 0.7 * random_.next()});
    }
}

// Point `index` (0...8) of a bolt's polyline in diameters from the center, re-randomized every 50 ms.
QPointF ReactorStyle::boltPoint(const Bolt& bolt, int index, double time) {
    const double u = static_cast<double>(index) / kBoltSegments;
    const double theta = bolt.from + (bolt.to - bolt.from) * u;
    const double radius = bolt.inner + (bolt.outer - bolt.inner) * u;
    const int step = static_cast<int>(std::floor((time - bolt.start) / kBoltRefresh));
    const double jitter =
        index == 0 || index == kBoltSegments ? 0 : (hash01(bolt.seed + step * 31, index) - 0.5) * 2 * kBoltJitter * std::sin(kPi * u);
    return {radius * std::cos(theta) - std::sin(theta) * jitter, radius * std::sin(theta) + std::cos(theta) * jitter};
}

double ReactorStyle::lockGold(const Frame& f) const {
    const double age = f.time - lockStart_;
    if (age < 0 || age >= kLockEnd) return 0;
    return std::max(0.0, 1 - std::abs(age / kLockSwing - 1) / 0.35);
}

double ReactorStyle::boltFlash(const Frame& f) const {
    const double age = f.time - lastFlash_;
    return age >= 0 && age < kFlash ? 1 - age / kFlash : 0;
}

bool ReactorStyle::isBusy(const Frame& f) const {
    const double lockAge = f.time - lockStart_;
    const double flareAge = f.time - flareStart_;
    return kick_ > 0 || (lockAge >= 0 && lockAge < kLockEnd) || pendingLock_ || pendingPulse_ || !bolts_.empty() ||
           !sparks_.empty() || (flareAge >= 0 && flareAge < kCoronaFlare) || boltFlash(f) > 0;
}

int ReactorStyle::ambientFps(const Frame& f) const {
    if (f.idle()) return 0;
    return f.tier >= Tier::Overdrive ? 60 : 30;
}

void ReactorStyle::build(Scene& scene, const Frame& f) {
    const bool thumbnail = f.s < 64;
    const bool active = !f.idle();
    const double h = f.heat * f.activity;
    const bool dark = f.dark;
    const Palette palette{f.labelColor(std::max(h, dark ? 0.02 : 0.05)), f.hot(h), f.tokens.unlitSegment, dark ? 0.25 : 0.5,
                          f.effects.level == 3 ? 1.3 : 1.0};
    const double gold = f.effects.celebrate ? lockGold(f) : 0;
    if (!thumbnail) {
        addStator(scene, f, palette);
        const Rgba track = palette.unlit;
        scene.add(Instance::ring(f.center, kRotorA * f.s, kRotorAWidth * f.s, track), Blend::Over);
        scene.add(Instance::ring(f.center, kRotorC * f.s, kRotorCWidth * f.s, track), Blend::Over);
        if (active) addCorona(scene, f, palette, h);
    }
    addRotorA(scene, f, palette, h, active, gold);
    if (!thumbnail) addRotorB(scene, f, palette, h, active, gold);
    addRotorC(scene, f, palette, h, active, gold);
    if (!thumbnail) {
        addFlareRing(scene, f);
        addLightning(scene, f);
    }
    addCore(scene, f, palette, h, active, gold);
}

void ReactorStyle::addStator(Scene& scene, const Frame& f, const Palette& palette) const {
    const double s = f.s;
    scene.add(Instance::ring(f.center, kStator * s, std::max(1.0, kStatorWidth * s), palette.unlit.opacity(1.4)), Blend::Over);
    const int count = f.small ? 4 : 24;
    for (int i = 0; i < count; ++i) {
        const double angle = 360.0 * i / count;
        const bool bright = count == 4 || i % 6 == 0;
        const Rgba color = palette.unlit.opacity(bright ? 2.6 : 1.4);
        scene.add(Instance::capsule(f.point(angle, kNotchInner * s), f.point(angle, kNotchOuter * s), std::max(0.8, kNotchWidth * s), color, color),
                  Blend::Over);
    }
}

void ReactorStyle::addCorona(Scene& scene, const Frame& f, const Palette& palette, double h) const {
    const bool still = f.effects.level == 0;
    const double breath = still ? 1 : 1 + 0.15 * std::sin(breathPhase_);
    const double flareAge = f.time - flareStart_;
    const double flare = !still && flareAge >= 0 && flareAge < kCoronaFlare ? 0.25 * (1 - flareAge / kCoronaFlare) : 0;
    const double flash = 0.2 * boltFlash(f) + 0.25 * f.flash;
    const double base = f.dark ? 0.10 + 0.42 * h : 0.06 + 0.2 * h;
    const double alpha = (base * breath + flare + flash) * (f.effects.level == 3 ? 1.2 : 1);
    if (alpha <= 0.004) return;
    const Rgba color = f.dark ? palette.heat.mixLinear(kWhiteHot, 0.2 * f.overdrive) : palette.heat;
    scene.add(Instance::radial(f.center, kCore * f.s, 0.42 * f.s, color, std::min(1.0, alpha)), f.glowBlend());
}

std::pair<Rgba, Rgba> ReactorStyle::arcColors(const Frame& f, const Palette& palette, bool active, double gold, double& alpha) const {
    if (!active) {
        alpha = f.dark ? 0.38 : 0.45;
        return {slate(), slate()};
    }
    Rgba head = palette.hot;
    if (gold > 0) head = head.mix(kLockGold, gold);
    if (f.flash > 0) head = head.mix(Rgba::white(), 0.25 * f.flash);
    alpha = 1;
    return {head, palette.heat};
}

void ReactorStyle::addRotorA(Scene& scene, const Frame& f, const Palette& palette, double h, bool active, double gold) const {
    const double s = f.s;
    const double length = std::min(120.0, 20 + 90 * (active ? h : 0) + 10 * (active ? f.overdrive : 0));
    const double width = kRotorAWidth * s * (active ? 1 : 0.6);
    const double radius = kRotorA * s;
    double alpha = 1;
    const auto [head, tail] = arcColors(f, palette, active, gold, alpha);
    const bool glows = active && f.dark && s >= 64;
    const double sigma = 0.015 * s * (f.effects.level == 3 ? 1.3 : 1);
    const double glowAlpha = 0.7 * palette.glow * shared::strokeGlow(width, sigma);
    for (int i = 0; i < kArcsA; ++i) {
        const double at = angleA_ + i * 120;
        if (glows) {
            const Rgba glow = palette.heat.opacity(glowAlpha);
            scene.add(Instance::bandArc(f.center, radius, width, at - length, length, glow, glow,
                                        Instance::Band{.startAlpha = palette.tailFloor, .core = 0, .glow = Instance::glowSigma(sigma), .glowAlpha = 1}),
                      Blend::Additive);
        }
        scene.add(Instance::bandArc(f.center, radius, width, at - length, length, head.opacity(alpha), tail.opacity(alpha),
                                    Instance::Band{.startAlpha = palette.tailFloor}),
                  f.glowBlend());
    }
}

void ReactorStyle::addRotorB(Scene& scene, const Frame& f, const Palette& palette, double h, bool active, double gold) const {
    const double s = f.s;
    const int count = coilCount(f.small);
    const double length = coilLength(f.small);
    const double radius = kRotorB * s, width = kRotorBWidth * s;
    for (int i = 0; i < count; ++i) {
        const double position = static_cast<double>(i) / count;
        // e = 0.18 + 0.82 (½ + ½ cos(3θ − ψ))^2.2 (0.35 + 0.65 h): three bright bands run against the coils.
        double energy = active ? 0.18 + 0.82 * std::pow(0.5 + 0.5 * std::cos(3 * position * 2 * kPi - bandPhase_), 2.2) * (0.35 + 0.65 * h) : 0.25;
        if (f.signOfLife) {
            double d = std::fmod(position - *f.signOfLife, 1.0);
            if (d < 0) d += 1;
            const double distance = std::min(d, 1 - d) * 7;
            energy += 0.6 * std::exp(-distance * distance);
        }
        Rgba color;
        if (gold > 0) {
            energy = std::max(energy, gold);
            color = (active ? palette.heat : slate()).mix(kLockGold, gold);
        } else if (!active) {
            color = slate();
        } else {
            color = energy > 0.75 ? palette.heat.mix(palette.hot, (energy - 0.75) * 3) : palette.heat;
            if (f.flash > 0) color = color.mix(Rgba::white(), 0.2 * f.flash);
        }
        const double center = angleB_ + position * 360;
        const Rgba c = color.opacity(std::min(1.0, energy));
        scene.add(Instance::bandArc(f.center, radius, width, center - length / 2, length, c, c), f.glowBlend());
    }
}

void ReactorStyle::addRotorC(Scene& scene, const Frame& f, const Palette& palette, double h, bool active, double gold) const {
    const double s = f.s;
    const double length = std::min(175.0, 14 + 130 * (active ? h : 0) + 20 * (active ? f.overdrive : 0));
    double alpha = 1;
    const auto [head, tail] = arcColors(f, palette, active, gold, alpha);
    alpha *= active ? 0.95 : 1;
    for (int i = 0; i < kArcsC; ++i) {
        const double at = angleC_ + i * 180;
        scene.add(Instance::bandArc(f.center, kRotorC * s, kRotorCWidth * s, at - length, length, head.opacity(alpha), tail.opacity(alpha),
                                    Instance::Band{.startAlpha = palette.tailFloor}),
                  f.glowBlend());
    }
}

// A ring from r 0.30 to 0.50 S in 0.32 s (ease-out), width 0.012 S → 0, at 55 %.
void ReactorStyle::addFlareRing(Scene& scene, const Frame& f) const {
    if (f.effects.level < 1) return;
    const double age = f.time - flareStart_;
    if (age < 0 || age >= kFlareRing) return;
    const double u = age / kFlareRing;
    const double eased = 1 - std::pow(1 - u, 3);
    const double radius = kCore + (0.5 - kCore) * eased;
    const Rgba color = f.labelColor(std::max(0.15, flareHeat_)).opacity(0.55 * (1 - u));
    scene.add(Instance::ring(f.center, radius * f.s, 0.012 * (1 - u) * f.s + 0.5, color), f.glowBlend());
}

void ReactorStyle::addLightning(Scene& scene, const Frame& f) const {
    if (bolts_.empty() && sparks_.empty()) return;
    const double s = f.s;
    const QPointF c = f.center;
    const bool dark = f.dark;
    const Rgba glowColor = dark ? heatColor(0.92) : f.labelColor(0.9);
    const Rgba coreColor = dark ? kWhiteHot : kAmberHot;
    const Blend blend = f.glowBlend();
    const auto at = [&](QPointF p) { return QPointF(c.x() + p.x() * s, c.y() + p.y() * s); };
    for (const auto& bolt : bolts_) {
        const double u = (f.time - bolt.start) / bolt.life;
        const double alpha = u < 0 || u >= 1 ? 0 : u < 0.2 ? 1 : 1 - (u - 0.2) / 0.8;
        if (alpha <= 0.01) continue;
        for (int stroke = 0; stroke < 3; ++stroke) {
            const double width = stroke == 0 ? 0.03 * s : stroke == 1 ? 0.011 * s : std::max(0.6, 0.0045 * s);
            const Rgba color = stroke == 0 ? glowColor.opacity(0.28 * alpha) : stroke == 1 ? glowColor.opacity(0.75 * alpha) : coreColor.opacity(alpha);
            QPointF previous = at(boltPoint(bolt, 0, f.time));
            for (int i = 1; i <= kBoltSegments; ++i) {
                const QPointF next = at(boltPoint(bolt, i, f.time));
                scene.add(Instance::capsule(previous, next, width, color, color), blend);
                previous = next;
            }
        }
    }
    if (f.effects.level < 2) return;
    for (const auto& spark : sparks_) {
        const double u = spark.age / spark.life;
        const QPointF center = at(QPointF(spark.x, spark.y));
        const Rgba color = dark ? kWhiteHot.mix(heatColor(0.9), u) : kAmberHot;
        scene.add(Instance::circle(center, spark.size * 0.6 * f.unit, color.opacity((1 - u) * f.readoutZoneFade(center))), blend);
    }
}

void ReactorStyle::addCore(Scene& scene, const Frame& f, const Palette& palette, double h, bool active, double gold) const {
    const double s = f.s;
    const double radius = kCore * s;
    const bool dark = f.dark;
    const Rgba top = dark ? Rgba::hex(0x181C28, 0.97f) : Rgba::hex(0xFFFFFF, 0.99f);
    const Rgba bottom = dark ? Rgba::hex(0x080A0F, 0.97f) : Rgba::hex(0xEEF1F6, 0.99f);
    scene.add(Instance::circle(f.center, radius, top).radialGradient(bottom, QPointF(0, -0.08 * s), radius + 0.08 * s), Blend::Over);
    const double width = std::max(1.0, kCoreRim * s);
    if (!active) {
        scene.add(Instance::ring(f.center, radius, width, palette.unlit.opacity(2.2)), Blend::Over);
        return;
    }
    Rgba rim = gold > 0 ? palette.heat.mix(kLockGold, gold) : palette.heat.mix(palette.hot, 0.4 * f.overdrive);
    if (f.flash > 0) rim = rim.mix(Rgba::white(), 0.25 * f.flash);
    if (dark && s >= 64) {
        const double sigma = (0.015 + 0.015 * h) * s * (f.effects.level == 3 ? 1.3 : 1);
        const Rgba glow = palette.heat.opacity(0.9 * palette.glow * shared::strokeGlow(width, sigma));
        scene.add(Instance::ring(f.center, radius, width, glow, 0, Instance::glowSigma(sigma), 1), Blend::Additive);
    }
    scene.add(Instance::ring(f.center, radius, width, rim.opacity(0.6 + 0.4 * h)), Blend::Over);
}

}  // namespace orb
