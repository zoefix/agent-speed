#include "report/report_settings.hpp"

#include <QJsonArray>
#include <QJsonDocument>
#include <QJsonObject>
#include <algorithm>

namespace {

const QString kPreset = QStringLiteral("https://deng.codexradar.com");

QJsonObject pinJson(const agentspeed::report::ConsentPin& pin) {
    QJsonArray sections;
    for (const auto& s : pin.sections) sections.append(QString::fromStdString(s));
    QJsonObject o{{QStringLiteral("name"), QString::fromStdString(pin.name)},
                  {QStringLiteral("terms"), QString::fromStdString(pin.terms)},
                  {QStringLiteral("privacy"), QString::fromStdString(pin.privacy)},
                  {QStringLiteral("api"), QString::fromStdString(pin.api)},
                  {QStringLiteral("sections"), sections},
                  {QStringLiteral("apiKeyMode"), QString::fromStdString(pin.apiKeyMode)}};
    if (pin.retentionDays) o.insert(QStringLiteral("retentionDays"), static_cast<qint64>(*pin.retentionDays));
    return o;
}

std::optional<agentspeed::report::ConsentPin> pinOf(const QJsonValue& value) {
    if (!value.isObject()) return std::nullopt;
    const auto o = value.toObject();
    agentspeed::report::ConsentPin pin;
    pin.name = o.value(QStringLiteral("name")).toString().toStdString();
    pin.terms = o.value(QStringLiteral("terms")).toString().toStdString();
    pin.privacy = o.value(QStringLiteral("privacy")).toString().toStdString();
    pin.api = o.value(QStringLiteral("api")).toString().toStdString();
    for (const auto& s : o.value(QStringLiteral("sections")).toArray()) pin.sections.push_back(s.toString().toStdString());
    std::sort(pin.sections.begin(), pin.sections.end());
    if (o.contains(QStringLiteral("retentionDays"))) pin.retentionDays = o.value(QStringLiteral("retentionDays")).toInteger();
    pin.apiKeyMode = o.value(QStringLiteral("apiKeyMode")).toString(QStringLiteral("none")).toStdString();
    return pin;
}

}  // namespace

bool ReportSettings::enabled() const { return settings_.value(QStringLiteral("report/enabled"), false).toBool(); }
void ReportSettings::setEnabled(bool value) { settings_.setValue(QStringLiteral("report/enabled"), value); }
QString ReportSettings::service() const { return settings_.value(QStringLiteral("report/service"), kPreset).toString(); }
void ReportSettings::setService(const QString& url) { settings_.setValue(QStringLiteral("report/service"), url); }
bool ReportSettings::custom() const { return settings_.value(QStringLiteral("report/custom"), false).toBool(); }
void ReportSettings::setCustom(bool value) { settings_.setValue(QStringLiteral("report/custom"), value); }
QString ReportSettings::customUrl() const { return settings_.value(QStringLiteral("report/customUrl")).toString(); }
void ReportSettings::setCustomUrl(const QString& text) { settings_.setValue(QStringLiteral("report/customUrl"), text); }

bool ReportSettings::sectionOn(agentspeed::report::Section section) const {
    return !settings_.value(QStringLiteral("report/sectionsOff")).toStringList().contains(QLatin1String(agentspeed::report::sectionName(section)));
}

void ReportSettings::setSection(agentspeed::report::Section section, bool on) {
    auto off = settings_.value(QStringLiteral("report/sectionsOff")).toStringList();
    const auto name = QLatin1String(agentspeed::report::sectionName(section));
    off.removeAll(name);
    if (!on) off.append(name);
    settings_.setValue(QStringLiteral("report/sectionsOff"), off);
}

qint64 ReportSettings::pauseUntil() const { return settings_.value(QStringLiteral("report/pauseUntil"), 0).toLongLong(); }
void ReportSettings::setPauseUntil(qint64 value) { settings_.setValue(QStringLiteral("report/pauseUntil"), value); }
QString ReportSettings::invite() const { return settings_.value(QStringLiteral("report/invite"), QStringLiteral("unanswered")).toString(); }
void ReportSettings::setInvite(const QString& answer) { settings_.setValue(QStringLiteral("report/invite"), answer); }
QString ReportSettings::username() const { return settings_.value(QStringLiteral("report/username")).toString(); }
void ReportSettings::setUsername(const QString& name) { settings_.setValue(QStringLiteral("report/username"), name); }

