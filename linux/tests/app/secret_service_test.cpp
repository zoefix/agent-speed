#include <gtest/gtest.h>

#include <QCoreApplication>
#include <QDBusArgument>
#include <QDBusConnection>
#include <QDBusMessage>
#include <QDBusMetaType>
#include <QDBusVariant>
#include <QDBusVirtualObject>
#include <QProcess>
#include <QThread>
#include <atomic>
#include <map>
#include <mutex>

#include "quota/secret_service.hpp"

// The Secret Service client against a fake org.freedesktop.secrets on a private bus (its own dbus-daemon): lookup,
// unlock through a prompt, a dismissed prompt not asked again, store and remove. No real keyring is touched.
namespace {

namespace quota = agentspeed::quota;
using StringMap = QMap<QString, QString>;

struct FakeSecret {
    QDBusObjectPath session;
    QByteArray parameters, value;
    QString contentType;
};

}  // namespace

Q_DECLARE_METATYPE(FakeSecret)

QDBusArgument& operator<<(QDBusArgument& a, const FakeSecret& s) {
    a.beginStructure();
    a << s.session << s.parameters << s.value << s.contentType;
    a.endStructure();
    return a;
}
const QDBusArgument& operator>>(const QDBusArgument& a, FakeSecret& s) {
    a.beginStructure();
    a >> s.session >> s.parameters >> s.value >> s.contentType;
    a.endStructure();
    return a;
}

namespace {

const QString kRoot = QStringLiteral("/org/freedesktop/secrets");

// The fake service: items with attributes, a lock flag and a value; one prompt that the test answers.
class FakeKeyring : public QDBusVirtualObject {
public:
    struct Item {
        StringMap attributes;
        QByteArray value;
        bool locked = false;
    };
    std::mutex mutex;
    std::map<QString, Item> items;
    bool collectionLocked = false;
    bool dismissPrompts = false;
    std::atomic<int> prompts{0};
    int next = 1;
    QStringList pendingUnlock;

    QString introspect(const QString&) const override { return {}; }

