#pragma once

#include <QHostAddress>
#include <QObject>
#include <QTcpServer>
#include <map>
#include <memory>
#include <string>
#include <vector>

#include "agentspeed/lan_frame.hpp"
#include "agentspeed/lan_protocol.hpp"

class LanConnection;

// LAN 4: devices read the live speed over HTTP, a WebSocket stream or the line protocol, all on one TCP port.
class LanServer : public QObject {
    Q_OBJECT
public:
    enum class State { Stopped, Listening, PortInUse, Failed };

    explicit LanServer(QObject* parent = nullptr);
    ~LanServer() override;

    // Every interface, IPv4 and IPv6 (IPv4 only where IPv6 is unavailable); `address` narrows it (tests).
    State start(quint16 port, const QHostAddress& address = QHostAddress::Any);
    // Closes the port and every connection; WebSockets get close 1001 first.
    void stop();
    // A new access key closes every stream (WebSocket 1008) and line connection.
    void setKey(const std::string& key);
    // The frame of a tick: answers /v1/live from now on and goes to the streams by the push rules.
    void publish(const std::shared_ptr<const agentspeed::lan::Frame>& frame, bool changed, qint64 nowMs);
    // The feed stopped: /v1/live answers `starting` again.
    void clearFrame();

    State state() const { return state_; }
    quint16 port() const { return server_.serverPort(); }
    // Distinct peers with an open stream or an authorized request in the last 10 s.
    int devices(qint64 nowMs) const;
    int connections() const { return static_cast<int>(connections_.size()); }

signals:
    void devicesChanged();

private:
    friend class LanConnection;
    void accept();
    void dropped(LanConnection* connection);
    void authorized(const QString& peer, qint64 nowMs);

    QTcpServer server_;
    State state_ = State::Stopped;
    std::vector<LanConnection*> connections_;
    agentspeed::lan::Lockout lockout_;
    std::string key_;
    std::shared_ptr<const agentspeed::lan::Frame> latest_;
    std::string latestWire_;
    std::map<QString, qint64> recent_;  // peer → its last authorized request
};
