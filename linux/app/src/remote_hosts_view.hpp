#pragma once

#include <QObject>
#include <QString>
#include <QStringList>
#include <QVariantList>
#include <QVariantMap>
#include <map>
#include <optional>
#include <string>
#include <vector>

#include "agentspeed/host_selection.hpp"
#include "agentspeed/remote_status.hpp"

class AppState;
class HistoryModel;
class Preferences;
class RemoteService;
class Strings;

// The remote hosts as the UI shows them (DESIGN 13), the `hostsView` object of QML: every configured host with its
// status in words (line, tooltip, notes, dot, word), the pickers' choices and entries, and the dashboard's Hosts rows.
// Refreshed with every snapshot, so a retry countdown ticks; signals fire only when something visible changed.
class RemoteHostsView : public QObject {
    Q_OBJECT
    // Whether any remote host is configured: every host label, picker and section waits for the first one.
    Q_PROPERTY(bool shown READ shown NOTIFY changed)
    Q_PROPERTY(QString localName READ localName NOTIFY changed)
    // Settings → Remote Hosts: one row per host in the order added ({hostId, name, destination, hostEnabled, line, …}).
    Q_PROPERTY(QVariantList rows READ rows NOTIFY rowsChanged)
    // This computer, then every remote host: {id, name, dot, word, local}; empty without remote hosts.
    Q_PROPERTY(QVariantList choices READ choices NOTIFY changed)
    // Dashboard → Hosts: the hosts that are connected or produced output today, this computer first.
    Q_PROPERTY(QVariantList dashboard READ dashboard NOTIFY dashboardChanged)

public:
    RemoteHostsView(RemoteService& remote, const Preferences& prefs, const AppState& state, const HistoryModel& history,
                    const Strings& strings, QObject* parent = nullptr);

    bool shown() const { return shown_; }
    QString localName() const;
    QVariantList rows() const { return rows_; }
    QVariantList choices() const { return choices_; }
    QVariantList dashboard() const { return dashboard_; }

    // A host's name: this computer's, the settings' name, else the name its history keeps; the id as a last resort.
    Q_INVOKABLE QString nameOf(const QString& id) const;
    // The picker rows of an orb's or board's `hosts` ("all" or ids): {id, name, checked, enabled, dot, word, local}.
    Q_INVOKABLE QVariantList entries(const QVariant& hosts) const;
    // The selection after the user toggles `id`; null when that would leave no host.
    Q_INVOKABLE QVariant toggled(const QVariant& hosts, const QString& id) const;
    // All Hosts on: "all"; off: every host this computer knows, so the reading does not jump.
    Q_INVOKABLE QVariant allHosts(bool on) const;
    // The automatic name's host part (` · devbox`, ` · devbox +1`); empty for every host.
    QString nameSuffix(const QVariant& hosts) const;
    QString nameSuffix(const std::optional<QStringList>& hosts) const;

    // The editor's messages (REMOTE 2.1): "" when the field is fine.
    Q_INVOKABLE QString destinationError(const QString& destination) const;
    Q_INVOKABLE QString folderError(const QString& folder) const;
    Q_INVOKABLE QString nameError(const QString& name) const;
    // The name a new or edited host would get without a custom one, distinct from the other hosts.
    Q_INVOKABLE QString automaticName(const QString& destination, const QString& editingId) const;
    // Test Connection's inline result: {ok, text, attention}.
    Q_INVOKABLE QVariantMap testResult(const QVariantMap& result, const QString& destination) const;
    // The removal confirmation's informative text: where the helper stays on the host.
    Q_INVOKABLE QString removeFiles(const QString& id) const;
    // Copies `ssh <destination>` (quoted for a shell) to the clipboard.
    Q_INVOKABLE void copySshCommand(const QString& id) const;

signals:
    // Whether hosts exist, this computer's name or a picker choice (name, dot, word) changed.
    void changed();
    // A settings row changed (its status line ticks while a retry is counted down).
    void rowsChanged();
    void dashboardChanged();
    // The hosts' ids or names changed (automatic orb names follow).
    void namesChanged();

private:
    void refresh();
    std::vector<agentspeed::hosts::HostChoice> hostChoices() const;
    std::vector<std::string> hostOrder() const;
    agentspeed::remote::HostStatus statusOf(const std::string& id) const;
    QString wordOf(const std::string& id) const;
    QString dotOf(const std::string& id) const;
    QString tooltipText(const agentspeed::remote::HostStatus& status, qint64 nowMs) const;
    int sessionsOf(const QString& id) const;

    RemoteService& remote_;
    const Preferences& prefs_;
    const AppState& state_;
    const HistoryModel& history_;
    const Strings& strings_;
    std::map<std::string, agentspeed::remote::HostStatus> statuses_;
    bool shown_ = false;
    QVariantList rows_, choices_, dashboard_;
    QStringList names_;
};
