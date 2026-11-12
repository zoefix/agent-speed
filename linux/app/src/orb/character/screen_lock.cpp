#include "orb/character/screen_lock.hpp"

#include <QDBusConnection>
#include <QDBusMessage>
#include <QDBusPendingCallWatcher>
#include <QDBusPendingReply>

namespace orb {

ScreenLock& ScreenLock::shared() {
    static auto* lock = new ScreenLock;
    return *lock;
}

ScreenLock::ScreenLock() {
    auto bus = QDBusConnection::sessionBus();
    if (!bus.isConnected()) return;
    for (const char* interface : {"org.freedesktop.ScreenSaver", "org.gnome.ScreenSaver"}) {
        bus.connect(QString(), QString(), QString::fromLatin1(interface), QStringLiteral("ActiveChanged"), this, SLOT(activeChanged(bool)));
    }
    const auto ask = QDBusMessage::createMethodCall(QStringLiteral("org.freedesktop.ScreenSaver"), QStringLiteral("/org/freedesktop/ScreenSaver"),
                                                    QStringLiteral("org.freedesktop.ScreenSaver"), QStringLiteral("GetActive"));
    auto* watcher = new QDBusPendingCallWatcher(bus.asyncCall(ask, 2000), this);
    connect(watcher, &QDBusPendingCallWatcher::finished, this, [this](QDBusPendingCallWatcher* w) {
        const QDBusPendingReply<bool> reply = *w;
        if (reply.isValid()) locked_ = reply.value();
        w->deleteLater();
    });
}

}  // namespace orb
