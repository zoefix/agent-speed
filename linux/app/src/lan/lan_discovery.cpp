#include "lan/lan_discovery.hpp"

#include <QDBusConnection>
#include <QDBusConnectionInterface>
#include <QDBusMessage>
#include <QDBusMetaType>
#include <QDBusReply>

#include "lan/lan_net.hpp"

namespace {

const QString kService = QStringLiteral("org.freedesktop.Avahi");
const QString kServer = QStringLiteral("org.freedesktop.Avahi.Server");
const QString kGroup = QStringLiteral("org.freedesktop.Avahi.EntryGroup");
constexpr int kUnspecified = -1;  // AVAHI_IF_UNSPEC, AVAHI_PROTO_UNSPEC
constexpr int kCollision = 3;     // AVAHI_ENTRY_GROUP_COLLISION
constexpr int kFailure = 4;       // AVAHI_ENTRY_GROUP_FAILURE
constexpr int kMaxRenames = 8;

QDBusMessage call(const QString& path, const QString& interface, const QString& method, const QVariantList& arguments = {}) {
    auto message = QDBusMessage::createMethodCall(kService, path, interface, method);
    message.setArguments(arguments);
    return QDBusConnection::systemBus().call(message, QDBus::Block, 3000);
}

bool failed(const QDBusMessage& reply, const char* what) {
    if (reply.type() != QDBusMessage::ErrorMessage) return false;
    qCWarning(lanLog).noquote() << "lan: discovery" << what << "failed:" << reply.errorMessage();
    return true;
}

}  // namespace

LanDiscovery::LanDiscovery(QObject* parent) : QObject(parent) { qDBusRegisterMetaType<QList<QByteArray>>(); }

LanDiscovery::~LanDiscovery() { withdraw(); }

QString LanDiscovery::serviceName(const QString& name) {
    auto bytes = name.toUtf8();
    if (bytes.size() <= 63) return name;
    qsizetype cut = 63;
    while (cut > 0 && (static_cast<unsigned char>(bytes[cut]) & 0xC0) == 0x80) --cut;
    return QString::fromUtf8(bytes.left(cut));
}

void LanDiscovery::publish(const QString& name, quint16 port, const QString& instance) {
    if (published() && name_ == serviceName(name) && port_ == port && instance_ == instance) return;
    withdraw();
    auto bus = QDBusConnection::systemBus();
    if (!bus.isConnected() || !bus.interface() || !bus.interface()->isServiceRegistered(kService).value()) {
        qCInfo(lanLog) << "lan: discovery skipped: the Avahi daemon is not running";
        return;
    }
    name_ = serviceName(name);
    port_ = port;
    instance_ = instance;
    renames_ = 0;
    const auto reply = call(QStringLiteral("/"), kServer, QStringLiteral("EntryGroupNew"));
    if (failed(reply, "EntryGroupNew") || reply.arguments().isEmpty()) return;
    group_ = reply.arguments().constFirst().value<QDBusObjectPath>();
    bus.connect(kService, group_.path(), kGroup, QStringLiteral("StateChanged"), this, SLOT(stateChanged(int, QString)));
    if (!add()) withdraw();
}

bool LanDiscovery::add() {
    const QList<QByteArray> txt = {QByteArrayLiteral("schema=1"), "id=" + instance_.toUtf8()};
    const auto added = call(group_.path(), kGroup, QStringLiteral("AddService"),
                            {kUnspecified, kUnspecified, 0u, name_, QStringLiteral("_agentspeed._tcp"), QString(), QString(),
                             QVariant::fromValue(port_), QVariant::fromValue(txt)});
    if (failed(added, "AddService")) return false;
    if (failed(call(group_.path(), kGroup, QStringLiteral("Commit")), "Commit")) return false;
    qCInfo(lanLog).noquote() << "lan: registered _agentspeed._tcp as" << name_;
    return true;
}

void LanDiscovery::stateChanged(int state, const QString& error) {
    if (state == kFailure) {
        qCWarning(lanLog).noquote() << "lan: discovery failed:" << error;
        return;
    }
    if (state != kCollision || renames_ >= kMaxRenames) return;
    ++renames_;
    const auto alternative = call(QStringLiteral("/"), kServer, QStringLiteral("GetAlternativeServiceName"), {name_});
    if (failed(alternative, "GetAlternativeServiceName") || alternative.arguments().isEmpty()) return;
    name_ = alternative.arguments().constFirst().toString();
    if (failed(call(group_.path(), kGroup, QStringLiteral("Reset")), "Reset")) return;
    add();
}

void LanDiscovery::withdraw() {
    if (!published()) return;
    auto bus = QDBusConnection::systemBus();
    bus.disconnect(kService, group_.path(), kGroup, QStringLiteral("StateChanged"), this, SLOT(stateChanged(int, QString)));
    call(group_.path(), kGroup, QStringLiteral("Free"));
    group_ = QDBusObjectPath();
    qCInfo(lanLog) << "lan: discovery withdrawn";
}
