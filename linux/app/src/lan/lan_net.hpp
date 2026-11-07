#pragma once

#include <QHostAddress>
#include <QLoggingCategory>
#include <QString>

// The local network feed's logging (LAN 6: peer addresses, protocols and status codes; never a key, a frame or a query)
// and address helpers shared by the server and the targets.
Q_DECLARE_LOGGING_CATEGORY(lanLog)

namespace lan_net {

// An IPv4-mapped address as IPv4, without an IPv6 scope.
QHostAddress plain(const QHostAddress& address);
// The key a peer is counted and locked out by.
QString peerKey(const QHostAddress& address);
// LAN 2.3 for a socket address; `udp` also accepts the limited broadcast.
bool isLocal(const QHostAddress& address, bool udp = false);
// The monotonic clock of the engine ticks (steadyMs of the monitor worker).
qint64 steadyMs();

}  // namespace lan_net
