#include "orb/idle_hide.hpp"

IdleHideMotion::IdleHideMotion(std::function<void()> apply, std::function<void()> gone) : apply_(std::move(apply)), gone_(std::move(gone)) {
    QObject::connect(&animation_, &QVariantAnimation::valueChanged, [this](const QVariant& value) {
        if (configuring_) return;
        factor_ = value.toDouble();
        apply_();
    });
    QObject::connect(&animation_, &QAbstractAnimation::finished, [this] {
        if (hidden_) gone_();
    });
}

void IdleHideMotion::setHidden(bool hidden, bool reduceMotion) {
    if (hidden == hidden_) return;
    hidden_ = hidden;
    animation_.stop();
    const double target = hidden ? 0.0 : 1.0;
    if (!reduceMotion && factor_ != target) {
        // A stopped animation reports values while it is set up again; the factor stays where it is until it starts.
        configuring_ = true;
        animation_.setDuration(hidden ? kHideMs : kReturnMs);
        animation_.setEasingCurve(hidden ? QEasingCurve::InOutCubic : QEasingCurve::OutCubic);
        animation_.setStartValue(factor_);
        animation_.setEndValue(target);
        configuring_ = false;
        animation_.start();
        return;
    }
    if (factor_ != target) {
        factor_ = target;
        apply_();
    }
    if (hidden) gone_();
}
