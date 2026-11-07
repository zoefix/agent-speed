#pragma once

#include <QByteArray>
#include <QHostAddress>
#include <QObject>
#include <QTimer>
#include <functional>
#include <optional>

class QTcpSocket;

// LAN 5.3: one POST over a raw TCP connection to a checked address — no proxy, no redirect, no TLS. Only the status
// line is read; no status line within 3 s is unreachable.
class LanHttpPost : public QObject {
    Q_OBJECT
public:
    static constexpr int kTimeoutMs = 3000;
    // `done` gets the status code, or nullopt when unreachable.
    using Done = std::function<void(std::optional<int>)>;

    LanHttpPost(const QHostAddress& address, quint16 port, QByteArray request, Done done, QObject* parent = nullptr);
    ~LanHttpPost() override;
    void cancel();

    // The request of 5.3 for a body, with the signature header when there is one.
    static QByteArray request(const QByteArray& target, const QByteArray& hostHeader, const QByteArray& body, const QByteArray& signature);
    // `HTTP/1.x NNN …` → NNN.
    static std::optional<int> statusOf(const QByteArray& line);

private:
    void finish(std::optional<int> status);

    QTcpSocket* socket_;
    QByteArray request_, received_;
    QTimer timer_;
    Done done_;
    bool finished_ = false;
};
