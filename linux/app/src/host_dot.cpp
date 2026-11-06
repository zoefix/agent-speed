#include "host_dot.hpp"

#include <QGuiApplication>
#include <QPainter>

#include "appearance.hpp"

QColor hostDotColor(const QString& kind, bool dark) {
    if (kind == QLatin1String("connected")) return dark ? QColor(0x4A, 0xDE, 0x80) : QColor(0x1F, 0x9D, 0x55);
    if (kind == QLatin1String("retrying")) return dark ? QColor(0xFB, 0x92, 0x3C) : QColor(0xE8, 0x6A, 0x0C);
    if (kind == QLatin1String("attention")) return dark ? QColor(0xF8, 0x71, 0x71) : QColor(0xD9, 0x2D, 0x20);
    QColor grey = dark ? QColor(0xEC, 0xEF, 0xF4) : QColor(0x16, 0x18, 0x1D);
    grey.setAlphaF(kind == QLatin1String("idle") ? 0.42f : 0.55f);
    return grey;
}

QPixmap hostDot(const QString& kind) {
    const qreal ratio = qGuiApp ? qGuiApp->devicePixelRatio() : 1.0;
    QPixmap pixmap(QSize(12, 12) * ratio);
    pixmap.setDevicePixelRatio(ratio);
    pixmap.fill(Qt::transparent);
    QPainter p(&pixmap);
    p.setRenderHint(QPainter::Antialiasing);
    const QColor color = hostDotColor(kind, Appearance::instance().dark());
    const QRectF dot(3, 3, 6, 6);
    if (kind == QLatin1String("idle")) {
        p.setPen(QPen(color, 1.2));
        p.setBrush(Qt::NoBrush);
        p.drawEllipse(dot.adjusted(0.6, 0.6, -0.6, -0.6));
    } else {
        p.setPen(Qt::NoPen);
        p.setBrush(color);
        p.drawEllipse(dot);
    }
    return pixmap;
}
