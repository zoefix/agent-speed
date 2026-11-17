#pragma once

#include <QVariantAnimation>
#include <functional>

// The factor an orb hidden when idle (DESIGN 2) multiplies its opacity by: 0.6 s ease-in-out to 0, after which `gone`
// tells the window to leave the screen; back to 1 over 0.12 s ease-out from wherever it is. Reduce Motion cuts both.
// Like the fade it is the compositor's window opacity, so it draws no frames of the window's own.
class IdleHideMotion {
public:
    static constexpr int kHideMs = 600;
    static constexpr int kReturnMs = 120;

    IdleHideMotion(std::function<void()> apply, std::function<void()> gone);
    void setHidden(bool hidden, bool reduceMotion = false);
    double factor() const { return factor_; }
    bool running() const { return animation_.state() == QAbstractAnimation::Running; }
    // Faded out completely: the window is off the screen.
    bool gone() const { return hidden_ && factor_ <= 0 && !running(); }

private:
    QVariantAnimation animation_;
    std::function<void()> apply_;
    std::function<void()> gone_;
    bool hidden_ = false;
    bool configuring_ = false;
    double factor_ = 1;
};
