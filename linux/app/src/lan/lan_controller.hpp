#pragma once

#include <QObject>
#include <QTimer>
#include <QVariantList>
#include <QVariantMap>
#include <optional>

#include "agentspeed/lan_feed.hpp"
#include "agentspeed/lan_settings.hpp"
#include "lan/lan_discovery.hpp"
#include "lan/lan_server.hpp"
#include "lan/lan_targets.hpp"
#include "monitor_worker.hpp"

class Preferences;
class Strings;

// DESIGN 14 Settings → API → 區域網路 and the feed behind it (LAN): the settings document, the feed fed by every engine
// tick, the server, the targets and discovery, under the network master switch. QML reads it as `lan`.
class LanController : public QObject {
    Q_OBJECT
    Q_PROPERTY(bool networkEnabled READ networkEnabled NOTIFY changed)
    Q_PROPERTY(bool serverEnabled READ serverEnabled NOTIFY changed)
    Q_PROPERTY(QString serverDot READ serverDot NOTIFY changed)
    Q_PROPERTY(QString serverLine READ serverLine NOTIFY changed)
    Q_PROPERTY(QString keyDisplay READ keyDisplay NOTIFY changed)
    Q_PROPERTY(QString keySpoken READ keySpoken NOTIFY changed)
    Q_PROPERTY(QVariantList addresses READ addresses NOTIFY addressesChanged)
    Q_PROPERTY(int port READ port NOTIFY changed)
    Q_PROPERTY(QVariantList targets READ targets NOTIFY changed)
    Q_PROPERTY(bool canAdd READ canAdd NOTIFY changed)
    Q_PROPERTY(int maxTargets READ maxTargets CONSTANT)
    Q_PROPERTY(bool shown READ shown NOTIFY changed)

public:
    LanController(Preferences& prefs, const Strings& strings, QObject* parent = nullptr);
    ~LanController() override;
    void start();
    // Quitting: streams get close 1001, discovery is withdrawn.
    void shutdown();

    bool networkEnabled() const;
    bool serverEnabled() const { return settings_.server.enabled; }
    QString serverDot() const;
    QString serverLine() const;
    QString keyDisplay() const;
    QString keySpoken() const;
    QVariantList addresses() const { return addresses_; }
    int port() const { return settings_.server.port; }
    QVariantList targets() const;
    bool canAdd() const { return settings_.targets.size() < agentspeed::lan::kMaxTargets; }
    int maxTargets() const { return static_cast<int>(agentspeed::lan::kMaxTargets); }
    // About shows `privacy.lan` while the server is on or any target exists.
    bool shown() const { return settings_.server.enabled || !settings_.targets.empty(); }

    Q_INVOKABLE void setServerEnabled(bool on);
    Q_INVOKABLE void regenerateKey();
    Q_INVOKABLE void copy(const QString& text) const;
    Q_INVOKABLE void copyKey() const;
    Q_INVOKABLE QString portError(const QString& text) const;
    Q_INVOKABLE bool setPort(const QString& text);
    // The sheet: the error sentence for an address (empty when valid), the broadcast note, the stored target.
    Q_INVOKABLE QString addressError(const QString& kind, const QString& address) const;
    Q_INVOKABLE bool broadcast(const QString& kind, const QString& address) const;
    Q_INVOKABLE QVariantMap target(const QString& id) const;
    Q_INVOKABLE QString addTarget(const QString& kind, const QString& address, const QString& name, const QString& key);
    Q_INVOKABLE bool updateTarget(const QString& id, const QString& kind, const QString& address, const QString& name, const QString& key);
    Q_INVOKABLE void removeTarget(const QString& id);
    Q_INVOKABLE void setTargetEnabled(const QString& id, bool on);
    // Send Test: one frame to the address in the fields; testFinished(token, {ok, dot, text}) follows.
    Q_INVOKABLE int test(const QString& kind, const QString& address, const QString& key);
    // The 區域網路 segment is on screen: the addresses refresh and the status lines tick every second.
    Q_INVOKABLE void setWatching(bool on);

public slots:
    void onTick(const ReportTick& tick);

signals:
    void changed();
    void addressesChanged();
    void testFinished(int token, const QVariantMap& result);

private:
    // lan_controller.cpp
    void load();
    void save();
    void apply();
    void applyServer();
    bool feedActive() const;
    std::string name() const;
    const std::string& instance();
    agentspeed::lan::Target* find(const QString& id);

    // lan_view.cpp
    QString t(const char* key) const;
    QString statusLine(const LanTargets::Status& status, qint64 nowMs, qint64 wallMs) const;
    static QString dotOf(agentspeed::lan::TargetStatus status);
    void refreshAddresses();

    Preferences& prefs_;
    const Strings& strings_;
    agentspeed::lan::Settings settings_;
    agentspeed::lan::Feed feed_;
    std::optional<agentspeed::lan::Tick> lastTick_;
    LanServer server_;
    LanTargets targets_;
    LanDiscovery discovery_;
    QTimer retryTimer_, clockTimer_;
    QVariantList addresses_;
    int watchers_ = 0;
    int tests_ = 0;
    qint64 addressesAtMs_ = 0;
    bool started_ = false;
};
