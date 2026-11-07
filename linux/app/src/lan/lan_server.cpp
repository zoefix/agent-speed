#include "lan/lan_server.hpp"

#include <QNetworkProxy>
#include <QTcpSocket>
#include <set>

#include "lan/lan_connection.hpp"
#include "lan/lan_net.hpp"

using namespace agentspeed::lan;

namespace {

constexpr qint64 kRecentMs = 10'000;

}  // namespace

LanServer::LanServer(QObject* parent) : QObject(parent) {
    server_.setProxy(QNetworkProxy::NoProxy);
    connect(&server_, &QTcpServer::newConnection, this, &LanServer::accept);
}

LanServer::~LanServer() {
    const auto open = connections_;
    for (auto* c : open) {
        c->disconnect(this);
        c->finish(1001);
    }
}

LanServer::State LanServer::start(quint16 port, const QHostAddress& address) {
    if (server_.isListening()) stop();
    bool ok = server_.listen(address, port);
    if (!ok && address == QHostAddress(QHostAddress::Any) && server_.serverError() != QAbstractSocket::AddressInUseError) {
        ok = server_.listen(QHostAddress::AnyIPv4, port);
    }
    if (ok) {
        state_ = State::Listening;
        qCInfo(lanLog) << "lan: listening on port" << server_.serverPort();
    } else {
        state_ = server_.serverError() == QAbstractSocket::AddressInUseError ? State::PortInUse : State::Failed;
        qCWarning(lanLog).noquote() << "lan: cannot listen on port" << port << "-" << server_.errorString();
    }
    return state_;
}

void LanServer::stop() {
    const auto open = connections_;
    for (auto* c : open) c->finish(1001);
    if (server_.isListening()) qCInfo(lanLog) << "lan: stopped listening on port" << server_.serverPort();
    server_.close();
    state_ = State::Stopped;
    recent_.clear();
    emit devicesChanged();
}

void LanServer::setKey(const std::string& key) {
    if (key == key_) return;
    const bool replaced = !key_.empty();
    key_ = key;
    if (!replaced) return;
    const auto open = connections_;
    for (auto* c : open) {
        if (c->streaming()) c->finish(1008);
    }
    recent_.clear();
    emit devicesChanged();
}

void LanServer::publish(const std::shared_ptr<const Frame>& frame, bool changed, qint64 nowMs) {
    latest_ = frame;
    latestWire_ = frame ? frame->wire() : std::string();
    if (!frame) return;
    const auto open = connections_;
    for (auto* c : open) c->push(latestWire_, changed, nowMs);
}

void LanServer::clearFrame() {
    latest_.reset();
    latestWire_.clear();
}

int LanServer::devices(qint64 nowMs) const {
    std::set<QString> peers;
    for (const auto* c : connections_) {
        if (c->streaming()) peers.insert(c->peer());
    }
    for (const auto& [peer, at] : recent_) {
        if (nowMs - at < kRecentMs) peers.insert(peer);
    }
    return static_cast<int>(peers.size());
}

void LanServer::accept() {
    while (server_.hasPendingConnections()) {
        auto* socket = server_.nextPendingConnection();
        const auto peer = socket->peerAddress();
        const char* refusal = !lan_net::isLocal(peer) ? "not a local address"
                              : connections_.size() >= static_cast<std::size_t>(kMaxConnections) ? "16 connections are open"
                                                                                                : nullptr;
        if (refusal) {
            qCInfo(lanLog).noquote() << "lan:" << lan_net::peerKey(peer) << "refused:" << refusal;
            socket->abort();
            socket->deleteLater();
            continue;
        }
        auto* connection = new LanConnection(socket, *this);
        connect(connection, &LanConnection::closed, this, &LanServer::dropped);
        connections_.push_back(connection);
    }
}

void LanServer::dropped(LanConnection* connection) {
    std::erase(connections_, connection);
    emit devicesChanged();
}

void LanServer::authorized(const QString& peer, qint64 nowMs) {
    std::erase_if(recent_, [&](const auto& entry) { return nowMs - entry.second >= kRecentMs; });
    recent_[peer] = nowMs;
    emit devicesChanged();
}
