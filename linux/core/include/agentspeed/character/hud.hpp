#pragma once

#include <optional>
#include <string>
#include <tuple>
#include <vector>

#include "agentspeed/character/rng.hpp"
#include "agentspeed/character/springs.hpp"

namespace agentspeed::character {

// A Kepler orbit for a floating readout (orbits.py): an ellipse in a horizontal plane through the character, seen
// slightly from above; faster near her, slower far out; the ellipse breathes, precesses and bobs.
struct Orbit {
    double a, e, period, phase, omega, height, tilt = 0.16, precess = 0.012, seed = 0;

    struct Position {
        double x, y, depth;
    };

    // Solves Kepler's equation E - e sin E = mean by six Newton steps.
    static double eccentricAnomaly(double mean, double e);
    // Screen position and depth (-1 farthest behind, 1 nearest in front) at time t around (cx, cy).
    Position at(double t, double cx, double cy) const;
    // The slow tumble of the text, in radians.
    double spin(double t) const;
};

// A 2D point that trails its target on springs and bobs on its own phase.
class Follow {
public:
    Follow(double f = 1.6, double z = 0.7, double bobAmplitude = 5.0, double bobPeriod = 3.6, double phase = 0);
    void snap(double tx, double ty);
    std::pair<double, double> update(double dt, double t, double tx, double ty);
    bool placed = false;

private:
    SecondOrder x_, y_;
    double amplitude_, period_, phase_;
};

// An integer readout whose digits roll (hud.py): sampled five times a second; a changed digit slides up out of view
// as the new one slides in over 0.12 s.
class Odometer {
public:
    struct Digit {
        char current = 0;  // 0: none
        char previous = 0;
        double t = 1;  // 0 when the digit just changed, 1 when its roll is done
        bool operator==(const Digit&) const = default;
    };

    static constexpr double kHold = 0.2;

    Odometer() { digits_.reserve(12); }
    const std::string& update(double dt, double target);
    double value() const { return value_; }
    long long held() const { return held_; }
    const std::vector<Digit>& digits() const { return digits_; }
    const std::string& text() const { return text_; }

private:
    double value_ = 0;
    std::vector<Digit> digits_;
    long long held_ = 0;
    double since_ = 1;
    std::string text_ = "0";
};

// A soft round light of the floating readout (hud.py dots): an ellipse with radii (rx, ry) around (x, y). Coverage
// at normalized radius r: `softness` ≥ 0.99 is a glow, (1 - r)²; otherwise 1 - smoothstep(1 - softness, 1, r).
// Colors are straight RGB 0…1 with `alpha`; `additive` lights add up, the others blend over.
struct HudDot {
    float x, y, rx, ry;
    float r, g, b, alpha;
    float softness;
    bool additive;
};

struct Rgb {
    double r, g, b;
};

// The reference heat palette (green → yellow → orange → red), as 0…255 integer RGB like hud.py.
namespace heat_color {
constexpr Rgb kWhiteHot{255, 247, 214};
Rgb rgb(double h);
}  // namespace heat_color

// Where the new-peak pill sits this frame.
struct HudBody {
    double x, y, scale, alpha;
};

struct Ellipse {
    double cx, cy, rx, ry;
};

// The geometry the readout is laid around, in screen points (y down).
struct HudLayout {
    Ellipse ground;  // the gauge ring's ellipse at the feet
    Ellipse waist;  // the agents' orbit around the waist
    double characterHeight = 0;
    double unit = 1;  // points per reference unit
};

// The floating readout's state and lights (hud.py): the gauge ring with its comet, response pulses, agent lights
// orbiting the waist, embers at redline, the rolling speed and the new peak burst. The cards are placed by
// `HudWings`; text is the renderer's.
class CharacterHud {
public:
    explicit CharacterHud(std::uint64_t seed = 5, double unit = 1);

    // A response finished: a ripple runs out from the ring.
    void pulse(double strength = 1);
    // A new peak: the gold pill pops above the speed.
    void celebratePeak();
    void update(double dt, double time, double speed, double heat, double intensity, const std::string& title);
    // Rebuilds the lights for this frame and moves the new-peak pill toward `peakAt` (above the speed).
    void layout(double dt, const HudLayout& l, const std::vector<Rgb>& agents, double digitAdvance, double peakX,
                double peakY);

    const Odometer& odometer() const { return odometer_; }
    const std::optional<std::string>& title() const { return title_; }
    const std::optional<std::string>& previousTitle() const { return previousTitle_; }
    double titleProgress() const { return titleProgress_; }
    std::optional<double> peakAge() const { return peakAge_; }
    const std::optional<HudBody>& peak() const { return peak_; }
    const std::vector<HudDot>& back() const { return back_; }
    const std::vector<HudDot>& front() const { return front_; }
    double speedWidth() const { return speedWidth_; }
    bool hasPulses() const { return !pulses_.empty(); }
    bool hasEmbers() const { return !embers_.empty(); }

    // Multiplies the ember rate (0 turns embers off: Soft and Off effects; 1.6 at Max).
    double emberRate = 1.0;

private:
    struct Pulse {
        double t, k;
    };
    struct Ember {
        double x, y, vy, age, life, seed;
        bool front;
    };

    HudDot dot(double x, double y, double rx, double ry, Rgb c, double a, double soft, bool additive = true) const;
    void put(const HudDot& d, bool isFront) { (isFront ? front_ : back_).push_back(d); }
    void ring(const Ellipse& g, double u);
    void pulseRows(const Ellipse& g, double u);
    void satellites(const Ellipse& w, double u, const std::vector<Rgb>& agents);
    void updateEmbers(double dt, const Ellipse& g, double u);
    void emberRows(double u);

    Odometer odometer_;
    std::optional<std::string> title_, previousTitle_;
    double titleProgress_ = 1;
    std::optional<double> peakAge_;
    std::optional<HudBody> peak_;
    std::vector<HudDot> back_, front_;
    double speedWidth_ = 0;
    Rng rng_;
    Follow peakFollow_;
    SecondOrder width_{2.5, 0.8, 0};
    double comet_ = 0;
    double orbitPhase_ = 0;
    std::vector<Pulse> pulses_;
    std::vector<Ember> embers_;
    double speed_ = 0, heat_ = 0, intensity_ = 0, time_ = 0;
};

}  // namespace agentspeed::character
