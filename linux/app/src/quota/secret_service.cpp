#include "quota/secret_service.hpp"

#include <QDBusArgument>
#include <QDBusConnectionInterface>
#include <QDBusMessage>
#include <QDBusMetaType>
#include <QDBusVariant>
#include <QEventLoop>
#include <QTimer>

namespace quota = agentspeed::quota;

namespace {

const QString kServicePath = QStringLiteral("/org/freedesktop/secrets");
const QString kDefaultCollection = QStringLiteral("/org/freedesktop/secrets/aliases/default");
const QString kServiceInterface = QStringLiteral("org.freedesktop.Secret.Service");
const QString kItemInterface = QStringLiteral("org.freedesktop.Secret.Item");
const QString kCollectionInterface = QStringLiteral("org.freedesktop.Secret.Collection");
const QString kPromptInterface = QStringLiteral("org.freedesktop.Secret.Prompt");
const QString kSessionInterface = QStringLiteral("org.freedesktop.Secret.Session");

using StringMap = QMap<QString, QString>;

StringMap toMap(const quota::SecretAttributes& attributes) {
    StringMap map;
    for (const auto& [key, value] : attributes) map.insert(QString::fromStdString(key), QString::fromStdString(value));
    return map;
}

bool noPrompt(const QDBusObjectPath& path) { return path.path().isEmpty() || path.path() == QLatin1String("/"); }

QList<QDBusObjectPath> paths(const QVariant& value) {
    QList<QDBusObjectPath> out;
    if (value.canConvert<QDBusArgument>()) {
        const auto argument = value.value<QDBusArgument>();
        argument >> out;
    } else {
        out = qdbus_cast<QList<QDBusObjectPath>>(value);
    }
    return out;
}

void wipe(QByteArray& bytes) {
    bytes.fill('\0');
    bytes.clear();
}

}  // namespace

// The `(oayays)` secret of the Secret Service API.
struct SecretValue {
    QDBusObjectPath session;
    QByteArray parameters;
    QByteArray value;
    QString contentType;
};
Q_DECLARE_METATYPE(SecretValue)

QDBusArgument& operator<<(QDBusArgument& argument, const SecretValue& secret) {
    argument.beginStructure();
    argument << secret.session << secret.parameters << secret.value << secret.contentType;
    argument.endStructure();
    return argument;
}

const QDBusArgument& operator>>(const QDBusArgument& argument, SecretValue& secret) {
    argument.beginStructure();
    argument >> secret.session >> secret.parameters >> secret.value >> secret.contentType;
    argument.endStructure();
    return argument;
}

// Waits on the calling thread for one prompt's Completed signal.
class PromptWaiter : public QObject {
    Q_OBJECT
public:
    bool done = false, dismissed = true;
    QEventLoop loop;

public slots:
    void completed(bool wasDismissed, const QDBusVariant&) {
        done = true;
        dismissed = wasDismissed;
        loop.quit();
    }
};

SecretService::SecretService(QDBusConnection bus, QString service) : bus_(std::move(bus)), service_(std::move(service)) {
    qDBusRegisterMetaType<SecretValue>();
    qDBusRegisterMetaType<StringMap>();
}

bool SecretService::available() const {
    if (!bus_.isConnected()) return false;
    auto* interface = bus_.interface();
    if (!interface) return false;
    const auto registered = interface->isServiceRegistered(service_);
    if (registered.isValid() && registered.value()) return true;
    // A keyring that starts on demand (D-Bus activation).
    const auto activatable = bus_.call(QDBusMessage::createMethodCall(QStringLiteral("org.freedesktop.DBus"), QStringLiteral("/org/freedesktop/DBus"),
                                                                      QStringLiteral("org.freedesktop.DBus"), QStringLiteral("ListActivatableNames")),
                                       QDBus::Block, kCallTimeoutMs);
    return activatable.type() == QDBusMessage::ReplyMessage && !activatable.arguments().isEmpty() &&
           activatable.arguments().first().toStringList().contains(service_);
}

