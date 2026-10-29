#include "hero/hero_stage.hpp"

#include <algorithm>
#include <cmath>

namespace hero {

namespace {

constexpr double kPi = 3.14159265358979323846;

double dot(QPointF a, QPointF b) { return a.x() * b.x() + a.y() * b.y(); }

}  // namespace

double clamp(double x, double lo, double hi) { return std::min(hi, std::max(lo, x)); }

double smooth(double t) {
    t = clamp(t, 0, 1);
    return t * t * (3 - 2 * t);
}

double easeInCubic(double t) {
    t = clamp(t, 0, 1);
    return t * t * t;
}

double easeOutCubic(double t) {
    const double u = 1 - clamp(t, 0, 1);
    return 1 - u * u * u;
}

double easeInOutSine(double t) { return 0.5 - 0.5 * std::cos(kPi * clamp(t, 0, 1)); }

double bump(double tau, double start, double peak, double end) {
    if (tau < start) return 0;
    if (tau < peak) return smooth((tau - start) / (peak - start));
    return 1 - smooth((tau - peak) / (end - peak));
}

// An underdamped spring with response / damping-fraction semantics.
double spring(double t, double from, double to, double velocity, double response, double damping) {
    if (t <= 0) return from;
    const double omega = 2 * kPi / response, zeta = damping;
    const double wd = omega * std::sqrt(1 - zeta * zeta);
    const double a = from - to;
    const double b = (velocity + zeta * omega * a) / wd;
    return to + std::exp(-zeta * omega * t) * (a * std::cos(wd * t) + b * std::sin(wd * t));
}

double springVelocity(double t, double from, double to, double velocity, double response, double damping) {
    if (t <= 0) return velocity;
    const double omega = 2 * kPi / response, zeta = damping;
    const double wd = omega * std::sqrt(1 - zeta * zeta);
    const double a = from - to;
    const double b = (velocity + zeta * omega * a) / wd;
    const double decay = std::exp(-zeta * omega * t);
    return decay * ((-zeta * omega * a + b * wd) * std::cos(wd * t) + (-zeta * omega * b - a * wd) * std::sin(wd * t));
}

double wrapAngle(double angle) { return angle - 2 * kPi * std::round(angle / (2 * kPi)); }

double CriticalSpring::step(double target, double dt) {
    const double c1 = value - target;
    const double c2 = velocity + omega * c1;
    const double decay = std::exp(-omega * dt);
    value = target + (c1 + c2 * dt) * decay;
    velocity = (c2 - omega * (c1 + c2 * dt)) * decay;
    return value;
}

double Random::next() {
    state ^= state << 13;
    state ^= state >> 17;
    state ^= state << 5;
    return static_cast<double>(state) / 4294967296.0;
}

// ---- speed lines

bool SpeedLines::empty() const {
    return std::none_of(pool.begin(), pool.end(), [](const SpeedLine& l) { return l.alive; });
}

void SpeedLines::clear() {
    for (auto& l : pool) l.alive = false;
    timer_ = 0;
}

int SpeedLines::cap(Level level, double heat, double ground, double low) {
    const double n = std::round((2 + 8 * heat) * clamp(ground / std::max(low, 1e-9), 0, 1));
    switch (level) {
    case Level::Off: return 0;
    case Level::Soft: return std::min(5, static_cast<int>(std::lround(n * 0.6)));
    case Level::Standard: return std::min(9, static_cast<int>(n));
    case Level::Max: return std::min(14, static_cast<int>(std::lround(n * 1.4)));
    }
    return 0;
}

// A line whose height crosses the vehicle is born at its tail so it peels off the body; bursts are longer, faster and
// brighter.
void SpeedLines::spawn(bool burst, double heat, double ground, double x0, double y0, double, double y1, std::optional<double> heading,
                       Random& random, const std::array<double, 4>* bands) {
    auto slot = std::find_if(pool.begin(), pool.end(), [](const SpeedLine& l) { return !l.alive; });
    if (slot == pool.end()) return;
    SpeedLine line;
    line.alive = true;
    line.length = (6 + 20 * heat) * (0.7 + 0.6 * random.next()) * (burst ? 1.4 : 1);
    line.speed = 1.25 * std::max(ground, 40.0) * (0.85 + 0.3 * random.next()) * (burst ? 1.3 : 1);
    line.opacity = (0.16 + 0.32 * heat) * (0.7 + 0.6 * random.next()) * (burst ? 1.4 : 1);
    if (heading) {
        const double lateral = (random.next() - 0.5) * 80;
        const double ux = std::cos(*heading), uy = std::sin(*heading);
        line.dx = -ux;
        line.dy = -uy;
        line.x = 50 + ux * 52 - uy * lateral;
        line.y = 50 + uy * 52 + ux * lateral;
    } else if (bands) {
        const double r = random.next();
        line.y = r < 0.5 ? (*bands)[0] + ((*bands)[1] - (*bands)[0]) * r * 2 : (*bands)[2] + ((*bands)[3] - (*bands)[2]) * (r - 0.5) * 2;
        line.x = 102 + line.length * random.next() * 0.5;
    } else {
        const double y = 16 + 68 * random.next();
        line.y = y;
        line.x = y > y0 - 2 && y < y1 + 2 ? x0 - 2 : 102 + line.length * random.next() * 0.5;
    }
    *slot = line;
}

void SpeedLines::step(double dt, int cap, double heat, double ground, double x0, double y0, double x1, double y1, std::optional<double> heading,
                      Random& random, const std::array<double, 4>* bands) {
    int alive = 0;
    for (auto& line : pool) {
        if (!line.alive) continue;
        line.age += dt;
        line.x += line.dx * line.speed * dt;
        line.y += line.dy * line.speed * dt;
        if (line.x < -line.length - 4 || line.x > 140 || line.y < -40 || line.y > 140) {
            line.alive = false;
        } else {
            ++alive;
        }
    }
    timer_ -= dt;
    if (alive < cap && timer_ <= 0) {
        spawn(false, heat, ground, x0, y0, x1, y1, heading, random, bands);
        timer_ = 0.9 / std::max(cap, 1) * (0.5 + random.next());
    }
}

// ---- particles

double Particle::progress() const {
    const double shown = age - delay;
    if (shown < 0) return -1;
    return std::min(1.0, shown / life);
}

bool Particles::empty() const {
    return std::none_of(pool.begin(), pool.end(), [](const Particle& p) { return p.alive; });
}

void Particles::clear() {
    for (auto& p : pool) p.alive = false;
    accumulator_ = 0;
}

Particle* Particles::free() {
    for (auto& p : pool) {
        if (!p.alive) return &p;
    }
    return nullptr;
}

void Particles::burst(Particle::Kind kind, QPointF at, QPointF dir, int count, int cap, Random& random) {
    for (int index = 0; index < std::min(count, cap); ++index) {
        Particle* slot = free();
        if (!slot) return;
        Particle p;
        p.alive = true;
        p.kind = kind;
        p.x = at.x();
        p.y = at.y();
        switch (kind) {
        case Particle::Kind::Puff: {
            const double speed = 8 + 14 * random.next();
            const double angle = std::atan2(dir.y(), dir.x()) + (random.next() - 0.5) * 0.9;
            p.vx = std::cos(angle) * speed;
            p.vy = std::sin(angle) * speed - 3 * random.next();
            p.life = 0.5 + 0.35 * random.next();
            p.size = 0.8 + 0.5 * random.next();
            p.grow = 2.6 + 1.6 * random.next();
            break;
        }
        case Particle::Kind::Arc:
            p.angle = random.next() * 2 * kPi;
            p.life = 0.16 + 0.14 * random.next();
            p.size = 3.8 + 2 * random.next();
            p.seed = random.next();
            p.delay = index * 0.03;
            break;
        case Particle::Kind::Pop:
            p.vx = dir.x() * 4;
            p.life = 0.22;
            p.angle = std::atan2(dir.y(), dir.x());
            break;
        case Particle::Kind::Ring: {
            const double side = index % 2 == 1 ? 1.0 : -1.0;
            const double angle = std::atan2(dir.y(), dir.x()) + side * (0.55 + 0.45 * random.next());
            const double speed = 22 + 14 * random.next();
            p.vx = std::cos(angle) * speed;
            p.vy = std::sin(angle) * speed;
            p.life = 0.45 + 0.2 * random.next();
            p.grow = 3 + 2 * random.next();
            break;
        }
        case Particle::Kind::Spark:
        case Particle::Kind::Ember:
            p.life = 0.3;
            break;
        }
        *slot = p;
    }
}

void Particles::stream(double rate, double dt, QPointF at, QPointF dir, double speed, double spread, double gravity, double life, Random& random) {
    accumulator_ += rate * dt;
    while (accumulator_ >= 1) {
        accumulator_ -= 1;
        Particle* slot = free();
        if (!slot) return;
        const double angle = std::atan2(dir.y(), dir.x()) + (random.next() - 0.5) * spread;
        const double velocity = speed * (0.6 + 0.6 * random.next());
        Particle p;
        p.alive = true;
        p.kind = Particle::Kind::Spark;
        p.x = at.x();
        p.y = at.y();
        p.vx = std::cos(angle) * velocity;
        p.vy = std::sin(angle) * velocity;
        p.life = life * (0.7 + 0.6 * random.next());
        p.gravity = gravity;
        *slot = p;
    }
}

void Particles::step(double dt, double ground) {
    for (auto& p : pool) {
        if (!p.alive) continue;
        p.age += dt;
        if (p.age > p.life + p.delay) {
            p.alive = false;
            continue;
        }
        switch (p.kind) {
        case Particle::Kind::Puff:
        case Particle::Kind::Ring: {
            const double drag = std::exp(-dt * 3);
            p.vx *= drag;
            p.vy *= drag;
            p.vx -= 0.15 * ground * dt;
            break;
        }
        case Particle::Kind::Spark:
            p.vy += p.gravity * dt;
            break;
        case Particle::Kind::Ember:
            p.vx *= std::exp(-p.drag * dt);
            p.vy *= std::exp(-p.drag * dt);
            break;
        default:
            break;
        }
        p.x += p.vx * dt;
        p.y += p.vy * dt;
    }
}

// ---- rigs

namespace {

// Two-bone IK; the knee (or elbow) lies on the +x side of the hip–foot line (cross < 0, y down).
QPointF knee(QPointF hip, QPointF foot, double upper, double lower) {
    const double dx = foot.x() - hip.x(), dy = foot.y() - hip.y();
    const double reach = upper + lower - 1e-3;
    double d = std::hypot(dx, dy);
    d = std::min(std::max({d, std::abs(upper - lower) + 1e-3, 1e-3}), reach);
    const double base = std::atan2(dy, dx);
    const double cosine = (upper * upper + d * d - lower * lower) / (2 * upper * d);
    const double alpha = std::acos(clamp(cosine, -1, 1));
    QPointF best(hip.x(), hip.y() + upper);
    double sign = 1;
    for (int i = 0; i < 2; ++i) {
        const double angle = base + sign * alpha;
        const QPointF k(hip.x() + upper * std::cos(angle), hip.y() + upper * std::sin(angle));
        if (dx * (k.y() - hip.y()) - dy * (k.x() - hip.x()) < 0) best = k;
        sign = -1;
    }
    return best;
}

Limb limb(std::initializer_list<QPointF> points) {
    Limb l;
    for (const auto& p : points) l.p[l.count++] = p;
    return l;
}

double lerp(double a, double b, double t) { return a + (b - a) * t; }

}  // namespace

RigPose walkPose(const WalkRig& rig, double distance, double ground, double stand) {
    double p = std::fmod(distance / rig.cycle, 1.0);
    if (p < 0) p += 1;
    const double stance = rig.stance;
    const double a = rig.cycle * stance / 2;
    const double bob = -rig.bob * std::cos(4 * kPi * (p - stance / 2)) * (1 - stand);
    const double standHip = ground - 0.6 - (rig.thigh + rig.shin) * 0.97;
    const QPointF hip(rig.hip.x(), lerp(rig.hip.y() + bob, standHip, stand));
    const QPointF shoulder = hip + rig.torso;
    const auto leg = [&](double phase, double spread) {
        QPointF foot;
        if (phase < stance) {
            foot = QPointF(hip.x() + a - 2 * a * (phase / stance), ground - 0.6);
        } else {
            const double s = (phase - stance) / (1 - stance);
            foot = QPointF(hip.x() - a + 2 * a * smooth(s), ground - 0.6 - rig.lift * std::sin(kPi * s));
        }
        const QPointF rest(hip.x() + spread, ground - 0.6);
        foot = foot + (rest - foot) * stand;
        return limb({hip, knee(hip, foot, rig.thigh, rig.shin), foot, QPointF(foot.x() + rig.foot, foot.y())});
    };
    const auto arm = [&](double phase, double rest) {
        const double swing = -rig.armSwing * std::cos(2 * kPi * phase);
        const double theta = lerp(swing, rest, stand) * kPi / 180;
        const QPointF elbow(shoulder.x() + rig.upperArm * std::sin(theta), shoulder.y() + rig.upperArm * std::cos(theta));
        const double bend = theta + rig.elbow * lerp(1, 0.5, stand) * kPi / 180;
        return limb({shoulder, elbow, QPointF(elbow.x() + rig.forearm * std::sin(bend), elbow.y() + rig.forearm * std::cos(bend))});
    };
    const double opposite = std::fmod(p + 0.5, 1.0);
    RigPose pose;
    pose.far0 = leg(opposite, -1.6);
    pose.far1 = arm(opposite, -4);
    pose.near[0] = limb({hip, shoulder});
    pose.near[1] = leg(p, 1.6);
    pose.near[2] = arm(p, 4);
    pose.nearCount = 3;
    pose.head = std::array<double, 3>{shoulder.x() + rig.headX, shoulder.y() + rig.headY, rig.headR};
    pose.hip = hip;
    return pose;
}

RigPose pedalPose(const PedalRig& rig, double distance, double lift) {
    const double phi = wrapAngle(distance / rig.crankRadius);
    const QPointF pivot = rig.crankPivot;
    const double length = rig.crankLength;
    const QPointF nearPedal(pivot.x() - length * std::sin(phi), pivot.y() + length * std::cos(phi));
    const QPointF farPedal(pivot.x() + length * std::sin(phi), pivot.y() - length * std::cos(phi));
    const double bob = rig.bob * std::cos(2 * phi) - lift;
    const QPointF hip(rig.hip.x() + lift * 0.6, rig.hip.y() + bob);
    const double half = rig.pedal / 2;
    RigPose pose;
    pose.far0 = limb({hip, knee(hip, farPedal, rig.thigh, rig.shin), farPedal});
    pose.far1 = limb({QPointF(farPedal.x() - half, farPedal.y()), QPointF(farPedal.x() + half, farPedal.y())});
    pose.near[0] = limb({hip, knee(hip, nearPedal, rig.thigh, rig.shin), nearPedal});
    pose.nearCount = 1;
    pose.nearPedal = limb({QPointF(nearPedal.x() - half, nearPedal.y()), QPointF(nearPedal.x() + half, nearPedal.y())});
    pose.riderOffset = QPointF(lift * 0.6, bob);
    pose.hip = hip;
    return pose;
}

// ---- start-up (DESIGN 4c, Start-up table)

StartupEffects startupEffects(Kind kind, double tau) {
    StartupEffects fx;
    if (tau < 0) {
        fx.flame = fx.contrail = fx.shock = fx.hover = 0;
        return fx;
    }
    const auto b = [tau](double s, double p, double e) { return bump(tau, s, p, e); };
    switch (kind) {
    case Kind::Walk: break;
    case Kind::Bicycle:
    case Kind::Ebike:
        fx.hipLift = 1.3 * b(0, 0.18, 0.6);
        fx.lurch = 1.6 * b(0, 0.22, 0.9);
        if (kind == Kind::Ebike) fx.lights = b(0, 0.08, 0.5);
        break;
    case Kind::Motorcycle:
        fx.pitch = -3 * b(0.12, 0.34, 0.8);
        fx.shake = 0.35 * (1 - smooth(tau / 0.3));
        fx.lurch = 1.4 * b(0.1, 0.35, 0.9);
        break;
    case Kind::Car:
    case Kind::SportsCar:
        fx.lights = std::max(b(0, 0.04, 0.12), b(0.16, 0.2, 0.3));
        fx.pitch = -1.3 * b(0.05, 0.25, 0.7);
        fx.lurch = 1.2 * b(0.08, 0.3, 0.9);
        break;
    case Kind::RaceCar:
        fx.pitch = -1.1 * b(0, 0.2, 0.6);
        fx.lurch = 2.0 * b(0.05, 0.28, 0.9);
        break;
    case Kind::BulletTrain:
        fx.lights = b(0, 0.15, 0.7);
        fx.lurch = 1.0 * b(0.1, 0.45, 1.1);
        break;
    case Kind::Maglev:
        fx.hover = smooth((tau - 0.2) / 0.5);
        fx.lights = b(0, 0.15, 0.7);
        fx.glow = b(0, 0.3, 0.8);
        break;
    case Kind::Jetliner:
        fx.contrail = smooth(tau / 1.2);
        fx.lift = -2 * b(0.1, 0.5, 1.2);
        fx.lights = b(0, 0.2, 0.8);
        break;
    case Kind::FighterJet:
    case Kind::Hypersonic:
        fx.flame = tau < 0.35 ? spring(tau, 0, 1, 9, 0.32, 0.55) : 1;
        fx.glow = b(0, 0.06, 0.35);
        fx.shock = smooth((tau - 0.1) / 0.4);
        fx.lurch = 1.4 * b(0, 0.2, 0.8);
        break;
    case Kind::Rocket:
        fx.flame = tau < 0.4 ? spring(tau, 0, 1, 8, 0.36, 0.5) : 1;
        fx.glow = b(0, 0.06, 0.4);
        fx.shake = 0.8 * (1 - smooth(tau / 0.6));
        fx.lift = 6 * (1 - easeInCubic(tau / 0.9));
        break;
    }
    return fx;
}

// ---- stage

Stage::Stage(const Library& lib, std::uint32_t seed) : library(lib), random_(seed) {
    current = Actor{lib.fallback, -1e9, Transition::Stay, 0, 0};
}

void Stage::prime(const Input& input, std::optional<int> parkedVehicle) {
    lastTime_ = input.time;
    time = input.time;
    apply(input);
    title = input.title;
    outgoing.reset();
    startupVehicle.reset();
    flash.reset();
    particles.clear();
    lines.clear();
    parked = !input.title;
    current = Actor{input.title.value_or(parkedVehicle.value_or(library.fallback)), -1e9, Transition::Stay, 0, 0};
    computeBand(input);
    ground.snap(groundTarget());
    wake.snap(parked ? 0 : 1);
    stand = parked ? 1 : 0;
    distance = vehicle().rest;
}

void Stage::update(const Input& input) {
    const double previous = lastTime_.value_or(input.time);
    const double dt = clamp(input.time - previous, 0, 0.1);
    lastTime_ = input.time;
    time = input.time;
    apply(input);
    if (input.title != title) {
        double at = clamp(input.titleChangedAt, previous - 0.1, input.time);
        if (!std::isfinite(at)) at = input.time;
        change(title, input.title, at);
        title = input.title;
    }
    computeBand(input);
    const double target = groundTarget();
    if (animated()) {
        ground.step(target, dt);
        distance += ground.value * dt;
    } else {
        ground.snap(target);
        distance = vehicle().rest;
    }
    if (level == Level::Off) wake.snap(parked ? 0 : 1);
    else wake.step(parked ? 0 : 1, dt);
    const double standTarget = parked && ground.value < 6 ? 1.0 : 0.0;
    stand = animated() ? stand + (standTarget - stand) * (1 - std::exp(-dt / 0.18)) : standTarget;
    suspensionPhase = std::fmod(suspensionPhase + 2 * kPi * (1 + 0.5 * band) * dt, 2 * kPi * 1000);
    if (outgoing && time - outgoing->start > 0.8) outgoing.reset();
    if (startupVehicle && time - startupStart > 1.4) startupVehicle.reset();
    if (flash && time - flash->first > 0.5) flash.reset();
    if (animated()) {
        particles.step(dt, ground.value);
        const auto& v = vehicle();
        const int cap = parked ? 0 : SpeedLines::cap(level, heat, ground.value, v.lo);
        lines.step(dt, cap, heat, ground.value, v.x0, v.y0, v.x1, v.y1, v.heading, random_);
        emitContinuous(dt);
        fireStartupEvents();
    } else {
        particles.clear();
        lines.clear();
    }
}

void Stage::apply(const Input& input) {
    level = input.level;
    heat = clamp(input.heat, 0, 1);
    overdrive = clamp(input.overdrive, 0, 1);
    tier = input.tier;
    sweepEpoch = input.sweepEpoch;
    smoothedValue = heat < 0.999 ? input.redline * std::pow(heat, 1 / 0.6) : input.redline * (1 + overdrive);
}

void Stage::computeBand(const Input& input) {
    if (!title) {
        band = coast = 0;
        return;
    }
    const double low = std::max(input.threshold, 1e-9);
    if (input.nextThreshold && *input.nextThreshold > low) {
        band = clamp(std::log(std::max(smoothedValue, 1e-9) / low) / std::log(*input.nextThreshold / low), 0, 1);
    } else {
        band = overdrive;
    }
    coast = clamp(smoothedValue / low, 0, 1);
}

// G* = lerp(lo, hi, b) √c, 0 when parked.
double Stage::groundTarget() const {
    if (parked) return 0;
    const auto& v = vehicle();
    return (v.lo + (v.hi - v.lo) * band) * std::sqrt(coast);
}

void Stage::change(std::optional<int> from, std::optional<int> to, double start) {
    if (!to) {
        parked = true;
        return;
    }
    const bool off = level == Level::Off;
    if (!from) {
        parked = false;
        const bool same = *to == current.vehicle;
        if (!same) outgoing = Actor{current.vehicle, start, off ? Transition::Cut : Transition::Wake, 0, 0};
        current = Actor{*to, start, same || off ? Transition::Stay : Transition::Wake, 0, 0};
        if (!off) {
            startupVehicle = *to;
            startupStart = start + (same ? 0.15 : 0.3);
            startupFired_ = 0;
        }
        return;
    }
    parked = false;
    const Transition transition = off ? Transition::Cut : *to > *from ? Transition::Upgrade : Transition::Downgrade;
    const QPointF base = placement(current, false).offset;
    const double along = dot(base, vehicle().headingVector());
    outgoing = Actor{current.vehicle, start, transition, 0, along};
    current = Actor{*to, start, transition, std::abs(*to - *from), 0};
    startupVehicle.reset();
    if (transition == Transition::Upgrade) {
        flash = std::make_pair(start + 0.32, std::min(1.6, 1 + 0.2 * (std::abs(*to - *from) - 1)));
        for (int i = 0; i < 4; ++i) spawnBurstLine();
    }
}

void Stage::spawnBurstLine() {
    if (!animated()) return;
    const auto& v = vehicle();
    lines.spawn(true, heat, ground.value, v.x0, v.y0, v.x1, v.y1, v.heading, random_);
}

Placement Stage::placement(const Actor& actor, bool leaving) const {
    const double tau = time - actor.start;
    const QPointF direction = library.vehicles[static_cast<std::size_t>(actor.vehicle)].headingVector();
    const auto along = [&](double d, double alpha, double ghost) { return Placement{direction * d, clamp(alpha, 0, 1), ghost, direction}; };
    if (leaving) {
        switch (actor.transition) {
        case Transition::Upgrade: {
            const double u = tau / 0.34;
            return along(actor.base + 90 * easeInCubic(u), 1 - smooth((tau - 0.12) / 0.18), u < 1 ? 90 * 3 * u * u / 0.34 : 0);
        }
        case Transition::Downgrade: return along(actor.base - 46 * easeInOutSine(tau / 0.55), 1 - smooth(tau / 0.45), 0);
        case Transition::Cut: return along(0, 0, 0);
        case Transition::Wake: return along(0, 1 - smooth(tau / 0.2), 0);
        default: return along(0, 1 - smooth(tau / 0.3), 0);
        }
    }
    switch (actor.transition) {
    case Transition::Upgrade: {
        const double s = tau - 0.12;
        if (s <= 0) return along(-62, 0, 0);
        return along(spring(s, -62, 0, 140, 0.5, 0.8), smooth(s / 0.14), std::max(0.0, springVelocity(s, -62, 0, 140, 0.5, 0.8)));
    }
    case Transition::Downgrade: {
        const double s = tau - 0.1;
        return along(8 * (1 - easeOutCubic(s / 0.55)), smooth(s / 0.35), 0);
    }
    case Transition::Fade: return along(0, smooth(tau / 0.3), 0);
    case Transition::Wake: return along(0, smooth((tau - 0.12) / 0.28), 0);
    default: return along(0, 1, 0);
    }
}

std::optional<double> Stage::startupTime(int vehicleIndex) const {
    if (startupVehicle != vehicleIndex) return std::nullopt;
    return time - startupStart;
}

StartupEffects Stage::startupEffectsFor(int vehicleIndex) const {
    const auto tau = startupTime(vehicleIndex);
    if (!tau) return {};
    return startupEffects(library.vehicles[static_cast<std::size_t>(vehicleIndex)].kind, *tau);
}

void Stage::fireStartupEvents() {
    if (!startupVehicle) return;
    const auto tau = startupTime(*startupVehicle);
    if (!tau || *tau < 0) return;
    const auto& v = library.vehicles[static_cast<std::size_t>(*startupVehicle)];
    const int cap = level >= Level::Standard ? 1 << 20 : 6;
    const auto once = [&](int bit, double at, auto&& body) {
        const std::uint32_t mask = 1u << bit;
        if (*tau < at || (startupFired_ & mask)) return;
        startupFired_ |= mask;
        body();
    };
    using K = Particle::Kind;
    if (const auto* dust = v.emitter(EmitterKind::Dust)) once(0, 0.05, [&] { particles.burst(K::Puff, dust->at, dust->dir, dust->count, cap, random_); });
    switch (v.kind) {
    case Kind::Ebike:
        if (const auto* arc = v.emitter(EmitterKind::Arc)) once(1, 0.02, [&] { particles.burst(K::Arc, arc->at, arc->dir, arc->count, cap, random_); });
        break;
    case Kind::Motorcycle:
        if (const auto* ex = v.emitter(EmitterKind::Exhaust)) {
            once(2, 0, [&] { particles.burst(K::Puff, ex->at, ex->dir, ex->count, cap, random_); });
            once(3, 0.18, [&] { particles.burst(K::Puff, ex->at, ex->dir, ex->count, cap, random_); });
        }
        break;
    case Kind::Car:
        if (const auto* ex = v.emitter(EmitterKind::Exhaust)) once(2, 0.1, [&] { particles.burst(K::Puff, ex->at, ex->dir, ex->count, cap, random_); });
        break;
    case Kind::SportsCar:
        if (const auto* ex = v.emitter(EmitterKind::Exhaust)) once(2, 0.12, [&] { particles.burst(K::Pop, ex->at, ex->dir, 1, cap, random_); });
        break;
    case Kind::RaceCar:
        for (const auto& part : v.parts) {
            if (part.id == QLatin1String("rearTire") && part.isCircle && v.groundY) {
                once(2, 0.02, [&] { particles.burst(K::Puff, QPointF(part.cx, *v.groundY), QPointF(-1, -0.35), 5, cap, random_); });
            }
        }
        break;
    case Kind::Rocket:
        if (const auto* plume = v.emitter(EmitterKind::Plume)) once(2, 0, [&] { particles.burst(K::Ring, plume->at, plume->dir, 6, cap, random_); });
        break;
    default:
        break;
    }
    for (int line = 0; line < 3; ++line) once(8 + line, 0.1 + 0.07 * line, [&] { spawnBurstLine(); });
}

void Stage::emitContinuous(double dt) {
    if (level < Level::Standard || parked) return;
    const double rate = level == Level::Max ? 1.6 : 1;
    const auto& v = vehicle();
    switch (v.kind) {
    case Kind::Rocket:
        if (const auto* s = v.emitter(EmitterKind::Sparks)) particles.stream((26 + 60 * overdrive) * rate, dt, s->at, s->dir, 70, 0.55, 0, 0.32, random_);
        break;
    case Kind::RaceCar:
        if (band > 0.55) {
            if (const auto* s = v.emitter(EmitterKind::Sparks)) particles.stream(30 * (band - 0.55) / 0.45 * rate, dt, s->at, s->dir, 120, 0.5, 260, 0.28, random_);
        }
        break;
    case Kind::FighterJet:
    case Kind::Hypersonic:
        if (tier >= 3) {
            if (const auto* a = v.emitter(EmitterKind::Afterburner)) particles.stream((6 + 14 * band) * rate, dt, a->at, a->dir, 60, 0.3, 0, 0.25, random_);
        }
        break;
    default:
        break;
    }
}

int Stage::demand() const {
    const double wakeTarget = parked ? 0.0 : 1.0;
    const bool waking = std::abs(wake.value - wakeTarget) > 0.002 || std::abs(wake.velocity) > 0.01;
    if (level == Level::Off) return 0;
    const bool moving = !parked || ground.value > 0.05 || waking || outgoing || startupVehicle || flash ||
                        std::abs(stand - (parked && ground.value < 6 ? 1 : 0)) > 0.002 || !particles.empty() || !lines.empty();
    if (moving) return 60;
    double phase = std::fmod(time - sweepEpoch, kSweepPeriod);
    if (phase < 0) phase += kSweepPeriod;
    return phase > kSweepPeriod - 0.4 || phase < kSweepDuration + 0.05 ? 30 : 10;
}

}  // namespace hero
