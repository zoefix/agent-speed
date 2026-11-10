#include "preferences.hpp"

#include "agentspeed/quota/sources.hpp"

#include <QDir>
#include <QFile>
#include <QStandardPaths>

namespace {

QString autostartPath() {
    return QStandardPaths::writableLocation(QStandardPaths::ConfigLocation) + QStringLiteral("/autostart/agent-speed.desktop");
}

const QStringList kLanguages = {QStringLiteral("system"), QStringLiteral("zh-Hant"), QStringLiteral("zh-Hans"), QStringLiteral("en"),
                                QStringLiteral("ja")};

}  // namespace

Preferences::Preferences(QObject* parent) : QObject(parent) {
    connect(this, &Preferences::quotaChanged, this, [this] { ++quotaRevision_; });
}

template <typename T>
void Preferences::store(const char* key, const T& value, void (Preferences::*signal)()) {
    const auto name = QString::fromLatin1(key);
    if (settings_.contains(name) && settings_.value(name) == QVariant::fromValue(value)) return;
    settings_.setValue(name, QVariant::fromValue(value));
    emit(this->*signal)();
}

// The group is spelled "General": QSettings writes any case of it as [%General] and reads that back as "General".
QString Preferences::appearance() const { return settings_.value(QStringLiteral("General/appearance"), QStringLiteral("auto")).toString(); }
void Preferences::setAppearance(const QString& value) { store("General/appearance", value, &Preferences::appearanceChanged); }

QString Preferences::language() const {
    const auto value = settings_.value(QStringLiteral("General/language"), QStringLiteral("system")).toString();
    return kLanguages.contains(value) ? value : QStringLiteral("system");
}
void Preferences::setLanguage(const QString& value) {
    if (kLanguages.contains(value)) store("General/language", value, &Preferences::languageChanged);
}

bool Preferences::launchAtLogin() const { return QFile::exists(autostartPath()); }
void Preferences::setLaunchAtLogin(bool value) {
    if (value == launchAtLogin()) return;
    const auto path = autostartPath();
    if (value) {
        QDir().mkpath(QFileInfo(path).absolutePath());
        QFile file(path);
        if (file.open(QIODevice::WriteOnly | QIODevice::Truncate)) {
            file.write("[Desktop Entry]\nType=Application\nName=Agent Speed\nExec=agent-speed --hidden\nIcon=agent-speed\n"
                       "X-GNOME-Autostart-enabled=true\nNoDisplay=false\n");
        }
    } else {
        QFile::remove(path);
    }
    emit launchAtLoginChanged();
}

QString Preferences::metric() const { return settings_.value(QStringLiteral("General/metric"), QStringLiteral("output")).toString(); }
void Preferences::setMetric(const QString& value) { store("General/metric", value, &Preferences::metricChanged); }

int Preferences::redline() const { return std::clamp(settings_.value(QStringLiteral("General/redline"), 2000).toInt(), 50, 2000); }
void Preferences::setRedline(int value) { store("General/redline", std::clamp(value / 50 * 50, 50, 2000), &Preferences::redlineChanged); }

QString Preferences::effects() const { return settings_.value(QStringLiteral("effects/intensity"), QStringLiteral("standard")).toString(); }
void Preferences::setEffects(const QString& value) { store("effects/intensity", value, &Preferences::effectsChanged); }

bool Preferences::celebratePeaks() const { return settings_.value(QStringLiteral("effects/peaks"), true).toBool(); }
void Preferences::setCelebratePeaks(bool value) { store("effects/peaks", value, &Preferences::celebratePeaksChanged); }

bool Preferences::responsePulses() const { return settings_.value(QStringLiteral("effects/pulses"), true).toBool(); }
void Preferences::setResponsePulses(bool value) { store("effects/pulses", value, &Preferences::responsePulsesChanged); }

