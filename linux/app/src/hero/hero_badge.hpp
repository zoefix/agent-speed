#pragma once

#include <QImage>
#include <QPainterPath>
#include <QString>
#include <array>
#include <optional>

#include "hero/hero_painter.hpp"

// DESIGN 4c Hero title badge: with any title theme but Vehicles the hero orb's center carries a glowing typographic
// medallion of the current title — halo, speed streaks, medallion rings, 13 rank pips, orbit motes, embers, the title
// with its glow and sheen, and the level-up flash, shockwave and sparks. Canvas units 0–100 on 0.72 S, like the vehicle.
namespace hero {

struct BadgeInput {
    double time = 0;
    int level = -1;  // 0…12, -1 idle
    double titleChangedAt = -1e9;
    QString text;    // the title (or the idle label)
    QString language;
    double heat = 0, overdrive = 0;
    int tier = 0;
    Level effects = Level::Standard;
    double sweepEpoch = 0;
    std::array<double, 13> pipHeat{};  // (T_k / R)^0.6 of each level
};

class Badge {
public:
    void update(const BadgeInput& input);
    QImage render(bool dark, double side, double dpr);
    int demand() const;

private:
    struct Layout {
        QString text;
        QPainterPath path;  // glyph outlines in canvas units, the ink box centered on (50, 50)
        QRectF ink;
    };
    struct Mote {
        double phase = 0;
    };

    Layout layoutOf(const QString& text) const;
    void drawTitle(QPainter& crisp, QPainter& glow, const Layout& layout, double scale, double dy, double alpha, bool dark,
                   const Paint& paint) const;

    BadgeInput in_;
    std::optional<double> lastTime_;
    int level_ = -1;          // the level on screen (after the last change)
    int previousLevel_ = -1;  // the level it replaced
    double changeAt_ = -1e9;
    Layout current_, previous_;
    CriticalSpring wake_{0.5};
    CriticalSpring ground_{0.9};
    CriticalSpring omega_{0.6};
    double phase_ = 0;
    double sheenPhase_ = 0;
    SpeedLines lines_;
    Particles particles_;
    Random random_{23};
    double emberCarry_ = 0;
    bool burstDone_ = true;
};

}  // namespace hero
