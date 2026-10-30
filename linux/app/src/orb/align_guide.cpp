#include "orb/align_guide.hpp"

#include <QPainter>
#include <QSurfaceFormat>

namespace orb {

namespace {
constexpr int kPad = 2;
}

AlignGuide::AlignGuide() {
    QSurfaceFormat format;
    format.setAlphaBufferSize(8);
    setFormat(format);
    setFlags(Qt::Tool | Qt::FramelessWindowHint | Qt::NoDropShadowWindowHint | Qt::WindowDoesNotAcceptFocus |
             Qt::WindowStaysOnTopHint | Qt::WindowTransparentForInput);
}

void AlignGuide::show(const std::vector<QLineF>& lines, bool dark) {
    if (lines.empty()) {
        clear();
        return;
    }
    QRectF bounds;
    for (const auto& l : lines) bounds |= QRectF(l.p1(), l.p2()).normalized().adjusted(-kPad, -kPad, kPad, kPad);
    const QRect rect = bounds.toAlignedRect();
    lines_.clear();
    for (const auto& l : lines) lines_.push_back(l.translated(-rect.topLeft()));
    dark_ = dark;
    setGeometry(rect);
    if (!isVisible()) QRasterWindow::show();
    update();
}

void AlignGuide::clear() {
    lines_.clear();
    if (isVisible()) hide();
}

void AlignGuide::paintEvent(QPaintEvent*) {
    QPainter p(this);
    p.setCompositionMode(QPainter::CompositionMode_Source);
    p.fillRect(QRect(0, 0, width(), height()), Qt::transparent);
    p.setCompositionMode(QPainter::CompositionMode_SourceOver);
    QColor accent = dark_ ? QColor(0x5B, 0x8C, 0xFF) : QColor(0x2F, 0x6B, 0xFF);
    accent.setAlphaF(0.6f);
    QPen pen(accent, 1);
    pen.setDashPattern({4, 3});
    pen.setCapStyle(Qt::FlatCap);
    p.setPen(pen);
    for (const auto& l : lines_) p.drawLine(l);
}

}  // namespace orb
