#pragma once

#include <QPointF>
#include <QRectF>
#include <QString>
#include <array>
#include <vector>

#include "orb/color.hpp"
#include "orb/led_math.hpp"

namespace orb {

// One instanced quad of the SDF pipeline (layout of the macOS OrbInstance; see shaders.cpp for the meaning per shape).
struct Instance {
    enum Shape { RoundedRect = 0, Circle = 1, Ring = 2, Capsule = 3, Radial = 4, Arc = 5, BandArc = 6, Ellipse = 7, Blade = 8, Flare = 10 };
    enum Fill { Solid = 0, Linear = 1, RadialFill = 2, Palette = 3 };

    std::array<float, 4> a{}, b{}, color{}, color2{}, extra{}, extra2{0, 0, 0, 1};

    static Instance make(Shape shape, QPointF center, double rotation, float sx, float sy, float corner, double glow,
                         Rgba color, Rgba tail, double core, double glowAlpha, float p0 = 0, float p1 = 0);

    static double glowSigma(double sigma) { return sigma * 1.414; }
    Instance exponentialGlow() const;
    Instance linearGradient(Rgba end, double angleDegrees, double halfLength) const;
    Instance radialGradient(Rgba end, QPointF offset, double radius) const;

    static Instance roundedRect(QPointF center, double halfW, double halfH, double rotation, double corner, Rgba color,
                                double core = 1, double glow = 0, double glowAlpha = 0);
    static Instance circle(QPointF center, double radius, Rgba color, double core = 1, double glow = 0,
                           double glowAlpha = 0);
    static Instance softDisc(QPointF center, double inner, double outer, Rgba color);
    static Instance ring(QPointF center, double radius, double width, Rgba color, double core = 1, double glow = 0,
                         double glowAlpha = 0);
    static Instance capsule(QPointF tail, QPointF head, double width, Rgba color, Rgba tailColor, double core = 1,
                            double glow = 0, double glowAlpha = 0);
    static Instance radial(QPointF center, double inner, double outer, Rgba color, double alpha);
    // Degrees, clockwise on screen.
    static Instance arc(QPointF center, double radius, double width, double start, double sweep, Rgba color,
                        Rgba tailColor, double glow = 0, double glowAlpha = 0);
    static Instance bandArc(QPointF center, double radius, double width, double start, double sweep, Rgba color,
                            Rgba startColor, double glow = 0, double glowAlpha = 0);
    // A band arc's alpha rises from startAlpha x at the start to 1 x at the end; butt caps unless roundCaps.
    struct Band {
        double startAlpha = 1;
        bool roundCaps = false;
        double core = 1;
        double glow = 0;
        double glowAlpha = 0;
    };
    static Instance bandArc(QPointF center, double radius, double width, double start, double sweep, Rgba color,
                            Rgba startColor, const Band& band);
    // A band arc colored by the heat palette from heatFrom at the start to heatTo at the end, alpha scaling it.
    static Instance paletteArc(QPointF center, double radius, double width, double start, double sweep, double heatFrom,
                               double heatTo, double alpha, const Band& band);
    // Radii (rx, ry), rotated by `rotation` degrees.
    static Instance ellipse(QPointF center, double rx, double ry, double rotation, Rgba color, double core = 1,
                            double glow = 0, double glowAlpha = 0);
    // The needle: pivot, pointing at `angle` degrees, a tapered blade to a round tip at tipLength (half widths
    // pivotHalf -> tipHalf) and a counterweight tailLength behind the pivot (pivotHalf -> tailHalf).
    static Instance blade(QPointF pivot, double angle, double tipLength, double tailLength, double pivotHalf, double tipHalf,
                          double tailHalf, Rgba color, double core = 1, double glow = 0, double glowAlpha = 0);
    static Instance flare(QPointF center, double rotation, double tangential, double radial, double radialWidth,
                          double tangentialWidth, Rgba color);
};

enum class Blend { Over, Additive };

// A text quad: a cached texture (see text.hpp) placed in points, with an optional glow and a shear (the value's lean).
struct TextDraw {
    QString text;
    double pixelSize = 12;
    int weight = 700;
    double tracking = 0;
    QPointF baselineCenter;  // horizontal center, baseline (points)
    Rgba color;
    Rgba glowColor;
    double glowStrength = 0;
    double glowRadius = 0;  // points
    double shear = 0;       // x offset per point above the baseline
    int clip = 0;           // 1: inside clipRect, 2: above the liquid surface, 3: below it
    QRectF clipRect;        // points (the odometer's character cell)
    int layer = -1;         // drawn before this batch; -1: above everything
};

// The draw list of one frame, in order: body, then batches of instances and the led fields, then texts.
class Scene {
public:
    struct Batch {
        enum class Kind { Shapes, LedField, Liquid } kind = Kind::Shapes;
        Blend blend = Blend::Over;
        int mode = 0;
        int start = 0;
        int count = 0;
        float clip = 0, feather = 0;
        float alpha = 1;
    };

    // Per-frame uniforms of the led fields (LedFieldParams).
    struct LedField {
        std::array<float, 4> frame{}, heat{}, phases{}, flags{}, corona{}, text{};
        std::array<std::array<float, 4>, 4> waves{{{-1, 0, 0, 0}, {-1, 0, 0, 0}, {-1, 0, 0, 0}, {-1, 0, 0, 0}}};
    };

    // The `liquid` volume (OrbLiquidUniforms).
    struct LiquidField {
        std::array<float, 4> frame{}, surface{}, phases{};
        std::array<std::array<float, 4>, 4> ripples{};
        std::array<float, 4> liquid{}, deep{}, back{}, spill{}, glow{}, glowGeom{}, shade{}, current{}, currentGeom{},
            meniscus{}, meniscusGlow{}, meniscusGeom{}, gold{}, flags{};
    };

    void reset();
    void add(const Instance& instance, Blend blend);
    void addLedField(int mode, Blend blend);
    void addLiquid(int mode);
    void add(const TextDraw& text);
    // A text drawn under the shapes added after it (the needle's dial legend).
    void addUnder(const TextDraw& text);

    const std::vector<Instance>& instances() const { return instances_; }
    const std::vector<Batch>& batches() const { return batches_; }
    const std::vector<TextDraw>& texts() const { return texts_; }

    LedField ledField;
    LiquidField liquidField;
    bool hasLiquid = false;
    std::array<float, led::kCoronaSamples> coronaBody{};
    double hoverScale = 1;
    // Multiplies the alpha of everything added (a style crossfade).
    double alphaScale = 1;
    // Shapes added while set are clipped to this radius around the disc center (points, 0: none) with this soft edge.
    double clipRadius = 0, clipFeather = 0;
    // Weight of the current body over the previous one (a style crossfade).
    double bodyMix = 1;

private:
    std::vector<Instance> instances_;
    std::vector<Batch> batches_;
    std::vector<TextDraw> texts_;
};

}  // namespace orb
