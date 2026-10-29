#include "orb/body.hpp"

#include <QPainter>
#include <QPainterPath>
#include <QRadialGradient>
#include <cmath>

namespace orb {

namespace {

QColor q(const Rgba& c, double alpha = 1) { return QColor::fromRgbF(c.r, c.g, c.b, static_cast<float>(std::clamp(c.a * alpha, 0.0, 1.0))); }

}  // namespace

QImage renderBody(double s, double side, double dpr, const Tokens& t, bool blurBehind) {
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
        g.setColorAt(0, q(t.bodyTop, alpha));
        g.setColorAt(1, q(t.bodyBottom, alpha));
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
    // Lens highlight at the top left.
    {
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