bool ReportSettings::http(const QString& origin) const {
    return settings_.value(QStringLiteral("report/httpOrigins")).toStringList().contains(origin);
}

void ReportSettings::setHttp(const QString& origin, bool http) {
    auto list = settings_.value(QStringLiteral("report/httpOrigins")).toStringList();
    list.removeAll(origin);
    if (http) list.append(origin);
    while (list.size() > kMaxHttpOrigins) list.removeFirst();
    settings_.setValue(QStringLiteral("report/httpOrigins"), list);
}

std::vector<ReportIdentity> ReportSettings::identities() const {
    std::vector<ReportIdentity> out;
    const auto doc = QJsonDocument::fromJson(settings_.value(QStringLiteral("report/identities")).toByteArray());
    for (const auto& value : doc.array()) {
        const auto o = value.toObject();
        ReportIdentity id;
        id.origin = o.value(QStringLiteral("origin")).toString();
        id.service = o.value(QStringLiteral("service")).toString();
        id.deviceId = o.value(QStringLiteral("deviceId")).toString();
        id.username = o.value(QStringLiteral("username")).toString();
        id.profileUrl = o.value(QStringLiteral("profileUrl")).toString();
        id.keyCreatedMs = o.value(QStringLiteral("keyCreated")).toInteger();
        id.pendingKey = o.value(QStringLiteral("pendingKey")).toBool();
        id.registered = o.value(QStringLiteral("registered")).toBool();
        id.pin = pinOf(o.value(QStringLiteral("pin")));
        id.discovery = o.value(QStringLiteral("discovery")).toString();
        id.fetchedMs = o.value(QStringLiteral("fetched")).toInteger();
        id.lastUsedMs = o.value(QStringLiteral("lastUsed")).toInteger();
        if (!id.origin.isEmpty() && !id.deviceId.isEmpty()) out.push_back(std::move(id));
    }
    return out;
}

std::optional<ReportIdentity> ReportSettings::identity(const QString& origin) const {
    for (auto& id : identities())
        if (id.origin == origin) return id;
    return std::nullopt;
}

void ReportSettings::write(const std::vector<ReportIdentity>& list) {
    QJsonArray array;
    for (const auto& id : list) {
        QJsonObject o{{QStringLiteral("origin"), id.origin},         {QStringLiteral("service"), id.service},
                      {QStringLiteral("deviceId"), id.deviceId},     {QStringLiteral("username"), id.username},
                      {QStringLiteral("profileUrl"), id.profileUrl}, {QStringLiteral("keyCreated"), id.keyCreatedMs},
                      {QStringLiteral("pendingKey"), id.pendingKey}, {QStringLiteral("registered"), id.registered},
                      {QStringLiteral("discovery"), id.discovery},   {QStringLiteral("fetched"), id.fetchedMs},
                      {QStringLiteral("lastUsed"), id.lastUsedMs}};
        if (id.pin) o.insert(QStringLiteral("pin"), pinJson(*id.pin));
        array.append(o);
    }
    settings_.setValue(QStringLiteral("report/identities"), QJsonDocument(array).toJson(QJsonDocument::Compact));
}

QStringList ReportSettings::save(const ReportIdentity& identity) {
    auto list = identities();
    std::erase_if(list, [&](const ReportIdentity& id) { return id.origin == identity.origin; });
    list.push_back(identity);
    QStringList dropped;
    while (static_cast<int>(list.size()) > kMaxIdentities) {
        auto oldest = std::min_element(list.begin(), list.end() - 1, [](const auto& a, const auto& b) { return a.lastUsedMs < b.lastUsedMs; });
        dropped.append(oldest->deviceId);
        list.erase(oldest);
    }
    write(list);
    return dropped;
}

void ReportSettings::remove(const QString& origin) {
    auto list = identities();
    std::erase_if(list, [&](const ReportIdentity& id) { return id.origin == origin; });
    write(list);
}
