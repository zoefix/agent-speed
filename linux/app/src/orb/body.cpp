#include "orb/body.hpp"

#include <QPainter>
#include <QPainterPath>
#include <QRadialGradient>
#include <cmath>

namespace orb {

namespace {

QColor q(const Rgba& c, double alpha = 1) { return QColor::fromRgbF(c.r, c.g, c.b, static_cast<float>(std::clamp(c.a * alpha, 0.0, 1.0))); }

}  // namespace

QImage renderBody(double s, double side, double dpr, const Tokens& t, bool blurBehind, StyleKind style) {
    QImage image(qMax(1, qCeil(side * dpr)), qMax(1, qCeil(side * dpr)), QImage::Format_RGBA8888_Premultiplied);
    image.setDevicePixelRatio(dpr);
    image.fill(Qt::transparent);
    QPainter p(&image);
    p.setRenderHint(QPainter::Antialiasing);
    const QPointF c(side / 2, side / 2);
    const double r = s / 2;
    QPainterPath disc;
    disc.addEllipse(c, r, r);

    // Drop shadow, only outside the disc so it never darkens the glass: a blurred disc is a radial falloff.
    {
        QPainterPath outside;
        outside.addRect(QRectF(0, 0, side, side));
        outside = outside.subtracted(disc);
        p.save();
        p.setClipPath(outside);
        const double blur = s * t.shadowBlur;
        const QPointF sc(c.x(), c.y() + s * t.shadowOffset);
        QRadialGradient g(sc, r + blur * 1.6);
        const double inner = std::max(0.0, (r - blur * 0.8) / (r + blur * 1.6));
        g.setColorAt(0, q(t.shadow));
        g.setColorAt(inner, q(t.shadow));
        g.setColorAt(inner + (1 - inner) * 0.45, q(t.shadow, 0.35));
        g.setColorAt(1, q(t.shadow, 0));
        p.fillRect(QRectF(0, 0, side, side), g);
        p.restore();
    }

    p.setClipPath(disc);
    // Body gradient; with the desktop blurred behind it stays translucent, else it is a solid gradient.
    {
        QLinearGradient g(QPointF(c.x(), c.y() - r), QPointF(c.x(), c.y() + r));
        const double alpha = blurBehind ? t.bodyOpacity : 1.0;
        // DESIGN 2a: `liquid` and `reactor` darken (or cool) the body.
        Rgba top = t.bodyTop, bottom = t.bodyBottom;
        if (style == StyleKind::Liquid) {
            top = t.dark ? Rgba::hex(0x181D2B) : Rgba::hex(0xFFFFFF);
            bottom = t.dark ? Rgba::hex(0x07090D) : Rgba::hex(0xE4E8F0);
        } else if (style == StyleKind::Reactor) {
            if (t.dark) top = Rgba::hex(0x141824);
            bottom = t.dark ? Rgba::hex(0x06070B) : Rgba::hex(0xE5E9F1);
        }
        g.setColorAt(0, q(top, alpha));
        g.setColorAt(1, q(bottom, alpha));
        p.fillPath(disc, g);
    }
    // Soft inner shadow along the rim.
    {
        QRadialGradient g(c, r);
        g.setColorAt(0, q(t.innerShadow, 0));
        g.setColorAt(0.8, q(t.innerShadow, 0));
        g.setColorAt(0.94, q(t.innerShadow, 0.45));
        g.setColorAt(1, q(t.innerShadow, 1));
        p.fillPath(disc, g);
    }
    // `liquid` volume shading: clear to 75 % of the radius around a point up-left of the center, then darker.
    if (style == StyleKind::Liquid) {
        QRadialGradient g(QPointF(c.x() - 0.12 * s, c.y() - 0.16 * s), 0.5 * s);
        const Rgba edge = t.dark ? Rgba::black(0.42f) : Rgba::hex(0x1E283C, 0.12f);
        g.setColorAt(0, q(edge, 0));
        g.setColorAt(0.75, q(edge, 0));
        g.setColorAt(1, q(edge));
        p.fillPath(disc, g);
    }
    // Lens highlight at the top left (not on `liquid` and `minimal`).
    if (style == StyleKind::Led || style == StyleKind::Needle || style == StyleKind::Reactor) {
        QRadialGradient g(QPointF(c.x() - r + 0.3 * s, c.y() - r + 0.2 * s), 0.55 * s);
        g.setColorAt(0, q(t.lens));
        g.setColorAt(1, q(t.lens, 0));
        p.fillPath(disc, g);
    }
    p.setClipping(false);
    // Specular rim: 1 pt, the top color fading out by three quarters down.
    {
        QLinearGradient g(QPointF(c.x(), c.y() - r), QPointF(c.x(), c.y() + 0.5 * r));
        g.setColorAt(0, q(t.rimTop));
        g.setColorAt(1, q(t.rimTop, 0));
        p.setPen(QPen(QBrush(g), 1.0));
        p.setBrush(Qt::NoBrush);
        p.drawEllipse(c, r - 0.5, r - 0.5);
    }
    p.setPen(QPen(q(t.outerStroke), 0.5));
    p.drawEllipse(c, r, r);
    p.end();
    return image;
}

}  // namespace orb
