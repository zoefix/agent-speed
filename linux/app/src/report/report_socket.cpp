#include "report/report_socket.hpp"

#include <QNetworkRequest>
#include <QRegularExpression>
#include <QWebSocketHandshakeOptions>

#include "agentspeed/report_stream.hpp"

ReportSocket::ReportSocket(QObject* parent) : QObject(parent) {
    ping_.setSingleShot(false);
    pong_.setSingleShot(true);
    connect(&ping_, &QTimer::timeout, this, [this] {
        if (!socket_) return;
        socket_->ping();
        if (!pong_.isActive()) pong_.start();
    });
    connect(&pong_, &QTimer::timeout, this, [this] {
        // Two Ping intervals without an answer: abort, a failure (1006).
        abort();
        emit closed(1006);
    });
}

ReportSocket::~ReportSocket() { abort(); }

void ReportSocket::open(const QString& url, const agentspeed::report::Headers& headers, int pingSeconds, int pongTimeoutSeconds) {
    abort();
    upgraded_ = false;
    socket_ = std::make_unique<QWebSocket>(QString(), QWebSocketProtocol::VersionLatest);
    auto* socket = socket_.get();
    QNetworkRequest request{QUrl(url)};
    for (const auto& [name, value] : headers) request.setRawHeader(QByteArray::fromStdString(name), QByteArray::fromStdString(value));
    request.setRawHeader("Accept-Language", "*");
    QWebSocketHandshakeOptions options;
    options.setSubprotocols({QString::fromLatin1(agentspeed::report::stream::kSubprotocol.data(), static_cast<int>(agentspeed::report::stream::kSubprotocol.size()))});
    ping_.setInterval(pingSeconds * 1000);
    pong_.setInterval(pongTimeoutSeconds * 1000);
    connect(socket, &QWebSocket::connected, this, [this, socket] {
        if (socket != socket_.get()) return;
        if (socket->subprotocol() != QLatin1String(agentspeed::report::stream::kSubprotocol.data())) {
            // The 101 must select the subprotocol, else the connection counts as close 1002.
            abort();
            emit closed(1002);
            return;
        }
        upgraded_ = true;
        ping_.start();
        emit opened();
    });
    connect(socket, &QWebSocket::textMessageReceived, this, [this, socket](const QString& text) {
        if (socket == socket_.get()) emit message(text.toUtf8(), false);
    });
    connect(socket, &QWebSocket::binaryMessageReceived, this, [this, socket](const QByteArray& data) {
        if (socket == socket_.get()) emit message(data, true);
    });
    connect(socket, &QWebSocket::pong, this, [this, socket] {
        if (socket == socket_.get()) pong_.stop();
    });
    const auto ended = [this, socket] {
        if (socket != socket_.get()) return;
        const int code = socket->closeCode();
        const bool upgraded = upgraded_;
        const auto reason = socket->errorString();
        finish();
        if (upgraded) {
            emit closed(code == 0 ? 1006 : code);
            return;
        }
        // A refused upgrade: Qt reports the status only in its error text.
        static const QRegularExpression status(QStringLiteral("(?:status code|Status|code)[^0-9]{0,4}([1-5][0-9][0-9])"));
        const auto match = status.match(reason);
        emit refused(match.hasMatch() ? match.captured(1).toInt() : 0);
    };
    connect(socket, &QWebSocket::disconnected, this, ended);
    connect(socket, &QWebSocket::errorOccurred, this, [this, ended](QAbstractSocket::SocketError) {
        if (!upgraded_) ended();
    });
    socket->open(request, options);
}

void ReportSocket::send(const QString& message) {
    if (socket_ && upgraded_) socket_->sendTextMessage(message);
}

void ReportSocket::finish() {
    ping_.stop();
    pong_.stop();
    if (socket_) {
        socket_->disconnect(this);
        socket_.release()->deleteLater();
    }
    upgraded_ = false;
}

void ReportSocket::close() {
    if (!socket_) return;
    auto* socket = socket_.get();
    finish();
    socket->close(QWebSocketProtocol::CloseCodeNormal);
}

void ReportSocket::abort() {
    if (!socket_) return;
    auto* socket = socket_.get();
    finish();
    socket->abort();
}
