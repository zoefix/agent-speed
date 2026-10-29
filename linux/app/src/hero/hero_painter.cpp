#include "hero/hero_painter.hpp"

#include <QLinearGradient>
#include <QPainter>
#include <QRadialGradient>
#include <algorithm>
#include <cmath>

#include "heat.hpp"

namespace hero {

namespace {

constexpr double kPi = 3.14159265358979323846;

QColor alpha(QColor c, double a) {
    c.setAlphaF(static_cast<float>(clamp(c.alphaF() * a, 0, 1)));
    return c;
}

double linear(double c) { return c <= 0.04045 ? c / 12.92 : std::pow((c + 0.055) / 1.055, 2.4); }
double gamma(double c) {
    c = clamp(c, 0, 1);
    return c <= 0.0031308 ? 12.92 * c : 1.055 * std::pow(c, 1 / 2.4) - 0.055;
}

double luminance(const QColor& c) { return 0.2126 * linear(c.redF()) + 0.7152 * linear(c.greenF()) + 0.0722 * linear(c.blueF()); }

// Darkens a light-mode color until it reaches `minimum`:1 against the popover.
QColor legible(QColor c, double minimum) {
    const double background = luminance(QColor(0xFB, 0xFB, 0xFD));
    for (int i = 0; i < 40 && (background + 0.05) / (luminance(c) + 0.05) < minimum; ++i) c = c.darker(104);
    return c;
}

QColor strokeOf(double h, bool dark) {
    if (dark) return heat::color(h);
    return legible(heat::color(h).darker(122), 4.5);
}

}  // namespace

QColor mixOklab(const QColor& a, const QColor& b, double t) {
    t = clamp(t, 0, 1);
    if (t <= 0) return a;
    if (t >= 1) return b;
    const auto lab = [](const QColor& c) {
        const double r = linear(c.redF()), g = linear(c.greenF()), bl = linear(c.blueF());
        const double l = std::cbrt(0.4122214708 * r + 0.5363325363 * g + 0.0514459929 * bl);
        const double m = std::cbrt(0.2119034982 * r + 0.6806995451 * g + 0.1073969566 * bl);
        const double s = std::cbrt(0.0883024619 * r + 0.2817188376 * g + 0.6299787005 * bl);
        return std::array<double, 3>{0.2104542553 * l + 0.7936177850 * m - 0.0040720468 * s, 1.9779984951 * l - 2.4285922050 * m + 0.4505937099 * s,
                                     0.0259040371 * l + 0.7827717662 * m - 0.8086757660 * s};
    };
    const auto la = lab(a), lb = lab(b);
    std::array<double, 3> m{};
    for (int i = 0; i < 3; ++i) m[i] = la[i] + (lb[i] - la[i]) * t;
    const double l = m[0] + 0.3963377774 * m[1] + 0.2158037573 * m[2];
    const double mm = m[0] - 0.1055613458 * m[1] - 0.0638541728 * m[2];
    const double s = m[0] - 0.0894841775 * m[1] - 1.2914855480 * m[2];
    const double l3 = l * l * l, m3 = mm * mm * mm, s3 = s * s * s;
    QColor out = QColor::fromRgbF(static_cast<float>(gamma(4.0767416621 * l3 - 3.3077115913 * m3 + 0.2309699292 * s3)),
                                  static_cast<float>(gamma(-1.2684380046 * l3 + 2.6097574011 * m3 - 0.3413193965 * s3)),
                                  static_cast<float>(gamma(-0.0041960863 * l3 - 0.7034186147 * m3 + 1.7076147010 * s3)));
    out.setAlphaF(static_cast<float>(a.alphaF() + (b.alphaF() - a.alphaF()) * t));
    return out;
}

Paint paintFor(double h, double o, double wake, bool dark) {
    const QColor whiteHot(0xFF, 0xF7, 0xD6), amberHot(0xFF, 0xB0, 0x20);
    const QColor idle = strokeOf(0, dark), awake = strokeOf(h, dark);
    const QColor hot = dark ? mixOklab(awake, whiteHot, 0.65 + 0.35 * o) : mixOklab(awake, amberHot, 0.45);
    Paint p;
    p.dark = dark;
    p.stroke = mixOklab(idle, awake, wake);
    p.hot = mixOklab(idle, hot, wake);
    p.core = dark ? mixOklab(whiteHot, Qt::white, o) : amberHot;
    p.sweep = dark ? alpha(whiteHot, 0.75) : alpha(idle, 0.6);
    return p;
}

Painter::Painter(const Library& library) : library_(library) {
    double seed = 11;
    const auto next = [&seed] {
        seed = std::fmod(seed * 16807, 2147483647.0);
        return seed / 2147483647.0;
    };
    for (int i = 0; i < library.scenery.stars; ++i) {
        const double x = next() * 100, y = next() * 100;
        stars_.emplace_back(x, y);
        starDepth_.push_back(0.5 + next() * 0.7);
    }
}

Painter::Frame Painter::makeFrame(const Stage& stage, const Paint& paint) const {
    Frame f;
    f.stage = &stage;
    f.paint = paint;
    f.time = stage.time;
    f.heat = stage.heat;
    f.overdrive = stage.overdrive;
    f.band = stage.band;
    f.wake = clamp(stage.wake.value, 0, 1);
    f.ground = stage.ground.value;
    f.distance = stage.distance;
    f.level = stage.level;
    f.animated = stage.animated();
    const bool dark = paint.dark;
    const double breath = f.animated ? 0.86 + 0.14 * std::sin(2 * kPi * f.time / 4.8) : 1;
    const double idleOpacity = (dark ? 0.55 : 0.5) * breath;
    f.effects = stage.startupEffectsFor(stage.current.vehicle);
    double glow = dark ? 0.30 + 0.45 * stage.heat + 0.15 * stage.overdrive : 0.14 + 0.18 * stage.heat;
    if (f.level == Level::Off) glow = dark ? 0.30 : 0.12;
    glow *= f.wake * (1 + f.effects.glow);
    if (stage.flash && f.time >= stage.flash->first) glow *= 1 + 0.6 * (1 - clamp((f.time - stage.flash->first) / 0.4, 0, 1));
    double shake = 0;
    if (f.level >= Level::Standard && stage.tier >= 4 && !stage.parked) shake = (0.2 + 0.5 * stage.overdrive) * (f.level == Level::Max ? 1.4 : 1);
    if (f.animated) shake += f.effects.shake;
    if (shake > 0) {
        const double p = 2 * kPi, t = f.time;
        const double sx = shake * (0.5 * std::sin(p * 23 * t) + 0.3 * std::sin(p * 31 * t + 1) + 0.2 * std::sin(p * 41 * t + 2));
        const double sy = shake * (0.5 * std::sin(p * 19 * t + 0.5) + 0.3 * std::sin(p * 29 * t + 2) + 0.2 * std::sin(p * 37 * t + 1));
        f.base.translate(sx, sy);
    }
    f.presence = idleOpacity + (1 - idleOpacity) * f.wake;
    f.width = library_.strokeWidth;
    f.glowOpacity = glow;
    f.sigma = (1.0 + 1.4 * stage.heat + 0.6 * stage.overdrive) * (f.level == Level::Max ? 1.2 : 1);
    return f;
}

// ---- emission

void Painter::addPath(const std::vector<Vertex>& vertices, const QTransform& t, bool stroke, bool fill, double halfWidth, double opacity,
                   const QColor& color, const QColor& fillColor, double glow) {
    if (opacity <= 0.002 && glow <= 0.002 && !fill) return;
    Item item;
    for (const auto& v : vertices) {
        const QPointF p = t.map(QPointF(v.x, v.y));
        if (v.move || item.path.elementCount() == 0) item.path.moveTo(p);
        else item.path.lineTo(p);
    }
    item.stroke = stroke;
    item.fill = fill;
    item.halfWidth = halfWidth;
    item.opacity = std::min(1.0, opacity);
    item.color = color;
    item.fillColor = fillColor;
    item.glow = std::min(1.0, glow);
    items_.push_back(std::move(item));
}

void Painter::emitLimb(const Limb& limb, const QTransform& t, double halfWidth, double opacity, const QColor& color, double glow) {
    if (limb.count < 2 || (opacity <= 0.002 && glow <= 0.002)) return;
    Item item;
    item.path.moveTo(t.map(limb.p[0]));
    for (int i = 1; i < limb.count; ++i) item.path.lineTo(t.map(limb.p[i]));
    item.halfWidth = halfWidth;
    item.opacity = std::min(1.0, opacity);
    item.color = color;
    item.glow = std::min(1.0, glow);
    items_.push_back(std::move(item));
}

void Painter::segment(QPointF from, QPointF to, double halfWidth, double opacity, const QColor& color, bool fading, double glow) {
    if (opacity <= 0.002) return;
    Item item;
    item.path.moveTo(from);
    item.path.lineTo(to);
    item.halfWidth = halfWidth;
    item.opacity = std::min(1.0, opacity);
    item.color = color;
    item.glow = glow;
    item.fade = fading;
    item.fadeFrom = from;
    item.fadeTo = to;
    items_.push_back(std::move(item));
}

void Painter::circle(QPointF c, double radius, double halfWidth, double opacity, const QColor& color, double glow) {
    if (opacity <= 0.002 && glow <= 0.002) return;
    Item item;
    item.path.addEllipse(c, radius, radius);
    item.halfWidth = halfWidth;
    item.opacity = std::min(1.0, opacity);
    item.color = color;
    item.glow = std::min(1.0, glow);
    items_.push_back(std::move(item));
}

void Painter::arc(QPointF center, double radius, double from, double to, const QTransform& t, double halfWidth, double opacity, const QColor& color,
                  bool soft) {
    if (opacity <= 0.002) return;
    const int steps = std::max(6, static_cast<int>(std::ceil(std::abs(to - from) * radius / 1.2)));
    Item item;
    for (int k = 0; k <= steps; ++k) {
        const double a = from + (to - from) * k / steps;
        const QPointF p = t.map(QPointF(center.x() + radius * std::cos(a), center.y() + radius * std::sin(a)));
        if (k == 0) item.path.moveTo(p);
        else item.path.lineTo(p);
    }
    item.halfWidth = halfWidth;
    item.opacity = std::min(1.0, opacity);
    item.color = color;
    item.soft = soft;
    items_.push_back(std::move(item));
}

// ---- frame

QImage Painter::render(const Stage& stage, const Paint& paint, double side, double dpr) {
    items_.clear();
    const Frame f = makeFrame(stage, paint);
    drawScenery(f);
    if (f.animated) drawSpeedLines(f);
    if (stage.outgoing) drawActor(*stage.outgoing, true, f);
    drawActor(stage.current, false, f);
    if (f.animated) drawParticles(f);
    drawFlash(f);

    const int pixels = std::max(8, static_cast<int>(std::ceil(side * dpr)));
    QImage crisp(pixels, pixels, QImage::Format_ARGB32_Premultiplied);
    crisp.fill(Qt::transparent);
    QImage glow(pixels, pixels, QImage::Format_ARGB32_Premultiplied);
    glow.fill(Qt::transparent);
    const double unit = pixels / 100.0;
    drawItems(crisp, glow, items_, unit);
    composite(crisp, glow, f.sigma * unit, paint.dark);

    // The idle light band crosses the strokes only: an 18-unit horizontal gradient, x −10 → 110 in 0.9 s.
    if (f.animated && stage.parked && 1 - f.wake >= 0.05) {
        double phase = std::fmod(f.time - stage.sweepEpoch, Stage::kSweepPeriod);
        if (phase < 0) phase += Stage::kSweepPeriod;
        const double u = phase / Stage::kSweepDuration;
        if (u < 1) {
            const double x = (-10 + 120 * (0.5 - 0.5 * std::cos(kPi * u))) * unit;
            QPainter p(&crisp);
            p.setCompositionMode(QPainter::CompositionMode_SourceAtop);
            QLinearGradient band(QPointF(x - 9 * unit, 0), QPointF(x + 9 * unit, 0));
            const QColor c = alpha(paint.sweep, 1 - f.wake);
            band.setColorAt(0, alpha(c, 0));
            band.setColorAt(0.5, c);
            band.setColorAt(1, alpha(c, 0));
            p.fillRect(QRectF(x - 9 * unit, 0, 18 * unit, pixels), band);
        }
    }
    applyMask(crisp, library_.maskSolid, library_.maskClear);
    crisp.setDevicePixelRatio(dpr);
    return crisp;
}

void Painter::drawItems(QImage& crisp, QImage& glowImage, const std::vector<Item>& items, double unit) {
    QPainter cp(&crisp);
    QPainter gp(&glowImage);
    for (QPainter* p : {&cp, &gp}) {
        p->setRenderHint(QPainter::Antialiasing);
        p->scale(unit, unit);
    }
    for (const auto& item : items) {
        if (item.kind == Item::Kind::Radial) {
            QRadialGradient g(item.center, item.radius);
            g.setColorAt(0, item.color);
            g.setColorAt(0.45, item.mid);
            g.setColorAt(1, alpha(item.mid, 0));
            cp.setCompositionMode(item.additive ? QPainter::CompositionMode_Plus : QPainter::CompositionMode_SourceOver);
            cp.setPen(Qt::NoPen);
            cp.setBrush(g);
            cp.drawEllipse(item.center, item.radius, item.radius);
            continue;
        }
        cp.setCompositionMode(item.additive ? QPainter::CompositionMode_Plus : QPainter::CompositionMode_SourceOver);
        if (item.fill) {
            QPainterPath path = item.path;
            path.setFillRule(Qt::OddEvenFill);
            cp.fillPath(path, item.fillColor);
        }
        if (item.stroke && item.opacity > 0.002) {
            QBrush brush(alpha(item.color, item.opacity));
            if (item.fade) {
                QLinearGradient g(item.fadeFrom, item.fadeTo);
                g.setColorAt(0, alpha(item.color, item.opacity));
                g.setColorAt(1, alpha(item.color, 0));
                brush = QBrush(g);
            }
            if (item.soft) {
                // A soft Gaussian profile: three widening strokes.
                for (const auto& [grow, share] : {std::pair{1.0, 0.45}, std::pair{1.8, 0.3}, std::pair{2.8, 0.18}}) {
                    QPen pen(alpha(item.color, item.opacity * share), 2 * item.halfWidth * grow, Qt::SolidLine, Qt::RoundCap, Qt::RoundJoin);
                    cp.strokePath(item.path, pen);
                }
            } else {
                cp.strokePath(item.path, QPen(brush, std::max(0.05, 2 * item.halfWidth), Qt::SolidLine, Qt::RoundCap, Qt::RoundJoin));
            }
        }
        if (item.glow > 0.002) {
            if (item.fill && !item.stroke) {
                QPainterPath path = item.path;
                path.setFillRule(Qt::OddEvenFill);
                QColor solid = item.fillColor;
                solid.setAlphaF(1);
                gp.fillPath(path, alpha(solid, item.glow));
            } else {
                gp.strokePath(item.path, QPen(alpha(item.color, item.glow), std::max(0.05, 2 * item.halfWidth), Qt::SolidLine, Qt::RoundCap, Qt::RoundJoin));
            }
        }
    }
}

// The glow over the strokes: additive on dark, normal blending on light (4c resolved detail 1).
void Painter::composite(QImage& crisp, QImage& glow, double sigmaPixels, bool dark) {
    blur(glow, sigmaPixels);
    QPainter p(&crisp);
    p.setCompositionMode(dark ? QPainter::CompositionMode_Plus : QPainter::CompositionMode_SourceOver);
    p.drawImage(0, 0, glow);
}

// Three box blurs approximate a Gaussian of `sigma` pixels.
void Painter::blur(QImage& image, double sigma) {
    if (sigma < 0.3) return;
    const int w = image.width(), h = image.height();
    const int radius = std::max(1, static_cast<int>(std::lround(std::sqrt(12 * sigma * sigma / 3 + 1) / 2)));
    std::vector<int> buffer(static_cast<std::size_t>(std::max(w, h)) * 4);
    auto* bits = reinterpret_cast<QRgb*>(image.bits());
    const int stride = static_cast<int>(image.bytesPerLine() / 4);
    const auto pass = [&](bool horizontal) {
        const int lines = horizontal ? h : w, length = horizontal ? w : h;
        for (int line = 0; line < lines; ++line) {
            const auto at = [&](int i) -> QRgb& { return horizontal ? bits[line * stride + i] : bits[i * stride + line]; };
            for (int i = 0; i < length; ++i) {
                const QRgb c = at(i);
                buffer[static_cast<std::size_t>(i) * 4 + 0] = qAlpha(c);
                buffer[static_cast<std::size_t>(i) * 4 + 1] = qRed(c);
                buffer[static_cast<std::size_t>(i) * 4 + 2] = qGreen(c);
                buffer[static_cast<std::size_t>(i) * 4 + 3] = qBlue(c);
            }
            int sum[4] = {0, 0, 0, 0};
            for (int i = -radius; i <= radius; ++i) {
                const int k = std::clamp(i, 0, length - 1);
                for (int ch = 0; ch < 4; ++ch) sum[ch] += i < 0 || i >= length ? 0 : buffer[static_cast<std::size_t>(k) * 4 + ch];
            }
            const int n = 2 * radius + 1;
            for (int i = 0; i < length; ++i) {
                at(i) = qRgba(sum[1] / n, sum[2] / n, sum[3] / n, sum[0] / n);
                const int add = i + radius + 1, drop = i - radius;
                for (int ch = 0; ch < 4; ++ch) {
                    if (add < length) sum[ch] += buffer[static_cast<std::size_t>(add) * 4 + ch];
                    if (drop >= 0) sum[ch] -= buffer[static_cast<std::size_t>(drop) * 4 + ch];
                }
            }
        }
    };
    for (int k = 0; k < 3; ++k) {
        pass(true);
        pass(false);
    }
}

// Opacity 1 to r = solid, falling linearly to 0 at r = clear (canvas units, centered).
void Painter::applyMask(QImage& image, double solid, double clear) {
    QPainter p(&image);
    p.setCompositionMode(QPainter::CompositionMode_DestinationIn);
    const double unit = image.width() / 100.0;
    QRadialGradient g(QPointF(50 * unit, 50 * unit), clear * unit);
    g.setColorAt(0, Qt::black);
    g.setColorAt(solid / clear, Qt::black);
    g.setColorAt(1, Qt::transparent);
    p.fillRect(image.rect(), g);
}

// ---- scenery

void Painter::drawScenery(const Frame& f) {
    const Stage& stage = *f.stage;
    const auto& current = library_.vehicles[static_cast<std::size_t>(stage.current.vehicle)];
    if (stage.outgoing) {
        const auto& previous = library_.vehicles[static_cast<std::size_t>(stage.outgoing->vehicle)];
        if (previous.scenery == current.scenery) {
            std::optional<double> ground = current.groundY;
            if (previous.groundY && current.groundY) {
                const double u = clamp((f.time - stage.current.start) / 0.45, 0, 1);
                ground = *previous.groundY + (*current.groundY - *previous.groundY) * (0.5 - 0.5 * std::cos(kPi * u));
            }
            drawSceneryKind(current.scenery, ground, current, f.presence, f);
            return;
        }
        drawSceneryKind(previous.scenery, previous.groundY, previous, stage.placement(*stage.outgoing, true).alpha * f.presence, f);
    }
    drawSceneryKind(current.scenery, current.groundY, current, stage.placement(stage.current, false).alpha * f.presence, f);
}

void Painter::drawSceneryKind(Scenery kind, std::optional<double> ground, const Vehicle& vehicle, double a, const Frame& f) {
    if (a <= 0.002) return;
    const auto& sp = library_.scenery;
    const double w = library_.strokeWidth;
    const double base = (f.paint.dark ? 0.30 : 0.34) * a;
    const QColor color = f.paint.stroke;
    const QTransform& t = f.base;
    const double travelled = f.distance, speed = f.ground;
    const auto line = [&](QPointF from, QPointF to, double halfWidth, double opacity) { segment(t.map(from), t.map(to), halfWidth, opacity, color, false); };
    switch (kind) {
    case Scenery::Road: {
        if (!ground) return;
        const double period = sp.dash + sp.dashGap;
        const double smear = clamp(speed / 60 / period / 0.45, 0, 1);
        const double length = sp.dash + smear * sp.dashGap * 0.85;
        const double offset = std::fmod(travelled * sp.roadParallax, period);
        Item item;
        for (double x = -offset - period; x < 104; x += period) {
            item.path.moveTo(t.map(QPointF(x, *ground)));
            item.path.lineTo(t.map(QPointF(x + length, *ground)));
        }
        item.halfWidth = w * 0.55 / 2;
        item.opacity = base * (1 - 0.35 * smear);
        item.color = color;
        items_.push_back(std::move(item));
        break;
    }
    case Scenery::Rail: {
        if (!ground) return;
        line(QPointF(-2, *ground), QPointF(102, *ground), w * 0.55 / 2, base * 1.3);
        const double smear = clamp(speed / 60 / sp.sleeperSpacing / 0.4, 0, 1);
        const double offset = std::fmod(travelled * sp.railParallax, sp.sleeperSpacing);
        Item item;
        for (double x = -offset; x < 106; x += sp.sleeperSpacing) {
            item.path.moveTo(t.map(QPointF(x, *ground + 1.4)));
            item.path.lineTo(t.map(QPointF(x - 1.2 + smear * 4, *ground + 1.4 + sp.sleeperDepth)));
        }
        item.halfWidth = w * 0.5 / 2;
        item.opacity = base * (1 - 0.55 * smear);
        item.color = color;
        items_.push_back(std::move(item));
        break;
    }
    case Scenery::Guideway: {
        if (!ground) return;
        Item beams;
        for (int beam = 0; beam < 2; ++beam) {
            const double y = *ground + (beam == 0 ? 0 : sp.guidewayDepth);
            beams.path.moveTo(t.map(QPointF(-2, y)));
            beams.path.lineTo(t.map(QPointF(102, y)));
        }
        beams.halfWidth = w * 0.55 / 2;
        beams.opacity = base * 1.2;
        beams.color = color;
        items_.push_back(std::move(beams));
        const double offset = std::fmod(travelled * sp.guidewayParallax, sp.pylonSpacing);
        Item pylons;
        for (double x = -offset; x < 110; x += sp.pylonSpacing) {
            pylons.path.moveTo(t.map(QPointF(x, *ground + sp.guidewayDepth)));
            pylons.path.lineTo(t.map(QPointF(x, *ground + sp.guidewayDepth + sp.pylonDepth)));
        }
        pylons.halfWidth = w * 0.55 / 2;
        pylons.opacity = base;
        pylons.color = color;
        items_.push_back(std::move(pylons));
        break;
    }
    case Scenery::Sky: {
        const double spacing = 72;
        for (std::size_t index = 0; index < sp.skyLayers.size(); ++index) {
            const auto& layer = sp.skyLayers[index];
            const double drift = travelled * layer.parallax;
            const double offset = std::fmod(drift + static_cast<double>(index) * 31.0, spacing);
            const double opacity = layer.opacity * a * (f.paint.dark ? 1 : 1.2);
            for (double x = 104 - offset; x > -30; x -= spacing) {
                const int shape = (static_cast<int>(std::lround((x + drift) / spacing)) + static_cast<int>(index)) & 1;
                QTransform placed = t;
                placed.translate(x - 8, layer.y);
                placed.scale(layer.scale, layer.scale);
                if (!library_.clouds.empty()) {
                    addPath(library_.clouds[static_cast<std::size_t>(shape) % library_.clouds.size()], placed, true, false, w * 0.5 / 2, opacity, color, QColor(), 0);
                }
            }
        }
        break;
    }
    case Scenery::Stratosphere:
        arc(QPointF(50, sp.horizonCy), sp.horizonR, kPi * 1.15, kPi * 1.85, t, w * 0.55 / 2, base, color);
        arc(QPointF(50, sp.horizonCy + 1.4), sp.horizonR, kPi * 1.2, kPi * 1.8, t, w * 2.2 / 2, base * 0.35, color, true);
        break;
    case Scenery::Space: {
        const QPointF d = vehicle.headingVector();
        const double streak = std::max(0.3, speed * sp.spaceParallax / 60 * 1.3);
        for (std::size_t i = 0; i < stars_.size(); ++i) {
            const double z = starDepth_[i];
            const double dd = std::fmod(travelled * sp.spaceParallax * z, 140.0);
            double x = stars_[i].x() - d.x() * dd, y = stars_[i].y() - d.y() * dd;
            x = std::fmod(std::fmod(x, 120.0) + 120, 120.0) - 10;
            y = std::fmod(std::fmod(y, 120.0) + 120, 120.0) - 10;
            line(QPointF(x, y), QPointF(x + d.x() * streak * z, y + d.y() * streak * z), w * 0.4 / 2, base * z * 1.2);
        }
        break;
    }
    }
}

void Painter::drawSpeedLines(const Frame& f) {
    const QColor color = f.paint.dark && f.stage->tier >= 4 ? f.paint.hot : f.paint.stroke;
    const double extra = f.ground / 60 * 1.5;
    for (const auto& line : f.stage->lines.pool) {
        if (!line.alive) continue;
        const double length = line.length + extra;
        segment(f.base.map(QPointF(line.x, line.y)), f.base.map(QPointF(line.x - line.dx * length, line.y - line.dy * length)),
                library_.strokeWidth * 0.5 / 2, line.opacity * clamp(line.age / 0.12, 0, 1), color, true);
    }
}

// ---- actors

void Painter::drawActor(const Actor& actor, bool outgoing, const Frame& f) {
    const Placement placement = f.stage->placement(actor, outgoing);
    if (placement.alpha <= 0.002) return;
    const StartupEffects fx = outgoing ? StartupEffects{} : f.effects;
    if (placement.ghostSpeed > 40 && f.animated) {
        static const std::pair<double, double> ghosts[] = {{0.012, 0.26}, {0.026, 0.13}, {0.042, 0.06}};
        for (const auto& [delay, opacity] : ghosts) {
            Placement copy = placement;
            copy.offset -= placement.direction * (placement.ghostSpeed * delay);
            copy.alpha *= opacity;
            drawVehicle(actor.vehicle, copy, fx, true, f);
        }
    }
    drawVehicle(actor.vehicle, placement, fx, false, f);
}

void Painter::drawVehicle(int index, const Placement& placement, const StartupEffects& fx, bool ghost, const Frame& f) {
    const auto& v = library_.vehicles[static_cast<std::size_t>(index)];
    const Stage& stage = *f.stage;
    const double t = f.time, b = f.band, wake = f.wake;
    const bool animated = f.animated;
    const double a = placement.alpha * f.presence;
    const Paint& paint = f.paint;
    const double glowBase = ghost ? 0 : f.glowOpacity;
    const double w0 = f.width;
    const double accel = stage.acceleration();
    double bob = 0, pitch = 0, floatY = 0, hover = 0;
    const double lurch = animated ? clamp(0.008 * accel, -1.2, 1.2) + fx.lurch : 0;
    if (animated) {
        if (v.bob > 0) {
            const double phase = stage.suspensionPhase;
            bob = v.bob * (0.6 * std::sin(1.7 * phase) + 0.4 * std::sin(3.3 * phase + 1.3)) * (0.4 + 0.6 * b) * wake;
        }
        if (v.floatAmount > 0) {
            floatY = v.floatAmount * std::sin(2 * kPi * 0.32 * t) * (0.3 + 0.7 * wake);
            pitch = 0.8 * (v.floatAmount / 0.7) * std::cos(2 * kPi * 0.32 * t) * wake;
        }
        if (v.rollsOnTires) pitch += clamp(-0.0035 * accel, -1, 1) + fx.pitch;
    }
    if (v.hover > 0) hover = v.hover * fx.hover * wake * (animated ? 0.75 + 0.25 * std::sin(2 * kPi * 0.7 * t) : 1);
    const QPointF direction = v.headingVector();
    double ox = placement.offset.x() + direction.x() * lurch;
    double oy = placement.offset.y() + direction.y() * lurch;
    if (v.heading) {
        ox -= direction.x() * fx.lift;
        oy -= direction.y() * fx.lift;
        if (animated && v.floatAmount > 0) {
            const double n = v.floatAmount * std::sin(2 * kPi * 0.55 * t);
            ox -= direction.y() * n;
            oy += direction.x() * n;
            floatY = 0;
            pitch = 0;
        }
    } else {
        oy += fx.lift;
    }
    QTransform unsprung = f.base;
    unsprung.translate(ox, oy);
    QTransform sprung = f.base;
    sprung.translate(ox, oy + bob + floatY - hover);
    if (pitch != 0) {
        sprung.translate(v.pitchPivot.x(), v.pitchPivot.y());
        sprung.rotateRadians(pitch * kPi / 180);
        sprung.translate(-v.pitchPivot.x(), -v.pitchPivot.y());
    }
    if (!ghost) {
        drawTrailMarks(v, f, ox, oy, floatY, a);
        drawContrails(v, f, ox, oy, a, fx.contrail);
    }
    std::optional<RigPose> pose;
    const double travelled = animated ? f.distance : v.rest;
    if (v.walk) pose = walkPose(*v.walk, travelled, v.groundY.value_or(v.walk->hip.y()), stage.stand);
    else if (v.pedal) pose = pedalPose(*v.pedal, travelled, fx.hipLift);
    const auto& rider = library_.style(Role::Rider);
    const auto& crank = library_.style(Role::Crank);
    if (pose) {
        const double farOpacity = a * rider.opacity * 0.42;
        const double farGlow = glowBase * a * 0.42 * std::min(1.0, rider.glow);
        emitLimb(pose->far0, unsprung, w0 * rider.weight / 2, farOpacity, paint.stroke, farGlow);
        emitLimb(pose->far1, unsprung, w0 * rider.weight / 2, farOpacity, paint.stroke, farGlow);
    }
    for (const auto& part : v.parts) drawPart(part, v, f, sprung, unsprung, pose ? &*pose : nullptr, fx, a, hover, glowBase, ghost);
    if (!ghost && f.level >= Level::Standard && b > 0.35 && animated) drawShockDiamonds(v, f, sprung, fx.flame, a);
    if (!ghost && v.hover > 0 && animated && wake > 0.05) {
        QTransform gap = sprung;
        gap.translate(0, hover / 2);
        drawFieldPulse(v, f, gap, a * fx.hover);
    }
    if (pose) {
        if (pose->nearPedal) emitLimb(*pose->nearPedal, unsprung, w0 * crank.weight / 2, a * crank.opacity, paint.stroke, glowBase * a * std::min(1.0, crank.glow));
        const double opacity = a * rider.opacity;
        const double glow = glowBase * a * std::min(1.0, rider.glow);
        for (int limb = 0; limb < pose->nearCount; ++limb) emitLimb(pose->near[limb], unsprung, w0 * rider.weight / 2, opacity, paint.stroke, glow);
        if (pose->head) {
            const auto& h = *pose->head;
            circle(unsprung.map(QPointF(h[0], h[1])), h[2], w0 * rider.weight / 2, opacity, paint.stroke, glow);
        }
    }
}

void Painter::drawPart(const Part& part, const Vehicle& vehicle, const Frame& f, const QTransform& sprung, const QTransform& unsprung,
                       const RigPose* pose, const StartupEffects& fx, double a, double hover, double glowBase, bool ghost) {
    const auto& style = library_.style(part.role);
    const Paint& paint = f.paint;
    const double t = f.time, b = f.band, wake = f.wake;
    double op = a;
    QTransform transform = part.sprung ? sprung : unsprung;
    if (part.role == Role::Rider && pose) transform.translate(pose->riderOffset.x(), pose->riderOffset.y());
    double spinBlur = 0;
    if (part.hasSpin) {
        const double travelled = f.animated ? f.distance : vehicle.rest;
        if (travelled != 0) {
            transform.translate(part.spinPivot.x(), part.spinPivot.y());
            transform.rotateRadians(wrapAngle(travelled / part.spinRadius));
            transform.translate(-part.spinPivot.x(), -part.spinPivot.y());
        }
        if (f.animated) spinBlur = smooth((f.ground / (2 * kPi * part.spinRadius) * part.spinSymmetry - 14) / 12);
    }
    switch (part.role) {
    case Role::Spoke: op *= 1 - spinBlur; break;
    case Role::Light: op *= 0.7 + 0.3 * wake + 0.6 * fx.lights; break;
    case Role::Glow:
        op *= fx.hover * (0.5 + 0.5 * b) * wake;
        transform.translate(0, hover / 2);
        break;
    case Role::Shock: {
        const double squeeze = 1.15 - 0.35 * b;
        transform.translate(part.apex.x(), part.apex.y());
        transform.scale(1, squeeze);
        transform.translate(-part.apex.x(), -part.apex.y());
        op *= fx.shock * (0.4 + 0.6 * b) * wake * (f.animated ? 0.7 + 0.3 * std::sin(2 * kPi * 11 * t) : 1);
        break;
    }
    case Role::Flame:
    case Role::FlameCore:
        drawFlame(part, f, transform, fx, op, glowBase);
        return;
    default: break;
    }
    const double opacity = std::min(1.0, op * style.opacity);
    const double glow = glowBase * std::min(1.0, op * std::min(1.0, style.glow));
    const QColor color = style.hot ? paint.hot : paint.stroke;
    const double halfWidth = f.width * style.weight / 2;
    if (part.isCircle) {
        circle(transform.map(QPointF(part.cx, part.cy)), part.r, halfWidth, opacity, color, glow);
    } else {
        addPath(part.path, transform, true, false, halfWidth, opacity, color, QColor(), glow);
    }
    if (part.role == Role::Spoke && spinBlur > 0.01 && !ghost && part.hasSpin) {
        const double rr = part.spinRadius - 2;
        const double turn = std::fmod(t * 0.6, 1.0) * 2 * kPi;
        const double arcOpacity = 0.5 * spinBlur * a;
        const double arcWidth = f.width * 0.45 / 2;
        arc(part.spinPivot, rr * 0.72, turn, turn + 2.1, unsprung, arcWidth, arcOpacity, paint.stroke);
        arc(part.spinPivot, rr * 0.45, turn + kPi, turn + kPi + 2.1, unsprung, arcWidth, arcOpacity, paint.stroke);
    }
}

// Flames scale about their nozzle with band, overdrive and flicker; the outline glows in the accent.
void Painter::drawFlame(const Part& part, const Frame& f, const QTransform& transform, const StartupEffects& fx, double a, double glowBase) {
    if (!part.hasNozzle || part.path.empty()) return;
    const double t = f.time;
    const double flicker = f.animated ? 1 + 0.1 * (std::sin(2 * kPi * 17 * t) + std::sin(2 * kPi * 23 * t + 1.1) + std::sin(2 * kPi * 31 * t + 2.3)) / 3 : 1;
    const double length = (0.55 + 0.45 * f.band + 0.35 * f.overdrive) * flicker * fx.flame * f.wake;
    if (length < 0.02) return;
    const double width = (1 + (f.animated ? 0.06 * std::sin(2 * kPi * 13 * t) : 0)) * std::min(1.0, 0.6 + 0.4 * length);
    const double angle = std::atan2(part.nozzleDir.y(), part.nozzleDir.x());
    QTransform shaped = transform;
    shaped.translate(part.nozzleAt.x(), part.nozzleAt.y());
    shaped.rotateRadians(angle);
    shaped.scale(length, width);
    shaped.rotateRadians(-angle);
    shaped.translate(-part.nozzleAt.x(), -part.nozzleAt.y());
    const auto& style = library_.style(part.role);
    if (part.role == Role::FlameCore) {
        addPath(part.path, shaped, false, true, 0, 0, f.paint.core, alpha(f.paint.core, std::min(1.0, a * style.fillOpacity)),
             glowBase * std::min(1.0, a * style.fillOpacity));
    } else {
        addPath(part.path, shaped, true, true, f.width * style.weight / 2, a * style.opacity, f.paint.hot,
             alpha(f.paint.stroke, std::min(1.0, a * style.fillOpacity)), glowBase * std::min(1.0, a));
    }
}

void Painter::drawTrailMarks(const Vehicle& v, const Frame& f, double ox, double oy, double floatY, double a) {
    const double halfWidth = f.width * 0.5 / 2;
    if (f.animated) {
        if (f.wake <= 0.05 || f.ground <= 4) return;
        QTransform t = f.base;
        t.translate(ox, oy + floatY);
        const double speed = clamp(f.ground / 20, 0, 1);
        for (const auto& e : v.emitters) {
            if (e.kind != EmitterKind::Trail) continue;
            for (std::size_t lane = 0; lane < e.lanes.size(); ++lane) {
                const double i = static_cast<double>(lane);
                const double wobble = 1 + 0.18 * std::sin(2 * kPi * (5.3 + 1.7 * i) * f.time + i);
                const double length = e.length * (0.45 + 0.55 * f.band + 0.25 * f.heat) * wobble * speed;
                const double x = e.at.x() - 1.5 - static_cast<double>(lane % 2) * 2.5, y = e.at.y() + e.lanes[lane];
                segment(t.map(QPointF(x, y)), t.map(QPointF(x - length, y)), halfWidth, 0.55 * a * f.wake, f.paint.stroke, true);
            }
        }
        return;
    }
    if (f.wake <= 0.5 || f.stage->parked) return;
    const int count = f.band < 0.34 ? 1 : f.band < 0.67 ? 2 : 3;
    QTransform t = f.base;
    t.translate(ox, oy);
    for (const auto& e : v.emitters) {
        if (e.kind != EmitterKind::Trail) continue;
        for (std::size_t lane = 0; lane < e.lanes.size() && static_cast<int>(lane) < count; ++lane) {
            const double x = e.at.x() - 1.5 - static_cast<double>(lane % 2) * 2.5, y = e.at.y() + e.lanes[lane];
            segment(t.map(QPointF(x, y)), t.map(QPointF(x - e.length * 0.7, y)), halfWidth, 0.5 * a, f.paint.stroke, false);
        }
    }
}

void Painter::drawContrails(const Vehicle& v, const Frame& f, double ox, double oy, double a, double contrail) {
    if (f.wake <= 0.02) return;
    QTransform t = f.base;
    t.translate(ox, oy);
    for (const auto& e : v.emitters) {
        if (e.kind != EmitterKind::Contrail) continue;
        const double length = (e.weak ? 22 : 46) * contrail * (f.animated ? 1 : 0.8);
        if (length < 1) continue;
        const double ax = e.at.x() - 2, ay = e.at.y();
        const double opacity = (e.weak ? 0.26 : 0.55) * (0.5 + 0.5 * f.band) * a * f.wake;
        if (opacity <= 0.002) continue;
        Item item;
        for (double d = 0; d <= length; d += 2) {
            double y = ay;
            if (f.animated) {
                if (v.floatAmount > 0) y += v.floatAmount * std::sin(2 * kPi * 0.32 * (f.time - d / 90)) * (0.3 + 0.7 * f.wake);
                y += 0.35 * std::sin((ax - d + std::fmod(f.distance * 0.05, 14 * kPi)) / 7) * (d / 40);
            }
            const QPointF p = t.map(QPointF(ax - d, y));
            if (d == 0) item.path.moveTo(p);
            else item.path.lineTo(p);
        }
        item.halfWidth = f.width * (e.weak ? 0.45 : 0.8) / 2;
        item.opacity = std::min(1.0, opacity);
        item.color = f.paint.stroke;
        item.fade = true;
        item.fadeFrom = t.map(QPointF(ax, ay));
        item.fadeTo = t.map(QPointF(ax - length, ay));
        items_.push_back(std::move(item));
    }
}

void Painter::drawShockDiamonds(const Vehicle& v, const Frame& f, const QTransform& sprung, double flame, double a) {
    const double b = f.band;
    const double length = (0.55 + 0.45 * b + 0.35 * f.overdrive) * flame;
    const QColor color = f.paint.dark ? f.paint.core : f.paint.hot;
    for (const auto& e : v.emitters) {
        if (e.kind != EmitterKind::Afterburner) continue;
        for (int index = 0; index < 3; ++index) {
            const QPointF center = e.at + e.dir * ((3.0 + 2.8 * index) * length);
            const double height = 1.0 - index * 0.18;
            const double opacity = 0.75 * (b - 0.35) / 0.65 * a * (0.75 + 0.25 * std::sin(2 * kPi * 19 * f.time + index));
            if (opacity <= 0.002) continue;
            Item item;
            for (int corner = 0; corner <= 4; ++corner) {
                const double angle = corner * kPi / 2;
                const QPointF p = sprung.map(QPointF(center.x() - std::cos(angle) * height * 0.9, center.y() - std::sin(angle) * height));
                if (corner == 0) item.path.moveTo(p);
                else item.path.lineTo(p);
            }
            item.halfWidth = f.width * 0.35 / 2;
            item.opacity = std::min(1.0, opacity);
            item.color = color;
            items_.push_back(std::move(item));
        }
    }
}

// The maglev's bright pulse along the hover gap, nose to tail, at half the ground speed.
void Painter::drawFieldPulse(const Vehicle& v, const Frame& f, const QTransform& sprung, double a) {
    const Part* gap = nullptr;
    for (const auto& part : v.parts) {
        if (part.role == Role::Glow) gap = &part;
    }
    if (!gap || a <= 0.002) return;
    const double x0 = gap->spanX0, x1 = gap->spanX1, y = gap->spanY;
    const double span = x1 - x0;
    const double position = x1 - std::fmod(f.distance * 0.5, span + 20);
    const double head = std::min(x1, position), tail = std::max(x0, position - 14);
    if (head <= tail) return;
    segment(sprung.map(QPointF(head, y)), sprung.map(QPointF(tail, y)), f.width * 0.7 / 2, 0.9 * a, f.paint.hot, true);
}

void Painter::drawParticles(const Frame& f) {
    const Paint& paint = f.paint;
    const double w0 = library_.strokeWidth;
    const double glow = f.glowOpacity;
    const QColor hot = f.paint.dark ? paint.core : paint.hot;
    for (const auto& p : f.stage->particles.pool) {
        if (!p.alive) continue;
        const double u = p.progress();
        if (u < 0) continue;
        const QPointF center = f.base.map(QPointF(p.x, p.y));
        switch (p.kind) {
        case Particle::Kind::Puff:
        case Particle::Kind::Ring: {
            const double radius = p.size + p.grow * (1 - (1 - u) * (1 - u));
            const double opacity = (p.kind == Particle::Kind::Ring ? 0.7 : 0.5) * (1 - u);
            const QColor color = p.kind == Particle::Kind::Ring ? mixOklab(paint.hot, paint.stroke, u) : paint.stroke;
            circle(center, radius, w0 * 0.45 * (1 - 0.5 * u) / 2, opacity, color, glow * opacity * 0.6);
            break;
        }
        case Particle::Kind::Spark:
        case Particle::Kind::Ember: {
            const QPointF tail = f.base.map(QPointF(p.x - p.vx * 0.022, p.y - p.vy * 0.022));
            segment(center, tail, w0 * 0.45 / 2, (1 - u) * 0.95, mixOklab(hot, paint.stroke, u), false, std::min(1.0, glow * (1 - u)));
            break;
        }
        case Particle::Kind::Arc: {
            const double opacity = 1 - u;
            const double inner = 3.2, outer = inner + p.size;
            Item item;
            item.path.moveTo(f.base.map(QPointF(p.x + std::cos(p.angle) * inner, p.y + std::sin(p.angle) * inner)));
            for (int step = 1; step <= 3; ++step) {
                const double radius = inner + (outer - inner) * step / 3;
                const double jitter = (step % 2 == 1 ? 1.0 : -1.0) * 0.35 * (0.5 + p.seed);
                item.path.lineTo(f.base.map(QPointF(p.x + std::cos(p.angle + jitter) * radius, p.y + std::sin(p.angle + jitter) * radius)));
            }
            item.halfWidth = w0 * 0.4 / 2;
            item.opacity = opacity * 0.95;
            item.color = hot;
            item.glow = std::min(1.0, glow * opacity);
            items_.push_back(std::move(item));
            break;
        }
        case Particle::Kind::Pop: {
            const double length = 3.4 * std::sin(kPi * u);
            if (length <= 0.05) break;
            QTransform placed = f.base;
            placed.translate(p.x, p.y);
            placed.rotateRadians(p.angle);
            Item item;
            const int steps = 8;
            item.path.moveTo(placed.map(QPointF(0, -0.9)));
            for (int k = 1; k <= steps; ++k) {
                const double t = static_cast<double>(k) / steps, v = 1 - t;
                item.path.lineTo(placed.map(QPointF(2 * v * t * length * 0.6 + t * t * length, v * v * -0.9 + 2 * v * t * -0.9)));
            }
            for (int k = 1; k <= steps; ++k) {
                const double t = static_cast<double>(k) / steps, v = 1 - t;
                item.path.lineTo(placed.map(QPointF(v * v * length + 2 * v * t * length * 0.6, 2 * v * t * 0.9 + t * t * 0.9)));
            }
            item.path.closeSubpath();
            item.stroke = false;
            item.fill = true;
            item.fillColor = alpha(paint.core, 0.95);
            item.color = paint.core;
            item.glow = std::min(1.0, glow);
            items_.push_back(std::move(item));
            break;
        }
        }
    }
}

// The upgrade flash: a radial gradient on the incoming vehicle's center, radius 8 → 36 over 0.35 s.
void Painter::drawFlash(const Frame& f) {
    const Stage& stage = *f.stage;
    if (!stage.flash || f.time < stage.flash->first) return;
    const double u = clamp((f.time - stage.flash->first) / 0.35, 0, 1);
    const auto& v = library_.vehicles[static_cast<std::size_t>(stage.current.vehicle)];
    const double a = (f.paint.dark ? 0.42 : 0.26) * stage.flash->second * (1 - u) * (1 - u) * (f.level == Level::Soft ? 0.6 : 1);
    if (a <= 0.002) return;
    Item item;
    item.kind = Item::Kind::Radial;
    item.center = f.base.map(v.center());
    item.radius = 8 + 28 * easeOutCubic(u);
    item.color = alpha(f.paint.dark ? QColor(0xFF, 0xF7, 0xD6) : f.paint.hot, a);
    item.mid = alpha(f.paint.hot, a * 0.45);
    item.additive = f.paint.dark;
    items_.push_back(std::move(item));
}

}  // namespace hero
