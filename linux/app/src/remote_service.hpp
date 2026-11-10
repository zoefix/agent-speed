#pragma once

#include <functional>
#include <map>
#include <mutex>

#include <QObject>
#include <QSet>
#include <QString>
#include <QTimer>
#include <QVariantList>
#include <QVariantMap>
#include <map>
#include <memory>
#include <set>

#include "agentspeed/remote_coordinator.hpp"
#include "agentspeed/remote_feed.hpp"
#include "agentspeed/remote_host.hpp"

class BoardStore;
class HistoryWorker;
class OrbsStore;
class Preferences;

namespace agentspeed::pricing {
class PriceBook;
}

// REMOTE 2, 4.4: the remote hosts of the settings (`remoteHosts`) connected over the user's ssh, kept in step with the
// network master switch, the enabled sources, the price table and live reporting's watch. Its feed goes to the monitor
// worker, its history events to the history worker. The methods are what the Settings → Remote Hosts tab and the
// pickers (DESIGN 13) call; the live statuses come with every snapshot (`hosts`).
class RemoteService : public QObject {
    Q_OBJECT
    Q_PROPERTY(QVariantList hosts READ hosts NOTIFY hostsChanged)
    Q_PROPERTY(bool canAdd READ canAdd NOTIFY hostsChanged)
    Q_PROPERTY(bool relaysBundled READ relaysBundled CONSTANT)

public:
    RemoteService(Preferences& prefs, std::shared_ptr<agentspeed::pricing::PriceBook> prices, QObject* parent = nullptr);
    ~RemoteService() override;

    std::shared_ptr<agentspeed::remote::Feed> feed() const { return feed_; }
    // The history worker answers historyDue, stores the hosts' days and reports historyTooLarge.
    void attachHistory(HistoryWorker* worker);
    // Removing a host removes it from every orb and board selection (REMOTE 2.4).
    void setSelectionStores(OrbsStore* orbs, BoardStore* boards);
    // Live reporting's watch (METRICS 11.10).
    void setWatch(bool on);
    // A newer price table became current.
    void pricesChanged() { apply(); }
    // Quitting: every relay's stdin closes and every ssh ends.
    void stop();

    // Every host in settings order: id, destination, name (custom, else ""), displayName, automaticName, enabled,
    // relayFolder (custom, else ""), effectiveRelayFolder, connectable.
    QVariantList hosts() const;
    bool canAdd() const { return document_.canAdd(); }
    bool relaysBundled() const { return relaysBundled_; }
    const agentspeed::remote::HostsDocument& document() const { return document_; }
    // The connections' last status of every host (thread-safe snapshot).
    std::map<std::string, agentspeed::remote::HostStatus> statuses() const { return coordinator_->statuses(); }

    // REMOTE 2.1 for the editor: "" when valid, else the issue (empty, tooLong, leadingDash, forbiddenCharacter,
    // notAbsolute, unsafeComponent).
    Q_INVOKABLE QString destinationIssue(const QString& destination) const;
    Q_INVOKABLE QString folderIssue(const QString& folder) const;
    Q_INVOKABLE QString automaticName(const QString& destination) const;
    // The new host's id; "" when a field is invalid or 8 hosts exist. An empty name or folder means automatic.
    Q_INVOKABLE QString addHost(const QString& destination, const QString& name = {}, const QString& relayFolder = {});
    Q_INVOKABLE bool updateHost(const QString& id, const QString& destination, const QString& name, const QString& relayFolder);
    Q_INVOKABLE void setHostEnabled(const QString& id, bool enabled);
    // REMOTE 11.3: the host's Claude Code status line bridge (`quotaClaudeCli`).
    Q_INVOKABLE void setHostQuotaBridge(const QString& id, bool on);
    Q_INVOKABLE bool hostQuotaBridge(const QString& id) const;
    // REMOTE 11.1: the hosts' quota lines (`quota`, `quotaRemoved`, `quotaBridge`), on the coordinator's thread.
    void setQuotaSink(std::function<void(const agentspeed::remote::RelayMessage&)> sink);
    // For the quota views: every host's name, the connected ones, and the sources each host's quota may show.
    std::map<QString, QString> hostNames() const;
    QSet<QString> connectedHosts() const;
    std::map<QString, QSet<QString>> hostQuotaSources() const;
    // Removes the host from the settings and every selection; with `deleteHistory`, its daily history too.
    Q_INVOKABLE void removeHost(const QString& id, bool deleteHistory);
    Q_INVOKABLE void retryNow(const QString& id);
    // `ssh <destination>`, quoted for a shell, for the first connection's host key check in a terminal.
    Q_INVOKABLE QString sshCommand(const QString& id) const;
    // Test Connection: the probe only; `testFinished(token, {supported, system, arch, status})` follows.
    Q_INVOKABLE int testConnection(const QString& destination, const QString& relayFolder);
    Q_INVOKABLE void cancelTest(int token);

signals:
    void hostsChanged();
    void testFinished(int token, const QVariantMap& result);

private:
    void load();
    void save();
    void apply();
    void watchSystem();
    void finishRemoval(const QString& id);
    agentspeed::remote::QuotaSwitches quotaSwitches() const;
    // Hosts removed while their bridge was on: removed once the host reports it off, or after a timeout (REMOTE 2.4).
    std::map<QString, bool> pendingRemovals_;
    std::function<void(const agentspeed::remote::RelayMessage&)> quotaSink_;
    mutable std::mutex sinkMutex_;

    Preferences& prefs_;
    std::shared_ptr<agentspeed::pricing::PriceBook> prices_;
    std::shared_ptr<agentspeed::remote::Feed> feed_ = std::make_shared<agentspeed::remote::Feed>();
    std::unique_ptr<agentspeed::remote::Coordinator> coordinator_;
    agentspeed::remote::HostsDocument document_;
    HistoryWorker* history_ = nullptr;
    OrbsStore* orbs_ = nullptr;
    BoardStore* boards_ = nullptr;
    bool relaysBundled_ = false;
    QTimer zoneTimer_;
    QString zoneId_;
    std::map<int, std::uint64_t> tests_;
    int nextTest_ = 1;
};
