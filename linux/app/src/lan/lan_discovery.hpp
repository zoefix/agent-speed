#pragma once

#include <QDBusObjectPath>
#include <QObject>
#include <QString>

// LAN 4.1 Discovery: `_agentspeed._tcp` on the port, through the Avahi daemon over D-Bus while it runs. A failure is
// logged and changes nothing else; on a name conflict Avahi's alternative name is taken.
class LanDiscovery : public QObject {
    Q_OBJECT
public:
    explicit LanDiscovery(QObject* parent = nullptr);
    ~LanDiscovery() override;

    void publish(const QString& name, quint16 port, const QString& instance);
    void withdraw();
    bool published() const { return !group_.path().isEmpty(); }

    // The instance name: cut to 63 UTF-8 bytes at a code point boundary.
    static QString serviceName(const QString& name);

private slots:
    void stateChanged(int state, const QString& error);

private:
    bool add();

    QDBusObjectPath group_;
    QString name_, instance_;
    quint16 port_ = 0;
    int renames_ = 0;
};
