#pragma once

#include <functional>
#include <optional>
#include <string>
#include <utility>
#include <vector>

#include "agentspeed/character/curve.hpp"
#include "agentspeed/character/numeric.hpp"

namespace agentspeed::character {

using KeyList = std::vector<std::pair<double, double>>;

// Named key lists in insertion order, like the reference's curve dictionaries (assigning an existing name replaces
// its keys in place).
class CurveSet {
public:
    const std::vector<std::string>& names() const { return names_; }
    const std::vector<std::vector<CurveKey>>& keys() const { return keys_; }
    const std::vector<CurveKey>* find(const std::string& name) const;
    void assign(const std::string& name, std::vector<CurveKey> keys);
    void set(const std::string& name, const KeyList& values);

private:
    std::vector<std::string> names_;
    std::vector<std::vector<CurveKey>> keys_;
};

struct MotionEvent {
    double time = 0;
    std::string name;
};

// Curves over time, bound to parameter slots. One-shot motions may carry an expression, lip-flap (`talk`), timed
// events and a sideways `travel` that stays after they end.
struct Motion {
    struct Entry {
        int slot;
        std::string name;
        Curve curve;
    };

    std::string name;
    double duration = 0;
    bool loop = false;
    double fade = 0.12;
    std::optional<std::string> expression;
    bool talk = false;
    std::vector<MotionEvent> events;
    double travel = 0;
    std::vector<Entry> entries;

    // The time a sample at `t` reads: wrapped when looping, held at the end otherwise.
    double localTime(double t) const { return loop ? math::mod(t, duration) : std::min(t, duration); }
};

// A motion before its curves are bound to parameter slots.
struct MotionSpec {
    std::string name;
    double duration = 0;
    CurveSet curves;
    bool loop = false;
    double fade = 0.12;
    std::optional<std::string> expression;
    bool talk = false;
    std::vector<MotionEvent> events;
    double travel = 0;

    MotionSpec() = default;
    MotionSpec(std::string name, double duration, CurveSet curves, bool loop = false, double fade = 0.12,
               std::optional<std::string> expression = std::nullopt, bool talk = false,
               std::vector<MotionEvent> events = {}, double travel = 0);

    // The motion with its curves bound to slots; names `slot` returns nullopt for are dropped.
    Motion bind(const std::function<std::optional<int>(const std::string&)>& slot) const;
};

// Key builders of the reference (motions/curves.py).
namespace keys {
KeyList loop(double period, const std::vector<double>& values);
// Quarter-period keys of a sine from start to end, settling on `base`.
KeyList oscillate(double start, double end, double period, double amplitude, double base = 0, double phase = 0);
// A jump arc: up from the ground at `start`, `height` at the top, back down after `air` seconds.
KeyList ballistic(double start, double air, double height, int samples = 10);
// Keys delayed by dt, holding the first value meanwhile.
std::vector<CurveKey> shift(const std::vector<CurveKey>& keys, double dt);
KeyList cat(std::initializer_list<KeyList> parts);
}  // namespace keys

}  // namespace agentspeed::character
