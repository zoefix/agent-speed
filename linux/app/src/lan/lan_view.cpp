#include <QClipboard>
#include <QDateTime>
#include <QGuiApplication>
#include <QNetworkInterface>

#include "agentspeed/lan_address.hpp"
#include "lan/lan_controller.hpp"
#include "lan/lan_net.hpp"
#include "strings.hpp"

// What DESIGN 14 shows: the server's status line, the key, this computer's addresses, the target rows and the sheet's
// address checks.
using namespace agentspeed::lan;

namespace {

constexpr int kMaxAddresses = 3;

QString kindLabel(TargetKind kind) { return kind == TargetKind::Udp ? QStringLiteral("UDP") : QStringLiteral("HTTP"); }

}  // namespace

QString LanController::t(const char* key) const { return strings_.t(QString::fromLatin1(key)); }

QString LanController::serverDot() const {
    if (!networkEnabled()) return QStringLiteral("idle");
    switch (server_.state()) {
    case LanServer::State::Listening: return server_.devices(lan_net::steadyMs()) > 0 ? QStringLiteral("connected") : QStringLiteral("progress");
    case LanServer::State::PortInUse: return QStringLiteral("retrying");
    case LanServer::State::Failed: return QStringLiteral("attention");
    case LanServer::State::Stopped: return QStringLiteral("idle");
    }
    return QStringLiteral("idle");
}

QString LanController::serverLine() const {
    if (!networkEnabled()) return t("lan.server.status.networkOff");
    switch (server_.state()) {
    case LanServer::State::Listening: {
        const int devices = server_.devices(lan_net::steadyMs());
        if (devices == 0) return t("lan.server.status.listening");
        return strings_.fmt(devices == 1 ? QStringLiteral("lan.server.status.connected.one") : QStringLiteral("lan.server.status.connected"), {devices});
    }
    case LanServer::State::PortInUse: return strings_.fmt(QStringLiteral("lan.server.status.portInUse"), {settings_.server.port});
    case LanServer::State::Failed: return t("lan.server.status.failed");
    case LanServer::State::Stopped: return t("lan.server.status.listening");
    }
    return {};
}

QString LanController::keyDisplay() const { return settings_.server.key ? QString::fromStdString(displayKey(*settings_.server.key)) : QString(); }

QString LanController::keySpoken() const { return keyDisplay().replace(QLatin1Char('-'), QStringLiteral(", ")); }

QString LanController::dotOf(TargetStatus status) {
    switch (status) {
    case TargetStatus::Ok: return QStringLiteral("connected");
    case TargetStatus::Unreachable:
    case TargetStatus::HttpStatus:
    case TargetStatus::Failed: return QStringLiteral("retrying");
    case TargetStatus::NotLocal:
    case TargetStatus::NotFound: return QStringLiteral("attention");
    case TargetStatus::Waiting: return QStringLiteral("progress");
    case TargetStatus::Off:
    case TargetStatus::NetworkOff: return QStringLiteral("idle");
    }
    return QStringLiteral("idle");
}

QString LanController::statusLine(const LanTargets::Status& status, qint64 nowMs, qint64 wallMs) const {
    switch (status.status) {
    case TargetStatus::Ok: {
        const auto seconds = std::max<qint64>(0, (wallMs - status.lastSuccessWallMs) / 1000);
        const auto ago = seconds < 5 ? t("time.justNow")
                         : seconds < 60 ? strings_.fmt(QStringLiteral("time.secondsAgo"), {seconds})
                                        : strings_.fmt(QStringLiteral("time.minutesAgo"), {seconds / 60});
        return strings_.fmt(QStringLiteral("lan.target.status.ok"), {ago});
    }
    case TargetStatus::Waiting: return t("lan.target.status.waiting");
    case TargetStatus::NotFound: return t("lan.target.status.notFound");
    case TargetStatus::NotLocal: return t("lan.target.status.notLocal");
    case TargetStatus::Unreachable:
        return strings_.fmt(QStringLiteral("lan.target.status.unreachable"), {std::max<qint64>(1, (status.retryAtMs - nowMs + 999) / 1000)});
    case TargetStatus::HttpStatus: return strings_.fmt(QStringLiteral("lan.target.status.httpStatus"), {status.httpCode});
    case TargetStatus::Failed: return t("lan.target.status.failed");
    case TargetStatus::NetworkOff: return t("lan.target.status.networkOff");
    case TargetStatus::Off: return t("lan.target.status.off");
    }
    return {};
}

