#include "orbs/name_plate.hpp"

#include <QConicalGradient>
#include <QFontMetricsF>
#include <QGuiApplication>
#include <QLinearGradient>
#include <QPainter>
#include <QPainterPath>
#include <QSurfaceFormat>
#include <QVariantAnimation>
#include <algorithm>
#include <cmath>

namespace {

// Geometry (DESIGN 2b Name plate): text size f, plate height h, dot d.
double textSize(double s) { return std::clamp(std::round(0.066 * s * 2) / 2, 9.0, 12.0); }
double plateHeight(double s) { return std::round(1.78 * textSize(s)); }
double dotSize(double s) { return std::max(5.0, 0.6 * textSize(s)); }

bool cjk(const QString& text) {
    for (QChar c : text) {
        if (c.unicode() >= 0x2E80) return true;
    }
    return false;
}

}  // namespace

NamePlate::NamePlate() {
    QSurfaceFormat format;
    format.setAlphaBufferSize(8);
    setFormat(format);
    setFlags(Qt::Tool | Qt::FramelessWindowHint | Qt::NoDropShadowWindowHint | Qt::WindowDoesNotAcceptFocus |
             Qt::WindowStaysOnTopHint | Qt::WindowTransparentForInput);
    setOpacity(0);
}

double NamePlate::gap(double s) { return std::clamp(std::round(0.034 * s), 4.0, 7.0); }

QFont NamePlate::font() const {
    QFont font = QGuiApplication::font();
    font.setPixelSize(static_cast<int>(std::lround(textSize(look_.diameter))));
    font.setWeight(QFont::DemiBold);
    font.setFeature(QFont::Tag("tnum"), 1);
    const QString name = look_.forms.isEmpty() ? QString() : look_.forms.front();
    font.setLetterSpacing(QFont::PercentageSpacing, cjk(name) ? 104 : 101.2);
    return font;
}

// The longest form that fits the budget; else the last form with its head ellipsized before `tail`.
QString NamePlate::fitted() const {
    const double s = look_.diameter, f = textSize(s), h = plateHeight(s), d = dotSize(s);
    const int dots = look_.spectrum.empty() ? std::min<int>(3, static_cast<int>(look_.dots.size())) : 1;
    const double chrome = h / 2 + (dots > 0 ? (dots - 1) * d * 2 / 3 + d / 2 + 0.42 * f : 0) + 0.52 * h;
    const QFontMetricsF fm(font());
    const double statusWidth = look_.status.isEmpty() ? 0 : fm.horizontalAdvance(QStringLiteral(" · ") + look_.status);
    const double room = look_.budget - chrome - statusWidth;
    for (const auto& form : look_.forms) {
        if (fm.horizontalAdvance(form) <= room) return form;
    }
    if (look_.forms.isEmpty()) return {};
    QString head = look_.forms.back();
    if (!look_.tail.isEmpty() && head.endsWith(look_.tail)) head.chop(look_.tail.size());
    return fm.elidedText(head, Qt::ElideRight, std::max(0.0, room - fm.horizontalAdvance(look_.tail))) + look_.tail;
}

void NamePlate::setLook(const Look& look) {
    if (look == look_ && !text_.isEmpty()) return;
    look_ = look;
    text_ = fitted();
    const double s = look_.diameter, f = textSize(s), h = plateHeight(s), d = dotSize(s);
    const int dots = look_.spectrum.empty() ? std::min<int>(3, static_cast<int>(look_.dots.size())) : 1;
    const QFontMetricsF fm(font());
    const QString status = look_.status.isEmpty() ? QString() : QStringLiteral(" · ") + look_.status;
    const double width = h / 2 + (dots > 0 ? (dots - 1) * d * 2 / 3 + d / 2 + 0.42 * f : 0) + fm.horizontalAdvance(text_ + status) + 0.52 * h;
    capsule_ = QSizeF(std::ceil(width), h);
    resize(qCeil(capsule_.width()) + 2 * kMargin, qCeil(capsule_.height()) + 2 * kMargin);
    update();
}

void NamePlate::placeCapsule(QPointF point) { setPosition((point - QPointF(kMargin, kMargin)).toPoint()); }

// Appear: opacity 0 → 1 in 0.28 s; disappear: fade out in 0.18 s.
void NamePlate::appear() {
    if (wanted_ && isVisible()) return;
    wanted_ = true;
    if (!isVisible()) show();
    if (fade_) fade_->stop();
    auto* fade = new QVariantAnimation(this);
    fade->setStartValue(opacity());
    fade->setEndValue(1.0);
    fade->setDuration(280);
    fade->setEasingCurve(QEasingCurve::OutCubic);
    connect(fade, &QVariantAnimation::valueChanged, this, [this](const QVariant& v) { setOpacity(v.toDouble()); });
    fade_ = fade;
    fade->start(QAbstractAnimation::DeleteWhenStopped);
}

void NamePlate::disappear() {
    if (!wanted_) return;
    wanted_ = false;
    if (fade_) fade_->stop();
    auto* fade = new QVariantAnimation(this);
    fade->setStartValue(opacity());
    fade->setEndValue(0.0);
    fade->setDuration(180);
    connect(fade, &QVariantAnimation::valueChanged, this, [this](const QVariant& v) { setOpacity(v.toDouble()); });
    connect(fade, &QVariantAnimation::finished, this, [this] {
        if (!wanted_) hide();
    });
    fade_ = fade;
    fade->start(QAbstractAnimation::DeleteWhenStopped);
}

