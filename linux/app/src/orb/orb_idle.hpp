#pragma once

#include "orb/idle_fade.hpp"

// The idle rules of one orb window (DESIGN 2, 2b) on its own idle state: fade when idle and hide when idle. Hiding uses
// the fade's rule (60 s idle, deferred by hover and a drag until 1.5 s after the pointer leaves) and replaces the fade
// when both are on. The pointer brings back an orb only while it is still on the screen (fading out); once off the
// screen only activity, the switch or being shown again does.
class OrbIdle {
public:
    // The orb's switches. Changing hide when idle starts the 60 s again, for the fade too; the next update applies them.
    void configure(bool fadeWhenIdle, bool hideWhenIdle, double now);
    // An engine tick or a pointer change: `idle` is the orb's tier, `engaged` hover or a drag, `onScreen` the window
    // not yet gone. True when faded() or hidden() changed.
    bool update(bool idle, bool engaged, bool onScreen, double now);
    // Its visible or the master switch was turned on: the hide's 60 s start again (the next update brings it back).
    void rearmHide(double now) { hide_.rearm(now); }
    bool faded() const { return fade_.faded(); }
    // Fading out or off the screen.
    bool hidden() const { return hide_.faded(); }

private:
    IdleFade fade_;
    IdleFade hide_;
    bool fadeOn_ = false;
    bool hideOn_ = false;
};
