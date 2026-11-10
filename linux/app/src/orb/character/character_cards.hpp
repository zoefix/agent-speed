#pragma once

#include <QPointF>
#include <QString>
#include <algorithm>
#include <cmath>

#include "orb/renderer.hpp"
#include "orb/scene.hpp"

// What the character orb's cards share (DESIGN 2a.6, Readouts): their sizes, their text and the pen that lays a card
// out around its floating center.
namespace orb::cards {

inline constexpr double kUnitScale = 0.82;

// Sizes of the cards (CharacterCardMetrics).
struct CardMetrics {
    double t, p, padX, padY, rowGap;
    explicit CardMetrics(double d) : t(std::max(8.0, 0.027 * d)), p(1.25 * t), padX(0.8 * t), padY(0.55 * t), rowGap(0.36 * t) {}
    static double rowHeight(double size) { return 0.92 * size; }
    static double baseline(double size) { return 0.82 * size; }
    double glyphGap() const { return 0.24 * t; }
    double cellGap() const { return 0.16 * t; }  // OrbLinePlacement.gap
};

inline TextDraw textOf(const QString& text, double size, int weight) {
    TextDraw d;
    d.text = text;
    d.pixelSize = size;
    d.weight = weight;
    return d;
}

inline double advance(const QString& text, double size, int weight) {
    return text.isEmpty() ? 0 : textAdvance(textOf(text, size, weight));
}

inline double capHeight(double size, int weight) { return textCapHeight(textOf(QStringLiteral("0"), size, weight)); }

// A body of the readout laid out around `center`, scaled by `scale` and turned by `spin` (CharacterBodyPainter).
struct Pen {
    QPointF center;
    double scale, spin, alpha;
    QPointF point(double ox, double oy) const {
        const double c = std::cos(spin), s = std::sin(spin), x = ox * scale, y = oy * scale;
        return {center.x() + x * c - y * s, center.y() + x * s + y * c};
    }
    // A text centered at `ox` with its baseline at `oy` (offsets before the scale and spin).
    TextDraw text(const QString& string, double size, int weight, double ox, double oy, Rgba color) const {
        TextDraw d = textOf(string, size, weight);
        d.baselineCenter = QPointF(center.x() + ox, center.y() + oy);
        d.color = color.opacity(alpha);
        d.pivot = center;
        d.scale = scale;
        d.rotation = spin;
        return d;
    }
    // A text starting at `ox` (its left edge).
    TextDraw leading(const QString& string, double size, int weight, double ox, double oy, Rgba color) const {
        return text(string, size, weight, ox + advance(string, size, weight) / 2, oy, color);
    }
    Pen faded(double k) const { return {center, scale, spin, alpha * k}; }
};

}  // namespace orb::cards