void NamePlate::paintEvent(QPaintEvent*) {
    QPainter p(this);
    p.setRenderHint(QPainter::Antialiasing);
    p.setCompositionMode(QPainter::CompositionMode_Source);
    p.fillRect(QRect(0, 0, width(), height()), Qt::transparent);
    p.setCompositionMode(QPainter::CompositionMode_SourceOver);
    const bool dark = look_.dark;
    const double s = look_.diameter, f = textSize(s), h = capsule_.height(), d = dotSize(s);
    const QRectF plate(kMargin, kMargin, capsule_.width(), h);
    const double radius = h / 2;

    // Shadow: black 38 % / 14 %, y 1, blur 6, as a few widening translucent capsules.
    for (int i = 6; i >= 1; --i) {
        QPainterPath shadow;
        shadow.addRoundedRect(plate.adjusted(-i * 0.5, -i * 0.5 + 1, i * 0.5, i * 0.5 + 1), radius + i * 0.5, radius + i * 0.5);
        p.fillPath(shadow, QColor(0, 0, 0, static_cast<int>((dark ? 0.38 : 0.14) * 255 / 9)));
    }
    // Fill: the solid glass (no backdrop blur here).
    QPainterPath capsule;
    capsule.addRoundedRect(plate, radius, radius);
    QLinearGradient fill(plate.topLeft(), plate.bottomLeft());
    fill.setColorAt(0, dark ? QColor(0x1F, 0x24, 0x32) : QColor(0xFF, 0xFF, 0xFF));
    fill.setColorAt(1, dark ? QColor(0x0E, 0x10, 0x16) : QColor(0xF0, 0xF2, 0xF6));
    p.fillPath(capsule, fill);
    // Rim: an inner highlight at the top, a hairline outside.
    QLinearGradient rim(plate.topLeft(), plate.bottomLeft());
    rim.setColorAt(0, QColor(255, 255, 255, dark ? 41 : 242));
    rim.setColorAt(1, QColor(255, 255, 255, dark ? 10 : 0));
    p.setPen(QPen(QBrush(rim), 1));
    p.setBrush(Qt::NoBrush);
    p.drawRoundedRect(plate.adjusted(0.5, 0.5, -0.5, -0.5), radius - 0.5, radius - 0.5);
    p.setPen(QPen(QColor(0, 0, 0, dark ? 89 : 26), 0.5));
    p.drawRoundedRect(plate.adjusted(-0.25, -0.25, 0.25, 0.25), radius + 0.25, radius + 0.25);

    // Dots: overlapping by a third, the first in front, each ringed in the plate color.
    const QColor ring = dark ? QColor(0x16, 0x1A, 0x24) : QColor(0xF8, 0xF9, 0xFB);
    const QColor secondary = dark ? QColor(255, 255, 255, 168) : QColor(0, 0, 0, 153);
    const double cy = plate.center().y();
    double x = plate.left() + h / 2;
    int count = 0;
    if (!look_.spectrum.empty()) {
        QConicalGradient conic(QPointF(x, cy), 90);
        const int n = static_cast<int>(look_.spectrum.size());
        // Clockwise from 12 o'clock: the conical gradient runs counter-clockwise, so the stops go in reverse.
        for (int i = 0; i < n; ++i) conic.setColorAt(1.0 - static_cast<double>(i) / n, look_.spectrum[static_cast<std::size_t>(i)]);
        conic.setColorAt(0, look_.spectrum.front());
        p.setPen(Qt::NoPen);
        p.setBrush(conic);
        p.drawEllipse(QPointF(x, cy), d / 2, d / 2);
        count = 1;
    } else {
        count = std::min<int>(3, static_cast<int>(look_.dots.size()));
        for (int i = count - 1; i >= 0; --i) {
            const auto& dot = look_.dots[static_cast<std::size_t>(i)];
            const QPointF c(x + i * d * 2 / 3, cy);
            p.setPen(Qt::NoPen);
            p.setBrush(ring);
            p.drawEllipse(c, d / 2 + 1, d / 2 + 1);
            if (dot.available) {
                p.setBrush(dark ? dot.color : dot.color.darker(111));
                p.drawEllipse(c, d / 2, d / 2);
            } else {
                const double stroke = std::max(1.0, 0.18 * d);
                p.setBrush(Qt::NoBrush);
                p.setPen(QPen(secondary, stroke));
                p.drawEllipse(c, d / 2 - stroke / 2, d / 2 - stroke / 2);
            }
        }
    }
    double textX = plate.left() + h / 2;
    if (count > 0) textX += (count - 1) * d * 2 / 3 + d / 2 + 0.42 * f;
    else textX = plate.left() + 0.52 * h;
    p.setFont(font());
    const QFontMetricsF fm(font());
    const double baseline = cy + (fm.capHeight() > 0 && !cjk(text_) ? fm.capHeight() / 2 : (fm.ascent() - fm.descent()) / 2);
    p.setPen(dark ? QColor(255, 255, 255, 235) : QColor(0x0B, 0x0D, 0x12, 224));
    p.drawText(QPointF(textX, baseline), text_);
    if (!look_.status.isEmpty()) {
        p.setPen(secondary);
        p.drawText(QPointF(textX + fm.horizontalAdvance(text_), baseline), QStringLiteral(" · ") + look_.status);
    }
}
