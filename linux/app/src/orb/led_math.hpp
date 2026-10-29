#pragma once

#include <array>
#include <cstdint>
#include <vector>

// The pure numbers of the `led` effects (DESIGN 2.1; prototype spec/prototypes/led-effects.html), ported from the macOS
// LedMath. Radii and lengths are fractions of the diameter S unless a function takes the diameter.
namespace orb::led {

inline constexpr double kMaxStep = 1.0 / 15;
inline constexpr double kOuterReach = 0.74;
inline constexpr int kSegments = 36;
inline constexpr int kWarpBins = 96;
inline constexpr int kCoronaSamples = 128;
inline constexpr double kPulseDuration = 0.85;
inline constexpr double kPeakRingDuration = 1.4;
inline constexpr double kPeakRayDuration = 1.3;

struct Phases {
    double silk = 0, light = 0, flow = 0, warp = 0, corona = 0, comet = 0, clock = 0;
};

double smoothstep(double e0, double e1, double x);
double easeOut(double t);
double fract(double x);
double clampedStep(double dt);

Phases rates(double h, double o);
void advance(Phases& phases, double h, double o, double dt, double silkRate, bool comet);

inline double activity(double h) { return smoothstep(0, 0.08, h); }
double silkBrightness(double h, double o, double lvl);
double ripple(double o);
double flow(int segment, double phase, double h);
double flareAmount(double h, double o, double lvl);
inline double flareTangential(double o) { return 0.05 + 0.16 * o; }
inline double flareRadial(double o) { return 0.035 + 0.09 * o; }
inline double cometTail(double h) { return 40 + 60 * h; }
int cometCount(double h, int maxComets);
double coronaAmount(double h, double o, double lvl);
double warpAmount(double h, double lvl);
double warpSeed(int bin);
double shockRadius(double age, double reach);
double wakeRadius(double age, double reach);
double peakRingRadius(double age, double reach);
double peakRayReach(double age, double reach);
double peakFlash(double age);
inline double particleFade(double r) { return smoothstep(kOuterReach, 0.66, r); }
inline double leanDegrees(double h) { return 7.5 * h; }
inline double jitter(double o) { return 0.9 + 1.2 * o; }
inline double glowScale(double o) { return 1 + 0.8 * o; }

// 3D simplex noise (Ashima Arts / Stefan Gustavson, MIT), the same function the shaders evaluate.
double simplex(double x, double y, double z);
// The corona body 0.5 + 0.5 snoise(2.2 dir, 0.35 φcorona) at 128 angles clockwise from 3 o'clock (y up).
void coronaBody(double phase, std::array<float, kCoronaSamples>& out);

// Small, fast, seedable xorshift generator.
class Random {
public:
    explicit Random(std::uint64_t seed = 0x9E3779B97F4A7C15ULL) : state_(seed == 0 ? 1 : seed) {}
    double next() {
        state_ ^= state_ << 13;
        state_ ^= state_ >> 7;
        state_ ^= state_ << 17;
        return static_cast<double>(state_ >> 11) / static_cast<double>(1ULL << 53);
    }

private:
    std::uint64_t state_;
};

struct Particle {
    enum class Kind { Spark, Ember, Gold };
    Kind kind = Kind::Spark;
    // Points and points per second relative to the center, y down.
    double x = 0, y = 0, vx = 0, vy = 0;
    double age = 0, life = 1, size = 1, heat = 0, drag = 0, seed = 0;
    bool streak = true;
};

// Sparks, embers and the new-peak burst in a fixed pool (prototype emitSpark, emitEmber, burst, step).
class Particles {
public:
    static constexpr std::size_t kCapacity = 640;
    static constexpr int kBurstCount = 160;

    Particles();
    const std::vector<Particle>& items() const { return items_; }
    bool empty() const { return items_.empty(); }
    void clear();
    static double sparkRate(double h, double lvl, double countScale);
    static double emberRate(double o, bool redline, double lvl, double countScale);
    void step(double dt, double time, double s, double sparkRate, double emberRate, double lead, double o);
    void burst(double s, int count);

private:
    static int due(double& carry, double rate, double dt);
    void emitSpark(double s, double lead, double o);
    void emitEmber(double s);

    std::vector<Particle> items_;
    Random random_{0x5EED0005};
    double sparkCarry_ = 0, emberCarry_ = 0;
};

// The effects-level table of DESIGN 2.1 after the caps. level: 0 Off, 1 Soft, 2 Standard, 3 Max.
struct Effects {
    int level = 2;
    double lvl = 1;
    double silkRate = 1;
    bool silkRipples = true, silkWaves = true, flow = true, tipFlare = true, corona = true, warp = true;
    int maxComets = 2;
    bool pulses = true, particles = true, jitter = true, skew = true;
    double countScale = 1;
    // OrbEffects (DESIGN 2, Effects intensity).
    bool breathing = true, shimmer = true, flash = true, idleSweep = true, celebrate = true;
    double bloom = 1;

    static Effects make(int level, bool small);
};

}  // namespace orb::led
