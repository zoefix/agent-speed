#include "orb/orb_idle.hpp"

void OrbIdle::configure(bool fadeWhenIdle, bool hideWhenIdle, double now) {
    if (hideWhenIdle != hideOn_) {
        fade_.rearm(now);
        hide_.rearm(now);
    }
    fadeOn_ = fadeWhenIdle;
    hideOn_ = hideWhenIdle;
}

bool OrbIdle::update(bool idle, bool engaged, bool onScreen, double now) {
    const bool hide = hide_.update(hideOn_, idle, engaged && onScreen, now);
    const bool fade = fade_.update(fadeOn_ && !hideOn_, idle, engaged, now);
    return hide || fade;
}
