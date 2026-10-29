#include "orb/scene.hpp"

#include <algorithm>
#include <cmath>

namespace orb {

namespace {

constexpr double kPi = 3.14159265358979323846;

std::array<float, 4> vec(const Rgba& c) { return {c.r, c.g, c.b, std::clamp(c.a, 0.0f, 1.0f)}; }

}  // namespace

Instance Instance::make(Shape shape, QPointF center, double rotation, float sx, float sy, float corner, double glow,
                        Rgba color, Rgba tail, double core, double glowAlpha, float p0, float p1) {
    Instance i;
    i.a = {static_cast<float>(center.x()), static_cast<float>(center.y()), static_cast<float>(rotation),
           static_cast<float>(shape)};
    i.b = {sx, sy, corner, static_cast<float>(glow)};
    i.color = vec(color);
    i.color2 = vec(tail);
    i.extra = {static_cast<float>(core), static_cast<float>(glowAlpha), p0, p1};
    return i;
}

Instance Instance::exponentialGlow() const {
    Instance copy = *this;
    copy.b[3] = -std::abs(copy.b[3]);
    return copy;
}

Instance Instance::linearGradient(Rgba end, double angleDegrees, double halfLength) const {
    Instance copy = *this;
    copy.color2 = vec(end);
    copy.extra2 = {static_cast<float>(Linear), static_cast<float>(angleDegrees * kPi / 180), static_cast<float>(halfLength), 1};
    return copy;
}

Instance Instance::radialGradient(Rgba end, QPointF offset, double radius) const {
    Instance copy = *this;
    copy.color2 = vec(end);
    copy.extra2 = {static_cast<float>(RadialFill), static_cast<float>(offset.x()), static_cast<float>(offset.y()),
                   static_cast<float>(radius)};
    return copy;
}

Instance Instance::roundedRect(QPointF center, double halfW, double halfH, double rotation, double corner, Rgba color,
                               double core, double glow, double glowAlpha) {
    return make(RoundedRect, center, rotation, static_cast<float>(halfW), static_cast<float>(halfH),
                static_cast<float>(corner), glow, color, color, core, glowAlpha);
}

Instance Instance::circle(QPointF center, double radius, Rgba color, double core, double glow, double glowAlpha) {
    return make(Circle, center, 0, static_cast<float>(radius), 0, 0, glow, color, color, core, glowAlpha);
}

Instance Instance::softDisc(QPointF center, double inner, double outer, Rgba color) {
    return make(Circle, center, 0, static_cast<float>(inner), 0, static_cast<float>(std::max(outer, inner + 0.01)), 0,
                color, color, 1, 0);
}

Instance Instance::ring(QPointF center, double radius, double width, Rgba color, double core, double glow,
                        double glowAlpha) {
    return make(Ring, center, 0, static_cast<float>(radius), static_cast<float>(width), 0, glow, color, color, core,
                glowAlpha);
}

Instance Instance::capsule(QPointF tail, QPointF head, double width, Rgba color, Rgba tailColor, double core,
                           double glow, double glowAlpha) {
    const double dx = head.x() - tail.x(), dy = head.y() - tail.y();
    const double length = std::max(0.001, std::sqrt(dx * dx + dy * dy));
    const QPointF center((head.x() + tail.x()) / 2, (head.y() + tail.y()) / 2);
    return make(Capsule, center, std::atan2(dy, dx), static_cast<float>(length), static_cast<float>(width), 0, glow,
                color, tailColor, core, glowAlpha);
}

Instance Instance::radial(QPointF center, double inner, double outer, Rgba color, double alpha) {
    return make(Radial, center, 0, 0, 0, 0, 0, color, color, alpha, 0, static_cast<float>(inner),
                static_cast<float>(outer));
}

Instance Instance::arc(QPointF center, double radius, double width, double start, double sweep, Rgba color,
                       Rgba tailColor, double glow, double glowAlpha) {
    return make(Arc, center, 0, static_cast<float>(radius), static_cast<float>(width), 0, glow, color, tailColor, 1,
                glowAlpha, static_cast<float>(start * kPi / 180), static_cast<float>(sweep * kPi / 180));
}

Instance Instance::bandArc(QPointF center, double radius, double width, double start, double sweep, Rgba color,
                           Rgba startColor, double glow, double glowAlpha) {
    Instance i = make(BandArc, center, 0, static_cast<float>(radius), static_cast<float>(width), 0, glow, color,
                      startColor, 1, glowAlpha, static_cast<float>(start * kPi / 180),
                      static_cast<float>(std::clamp(sweep, 0.0001, 360.0) * kPi / 180));
    i.extra2 = {static_cast<float>(Solid), 0, 0, 1};
    return i;
}

Instance Instance::flare(QPointF center, double rotation, double tangential, double radial, double radialWidth,
                         double tangentialWidth, Rgba color) {
    return make(Flare, center, rotation, static_cast<float>(tangential), static_cast<float>(radial), 0, 0, color, color,
                0, 0, static_cast<float>(radialWidth), static_cast<float>(tangentialWidth));
}

void Scene::reset() {
    instances_.clear();
    batches_.clear();
    texts_.clear();
    hoverScale = 1;
}

void Scene::add(const Instance& instance, Blend blend) {
    if (!batches_.empty()) {
        Batch& last = batches_.back();
        if (last.kind == Batch::Kind::Shapes && last.blend == blend &&
            last.start + last.count == static_cast<int>(instances_.size())) {
            ++last.count;
            instances_.push_back(instance);
            return;
        }
    }
    batches_.push_back({Batch::Kind::Shapes, blend, 0, static_cast<int>(instances_.size()), 1});
    instances_.push_back(instance);
}

void Scene::addLedField(int mode, Blend blend) {
    batches_.push_back({Batch::Kind::LedField, blend, mode, static_cast<int>(instances_.size()), 0});
}

}  // namespace orb