    bool handleMessage(const QDBusMessage& message, const QDBusConnection& connection) override {
        std::lock_guard lock(mutex);
        const auto member = message.member();
        const auto path = message.path();
        const auto args = message.arguments();
        auto reply = message.createReply();
        if (member == QLatin1String("OpenSession")) {
            reply << QVariant::fromValue(QDBusVariant(QString())) << QVariant::fromValue(QDBusObjectPath(kRoot + "/session/s" + QString::number(next++)));
        } else if (member == QLatin1String("Close")) {
        } else if (member == QLatin1String("SearchItems")) {
            StringMap wanted;
            args.at(0).value<QDBusArgument>() >> wanted;
            QList<QDBusObjectPath> unlocked, locked;
            for (const auto& [itemPath, item] : items) {
                bool match = true;
                for (auto it = wanted.begin(); it != wanted.end(); ++it) match = match && item.attributes.value(it.key()) == it.value();
                if (match) (item.locked ? locked : unlocked).push_back(QDBusObjectPath(itemPath));
            }
            reply << QVariant::fromValue(unlocked) << QVariant::fromValue(locked);
        } else if (member == QLatin1String("Unlock")) {
            QList<QDBusObjectPath> objects;
            args.at(0).value<QDBusArgument>() >> objects;
            bool needs = false;
            for (const auto& o : objects) {
                needs = needs || (items.contains(o.path()) && items[o.path()].locked) ||
                        (o.path().endsWith(QLatin1String("/aliases/default")) && collectionLocked);
                pendingUnlock.push_back(o.path());
            }
            if (needs) {
                reply << QVariant::fromValue(QList<QDBusObjectPath>{}) << QVariant::fromValue(QDBusObjectPath(kRoot + "/prompt/p1"));
            } else {
                pendingUnlock.clear();
                reply << QVariant::fromValue(objects) << QVariant::fromValue(QDBusObjectPath(QStringLiteral("/")));
            }
        } else if (member == QLatin1String("Prompt")) {
            ++prompts;
            connection.send(reply);
            const bool dismissed = dismissPrompts;
            if (!dismissed) {
                for (const auto& p : pendingUnlock) {
                    if (items.contains(p)) items[p].locked = false;
                    if (p.endsWith(QLatin1String("/aliases/default"))) collectionLocked = false;
                }
            }
            pendingUnlock.clear();
            auto signal = QDBusMessage::createSignal(path, QStringLiteral("org.freedesktop.Secret.Prompt"), QStringLiteral("Completed"));
            signal << dismissed << QVariant::fromValue(QDBusVariant(QString()));
            connection.send(signal);
            return true;
        } else if (member == QLatin1String("GetSecret")) {
            if (!items.contains(path) || items[path].locked) {
                connection.send(message.createErrorReply(QStringLiteral("org.freedesktop.Secret.Error.IsLocked"), {}));
                return true;
            }
            FakeSecret secret{args.at(0).value<QDBusObjectPath>(), {}, items[path].value, QStringLiteral("text/plain")};
            reply << QVariant::fromValue(secret);
        } else if (member == QLatin1String("CreateItem")) {
            QVariantMap properties;
            args.at(0).value<QDBusArgument>() >> properties;
            FakeSecret secret;
            args.at(1).value<QDBusArgument>() >> secret;
            Item item;
            const auto attributes = properties.value(QStringLiteral("org.freedesktop.Secret.Item.Attributes"));
            attributes.value<QDBusArgument>() >> item.attributes;
            item.value = secret.value;
            QString itemPath;
            for (const auto& [p, existing] : items) {
                if (existing.attributes == item.attributes) itemPath = p;
            }
            if (itemPath.isEmpty()) itemPath = kRoot + "/collection/login/i" + QString::number(next++);
            items[itemPath] = item;
            reply << QVariant::fromValue(QDBusObjectPath(itemPath)) << QVariant::fromValue(QDBusObjectPath(QStringLiteral("/")));
        } else if (member == QLatin1String("Delete")) {
            items.erase(path);
            reply << QVariant::fromValue(QDBusObjectPath(QStringLiteral("/")));
        } else {
            connection.send(message.createErrorReply(QDBusError::UnknownMethod, member));
            return true;
        }
        connection.send(reply);
        return true;
    }
};

class SecretServiceTest : public ::testing::Test {
protected:
    void SetUp() override {
        qDBusRegisterMetaType<FakeSecret>();
        qDBusRegisterMetaType<StringMap>();
        daemon_.setProgram(QStringLiteral("dbus-daemon"));
        daemon_.setArguments({QStringLiteral("--session"), QStringLiteral("--nofork"), QStringLiteral("--print-address=1"),
                              QStringLiteral("--address=unix:tmpdir=/tmp")});
        daemon_.start();
        if (!daemon_.waitForStarted(3000) || !daemon_.waitForReadyRead(3000)) GTEST_SKIP() << "no dbus-daemon";
        address_ = QString::fromUtf8(daemon_.readLine()).trimmed();
        host_.moveToThread(&thread_);
        keyring_.moveToThread(&thread_);
        thread_.start();
        QMetaObject::invokeMethod(
            &host_,
            [this] {
                auto bus = QDBusConnection::connectToBus(address_, QStringLiteral("fake-keyring"));
                bus.registerVirtualObject(kRoot, &keyring_, QDBusConnection::SubPath);
                bus.registerService(QStringLiteral("org.freedesktop.secrets"));
            },
            Qt::BlockingQueuedConnection);
        client_ = std::make_unique<QDBusConnection>(QDBusConnection::connectToBus(address_, QStringLiteral("secret-client")));
    }
    void TearDown() override {
        client_.reset();
        QDBusConnection::disconnectFromBus(QStringLiteral("secret-client"));
        if (thread_.isRunning()) {
            QMetaObject::invokeMethod(&host_, [] { QDBusConnection::disconnectFromBus(QStringLiteral("fake-keyring")); }, Qt::BlockingQueuedConnection);
            thread_.quit();
            thread_.wait();
        }
        daemon_.kill();
        daemon_.waitForFinished(3000);
    }
    void add(const QString& path, StringMap attributes, QByteArray value, bool locked = false) {
        std::lock_guard lock(keyring_.mutex);
        keyring_.items[path] = {std::move(attributes), std::move(value), locked};
    }

