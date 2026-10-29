#pragma once

#include <QObject>
#include <QTimer>
#include <QWebSocket>
#include <memory>

#include "agentspeed/report.hpp"

// One WebSocket connection of the report stream (METRICS 11.9 Connection): the signed upgrade with the subprotocol, no
// compression, cookies or redirects; Pings every pingIntervalSeconds and an abort when no Pong comes back in time.
class ReportSocket : public QObject {
    Q_OBJECT
public:
    explicit ReportSocket(QObject* parent = nullptr);
    ~ReportSocket() override;
    void open(const QString& url, const agentspeed::report::Headers& headers, int pingSeconds, int pongTimeoutSeconds);
    void send(const QString& message);
    void close();  // close 1000
    void abort();  // drops the socket without a close handshake
    bool active() const { return static_cast<bool>(socket_); }

signals:
    void opened();                                       // 101 with the subprotocol
    void message(const QByteArray& text, bool binary);  // a text (or binary) message
    void closed(int code);                               // the service or the network ended it
    void refused(int status);                            // a handshake answered without 101 (0: no answer)

private:
    void finish();
    std::unique_ptr<QWebSocket> socket_;
    QTimer ping_, pong_;
    bool upgraded_ = false;
};
