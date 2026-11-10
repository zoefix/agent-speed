#pragma once

#include <QDBusConnection>
#include <QDBusObjectPath>
#include <QString>
#include <mutex>
#include <set>

#include "agentspeed/quota/secrets.hpp"

// The freedesktop Secret Service (org.freedesktop.secrets) over Qt D-Bus, as libsecret uses it: a `plain` session,
// SearchItems by attributes, Unlock through the service's prompt, GetSecrets, CreateItem in the default collection and
// Delete. Calls block the calling thread (the quota worker's), a prompt for at most `kPromptTimeoutMs`. A dismissed
// prompt is remembered per attribute set and not asked again until the app restarts. Secrets are never logged.
class SecretService : public agentspeed::quota::SecretStore {
public:
    static constexpr int kPromptTimeoutMs = 120'000;
    static constexpr int kCallTimeoutMs = 5'000;

    explicit SecretService(QDBusConnection bus = QDBusConnection::sessionBus(), QString service = QStringLiteral("org.freedesktop.secrets"));

    agentspeed::quota::SecretLookup lookup(const agentspeed::quota::SecretAttributes& attributes) override;
    bool store(const agentspeed::quota::SecretAttributes& attributes, const std::string& label, const std::string& secret) override;
    bool remove(const agentspeed::quota::SecretAttributes& attributes) override;
    // Whether an item exists, locked or not, without reading or unlocking it.
    bool has(const agentspeed::quota::SecretAttributes& attributes);

private:
    enum class Unlock { Done, Dismissed, Failed };
    bool available() const;
    std::optional<QDBusObjectPath> openSession();
    void closeSession(const QDBusObjectPath& session);
    // Unlocked and locked items matching the attributes.
    bool search(const agentspeed::quota::SecretAttributes& attributes, QList<QDBusObjectPath>& unlocked, QList<QDBusObjectPath>& locked);
    Unlock unlock(const QList<QDBusObjectPath>& objects);
    // Waits for a prompt's Completed signal: false when dismissed or timed out.
    bool prompt(const QDBusObjectPath& path);
    bool refused(const agentspeed::quota::SecretAttributes& attributes) const;
    void noteRefusal(const agentspeed::quota::SecretAttributes& attributes);

    QDBusConnection bus_;
    QString service_;
    mutable std::mutex mutex_;
    std::set<agentspeed::quota::SecretAttributes> refused_;
};
