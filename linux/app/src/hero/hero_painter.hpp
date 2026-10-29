#pragma once

#include <QColor>
#include <QImage>
#include <QPainterPath>
#include <QTransform>
#include <vector>

#include "hero/hero_stage.hpp"

namespace hero {

// The colors of one frame (DESIGN 4c, Color and glow).
struct Paint {
    QColor stroke, hot, core, sweep;
    bool dark = true;
};
// C follows the title label (heat on dark, a legible darker heat on light); a parked vehicle is slate (by `wake`).
Paint paintFor(double heat, double overdrive, double wake, bool dark);
QColor mixOklab(const QColor& a, const QColor& b, double t);

// One draw of the layer, in canvas units (0–100, y down).
struct Item {
    enum class Kind { Path, Radial };
    Kind kind = Kind::Path;
    QPainterPath path;
    bool stroke = true, fill = false, soft = false, additive = false;
    double halfWidth = 1, opacity = 1, glow = 0;
    QColor color, fillColor;
    bool fade = false;
    QPointF fadeFrom, fadeTo;  // opacity 1 at fadeFrom, 0 at fadeTo
    // Radial: center color at the center, mid color at 45 %, clear at the radius.
    QPointF center;
    double radius = 0;
    QColor mid;
};

// Builds and renders the vehicle layer (the macOS HeroFrameBuilder and HeroLayerEncoder): crisp strokes, their glow
// blurred and composited over them, the idle light band over the strokes, and the soft circular mask.
class Painter {
public:
    explicit Painter(const Library& library);
    // The layer of `side` × `side` points at `dpr`, premultiplied, the mask applied.
    QImage render(const Stage& stage, const Paint& paint, double side, double dpr);

    // Shared with the badge.
    static void blur(QImage& image, double sigma);
    static void applyMask(QImage& image, double solid, double clear);
    static void drawItems(QImage& crisp, QImage& glow, const std::vector<Item>& items, double unit);
    static void composite(QImage& crisp, QImage& glow, double sigmaPixels, bool dark);

private:
    struct Frame {
        const Stage* stage;
        Paint paint;
        double time, heat, overdrive, band, wake, ground, distance;
        Level level;
        bool animated;
        double presence, width, glowOpacity, sigma;
        QTransform base;
        StartupEffects effects;
    };

    Frame makeFrame(const Stage& stage, const Paint& paint) const;
    void addPath(const std::vector<Vertex>& vertices, const QTransform& t, bool stroke, bool fill, double halfWidth, double opacity,
              const QColor& color, const QColor& fillColor, double glow);
    void emitLimb(const Limb& limb, const QTransform& t, double halfWidth, double opacity, const QColor& color, double glow);
    void segment(QPointF from, QPointF to, double halfWidth, double opacity, const QColor& color, bool fading, double glow = 0);
    void circle(QPointF c, double radius, double halfWidth, double opacity, const QColor& color, double glow);
    void arc(QPointF center, double radius, double from, double to, const QTransform& t, double halfWidth, double opacity, const QColor& color,
             bool soft = false);
    void drawScenery(const Frame& f);
    void drawSceneryKind(Scenery kind, std::optional<double> ground, const Vehicle& vehicle, double alpha, const Frame& f);
    void drawSpeedLines(const Frame& f);
    void drawActor(const Actor& actor, bool outgoing, const Frame& f);
    void drawVehicle(int index, const Placement& placement, const StartupEffects& fx, bool ghost, const Frame& f);
    void drawPart(const Part& part, const Vehicle& vehicle, const Frame& f, const QTransform& sprung, const QTransform& unsprung,
                  const RigPose* pose, const StartupEffects& fx, double alpha, double hover, double glowBase, bool ghost);
    void drawFlame(const Part& part, const Frame& f, const QTransform& t, const StartupEffects& fx, double alpha, double glowBase);
    void drawTrailMarks(const Vehicle& v, const Frame& f, double ox, double oy, double floatY, double alpha);
    void drawContrails(const Vehicle& v, const Frame& f, double ox, double oy, double alpha, double contrail);
    void drawShockDiamonds(const Vehicle& v, const Frame& f, const QTransform& sprung, double flame, double alpha);
    void drawFieldPulse(const Vehicle& v, const Frame& f, const QTransform& sprung, double alpha);
    void drawParticles(const Frame& f);
    void drawFlash(const Frame& f);

    const Library& library_;
    std::vector<QPointF> stars_;
    std::vector<double> starDepth_;
    std::vector<Item> items_;
};

}  // namespace hero