std::optional<QDBusObjectPath> SecretService::openSession() {
    auto call = QDBusMessage::createMethodCall(service_, kServicePath, kServiceInterface, QStringLiteral("OpenSession"));
    call << QStringLiteral("plain") << QVariant::fromValue(QDBusVariant(QString()));
    const auto reply = bus_.call(call, QDBus::Block, kCallTimeoutMs);
    if (reply.type() != QDBusMessage::ReplyMessage || reply.arguments().size() < 2) return std::nullopt;
    return qdbus_cast<QDBusObjectPath>(reply.arguments().at(1));
}

void SecretService::closeSession(const QDBusObjectPath& session) {
    bus_.call(QDBusMessage::createMethodCall(service_, session.path(), kSessionInterface, QStringLiteral("Close")), QDBus::Block, kCallTimeoutMs);
}

bool SecretService::search(const quota::SecretAttributes& attributes, QList<QDBusObjectPath>& unlocked, QList<QDBusObjectPath>& locked) {
    auto call = QDBusMessage::createMethodCall(service_, kServicePath, kServiceInterface, QStringLiteral("SearchItems"));
    call << QVariant::fromValue(toMap(attributes));
    const auto reply = bus_.call(call, QDBus::Block, kCallTimeoutMs);
    if (reply.type() != QDBusMessage::ReplyMessage || reply.arguments().size() < 2) return false;
    unlocked = paths(reply.arguments().at(0));
    locked = paths(reply.arguments().at(1));
    return true;
}

bool SecretService::prompt(const QDBusObjectPath& path) {
    PromptWaiter waiter;
    if (!bus_.connect(service_, path.path(), kPromptInterface, QStringLiteral("Completed"), &waiter, SLOT(completed(bool, QDBusVariant)))) {
        return false;
    }
    auto call = QDBusMessage::createMethodCall(service_, path.path(), kPromptInterface, QStringLiteral("Prompt"));
    call << QString();
    const auto reply = bus_.call(call, QDBus::Block, kCallTimeoutMs);
    if (reply.type() == QDBusMessage::ReplyMessage && !waiter.done) {
        QTimer::singleShot(kPromptTimeoutMs, &waiter.loop, &QEventLoop::quit);
        waiter.loop.exec();
    }
    bus_.disconnect(service_, path.path(), kPromptInterface, QStringLiteral("Completed"), &waiter, SLOT(completed(bool, QDBusVariant)));
    return waiter.done && !waiter.dismissed;
}

SecretService::Unlock SecretService::unlock(const QList<QDBusObjectPath>& objects) {
    auto call = QDBusMessage::createMethodCall(service_, kServicePath, kServiceInterface, QStringLiteral("Unlock"));
    call << QVariant::fromValue(objects);
    const auto reply = bus_.call(call, QDBus::Block, kCallTimeoutMs);
    if (reply.type() != QDBusMessage::ReplyMessage || reply.arguments().size() < 2) return Unlock::Failed;
    const auto promptPath = qdbus_cast<QDBusObjectPath>(reply.arguments().at(1));
    if (noPrompt(promptPath)) return paths(reply.arguments().at(0)).isEmpty() ? Unlock::Failed : Unlock::Done;
    return prompt(promptPath) ? Unlock::Done : Unlock::Dismissed;
}

bool SecretService::refused(const quota::SecretAttributes& attributes) const {
    std::lock_guard lock(mutex_);
    return refused_.contains(attributes);
}

void SecretService::noteRefusal(const quota::SecretAttributes& attributes) {
    std::lock_guard lock(mutex_);
    refused_.insert(attributes);
}

