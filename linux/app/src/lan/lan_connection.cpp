#include "lan/lan_connection.hpp"

#include <QTcpSocket>

#include "agentspeed/lan_protocol.hpp"
#include "agentspeed/lan_websocket.hpp"
#include "lan/lan_net.hpp"
#include "lan/lan_server.hpp"

using namespace agentspeed::lan;

namespace {

constexpr qint64 kUnsentLimit = 65'536;
constexpr int kCloseGraceMs = 2000;

std::string_view view(const QByteArray& bytes) { return {bytes.constData(), static_cast<std::size_t>(bytes.size())}; }

}  // namespace

LanConnection::LanConnection(QTcpSocket* socket, LanServer& server)
    : QObject(&server), socket_(socket), server_(server), peer_(lan_net::peerKey(socket->peerAddress())),
      scheduler_(pushRule(Receiver::Stream)) {
    socket_->setParent(this);
    // Enough for a request head or a client frame; the rest waits in the system's buffer.
    socket_->setReadBufferSize(16'384);
    deadline_.setSingleShot(true);
    connect(&deadline_, &QTimer::timeout, this, [this] {
        qCDebug(lanLog) << "lan:" << peer_ << "closed: nothing within 5 s";
        drop();
    });
    deadline_.start(static_cast<int>(kHeadTimeoutMs));
    connect(socket_, &QTcpSocket::readyRead, this, &LanConnection::read);
    connect(socket_, &QTcpSocket::disconnected, this, &LanConnection::drop);
    connect(socket_, &QAbstractSocket::errorOccurred, this, [this](QAbstractSocket::SocketError) { drop(); });
    if (socket_->bytesAvailable() > 0) QMetaObject::invokeMethod(this, &LanConnection::read, Qt::QueuedConnection);
}

LanConnection::~LanConnection() { socket_->disconnect(this); }

void LanConnection::read() {
    if (done_) return;
    if (mode_ == Mode::Lines || mode_ == Mode::Closing) {
        socket_->readAll();
        return;
    }
    buffer_ += socket_->readAll();
    switch (mode_) {
    case Mode::Sniff: sniff(); break;
    case Mode::Http: http(); break;
    case Mode::WebSocket: frames(); break;
    default: break;
    }
}

void LanConnection::sniff() {
    switch (classifyFirstLine(view(buffer_))) {
    case FirstLine::Pending: return;
    case FirstLine::Close:
        qCDebug(lanLog) << "lan:" << peer_ << "closed: not a request";
        drop();
        return;
    case FirstLine::Http:
        mode_ = Mode::Http;
        http();
        return;
    case FirstLine::Line: lines(); return;
    }
}

void LanConnection::http() {
    const auto parsed = parseHead(view(buffer_));
    if (parsed.state == HeadState::Pending) return;
    if (parsed.state == HeadState::Invalid) {
        qCDebug(lanLog) << "lan:" << peer_ << "closed: request head";
        drop();
        return;
    }
    deadline_.stop();
    const auto now = lan_net::steadyMs();
    const auto response = respond(parsed.head, peer_.toStdString(), now, server_.key_, server_.latest_ != nullptr, server_.lockout_);
    qCInfo(lanLog).noquote() << "lan:" << peer_ << "http" << response.status << QString::fromStdString(parsed.head.path());
    if (response.authorized) server_.authorized(peer_, now);
    if (response.status != 101) {
        write(responseBytes(response, server_.latestWire_));
        endAfterWrite();
        return;
    }
    write(responseBytes(response, {}));
    mode_ = Mode::WebSocket;
    buffer_.remove(0, static_cast<qsizetype>(parsed.length));
    emit server_.devicesChanged();
    if (server_.latest_) send(server_.latestWire_, now);
    frames();
}

void LanConnection::lines() {
    deadline_.stop();
    auto line = buffer_.left(buffer_.indexOf('\n'));
    if (line.endsWith('\r')) line.chop(1);
    const auto now = lan_net::steadyMs();
    if (const auto error = hello(view(line), peer_.toStdString(), now, server_.key_, server_.lockout_)) {
        qCInfo(lanLog).noquote() << "lan:" << peer_ << "line" << QString::fromStdString(*error);
        write(errorBody(*error) + "\n");
        endAfterWrite();
        return;
    }
    qCInfo(lanLog).noquote() << "lan:" << peer_ << "line hello";
    mode_ = Mode::Lines;
    buffer_.clear();
    emit server_.devicesChanged();
    if (server_.latest_) send(server_.latestWire_, now);
}

void LanConnection::frames() {
    while (!done_ && mode_ == Mode::WebSocket) {
        const auto frame = ws::clientFrame(view(buffer_));
        using Action = ws::ClientFrame::Action;
        switch (frame.action) {
        case Action::Incomplete: return;
        case Action::Close:
        case Action::EchoClose:
            if (frame.action == Action::Close) qCInfo(lanLog) << "lan:" << peer_ << "websocket close" << *frame.code;
            write(ws::close(frame.code));
            endAfterWrite();
            return;
        case Action::Pong: write(ws::frame(ws::kPong, frame.payload)); break;
        case Action::Ignore: break;
        }
        buffer_.remove(0, static_cast<qsizetype>(frame.consumed));
    }
}

void LanConnection::push(const std::string& wire, bool changed, qint64 nowMs) {
    if (done_ || !streaming()) return;
    if (socket_->bytesToWrite() > kUnsentLimit) {
        qCInfo(lanLog) << "lan:" << peer_ << "closed: 64 KiB not sent";
        drop();
        return;
    }
    if (scheduler_.due(nowMs, changed) && socket_->bytesToWrite() == 0) send(wire, nowMs);
}

void LanConnection::send(const std::string& wire, qint64 nowMs) {
    write(mode_ == Mode::WebSocket ? ws::frame(ws::kText, wire) : wire + "\n");
    scheduler_.sent(nowMs);
}

void LanConnection::write(const std::string& bytes) { socket_->write(bytes.data(), static_cast<qint64>(bytes.size())); }

void LanConnection::finish(std::optional<int> wsCode) {
    if (done_) return;
    if (mode_ == Mode::WebSocket && wsCode) write(ws::close(*wsCode));
    if (mode_ == Mode::WebSocket || mode_ == Mode::Lines || mode_ == Mode::Closing) {
        socket_->flush();
        endAfterWrite();
    } else {
        drop();
    }
}

void LanConnection::endAfterWrite() {
    const bool wasStreaming = streaming();
    mode_ = Mode::Closing;
    if (wasStreaming) emit server_.devicesChanged();
    QTimer::singleShot(kCloseGraceMs, this, &LanConnection::drop);
    socket_->disconnectFromHost();
}

void LanConnection::drop() {
    if (done_) return;
    done_ = true;
    deadline_.stop();
    socket_->abort();
    emit closed(this);
    deleteLater();
}