    QProcess daemon_;
    QString address_;
    QThread thread_;
    QObject host_;
    FakeKeyring keyring_;
    std::unique_ptr<QDBusConnection> client_;
};

const quota::SecretAttributes kChrome{{"application", "chrome"}};

TEST_F(SecretServiceTest, FindsAnUnlockedItemByItsAttributes) {
    add(kRoot + "/collection/login/1", {{"application", "chrome"}, {"xdg:schema", "chrome_libsecret_os_crypt_password_v2"}}, "chrome-pass");
    add(kRoot + "/collection/login/2", {{"application", "chromium"}}, "other");
    SecretService service(*client_);
    const auto found = service.lookup(kChrome);
    EXPECT_EQ(found.result, quota::SecretResult::Found);
    EXPECT_EQ(found.secret, "chrome-pass");
    EXPECT_EQ(service.lookup({{"application", "brave"}}).result, quota::SecretResult::Missing);
    EXPECT_EQ(keyring_.prompts.load(), 0);
}

TEST_F(SecretServiceTest, UnlocksThroughThePrompt) {
    add(kRoot + "/collection/login/1", {{"application", "chrome"}}, "chrome-pass", true);
    SecretService service(*client_);
    const auto found = service.lookup(kChrome);
    EXPECT_EQ(found.result, quota::SecretResult::Found);
    EXPECT_EQ(found.secret, "chrome-pass");
    EXPECT_EQ(keyring_.prompts.load(), 1);
}

TEST_F(SecretServiceTest, ADismissedPromptIsNotAskedAgain) {
    add(kRoot + "/collection/login/1", {{"application", "chrome"}}, "chrome-pass", true);
    keyring_.dismissPrompts = true;
    SecretService service(*client_);
    EXPECT_EQ(service.lookup(kChrome).result, quota::SecretResult::Refused);
    EXPECT_EQ(service.lookup(kChrome).result, quota::SecretResult::Refused);
    EXPECT_EQ(keyring_.prompts.load(), 1);
    // Another item is still asked for; a new app run asks again.
    SecretService restarted(*client_);
    EXPECT_EQ(restarted.lookup(kChrome).result, quota::SecretResult::Refused);
    EXPECT_EQ(keyring_.prompts.load(), 2);
}

TEST_F(SecretServiceTest, StoresReplacesAndRemovesTheZcodeKey) {
    SecretService service(*client_);
    EXPECT_EQ(service.lookup(quota::zcodeKeyAttributes()).result, quota::SecretResult::Missing);
    ASSERT_TRUE(service.store(quota::zcodeKeyAttributes(), "Agent Speed ZCode API key", "zk-1"));
    EXPECT_EQ(service.lookup(quota::zcodeKeyAttributes()).secret, "zk-1");
    ASSERT_TRUE(service.store(quota::zcodeKeyAttributes(), "Agent Speed ZCode API key", "zk-2"));
    EXPECT_EQ(service.lookup(quota::zcodeKeyAttributes()).secret, "zk-2");
    {
        std::lock_guard lock(keyring_.mutex);
        EXPECT_EQ(keyring_.items.size(), 1u);
        EXPECT_EQ(keyring_.items.begin()->second.attributes.value(QStringLiteral("service")), QStringLiteral("AgentSpeed"));
        EXPECT_EQ(keyring_.items.begin()->second.attributes.value(QStringLiteral("account")), QStringLiteral("zcode-api-key"));
    }
    ASSERT_TRUE(service.remove(quota::zcodeKeyAttributes()));
    EXPECT_EQ(service.lookup(quota::zcodeKeyAttributes()).result, quota::SecretResult::Missing);
}

TEST_F(SecretServiceTest, StoringIntoALockedCollectionUnlocksItFirst) {
    keyring_.collectionLocked = true;
    SecretService service(*client_);
    EXPECT_TRUE(service.store(quota::zcodeKeyAttributes(), "Agent Speed ZCode API key", "zk"));
    EXPECT_EQ(keyring_.prompts.load(), 1);
    keyring_.collectionLocked = true;
    keyring_.dismissPrompts = true;
    EXPECT_FALSE(service.store(quota::zcodeKeyAttributes(), "Agent Speed ZCode API key", "zk2"));
}

TEST(SecretServiceAbsent, NoServiceIsUnavailable) {
    SecretService service(QDBusConnection(QStringLiteral("not-connected")));
    EXPECT_EQ(service.lookup(kChrome).result, quota::SecretResult::Unavailable);
    EXPECT_FALSE(service.store(quota::zcodeKeyAttributes(), "x", "y"));
}

}  // namespace
