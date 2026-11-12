#pragma once

#include <QObject>

namespace orb {

// Whether the screen saver or the lock screen is up (the session bus's `org.freedesktop.ScreenSaver` and
// `org.gnome.ScreenSaver` `ActiveChanged` signals, and `GetActive` at start): a living character does not read the
// pointer meanwhile (CHARACTERS 5, Behavior, Inputs). Without such a service it is never locked.
class ScreenLock : public QObject {
    Q_OBJECT
public:
    static ScreenLock& shared();
    bool locked() const { return locked_; }

private slots:
    void activeChanged(bool active) { locked_ = active; }

private:
    ScreenLock();
    bool locked_ = false;
};

}  // namespace orb