QVariantList LanController::targets() const {
    QVariantList rows;
    const auto now = lan_net::steadyMs();
    const auto wall = QDateTime::currentMSecsSinceEpoch();
    for (const auto& target : settings_.targets) {
        const auto status = targets_.status(target.id);
        const auto address = QString::fromStdString(target.address);
        QVariantMap row;
        row.insert(QStringLiteral("targetId"), QString::fromStdString(target.id));
        row.insert(QStringLiteral("title"), target.name.empty() ? address : QString::fromStdString(target.name));
        row.insert(QStringLiteral("kind"), QString::fromLatin1(kindName(target.kind).data()));
        // The address is already the title when there is no name (DESIGN 14).
        row.insert(QStringLiteral("detail"), target.name.empty() ? kindLabel(target.kind)
                                                                 : kindLabel(target.kind) + QStringLiteral(" · ") + address);
        row.insert(QStringLiteral("targetEnabled"), target.enabled);
        row.insert(QStringLiteral("dot"), dotOf(status.status));
        row.insert(QStringLiteral("line"), statusLine(status, now, wall));
        rows.push_back(row);
    }
    return rows;
}

QVariantMap LanController::target(const QString& id) const {
    for (const auto& target : settings_.targets) {
        if (target.id != id.toStdString()) continue;
        return {{QStringLiteral("targetId"), id},
                {QStringLiteral("kind"), QString::fromLatin1(kindName(target.kind).data())},
                {QStringLiteral("address"), QString::fromStdString(target.address)},
                {QStringLiteral("name"), QString::fromStdString(target.name)},
                {QStringLiteral("key"), target.key ? QString::fromStdString(*target.key) : QString()},
                {QStringLiteral("title"), QString::fromStdString(target.name.empty() ? target.address : target.name)}};
    }
    return {};
}

QString LanController::addressError(const QString& kind, const QString& address) const {
    const auto parsedKind = kindFromName(kind.toStdString());
    if (!parsedKind) return t("lan.target.address.invalid");
    const auto result = parseTarget(*parsedKind, address.trimmed().toStdString());
    if (std::holds_alternative<TargetAddress>(result)) return {};
    const auto& reason = std::get<std::string>(result);
    if (reason == "port") return t("lan.target.address.port");
    if (reason == "https") return t("lan.target.address.https");
    return t("lan.target.address.invalid");
}

bool LanController::broadcast(const QString& kind, const QString& address) const {
    if (kind != QLatin1String("udp")) return false;
    const auto result = parseTarget(TargetKind::Udp, address.trimmed().toStdString());
    return std::holds_alternative<TargetAddress>(result) && isBroadcast(std::get<TargetAddress>(result));
}

void LanController::copy(const QString& text) const {
    if (auto* clipboard = QGuiApplication::clipboard()) clipboard->setText(text);
}

void LanController::copyKey() const { copy(keyDisplay()); }

void LanController::refreshAddresses() {
    addressesAtMs_ = lan_net::steadyMs();
    QVariantList list;
    const auto key = keyDisplay();
    const auto port = QString::number(settings_.server.port);
    for (const auto& interface : QNetworkInterface::allInterfaces()) {
        const auto flags = interface.flags();
        if (!flags.testFlag(QNetworkInterface::IsUp) || !flags.testFlag(QNetworkInterface::IsRunning) || flags.testFlag(QNetworkInterface::IsLoopBack)) {
            continue;
        }
        for (const auto& entry : interface.addressEntries()) {
            const auto ip = entry.ip();
            if (ip.protocol() != QAbstractSocket::IPv4Protocol || ip.isLoopback() || !lan_net::isLocal(ip)) continue;
            if (list.size() == kMaxAddresses) break;
            const auto host = ip.toString() + QLatin1Char(':') + port;
            list.push_back(QVariantMap{{QStringLiteral("address"), host},
                                       {QStringLiteral("http"), QStringLiteral("http://%1/v1/live?key=%2").arg(host, key)},
                                       {QStringLiteral("ws"), QStringLiteral("ws://%1/v1/stream?key=%2").arg(host, key)},
                                       {QStringLiteral("tcp"), host}});
        }
    }
    if (list != addresses_) {
        addresses_ = list;
        emit addressesChanged();
    }
}
