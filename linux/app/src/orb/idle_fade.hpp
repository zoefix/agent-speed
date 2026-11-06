#pragma once

#include <QVariantAnimation>
#include <functional>

// DESIGN 2 "fade when idle", one rule for the orb and board windows: 60 s with the window's tier idle (counted from its
// first tick) fades it to 45 % of its opacity; activity, hover, a drag or switching it off brings it back at once, and
// it fades again 1.5 s after the pointer leaves (on the next tick).
class IdleFade {
public:
    static constexpr double kDelay = 60;
    static constexpr double kLeaveDelay = 1.5;
    static constexpr double kFactor = 0.45;

    // The window's state at `now` (seconds on a monotonic clock); true when it fades or comes back.
    bool update(bool enabled, bool idle, bool engaged, double now);
    bool faded() const { return faded_; }

private:
    bool started_ = false;
    bool engaged_ = false;
    bool faded_ = false;
    double lastActive_ = 0;
    double leftAt_ = 0;
};

// The factor a faded window multiplies its opacity by: 0.6 s ease-in-out to 45 %, at once back to 1, a cut both ways
// with Reduce Motion. The window opacity is the compositor's, so the fade draws no frames of the window's own.
class IdleFadeMotion {
public:
    static constexpr int kFadeMs = 600;

    explicit IdleFadeMotion(std::function<void()> apply);
    void setFaded(bool faded, bool reduceMotion = false);
    double factor() const { return factor_; }
    bool running() const { return animation_.state() == QAbstractAnimation::Running; }

private:
    QVariantAnimation animation_;
    std::function<void()> apply_;
    double factor_ = 1;
};
