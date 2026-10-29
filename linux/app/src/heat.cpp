#include "heat.hpp"

#include <algorithm>
#include <array>
#include <cmath>

namespace heat {

namespace {

struct Stop {
    double at;
    QRgb rgb;
};

constexpr std::array<Stop, 8> kStops{{{0.00, 0x7C8798}, {0.10, 0x2DD4BF}, {0.30, 0x38BDF8}, {0.48, 0x818CF8},
                                      {0.64, 0xE879F9}, {0.78, 0xFB7185}, {0.90, 0xFB923C}, {1.00, 0xFACC15}}};

double toLinear(double c) { return c <= 0.04045 ? c / 12.92 : std::pow((c + 0.055) / 1.055, 2.4); }
double toSrgb(double c) { return c <= 0.0031308 ? c * 12.92 : 1.055 * std::pow(c, 1 / 2.4) - 0.055; }

double channel(QRgb a, QRgb b, double t, int shift) {
    const double ca = toLinear(((a >> shift) & 0xFF) / 255.0);
    const double cb = toLinear(((b >> shift) & 0xFF) / 255.0);
    return std::clamp(toSrgb(ca + (cb - ca) * t), 0.0, 1.0);
}

}  // namespace

double of(double tokS, double redline) { return std::min(1.0, std::pow(std::max(0.0, tokS) / redline, 0.6)); }

QColor color(double h) {
    h = std::clamp(h, 0.0, 1.0);
    for (std::size_t i = 1; i < kStops.size(); ++i) {
        if (h <= kStops[i].at) {
            const auto& lo = kStops[i - 1];
            const auto& hi = kStops[i];
            const double t = (h - lo.at) / (hi.at - lo.at);
            return QColor::fromRgbF(static_cast<float>(channel(lo.rgb, hi.rgb, t, 16)),
                                    static_cast<float>(channel(lo.rgb, hi.rgb, t, 8)),
                                    static_cast<float>(channel(lo.rgb, hi.rgb, t, 0)));
        }
    }
    return QColor::fromRgb(kStops.back().rgb);
}

}  // namespace heat
