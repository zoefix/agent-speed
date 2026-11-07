#pragma once

#include <QByteArray>
#include <QObject>
#include <QTimer>
#include <optional>
#include <string>

#include "agentspeed/lan_push.hpp"

class QTcpSocket;
class LanServer;

// One accepted connection (LAN 4.2–4.6): the first line decides HTTP or the line protocol; an upgraded request becomes a
// WebSocket stream. Frames follow the push rules of 4.7 and are never queued.
class LanConnection : public QObject {
    Q_OBJECT
public:
    enum class Mode { Sniff, Http, WebSocket, Lines, Closing };

    LanConnection(QTcpSocket* socket, LanServer& server);
    ~LanConnection() override;

    Mode mode() const { return mode_; }
    bool streaming() const { return mode_ == Mode::WebSocket || mode_ == Mode::Lines; }
    const QString& peer() const { return peer_; }
    // A tick's frame for a stream.
    void push(const std::string& wire, bool changed, qint64 nowMs);
    // Ends the connection: a WebSocket gets `wsCode` first.
    void finish(std::optional<int> wsCode);

signals:
    void closed(LanConnection* connection);

private:
    void read();
    void sniff();
    void http();
    void lines();
    void frames();
    void send(const std::string& wire, qint64 nowMs);
    void write(const std::string& bytes);
    void endAfterWrite();
    void drop();

    QTcpSocket* socket_;
    LanServer& server_;
    QString peer_;
    Mode mode_ = Mode::Sniff;
    QByteArray buffer_;
    QTimer deadline_;
    agentspeed::lan::PushScheduler scheduler_;
    bool done_ = false;
};