QString Preferences::currency() const { return settings_.value(QStringLiteral("cost/currency"), QStringLiteral("USD")).toString(); }
void Preferences::setCurrency(const QString& value) { store("cost/currency", value, &Preferences::currencyChanged); }

QString Preferences::liveUnit() const { return settings_.value(QStringLiteral("cost/liveUnit"), QStringLiteral("tokens")).toString(); }
void Preferences::setLiveUnit(const QString& value) { store("cost/liveUnit", value, &Preferences::liveUnitChanged); }

QStringList Preferences::subscriptions() const { return settings_.value(QStringLiteral("cost/subscriptions")).toStringList(); }
void Preferences::setSubscriptions(const QStringList& value) { store("cost/subscriptions", value, &Preferences::subscriptionsChanged); }

bool Preferences::includeSubscriptions() const { return settings_.value(QStringLiteral("cost/includeSubscriptions"), false).toBool(); }
void Preferences::setIncludeSubscriptions(bool value) {
    store("cost/includeSubscriptions", value, &Preferences::includeSubscriptionsChanged);
}

QStringList Preferences::disabledSources() const { return settings_.value(QStringLiteral("sources/disabled")).toStringList(); }

agentspeed::LocationLists Preferences::sourceLocations() const {
    const auto stored = settings_.value(QStringLiteral("sourceLocations"));
    if (stored.isValid()) {
        if (auto lists = agentspeed::LocationLists::fromDocument(stored.toString().toStdString())) return std::move(*lists);
    }
    std::map<agentspeed::RootFamily, std::string> legacy;
    const auto folders = settings_.value(QStringLiteral("sources/folders")).toMap();
    for (auto it = folders.begin(); it != folders.end(); ++it) {
        const auto kind = agentspeed::sourceFromId(it.key().toStdString());
        const auto path = it.value().toString().trimmed();
        if (kind && !path.isEmpty()) legacy.emplace(agentspeed::familyOf(*kind), path.toStdString());
    }
    return agentspeed::LocationLists::fromLegacy(legacy);
}

void Preferences::setSourceLocations(const agentspeed::LocationLists& lists) {
    if (settings_.contains(QStringLiteral("sourceLocations")) && lists == sourceLocations()) return;
    settings_.setValue(QStringLiteral("sourceLocations"), QString::fromStdString(lists.document()));
    emit sourcesChanged();
}

bool Preferences::networkEnabled() const { return settings_.value(QStringLiteral("network/enabled"), true).toBool(); }
void Preferences::setNetworkEnabled(bool value) { store("network/enabled", value, &Preferences::networkEnabledChanged); }

QString Preferences::remoteHosts() const { return settings_.value(QStringLiteral("remoteHosts")).toString(); }
void Preferences::setRemoteHosts(const QString& json) {
    store("remoteHosts", json, &Preferences::remoteHostsChanged);
    settings_.sync();
}

QString Preferences::lan() const { return settings_.value(QStringLiteral("lan")).toString(); }
void Preferences::setLan(const QString& json) {
    store("lan", json, &Preferences::lanChanged);
    settings_.sync();
}

bool Preferences::syncPrices() const { return settings_.value(QStringLiteral("network/syncPrices"), true).toBool(); }
void Preferences::setSyncPrices(bool value) { store("network/syncPrices", value, &Preferences::syncPricesChanged); }
bool Preferences::trafficEnabled() const { return settings_.value(QStringLiteral("traffic/enabled"), true).toBool(); }
void Preferences::setTrafficEnabled(bool value) { store("traffic/enabled", value, &Preferences::trafficChanged); }
bool Preferences::trafficIncludeTools() const { return settings_.value(QStringLiteral("traffic/includeTools"), true).toBool(); }
void Preferences::setTrafficIncludeTools(bool value) { store("traffic/includeTools", value, &Preferences::trafficChanged); }
QString Preferences::trafficUnit() const {
    return settings_.value(QStringLiteral("traffic/unit")).toString() == QLatin1String("bits") ? QStringLiteral("bits") : QStringLiteral("bytes");
}
void Preferences::setTrafficUnit(const QString& value) { store("traffic/unit", value, &Preferences::trafficChanged); }

