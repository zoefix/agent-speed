#pragma once

#include <QColor>
#include <algorithm>
#include <cmath>
#include <cstdint>

namespace orb {

// A straight-alpha sRGB color, as the shaders take it.
struct Rgba {
    float r = 0, g = 0, b = 0, a = 1;

    static constexpr Rgba hex(std::uint32_t rgb, float alpha = 1) {
        return {static_cast<float>((rgb >> 16) & 0xFF) / 255.0f, static_cast<float>((rgb >> 8) & 0xFF) / 255.0f,
                static_cast<float>(rgb & 0xFF) / 255.0f, alpha};
    }
    static Rgba from(const QColor& c) {
        return {static_cast<float>(c.redF()), static_cast<float>(c.greenF()), static_cast<float>(c.blueF()),
                static_cast<float>(c.alphaF())};
    }
    static constexpr Rgba white(float alpha = 1) { return {1, 1, 1, alpha}; }
    static constexpr Rgba black(float alpha = 1) { return {0, 0, 0, alpha}; }

    Rgba opacity(double k) const { return {r, g, b, std::clamp(a * static_cast<float>(k), 0.0f, 1.0f)}; }
    Rgba withAlpha(double alpha) const { return {r, g, b, std::clamp(static_cast<float>(alpha), 0.0f, 1.0f)}; }
    Rgba scaled(double k) const {
        const auto f = static_cast<float>(k);
        return {std::min(1.0f, r * f), std::min(1.0f, g * f), std::min(1.0f, b * f), a};
    }
    Rgba mix(const Rgba& o, double t) const {
        const auto k = std::clamp(static_cast<float>(t), 0.0f, 1.0f);
        return {r + (o.r - r) * k, g + (o.g - g) * k, b + (o.b - b) * k, a + (o.a - a) * k};
    }
    // Mixed in linear light, close to the OKLab mixes of the other ports for these palettes.
    Rgba mixLinear(const Rgba& o, double t) const {
        const auto k = std::clamp(static_cast<float>(t), 0.0f, 1.0f);
        auto lin = [](float c) { return std::pow(c, 2.2f); };
        auto enc = [](float c) { return std::pow(std::max(c, 0.0f), 1.0f / 2.2f); };
        return {enc(lin(r) + (lin(o.r) - lin(r)) * k), enc(lin(g) + (lin(o.g) - lin(g)) * k),
                enc(lin(b) + (lin(o.b) - lin(b)) * k), a + (o.a - a) * k};
    }
    Rgba darkened(double k) const { return scaled(1.0 - k); }
    // The prototype's porcelain ink: pow(c, 1.55) deepens a color without muddying it.
    Rgba inked() const { return {std::pow(r, 1.55f), std::pow(g, 1.55f), std::pow(b, 1.55f), a}; }
};

inline constexpr Rgba kWhiteHot = Rgba::hex(0xFFF7D6);
inline constexpr Rgba kAmberHot = Rgba::hex(0xFFB020);
inline constexpr Rgba kGold = Rgba::hex(0xFACC15);

Rgba heatColor(double h);
// Darker on light surfaces for contrast (DESIGN 1).
inline Rgba heatColor(double h, bool dark) { return dark ? heatColor(h) : heatColor(h).darkened(0.18); }
inline Rgba highlight(double h, double overdrive, bool dark) {
    return dark ? heatColor(h).mixLinear(kWhiteHot, 0.7 + 0.3 * overdrive) : heatColor(h).mixLinear(kAmberHot, 0.25 * overdrive);
}

// Appearance tokens of the orb (DESIGN 2 token table).
struct Tokens {
    bool dark = true;
    Rgba bodyTop, bodyBottom;
    double bodyOpacity = 0.88;
    Rgba rimTop, outerStroke, shadow;
    double shadowOffset = 0.03, shadowBlur = 0.10;
    Rgba unlitSegment, valueText, idleValueText, secondaryText;
    double auraOpacity = 0.55;
    Rgba lens, innerShadow;

    static Tokens forDark(bool dark);
    Rgba heat(double h) const { return heatColor(h, dark); }
    Rgba leadingSegment(double h, double overdrive) const {
        return dark ? kWhiteHot.mixLinear(Rgba::white(), overdrive * 0.4)
                    : heatColor(h).mixLinear(kAmberHot, 0.2 + 0.3 * overdrive);
    }
};

}  // namespace orb
