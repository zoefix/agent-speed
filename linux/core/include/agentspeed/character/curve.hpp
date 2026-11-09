#pragma once

#include <vector>

namespace agentspeed::character {

// One key of a motion curve: a value at a time.
struct CurveKey {
    double t = 0;
    double v = 0;
    bool operator==(const CurveKey&) const = default;
};

// Keys in the reference's tuple order: by time, then by value.
std::vector<CurveKey> sortedKeys(std::vector<CurveKey> keys);

// A keyframed curve through its keys on a monotone cubic (PCHIP, Fritsch-Carlson): holds stay flat, peaks stop
// smoothly and a run in one direction never pauses at a key. Ends have zero slope; a periodic curve whose ends meet
// joins smoothly across the wrap.
class Curve {
public:
    Curve() = default;
    explicit Curve(const std::vector<CurveKey>& keys, bool periodic = false);

    double start() const { return v_[0]; }
    double operator()(double x) const;

    const std::vector<double>& times() const { return t_; }

private:
    std::vector<double> t_, v_, m_;
};

}  // namespace agentspeed::character
