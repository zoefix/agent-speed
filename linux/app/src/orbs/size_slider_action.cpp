#include "orbs/size_slider_action.hpp"

#include <QHBoxLayout>
#include <QKeyEvent>
#include <QLabel>
#include <QSlider>
#include <QStyle>
#include <algorithm>
#include <cmath>

namespace {

// ↑ / ↓ belong to the menu, so the row never traps the keyboard; the rest is the slider's.
class MenuSlider : public QSlider {
public:
    using QSlider::QSlider;

protected:
    void keyPressEvent(QKeyEvent* event) override {
        if (event->key() == Qt::Key_Up || event->key() == Qt::Key_Down) {
            event->ignore();
            return;
        }
        QSlider::keyPressEvent(event);
    }
};

}  // namespace

int SizeSliderAction::snap(double size) {
    if (!std::isfinite(size)) return kMin;
    const int stepped = kMin + static_cast<int>(std::lround((size - kMin) / kStep)) * kStep;
    return std::clamp(stepped, kMin, kMax);
}

SizeSliderAction::SizeSliderAction(double size, const QString& accessibleName, QObject* parent)
    : QWidgetAction(parent), size_(snap(size)) {
    auto* row = new QWidget;
    auto* layout = new QHBoxLayout(row);
    // The track starts in the label column of the checkable rows above and the value ends with the separator.
    const int indent = row->style()->pixelMetric(QStyle::PM_MenuHMargin) + row->style()->pixelMetric(QStyle::PM_IndicatorWidth) + 10;
    layout->setContentsMargins(indent, 4, 14, 4);
    layout->setSpacing(10);

    slider_ = new MenuSlider(Qt::Horizontal, row);
    slider_->setRange(kMin, kMax);
    slider_->setSingleStep(kStep);
    slider_->setPageStep(6 * kStep);
    slider_->setValue(size_);
    slider_->setMinimumWidth(150);
    slider_->setAccessibleName(accessibleName);

    value_ = new QLabel(row);
    // Secondary text: the menu's text color at 62 %, legible on light and dark palettes alike.
    QPalette secondary = value_->palette();
    QColor ink = secondary.color(QPalette::WindowText);
    ink.setAlphaF(0.62f);
    secondary.setColor(QPalette::WindowText, ink);
    value_->setPalette(secondary);
    value_->setAlignment(Qt::AlignRight | Qt::AlignVCenter);
    value_->setMinimumWidth(value_->fontMetrics().horizontalAdvance(QStringLiteral("240 pt")));

    layout->addWidget(slider_, 1);
    layout->addWidget(value_);
    setDefaultWidget(row);
    show(size_);

    connect(slider_, &QSlider::valueChanged, this, [this](int value) {
        const int stepped = snap(value);
        if (stepped != value) {
            slider_->setValue(stepped);
            return;
        }
        if (stepped == size_) return;
        size_ = stepped;
        show(stepped);
        emit sizeChanged(stepped);
    });
}

void SizeSliderAction::show(int size) {
    const QString text = QStringLiteral("%1 pt").arg(size);
    value_->setText(text);
    slider_->setAccessibleDescription(text);
}