quota::SecretLookup SecretService::lookup(const quota::SecretAttributes& attributes) {
    if (refused(attributes)) return {quota::SecretResult::Refused, {}};
    if (!available()) return {quota::SecretResult::Unavailable, {}};
    QList<QDBusObjectPath> unlocked, locked;
    if (!search(attributes, unlocked, locked)) return {quota::SecretResult::Unavailable, {}};
    if (unlocked.isEmpty() && locked.isEmpty()) return {quota::SecretResult::Missing, {}};
    auto items = unlocked;
    if (items.isEmpty()) {
        const auto result = unlock({locked.first()});
        if (result == Unlock::Dismissed) {
            noteRefusal(attributes);
            return {quota::SecretResult::Refused, {}};
        }
        if (result == Unlock::Failed) return {quota::SecretResult::Unavailable, {}};
        items = {locked.first()};
    }
    const auto session = openSession();
    if (!session) return {quota::SecretResult::Unavailable, {}};
    auto call = QDBusMessage::createMethodCall(service_, items.first().path(), kItemInterface, QStringLiteral("GetSecret"));
    call << QVariant::fromValue(*session);
    const auto reply = bus_.call(call, QDBus::Block, kCallTimeoutMs);
    closeSession(*session);
    if (reply.type() != QDBusMessage::ReplyMessage || reply.arguments().isEmpty()) return {quota::SecretResult::Unavailable, {}};
    SecretValue secret;
    reply.arguments().first().value<QDBusArgument>() >> secret;
    quota::SecretLookup out{quota::SecretResult::Found, secret.value.toStdString()};
    wipe(secret.value);
    return out;
}

bool SecretService::store(const quota::SecretAttributes& attributes, const std::string& label, const std::string& value) {
    if (!available()) return false;
    if (unlock({QDBusObjectPath(kDefaultCollection)}) != Unlock::Done) return false;
    const auto session = openSession();
    if (!session) return false;
    QVariantMap properties;
    properties.insert(QStringLiteral("org.freedesktop.Secret.Item.Label"), QString::fromStdString(label));
    properties.insert(QStringLiteral("org.freedesktop.Secret.Item.Attributes"), QVariant::fromValue(toMap(attributes)));
    SecretValue secret{*session, {}, QByteArray::fromStdString(value), QStringLiteral("text/plain")};
    auto call = QDBusMessage::createMethodCall(service_, kDefaultCollection, kCollectionInterface, QStringLiteral("CreateItem"));
    call << properties << QVariant::fromValue(secret) << true;
    const auto reply = bus_.call(call, QDBus::Block, kCallTimeoutMs);
    wipe(secret.value);
    closeSession(*session);
    if (reply.type() != QDBusMessage::ReplyMessage || reply.arguments().size() < 2) return false;
    const auto promptPath = qdbus_cast<QDBusObjectPath>(reply.arguments().at(1));
    if (!noPrompt(promptPath)) return prompt(promptPath);
    return !noPrompt(qdbus_cast<QDBusObjectPath>(reply.arguments().at(0)));
}

bool SecretService::has(const quota::SecretAttributes& attributes) {
    if (!available()) return false;
    QList<QDBusObjectPath> unlocked, locked;
    return search(attributes, unlocked, locked) && !(unlocked.isEmpty() && locked.isEmpty());
}

bool SecretService::remove(const quota::SecretAttributes& attributes) {
    if (!available()) return false;
    QList<QDBusObjectPath> unlocked, locked;
    if (!search(attributes, unlocked, locked)) return false;
    bool ok = true;
    for (const auto& item : unlocked + locked) {
        const auto reply = bus_.call(QDBusMessage::createMethodCall(service_, item.path(), kItemInterface, QStringLiteral("Delete")),
                                     QDBus::Block, kCallTimeoutMs);
        if (reply.type() != QDBusMessage::ReplyMessage) {
            ok = false;
            continue;
        }
        if (!reply.arguments().isEmpty()) {
            const auto promptPath = qdbus_cast<QDBusObjectPath>(reply.arguments().first());
            if (!noPrompt(promptPath)) ok = prompt(promptPath) && ok;
        }
    }
    return ok;
}

#include "secret_service.moc"