bool Preferences::autoCheckUpdates() const { return settings_.value(QStringLiteral("network/autoCheckUpdates"), true).toBool(); }
void Preferences::setAutoCheckUpdates(bool value) { store("network/autoCheckUpdates", value, &Preferences::autoCheckUpdatesChanged); }

namespace {

std::optional<bool> quotaDefault(const QString& key) {
    if (key == QLatin1String("quotaGrokBuildLive")) return false;
    for (const auto& route : agentspeed::quota::sourceRoutes()) {
        if (key.toStdString() == route.switchKey) return route.defaultOn;
    }
    return std::nullopt;
}

}  // namespace

bool Preferences::quotaSwitch(const QString& key) const {
    const auto fallback = quotaDefault(key);
    if (!fallback) return false;
    return settings_.value(QStringLiteral("quota/") + key, *fallback).toBool();
}

void Preferences::setQuotaSwitch(const QString& key, bool on) {
    if (!quotaDefault(key) || quotaSwitch(key) == on) return;
    settings_.setValue(QStringLiteral("quota/") + key, on);
    emit quotaChanged();
}

bool Preferences::quotaCliInviteDismissed() const {
    return settings_.value(QStringLiteral("quota/quotaCliInvite")).toString() == QLatin1String("dismissed");
}

void Preferences::setQuotaCliInviteDismissed(bool value) {
    if (value == quotaCliInviteDismissed()) return;
    if (value) {
        settings_.setValue(QStringLiteral("quota/quotaCliInvite"), QStringLiteral("dismissed"));
    } else {
        settings_.remove(QStringLiteral("quota/quotaCliInvite"));
    }
    emit quotaChanged();
}

QString Preferences::quotaZcodeRegion() const {
    return settings_.value(QStringLiteral("quota/quotaZcodeRegion")).toString() == QLatin1String("bigmodel-cn") ? QStringLiteral("bigmodel-cn")
                                                                                                            : QStringLiteral("global");
}

void Preferences::setQuotaZcodeRegion(const QString& value) {
    if (value != QLatin1String("global") && value != QLatin1String("bigmodel-cn")) return;
    store("quota/quotaZcodeRegion", value, &Preferences::quotaChanged);
}

bool Preferences::sourceEnabled(const QString& source) const { return !disabledSources().contains(source); }

void Preferences::setSourceEnabled(const QString& source, bool enabled) {
    auto disabled = disabledSources();
    if (enabled == !disabled.contains(source)) return;
    if (enabled) {
        disabled.removeAll(source);
    } else {
        disabled.append(source);
    }
    settings_.setValue(QStringLiteral("sources/disabled"), disabled);
    emit sourcesChanged();
}

bool Preferences::subscribed(const QString& source) const { return subscriptions().contains(source); }

void Preferences::setSubscribed(const QString& source, bool value) {
    auto list = subscriptions();
    if (value == list.contains(source)) return;
    if (value) {
        list.append(source);
    } else {
        list.removeAll(source);
    }
    setSubscriptions(list);
}

agentspeed::MonitorConfig Preferences::monitorConfig() const {
    using agentspeed::SourceKind;
    auto enabled = agentspeed::MonitorConfig::allSources();
    for (const auto& id : disabledSources()) {
        if (const auto kind = agentspeed::sourceFromId(id.toStdString())) enabled.erase(*kind);
    }
    // Claude Code and Claude Desktop share one root and one switch.
    if (!enabled.contains(SourceKind::ClaudeCode)) enabled.erase(SourceKind::ClaudeDesktop);
    return agentspeed::MonitorConfig::fromEnvironment(sourceLocations(), enabled);
}
