#pragma once

#include <QObject>
#include <QSettings>
#include <QStringList>
#include <QVariantMap>

#include "agentspeed/monitor.hpp"

// DESIGN 5: every user setting, persisted at once and applied live.
class Preferences : public QObject {
    Q_OBJECT
    Q_PROPERTY(QString appearance READ appearance WRITE setAppearance NOTIFY appearanceChanged)
    Q_PROPERTY(QString language READ language WRITE setLanguage NOTIFY languageChanged)
    Q_PROPERTY(bool launchAtLogin READ launchAtLogin WRITE setLaunchAtLogin NOTIFY launchAtLoginChanged)
    Q_PROPERTY(QString metric READ metric WRITE setMetric NOTIFY metricChanged)
    Q_PROPERTY(int redline READ redline WRITE setRedline NOTIFY redlineChanged)
    Q_PROPERTY(QString effects READ effects WRITE setEffects NOTIFY effectsChanged)
    Q_PROPERTY(bool celebratePeaks READ celebratePeaks WRITE setCelebratePeaks NOTIFY celebratePeaksChanged)
    Q_PROPERTY(bool responsePulses READ responsePulses WRITE setResponsePulses NOTIFY responsePulsesChanged)
    Q_PROPERTY(QString currency READ currency WRITE setCurrency NOTIFY currencyChanged)
    Q_PROPERTY(QString liveUnit READ liveUnit WRITE setLiveUnit NOTIFY liveUnitChanged)
    Q_PROPERTY(QStringList subscriptions READ subscriptions WRITE setSubscriptions NOTIFY subscriptionsChanged)
    Q_PROPERTY(bool includeSubscriptions READ includeSubscriptions WRITE setIncludeSubscriptions NOTIFY includeSubscriptionsChanged)
    Q_PROPERTY(QStringList disabledSources READ disabledSources NOTIFY sourcesChanged)
    Q_PROPERTY(bool networkEnabled READ networkEnabled WRITE setNetworkEnabled NOTIFY networkEnabledChanged)
    Q_PROPERTY(bool syncPrices READ syncPrices WRITE setSyncPrices NOTIFY syncPricesChanged)
    Q_PROPERTY(bool autoCheckUpdates READ autoCheckUpdates WRITE setAutoCheckUpdates NOTIFY autoCheckUpdatesChanged)
    Q_PROPERTY(bool trafficEnabled READ trafficEnabled WRITE setTrafficEnabled NOTIFY trafficChanged)
    Q_PROPERTY(bool trafficIncludeTools READ trafficIncludeTools WRITE setTrafficIncludeTools NOTIFY trafficChanged)
    Q_PROPERTY(QString trafficUnit READ trafficUnit WRITE setTrafficUnit NOTIFY trafficChanged)
    // DESIGN 5 額度 Storage: `quotaCliInvite` ("dismissed" or absent) and `quotaZcodeRegion`.
    Q_PROPERTY(bool quotaCliInviteDismissed READ quotaCliInviteDismissed WRITE setQuotaCliInviteDismissed NOTIFY quotaChanged)
    Q_PROPERTY(QString quotaZcodeRegion READ quotaZcodeRegion WRITE setQuotaZcodeRegion NOTIFY quotaChanged)
    // Bumped by every quota setting change, so QML bindings to quotaSwitch() re-evaluate.
    Q_PROPERTY(int quotaRevision READ quotaRevision NOTIFY quotaChanged)

public:
    explicit Preferences(QObject* parent = nullptr);

    QString appearance() const;
    void setAppearance(const QString& value);
    QString language() const;
    void setLanguage(const QString& value);
    bool launchAtLogin() const;
    void setLaunchAtLogin(bool value);
    QString metric() const;
    void setMetric(const QString& value);
    int redline() const;
    void setRedline(int value);
    QString effects() const;
    void setEffects(const QString& value);
    bool celebratePeaks() const;
    void setCelebratePeaks(bool value);
    bool responsePulses() const;
    void setResponsePulses(bool value);
    QString currency() const;
    void setCurrency(const QString& value);
    QString liveUnit() const;
    void setLiveUnit(const QString& value);
    QStringList subscriptions() const;
    void setSubscriptions(const QStringList& value);
    bool includeSubscriptions() const;
    void setIncludeSubscriptions(bool value);
    QStringList disabledSources() const;
    // METRICS 2.1, DESIGN 5 Storage: the location lists, stored as one JSON value `sourceLocations`. While it is absent
    // the single folders of earlier versions (`sources/folders`) are read as [folder]; they are never written again.
    agentspeed::LocationLists sourceLocations() const;
    void setSourceLocations(const agentspeed::LocationLists& lists);
    bool networkEnabled() const;
    void setNetworkEnabled(bool value);
    bool syncPrices() const;
    void setSyncPrices(bool value);
    bool autoCheckUpdates() const;
    void setAutoCheckUpdates(bool value);
    // DESIGN 10 Settings → 網路流量.
    bool trafficEnabled() const;
    void setTrafficEnabled(bool value);
    bool trafficIncludeTools() const;
    void setTrafficIncludeTools(bool value);
    QString trafficUnit() const;
    void setTrafficUnit(const QString& value);
    // REMOTE 2.1: the remote hosts document (`remoteHosts`) as JSON text; empty when none was saved.
    QString remoteHosts() const;
    void setRemoteHosts(const QString& json);
    // LAN 2.1: the local network document (`lan`) as JSON text; empty when none was saved.
    QString lan() const;
    void setLan(const QString& json);

    // DESIGN 5 額度: the switches `quotaCodex` … `quotaWorkBuddy` and `quotaGrokBuildLive` (QUOTAS 9.1 defaults).
    Q_INVOKABLE bool quotaSwitch(const QString& key) const;
    Q_INVOKABLE void setQuotaSwitch(const QString& key, bool on);
    bool quotaCliInviteDismissed() const;
    void setQuotaCliInviteDismissed(bool value);
    QString quotaZcodeRegion() const;
    void setQuotaZcodeRegion(const QString& value);
    int quotaRevision() const { return quotaRevision_; }

    Q_INVOKABLE bool sourceEnabled(const QString& source) const;
    Q_INVOKABLE void setSourceEnabled(const QString& source, bool enabled);
    Q_INVOKABLE bool subscribed(const QString& source) const;
    Q_INVOKABLE void setSubscribed(const QString& source, bool value);

    // What the monitor reads from these settings.
    agentspeed::MonitorConfig monitorConfig() const;

signals:
    void appearanceChanged();
    void languageChanged();
    void launchAtLoginChanged();
    void metricChanged();
    void redlineChanged();
    void effectsChanged();
    void celebratePeaksChanged();
    void responsePulsesChanged();
    void currencyChanged();
    void liveUnitChanged();
    void subscriptionsChanged();
    void includeSubscriptionsChanged();
    void sourcesChanged();
    void networkEnabledChanged();
    void syncPricesChanged();
    void trafficChanged();
    void autoCheckUpdatesChanged();
    void remoteHostsChanged();
    void lanChanged();
    void quotaChanged();

private:
    template <typename T>
    void store(const char* key, const T& value, void (Preferences::*signal)());

    mutable QSettings settings_;
    int quotaRevision_ = 0;
};
