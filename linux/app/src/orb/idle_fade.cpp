#include "orb/idle_fade.hpp"

bool IdleFade::update(bool enabled, bool idle, bool engaged, double now) {
    if (!started_ || !idle) lastActive_ = now;
    if (!started_) leftAt_ = now - kLeaveDelay;
    started_ = true;
    if (engaged != engaged_) {
        engaged_ = engaged;
        if (!engaged) leftAt_ = now;
    }
    const bool faded = enabled && idle && !engaged && now - lastActive_ >= kDelay && now - leftAt_ >= kLeaveDelay;
    if (faded == faded_) return false;
    faded_ = faded;
    return true;
}

IdleFadeMotion::IdleFadeMotion(std::function<void()> apply) : apply_(std::move(apply)) {
    animation_.setDuration(kFadeMs);
    animation_.setEasingCurve(QEasingCurve::InOutCubic);
    QObject::connect(&animation_, &QVariantAnimation::valueChanged, [this](const QVariant& value) {
        factor_ = value.toDouble();
        apply_();
    });
}

void IdleFadeMotion::setFaded(bool faded, bool reduceMotion) {
    const double target = faded ? IdleFade::kFactor : 1.0;
    animation_.stop();
    if (faded && !reduceMotion && factor_ > target) {
        animation_.setStartValue(factor_);
        animation_.setEndValue(target);
        animation_.start();
        return;
    }
    if (factor_ == target) return;
    factor_ = target;
    apply_();
}
