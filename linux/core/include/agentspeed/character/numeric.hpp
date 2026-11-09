#pragma once

#include <algorithm>
#include <cmath>
#include <cstdint>
#include <vector>

// Arithmetic that matches the Python reference player bit for bit (spec/CHARACTERS.md, Conformance).
namespace agentspeed::character::math {

constexpr double kPi = 3.14159265358979323846;

// Python's float `%`: the result takes the sign of the divisor.
inline double mod(double a, double b) {
    double r = std::fmod(a, b);
    if (r != 0 && ((b < 0) != (r < 0))) r += b;
    return r;
}

// Python's `round()`: halves go to the even neighbour.
inline double roundHalfEven(double x) {
    double r = std::round(x);
    if (std::fabs(x - std::trunc(x)) == 0.5) r = 2.0 * std::round(x / 2.0);
    return r;
}

inline double clamp(double x, double lower, double upper) { return std::min(upper, std::max(lower, x)); }

// numpy.interp over keys sorted by x: linear between keys, flat outside.
inline double interpolate(const std::vector<double>& xs, const std::vector<double>& ys, double x) {
    const std::size_t n = xs.size();
    if (n == 0) return 0;
    if (x <= xs[0]) return ys[0];
    if (x >= xs[n - 1]) return ys[n - 1];
    std::size_t lo = 0, hi = n - 1;
    while (hi - lo > 1) {
        const std::size_t mid = (lo + hi) / 2;
        if (xs[mid] <= x) lo = mid; else hi = mid;
    }
    const double slope = (ys[lo + 1] - ys[lo]) / (xs[lo + 1] - xs[lo]);
    return slope * (x - xs[lo]) + ys[lo];
}

inline double smoothstep(double x) { return x * x * (3 - 2 * x); }

}  // namespace agentspeed::character::math
