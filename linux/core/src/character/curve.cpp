#include "agentspeed/character/curve.hpp"

#include <algorithm>
#include <cmath>

namespace agentspeed::character {

std::vector<CurveKey> sortedKeys(std::vector<CurveKey> keys) {
    std::stable_sort(keys.begin(), keys.end(),
                     [](const CurveKey& a, const CurveKey& b) { return a.t != b.t ? a.t < b.t : a.v < b.v; });
    return keys;
}

namespace {

std::vector<double> slopes(const std::vector<double>& t, const std::vector<double>& v, bool periodic) {
    const std::size_t n = t.size();
    std::vector<double> m(n, 0.0);
    if (n < 3) return m;
    std::vector<double> h(n - 1), d(n - 1);
    for (std::size_t i = 0; i + 1 < n; ++i) {
        h[i] = t[i + 1] - t[i];
        d[i] = h[i] > 1e-9 ? (v[i + 1] - v[i]) / h[i] : 0;
    }
    auto blend = [](double h0, double h1, double d0, double d1) {
        if (d0 * d1 <= 0) return 0.0;
        const double w1 = 2 * h1 + h0;
        const double w2 = h1 + 2 * h0;
        return (w1 + w2) / (w1 / d0 + w2 / d1);
    };
    for (std::size_t i = 1; i + 1 < n; ++i) m[i] = blend(h[i - 1], h[i], d[i - 1], d[i]);
    if (periodic && std::fabs(v[0] - v[n - 1]) < 1e-9) {
        const double s = blend(h[n - 2], h[0], d[n - 2], d[0]);
        m[0] = s;
        m[n - 1] = s;
    }
    return m;
}

}  // namespace

Curve::Curve(const std::vector<CurveKey>& keys, bool periodic) {
    const auto sorted = sortedKeys(keys);
    t_.reserve(sorted.size());
    v_.reserve(sorted.size());
    for (const auto& k : sorted) {
        t_.push_back(k.t);
        v_.push_back(k.v);
    }
    m_ = slopes(t_, v_, periodic);
}

double Curve::operator()(double x) const {
    const std::size_t n = t_.size();
    if (x <= t_[0]) return v_[0];
    if (x >= t_[n - 1]) return v_[n - 1];
    std::size_t lo = 0, hi = n;
    while (lo < hi) {
        const std::size_t mid = (lo + hi) / 2;
        if (x < t_[mid]) hi = mid; else lo = mid + 1;
    }
    const std::size_t i = lo - 1;
    const double h = t_[i + 1] - t_[i];
    if (h <= 1e-9) return v_[i + 1];
    const double s = (x - t_[i]) / h;
    const double sq = (1 - s) * (1 - s);
    double y = (1 + 2 * s) * sq * v_[i];
    y += s * sq * h * m_[i];
    y += s * s * (3 - 2 * s) * v_[i + 1];
    y += s * s * (s - 1) * h * m_[i + 1];
    return y;
}

}  // namespace agentspeed::character
