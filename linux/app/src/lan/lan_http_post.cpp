#include "lan/lan_http_post.hpp"

#include <QCoreApplication>
#include <QNetworkProxy>
#include <QTcpSocket>

namespace {

constexpr qsizetype kStatusLineLimit = 8192;

}  // namespace

LanHttpPost::LanHttpPost(const QHostAddress& address, quint16 port, QByteArray request, Done done, QObject* parent)
    : QObject(parent), socket_(new QTcpSocket(this)), request_(std::move(request)), done_(std::move(done)) {
    socket_->setProxy(QNetworkProxy::NoProxy);
    timer_.setSingleShot(true);
    connect(&timer_, &QTimer::timeout, this, [this] { finish(std::nullopt); });
    connect(socket_, &QTcpSocket::connected, this, [this] { socket_->write(request_); });
    connect(socket_, &QTcpSocket::readyRead, this, [this] {
        received_ += socket_->readAll();
        if (const auto end = received_.indexOf('\n'); end >= 0) {
            finish(statusOf(received_.left(end)));
        } else if (received_.size() > kStatusLineLimit) {
            finish(std::nullopt);
        }
    });
    connect(socket_, &QAbstractSocket::errorOccurred, this, [this](QAbstractSocket::SocketError) { finish(std::nullopt); });
    connect(socket_, &QTcpSocket::disconnected, this, [this] { finish(std::nullopt); });
    timer_.start(kTimeoutMs);
    socket_->connectToHost(address, port);
}

LanHttpPost::~LanHttpPost() { socket_->disconnect(this); }

void LanHttpPost::cancel() {
    finished_ = true;
    timer_.stop();
    socket_->abort();
}

void LanHttpPost::finish(std::optional<int> status) {
    if (finished_) return;
    finished_ = true;
    timer_.stop();
    socket_->abort();
    if (done_) done_(status);
}

QByteArray LanHttpPost::request(const QByteArray& target, const QByteArray& hostHeader, const QByteArray& body, const QByteArray& signature) {
    QByteArray out = "POST " + target + " HTTP/1.1\r\nHost: " + hostHeader + "\r\nUser-Agent: AgentSpeed/" +
                     QCoreApplication::applicationVersion().toUtf8() + " (Linux)\r\nContent-Type: application/json\r\nContent-Length: " +
                     QByteArray::number(body.size()) + "\r\n";
    if (!signature.isEmpty()) out += "X-AgentSpeed-Signature: " + signature + "\r\n";
    return out + "Connection: close\r\n\r\n" + body;
}

std::optional<int> LanHttpPost::statusOf(const QByteArray& line) {
    const auto parts = line.trimmed().split(' ');
    if (parts.size() < 2 || !(parts[0] == "HTTP/1.1" || parts[0] == "HTTP/1.0") || parts[1].size() != 3) return std::nullopt;
    bool ok = false;
    const int code = parts[1].toInt(&ok);
    return ok && code >= 100 ? std::optional<int>(code) : std::nullopt;
}
