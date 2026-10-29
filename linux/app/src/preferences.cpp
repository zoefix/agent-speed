#include "preferences.hpp"

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

Preferences::Preferences(QObject* parent) : QObject(parent) {}

template <typename T>
void Preferences::store(const char* key, const T& value, void (Preferences::*signal)()) {
    const auto name = QString::fromLatin1(key);
    if (settings_.contains(name) && settings_.value(name) == QVariant::fromValue(value)) return;
    settings_.setValue(name, QVariant::fromValue(value));
    emit(this->*signal)();
}

QString Preferences::appearance() const { return settings_.value(QStringLiteral("general/appearance"), QStringLiteral("auto")).toString(); }
void Preferences::setAppearance(const QString& value) { store("general/appearance", value, &Preferences::appearanceChanged); }

QString Preferences::language() const {
    const auto value = settings_.value(QStringLiteral("general/language"), QStringLiteral("system")).toString();
    return kLanguages.contains(value) ? value : QStringLiteral("system");
}
void Preferences::setLanguage(const QString& value) {
    if (kLanguages.contains(value)) store("general/language", value, &Preferences::languageChanged);
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

QString Preferences::metric() const { return settings_.value(QStringLiteral("general/metric"), QStringLiteral("output")).toString(); }
void Preferences::setMetric(const QString& value) { store("general/metric", value, &Preferences::metricChanged); }

int Preferences::redline() const { return std::clamp(settings_.value(QStringLiteral("general/redline"), 2000).toInt(), 50, 2000); }
void Preferences::setRedline(int value) { store("general/redline", std::clamp(value / 50 * 50, 50, 2000), &Preferences::redlineChanged); }

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
QVariantMap Preferences::folders() const { return settings_.value(QStringLiteral("sources/folders")).toMap(); }

bool Preferences::networkEnabled() const { return settings_.value(QStringLiteral("network/enabled"), true).toBool(); }
void Preferences::setNetworkEnabled(bool value) { store("network/enabled", value, &Preferences::networkEnabledChanged); }

bool Preferences::syncPrices() const { return settings_.value(QStringLiteral("network/syncPrices"), true).toBool(); }
void Preferences::setSyncPrices(bool value) { store("network/syncPrices", value, &Preferences::syncPricesChanged); }

bool Preferences::autoCheckUpdates() const { return settings_.value(QStringLiteral("network/autoCheckUpdates"), true).toBool(); }
void Preferences::setAutoCheckUpdates(bool value) { store("network/autoCheckUpdates", value, &Preferences::autoCheckUpdatesChanged); }

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

void Preferences::setFolder(const QString& source, const QString& path) {
    auto map = folders();
    const auto trimmed = path.trimmed();
    if (trimmed.isEmpty() ? !map.contains(source) : map.value(source).toString() == trimmed) return;
    if (trimmed.isEmpty()) {
        map.remove(source);
    } else {
        map.insert(source, trimmed);
    }
    settings_.setValue(QStringLiteral("sources/folders"), map);
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
    agentspeed::RootSettings roots;
    const auto map = folders();
    const auto folder = [&](SourceKind kind) -> std::optional<std::string> {
        const auto value = map.value(QString::fromUtf8(agentspeed::sourceId(kind))).toString();
        if (value.isEmpty()) return std::nullopt;
        return value.toStdString();
    };
    roots.codex = folder(SourceKind::Codex);
    roots.claude = folder(SourceKind::ClaudeCode);
    roots.dsh = folder(SourceKind::Deepseek);
    roots.openCode = folder(SourceKind::OpenCode);
    roots.gemini = folder(SourceKind::Gemini);
    roots.omp = folder(SourceKind::Omp);
    roots.antigravity = folder(SourceKind::Antigravity);
    roots.workBuddy = folder(SourceKind::WorkBuddy);
    roots.workBuddyAi = folder(SourceKind::WorkBuddyAi);
    roots.zcode = folder(SourceKind::ZCode);
    roots.kimi = folder(SourceKind::Kimi);
    roots.grokBuild = folder(SourceKind::GrokBuild);
    roots.pi = folder(SourceKind::Pi);
    roots.openClaw = folder(SourceKind::OpenClaw);
    roots.hermes = folder(SourceKind::Hermes);
    roots.droid = folder(SourceKind::Droid);
    auto enabled = agentspeed::MonitorConfig::allSources();
    for (const auto& id : disabledSources()) {
        if (const auto kind = agentspeed::sourceFromId(id.toStdString())) enabled.erase(*kind);
    }
    // Claude Code and Claude Desktop share one root and one switch.
    if (!enabled.contains(SourceKind::ClaudeCode)) enabled.erase(SourceKind::ClaudeDesktop);
    return agentspeed::MonitorConfig::fromEnvironment(roots, enabled);
}
