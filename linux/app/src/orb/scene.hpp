#pragma once

#include <QPointF>
#include <QString>
#include <array>
#include <vector>

#include "orb/color.hpp"
#include "orb/led_math.hpp"

namespace orb {

// One instanced quad of the SDF pipeline (layout of the macOS OrbInstance; see shaders.cpp for the meaning per shape).
struct Instance {
    enum Shape { RoundedRect = 0, Circle = 1, Ring = 2, Capsule = 3, Radial = 4, Arc = 5, BandArc = 6, Flare = 10 };
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
};

// The draw list of one frame, in order: body, then batches of instances and the led fields, then texts.
class Scene {
public:
    struct Batch {
        enum class Kind { Shapes, LedField } kind = Kind::Shapes;
        Blend blend = Blend::Over;
        int mode = 0;
        int start = 0;
        int count = 0;
    };

    // Per-frame uniforms of the led fields (LedFieldParams).
    struct LedField {
        std::array<float, 4> frame{}, heat{}, phases{}, flags{}, corona{}, text{};
        std::array<std::array<float, 4>, 4> waves{{{-1, 0, 0, 0}, {-1, 0, 0, 0}, {-1, 0, 0, 0}, {-1, 0, 0, 0}}};
    };

    void reset();
    void add(const Instance& instance, Blend blend);
    void addLedField(int mode, Blend blend);
    void add(const TextDraw& text) { texts_.push_back(text); }

    const std::vector<Instance>& instances() const { return instances_; }
    const std::vector<Batch>& batches() const { return batches_; }
    const std::vector<TextDraw>& texts() const { return texts_; }

    LedField ledField;
    std::array<float, led::kCoronaSamples> coronaBody{};
    double hoverScale = 1;

private:
    std::vector<Instance> instances_;
    std::vector<Batch> batches_;
    std::vector<TextDraw> texts_;
};

}  // namespace orb
