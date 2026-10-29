#include "orb/led_math.hpp"

#include <algorithm>
#include <cmath>

namespace orb::led {

namespace {

constexpr double kPi = 3.14159265358979323846;
constexpr double kSilkPeriod = 20000 * kPi;
constexpr double kClockPeriod = 10 * kPi;
constexpr double kStreakPeriod = 4096.0;

double wrap(double x, double period) {
    const double r = std::fmod(x, period);
    return r < 0 ? r + period : r;
}

double mod289(double x) { return x - std::floor(x * (1.0 / 289.0)) * 289.0; }
double permute(double x) { return mod289(((x * 34.0) + 1.0) * x); }

}  // namespace

double smoothstep(double e0, double e1, double x) {
    if (e0 == e1) return x < e0 ? 0 : 1;
    const double t = std::clamp((x - e0) / (e1 - e0), 0.0, 1.0);
    return t * t * (3 - 2 * t);
}

double easeOut(double t) {
    const double u = 1 - std::clamp(t, 0.0, 1.0);
    return 1 - u * u * u;
}

double fract(double x) { return x - std::floor(x); }

double clampedStep(double dt) { return std::isfinite(dt) ? std::clamp(dt, 0.0, kMaxStep) : 0; }

Phases rates(double h, double o) {
    Phases r;
    r.silk = 0.12 + 1.4 * h + 2.2 * o;
    r.light = 0.05 + 0.5 * h + 1.2 * o;
    r.flow = 0.35 + 1.9 * h + 1.6 * o;
    r.warp = 0.2 + 1.1 * h + 1.2 * o;
    r.corona = 0.5 + 1.7 * h + 1.8 * o;
    const double period = 6 + (0.7 - 6) * std::clamp(h, 0.0, 1.0);
    r.comet = (1 + 0.6 * o) / period;
    r.clock = 1;
    return r;
}

void advance(Phases& p, double h, double o, double dt, double silkRate, bool comet) {
    const double step = clampedStep(dt);
    if (step <= 0) return;
    const Phases r = rates(h, o);
    const double silk = std::max(0.0, silkRate) * step;
    p.silk = wrap(p.silk + r.silk * silk, kSilkPeriod);
    p.light = wrap(p.light + r.light * silk, kSilkPeriod);
    p.flow = wrap(p.flow + r.flow * step, 1);
    p.warp = wrap(p.warp + r.warp * step, kStreakPeriod);
    p.corona = wrap(p.corona + r.corona * step, kStreakPeriod);
    if (comet && h > 0.005) p.comet = wrap(p.comet + r.comet * step, 1);
    p.clock = wrap(p.clock + step, kClockPeriod);
}

double silkBrightness(double h, double o, double lvl) {
    return (0.3 + 0.65 * activity(h) + 0.3 * h + 0.25 * o) * (1 + 0.35 * (lvl - 1));
}

double ripple(double o) { return std::clamp(2.5 * o, 0.0, 1.0); }

double flow(int segment, double phase, double h) {
    const double ph = fract(2.2 * segment / kSegments - phase);
    return std::pow(1 - ph, 9) * activity(h) * (0.5 + 0.9 * h);
}

double flareAmount(double h, double o, double lvl) { return smoothstep(0.5, 0.85, h) * (0.55 + 0.9 * o) * lvl; }

int cometCount(double h, int maxComets) {
    if (maxComets <= 0 || h <= 0.01) return 0;
    return maxComets >= 2 && h > 0.55 ? 2 : 1;
}

double coronaAmount(double h, double o, double lvl) { return smoothstep(0.45, 0.85, h) * (0.45 + 0.55 * o) * lvl; }

double warpAmount(double h, double lvl) { return smoothstep(0.32, 0.62, h) * lvl; }

double warpSeed(int bin) { return fract(std::sin(static_cast<double>(bin + 3) * 127.1) * 43758.5453); }

double shockRadius(double age, double reach) { return 0.5 + std::min(0.24, std::max(0.0, reach - 0.51)) * easeOut(age); }

double wakeRadius(double age, double reach) {
    return 0.5 + std::min(0.16, std::max(0.0, reach - 0.51)) * easeOut(1.3 * age);
}

double peakRingRadius(double age, double reach) {
    const double end = std::min(kOuterReach, reach - 0.01);
    return 0.5 + std::max(0.0, end - 0.5) * easeOut(age / kPeakRingDuration);
}

double peakRayReach(double age, double reach) {
    const double end = std::min(kOuterReach, reach - 0.01);
    return 0.55 + std::max(0.0, end - 0.55) * easeOut(1.4 * age / kPeakRayDuration);
}

double peakFlash(double age) { return age < 0 ? 0 : 0.75 * std::exp(-4.5 * age); }

double simplex(double vx, double vy, double vz) {
    const double cx = 1.0 / 6.0, cy = 1.0 / 3.0;
    const double s = (vx + vy + vz) * cy;
    double ix = std::floor(vx + s), iy = std::floor(vy + s), iz = std::floor(vz + s);
    const double t = (ix + iy + iz) * cx;
    const double x0[3] = {vx - ix + t, vy - iy + t, vz - iz + t};
    const double g[3] = {x0[1] <= x0[0] ? 1.0 : 0.0, x0[2] <= x0[1] ? 1.0 : 0.0, x0[0] <= x0[2] ? 1.0 : 0.0};
    const double l[3] = {1 - g[0], 1 - g[1], 1 - g[2]};
    const double i1[3] = {std::min(g[0], l[2]), std::min(g[1], l[0]), std::min(g[2], l[1])};
    const double i2[3] = {std::max(g[0], l[2]), std::max(g[1], l[0]), std::max(g[2], l[1])};
    double xs[4][3];
    for (int k = 0; k < 3; ++k) {
        xs[0][k] = x0[k];
        xs[1][k] = x0[k] - i1[k] + cx;
        xs[2][k] = x0[k] - i2[k] + cy;
        xs[3][k] = x0[k] - 0.5;
    }
    ix = mod289(ix);
    iy = mod289(iy);
    iz = mod289(iz);
    const double oz[4] = {0, i1[2], i2[2], 1}, oy[4] = {0, i1[1], i2[1], 1}, ox[4] = {0, i1[0], i2[0], 1};
    double p[4];
    for (int k = 0; k < 4; ++k) p[k] = permute(permute(permute(iz + oz[k]) + iy + oy[k]) + ix + ox[k]);
    const double n = 1.0 / 7.0;
    const double ns[3] = {2 * n, 0.5 * n - 1, n};
    double x[4], y[4], h[4];
    for (int k = 0; k < 4; ++k) {
        const double j = p[k] - 49 * std::floor(p[k] / 49);
        const double xa = std::floor(j / 7);
        const double ya = std::floor(j - 7 * xa);
        x[k] = xa * ns[0] + ns[1];
        y[k] = ya * ns[0] + ns[1];
        h[k] = 1 - std::abs(x[k]) - std::abs(y[k]);
    }
    const double b0[4] = {x[0], x[1], y[0], y[1]}, b1[4] = {x[2], x[3], y[2], y[3]};
    double s0[4], s1[4], sh[4];
    for (int k = 0; k < 4; ++k) {
        s0[k] = std::floor(b0[k]) * 2 + 1;
        s1[k] = std::floor(b1[k]) * 2 + 1;
        sh[k] = h[k] <= 0 ? -1.0 : 0.0;
    }
    // a0 = b0.xzyw + s0.xzyw * sh.xxyy, a1 = b1.xzyw + s1.xzyw * sh.zzww
    const double a0[4] = {b0[0] + s0[0] * sh[0], b0[2] + s0[2] * sh[0], b0[1] + s0[1] * sh[1], b0[3] + s0[3] * sh[1]};
    const double a1[4] = {b1[0] + s1[0] * sh[2], b1[2] + s1[2] * sh[2], b1[1] + s1[1] * sh[3], b1[3] + s1[3] * sh[3]};
    double grads[4][3] = {{a0[0], a0[1], h[0]}, {a0[2], a0[3], h[1]}, {a1[0], a1[1], h[2]}, {a1[2], a1[3], h[3]}};
    double sum = 0;
    for (int k = 0; k < 4; ++k) {
        const double dd = grads[k][0] * grads[k][0] + grads[k][1] * grads[k][1] + grads[k][2] * grads[k][2];
        const double norm = 1.79284291400159 - 0.85373472095314 * dd;
        const double m = std::max(0.6 - (xs[k][0] * xs[k][0] + xs[k][1] * xs[k][1] + xs[k][2] * xs[k][2]), 0.0);
        const double dot = (grads[k][0] * xs[k][0] + grads[k][1] * xs[k][1] + grads[k][2] * xs[k][2]) * norm;
        sum += m * m * m * m * dot;
    }
    return 42.0 * sum;
}

void coronaBody(double phase, std::array<float, kCoronaSamples>& out) {
    const double z = phase * 0.35;
    for (int k = 0; k < kCoronaSamples; ++k) {
        const double a = static_cast<double>(k) / kCoronaSamples * 2 * kPi;
        out[static_cast<std::size_t>(k)] = static_cast<float>(0.5 + 0.5 * simplex(2.2 * std::cos(a), -2.2 * std::sin(a), z));
    }
}

Particles::Particles() { items_.reserve(kCapacity); }

void Particles::clear() {
    items_.clear();
    sparkCarry_ = emberCarry_ = 0;
}

double Particles::sparkRate(double h, double lvl, double countScale) {
    return h >= 0.45 ? 120 * ((h - 0.45) / 0.55) * lvl * 1.6 * countScale : 0;
}

double Particles::emberRate(double o, bool redline, double lvl, double countScale) {
    return redline ? 90 * std::max(o, 0.3) * lvl * 1.4 * countScale : 0;
}

int Particles::due(double& carry, double rate, double dt) {
    if (!std::isfinite(rate) || rate <= 0) return 0;
    carry += rate * dt;
    if (carry < 1) return 0;
    const double whole = std::floor(carry);
    carry -= whole;
    return static_cast<int>(std::min(whole, static_cast<double>(kCapacity)));
}

void Particles::step(double dt, double time, double s, double sparkRate, double emberRate, double lead, double o) {
    dt = clampedStep(dt);
    if (dt <= 0) return;
    for (std::size_t i = 0; i < items_.size();) {
        Particle& p = items_[i];
        p.age += dt;
        if (p.age >= p.life) {
            p = items_.back();
            items_.pop_back();
            continue;
        }
        if (p.kind == Particle::Kind::Ember) {
            p.vx += std::sin(time * 3 + p.seed) * 0.3 * s * dt;
            p.vy -= 0.12 * s * dt;
        }
        const double k = std::exp(-p.drag * dt);
        p.vx *= k;
        p.vy *= k;
        p.x += p.vx * dt;
        p.y += p.vy * dt;
        ++i;
    }
    for (int n = due(sparkCarry_, sparkRate, dt); n > 0 && items_.size() < kCapacity; --n) emitSpark(s, lead, o);
    for (int n = due(emberCarry_, emberRate, dt); n > 0 && items_.size() < kCapacity; --n) emitEmber(s);
}

void Particles::burst(double s, int count) {
    const auto room = static_cast<int>(kCapacity - items_.size());
    for (int i = 0, n = std::clamp(count, 0, room); i < n; ++i) {
        const double a = random_.next() * 2 * kPi;
        const double speed = (0.5 + random_.next() * 1.3) * s;
        Particle p;
        p.life = 0.7 + random_.next() * 0.9;
        p.size = 1.4 + random_.next() * 2.2;
        p.heat = 0.9 + random_.next() * 0.1;
        p.kind = random_.next() < 0.3 ? Particle::Kind::Gold : Particle::Kind::Spark;
        p.x = std::cos(a) * 0.48 * s;
        p.y = std::sin(a) * 0.48 * s;
        p.vx = std::cos(a) * speed;
        p.vy = std::sin(a) * speed;
        p.drag = 2.4;
        items_.push_back(p);
    }
}

void Particles::emitSpark(double s, double lead, double o) {
    lead = std::clamp(lead, 0.0, 1.0);
    const bool fromTip = random_.next() < 0.6;
    const double pos = fromTip ? lead : random_.next() * lead;
    const double a = (135 + pos * 270) * kPi / 180;
    const double speed = (0.3 + random_.next() * 0.6) * s * (1 + o * 0.8) * 1.3;
    const double tangential = (random_.next() - 0.35) * 0.6 * s * 1.3;
    Particle p;
    p.life = 0.5 + random_.next() * 0.7;
    p.size = 1.2 + random_.next() * 1.6;
    p.x = std::cos(a) * 0.41 * s;
    p.y = std::sin(a) * 0.41 * s;
    p.vx = std::cos(a) * speed - std::sin(a) * tangential;
    p.vy = std::sin(a) * speed + std::cos(a) * tangential;
    p.heat = std::min(1.0, pos + 0.1);
    p.drag = 2.2;
    items_.push_back(p);
}

void Particles::emitEmber(double s) {
    const double a = (200 + random_.next() * 140) * kPi / 180;
    Particle p;
    p.kind = Particle::Kind::Ember;
    p.vx = (random_.next() - 0.5) * 0.1 * s;
    p.vy = -(0.12 + random_.next() * 0.2) * s;
    p.life = 0.9 + random_.next() * 1.1;
    p.size = 1.2 + random_.next() * 1.8;
    p.heat = 0.85 + random_.next() * 0.15;
    p.seed = random_.next() * 100;
    p.x = std::cos(a) * 0.47 * s;
    p.y = std::sin(a) * 0.47 * s;
    p.drag = 0.4;
    p.streak = false;
    items_.push_back(p);
}

Effects Effects::make(int level, bool small) {
    Effects e;
    const int capped = std::clamp(level, 0, 3);
    const bool standard = capped >= 2;
    e.level = capped;
    e.lvl = capped == 3 ? 1.6 : 1;
    e.silkRate = capped == 0 ? 0 : (standard ? 1 : 0.5);
    e.silkRipples = standard;
    e.silkWaves = capped >= 1;
    e.flow = standard;
    e.tipFlare = standard;
    e.corona = standard;
    e.warp = standard;
    e.maxComets = capped == 0 ? 0 : (standard ? 2 : 1);
    e.pulses = capped >= 1;
    e.particles = standard;
    e.jitter = standard;
    e.skew = standard;
    e.countScale = small ? 0.5 : 1;
    e.breathing = capped >= 1;
    e.shimmer = capped >= 1;
    e.flash = capped >= 1;
    e.idleSweep = capped >= 1;
    e.celebrate = capped >= 1;
    e.bloom = capped == 3 ? 1.35 : (capped == 0 ? 0.7 : 1);
    return e;
}

}  // namespace orb::led
