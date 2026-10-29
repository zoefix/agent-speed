#pragma once

#include <QPointF>
#include <array>
#include <cstdint>
#include <optional>

#include "hero/hero_library.hpp"

// The hero vehicle's state (DESIGN 4c): which vehicle is on stage, the shared ground distance, transitions, start-up,
// parking, the speed lines and the particle pool. Ported from the macOS HeroStage; deterministic for a given input.
namespace hero {

double clamp(double x, double lo, double hi);
double smooth(double t);
double easeInCubic(double t);
double easeOutCubic(double t);
double easeInOutSine(double t);
double bump(double tau, double start, double peak, double end);
double spring(double t, double from, double to, double velocity, double response, double damping);
double springVelocity(double t, double from, double to, double velocity, double response, double damping);
double wrapAngle(double angle);

enum class Level { Off, Soft, Standard, Max };

// A critically damped spring evaluated exactly per step: ω = 6.2 / settle.
struct CriticalSpring {
    double omega = 6.2, value = 0, velocity = 0;
    explicit CriticalSpring(double settle = 1, double start = 0) : omega(6.2 / settle), value(start) {}
    double step(double target, double dt);
    void snap(double target) { value = target, velocity = 0; }
};

// xorshift32.
struct Random {
    std::uint32_t state = 1;
    explicit Random(std::uint32_t seed = 7) : state(seed == 0 ? 1 : seed) {}
    double next();
};

struct Input {
    double time = 0;
    std::optional<int> title;  // vehicle index of the displayed title; nullopt while idle
    double titleChangedAt = -1e9;
    double threshold = 0.5;
    std::optional<double> nextThreshold;
    double redline = 2000;
    double heat = 0, overdrive = 0;
    int tier = 0;
    Level level = Level::Standard;
    double sweepEpoch = 0;
};

enum class Transition { Stay, Upgrade, Downgrade, Fade, Cut, Wake };

struct Actor {
    int vehicle = 0;
    double start = -1e9;
    Transition transition = Transition::Stay;
    int levels = 0;
    double base = 0;
};

struct Placement {
    QPointF offset;
    double alpha = 1;
    double ghostSpeed = 0;
    QPointF direction{1, 0};
};

struct StartupEffects {
    double lurch = 0, pitch = 0, lift = 0, hipLift = 0, lights = 0;
    double flame = 1, contrail = 1, shock = 1, hover = 1, shake = 0, glow = 0;
};

struct SpeedLine {
    bool alive = false;
    double age = 0, x = 0, y = 0, dx = -1, dy = 0, length = 0, speed = 0, opacity = 0;
};

class SpeedLines {
public:
    static constexpr int kCapacity = 16;
    std::array<SpeedLine, kCapacity> pool{};
    bool empty() const;
    void clear();
    // Live count N = round((2 + 8 h) clamp(G / lo, 0, 1)), capped at 5 / 9 / 14.
    static int cap(Level level, double heat, double ground, double low);
    // y in [yLo, yHi]: the badge keeps its streaks off the title (bands), the vehicle uses the whole disc.
    void spawn(bool burst, double heat, double ground, double x0, double y0, double x1, double y1, std::optional<double> heading, Random& random,
               const std::array<double, 4>* bands = nullptr);
    void step(double dt, int cap, double heat, double ground, double x0, double y0, double x1, double y1, std::optional<double> heading,
              Random& random, const std::array<double, 4>* bands = nullptr);

private:
    double timer_ = 0;
};

struct Particle {
    enum class Kind { Puff, Ring, Arc, Pop, Spark, Ember };
    bool alive = false;
    Kind kind = Kind::Puff;
    double age = 0, life = 1, delay = 0, x = 0, y = 0, vx = 0, vy = 0, size = 1, grow = 0, angle = 0, seed = 0, gravity = 0, drag = 0;
    // Life progress 0…1 after the delay; < 0 while waiting.
    double progress() const;
};

class Particles {
public:
    static constexpr int kCapacity = 64;
    std::array<Particle, kCapacity> pool{};
    bool empty() const;
    void clear();
    void burst(Particle::Kind kind, QPointF at, QPointF dir, int count, int cap, Random& random);
    void stream(double rate, double dt, QPointF at, QPointF dir, double speed, double spread, double gravity, double life, Random& random);
    Particle* free();
    void step(double dt, double ground);

private:
    double accumulator_ = 0;
};

// A limb as a short polyline (at most four points).
struct Limb {
    int count = 0;
    QPointF p[4];
};

struct RigPose {
    Limb far0, far1, near[3];
    int nearCount = 0;
    std::optional<Limb> nearPedal;
    std::optional<std::array<double, 3>> head;  // x, y, r
    QPointF riderOffset;
    QPointF hip;
};

RigPose walkPose(const WalkRig& rig, double distance, double ground, double stand);
RigPose pedalPose(const PedalRig& rig, double distance, double lift);
StartupEffects startupEffects(Kind kind, double tau);

class Stage {
public:
    explicit Stage(const Library& library, std::uint32_t seed = 7);
    static constexpr double kSweepPeriod = 6, kSweepDuration = 0.9;

    const Library& library;
    double time = 0;
    std::optional<int> title;
    Actor current;
    std::optional<Actor> outgoing;
    bool parked = true;
    CriticalSpring ground{0.9};
    CriticalSpring wake{0.5};
    double distance = 0;
    double stand = 1;
    double band = 0, coast = 0, smoothedValue = 0, heat = 0, overdrive = 0;
    int tier = 0;
    Level level = Level::Standard;
    double sweepEpoch = 0;
    std::optional<int> startupVehicle;
    double startupStart = 0;
    std::optional<std::pair<double, double>> flash;  // start, strength
    double suspensionPhase = 0;
    Particles particles;
    SpeedLines lines;

    const Vehicle& vehicle() const { return library.vehicles[static_cast<std::size_t>(current.vehicle)]; }
    bool animated() const { return level >= Level::Soft; }
    double acceleration() const { return ground.velocity; }

    void prime(const Input& input, std::optional<int> parkedVehicle);
    void update(const Input& input);
    Placement placement(const Actor& actor, bool leaving) const;
    std::optional<double> startupTime(int vehicleIndex) const;
    StartupEffects startupEffectsFor(int vehicleIndex) const;
    // 0: still, 10 or 30: parked, 60: moving.
    int demand() const;

private:
    void apply(const Input& input);
    void computeBand(const Input& input);
    double groundTarget() const;
    void change(std::optional<int> from, std::optional<int> to, double start);
    void spawnBurstLine();
    void fireStartupEvents();
    void emitContinuous(double dt);

    std::optional<double> lastTime_;
    std::uint32_t startupFired_ = 0;
    Random random_;
};

}  // namespace hero
