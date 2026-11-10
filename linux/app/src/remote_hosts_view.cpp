#include "remote_hosts_view.hpp"

#include <QClipboard>
#include <QDateTime>
#include <QGuiApplication>
#include <QLocale>

#include "agentspeed/remote_host.hpp"
#include "agentspeed/remote_presentation.hpp"
#include "app_state.hpp"
#include "history_service.hpp"
#include "preferences.hpp"
#include "remote_service.hpp"
#include "strings.hpp"

namespace {

namespace remote = agentspeed::remote;
namespace ui = agentspeed::remote::ui;
namespace hosts = agentspeed::hosts;

constexpr const char* kLocal = "local";
// An id no settings host can have: the editor's preview of a new host's automatic name.
constexpr const char* kPreviewId = "00000000-0000-4000-8000-000000000000";

QString qt(const std::string& text) { return QString::fromStdString(text); }
QString qt(std::string_view text) { return QString::fromUtf8(text.data(), static_cast<qsizetype>(text.size())); }

QString phrase(const Strings& strings, const ui::Phrase& p) {
    QVariantList args;
    for (const auto& a : p.args) args.push_back(qt(a));
    return strings.fmt(qt(p.key), args);
}

QString lineText(const Strings& strings, const ui::Line& line) {
    const auto text = phrase(strings, line.sentence);
    if (line.retryInSeconds) return strings.fmt(QStringLiteral("remote.status.retryIn"), {text, static_cast<qlonglong>(*line.retryInSeconds)});
    if (line.retryingNow) return strings.fmt(QStringLiteral("remote.status.retryingNow"), {text});
    return text;
}

// "all" or a list of ids as an orb or board stores them.
hosts::HostSelection selectionOf(const QVariant& value) {
    if (value.typeId() == QMetaType::QString) {
        const auto text = value.toString();
        return text == QLatin1String("all") || text.isEmpty() ? hosts::HostSelection::all() : hosts::HostSelection::normalized({text.toStdString()});
    }
    std::vector<std::string> ids;
    for (const auto& id : value.toStringList()) ids.push_back(id.toStdString());
    return hosts::HostSelection::normalized(ids);
}

QVariant variantOf(const hosts::HostSelection& selection) {
    if (selection.isAll()) return QStringLiteral("all");
    QStringList ids;
    for (const auto& id : selection.ids()) ids.push_back(qt(id));
    return ids;
}

}  // namespace

RemoteHostsView::RemoteHostsView(RemoteService& remote, const Preferences& prefs, const AppState& state, const HistoryModel& history,
                                 const Strings& strings, QObject* parent)
    : QObject(parent), remote_(remote), prefs_(prefs), state_(state), history_(history), strings_(strings) {
    connect(&remote_, &RemoteService::hostsChanged, this, &RemoteHostsView::refresh);
    connect(&prefs_, &Preferences::networkEnabledChanged, this, &RemoteHostsView::refresh);
    connect(&state_, &AppState::snapshotChanged, this, &RemoteHostsView::refresh);
    connect(&history_, &HistoryModel::tooLargeHostsChanged, this, &RemoteHostsView::refresh);
    connect(&history_, &HistoryModel::hostsChanged, this, &RemoteHostsView::refresh);
    connect(&strings_, &Strings::changed, this, &RemoteHostsView::refresh);
    refresh();
}

QString RemoteHostsView::localName() const { return strings_.t(QStringLiteral("traffic.machine")); }

std::vector<hosts::HostChoice> RemoteHostsView::hostChoices() const {
    const auto& document = remote_.document();
    if (document.hosts().empty()) return {};
    const auto names = document.displayNames();
    std::vector<hosts::HostChoice> out{{kLocal, localName().toStdString()}};
    for (const auto& h : document.hosts()) out.push_back({h.id, names.contains(h.id) ? names.at(h.id) : h.automaticName()});
    return out;
}

std::vector<std::string> RemoteHostsView::hostOrder() const {
    std::vector<std::string> out{kLocal};
    for (const auto& h : remote_.document().hosts()) out.push_back(h.id);
    return out;
}

remote::HostStatus RemoteHostsView::statusOf(const std::string& id) const {
    const auto* host = remote_.document().find(id);
    if (!host) {
        remote::HostStatus connected;
        connected.status = remote::Status::of(remote::StatusCode::Connected);
        return connected;
    }
    const auto it = statuses_.find(id);
    const bool tooLarge = history_.tooLargeHosts().contains(qt(id));
    return ui::effective(*host, prefs_.networkEnabled(), it == statuses_.end() ? std::nullopt : std::optional(it->second), tooLarge);
}

QString RemoteHostsView::dotOf(const std::string& id) const { return qt(ui::dotName(ui::dotOf(statusOf(id).status.code))); }

QString RemoteHostsView::wordOf(const std::string& id) const {
    if (!remote_.document().find(id)) return {};
    const auto code = statusOf(id).status.code;
    return code == remote::StatusCode::Connected ? QString() : phrase(strings_, ui::word(code));
}

int RemoteHostsView::sessionsOf(const QString& id) const {
    for (const auto& item : state_.snapshot().value(QStringLiteral("hosts")).toList()) {
        const auto row = item.toMap();
        if (row.value(QStringLiteral("id")).toString() == id) return row.value(QStringLiteral("sessions")).toInt();
    }
    return 0;
}

QString RemoteHostsView::tooltipText(const remote::HostStatus& status, qint64 nowMs) const {
    const auto facts = ui::tooltip(status);
    QStringList lines;
    if (facts.relayVersion && facts.relayArch) {
        lines << strings_.fmt(QStringLiteral("remote.status.relay"), {qt(*facts.relayVersion), qt(*facts.relayArch)});
    }
    if (facts.lastErrorAtMs) {
        // `14:32` today, else the short date and time, in the app's language.
        const auto at = QDateTime::fromMSecsSinceEpoch(*facts.lastErrorAtMs);
        auto language = strings_.language();
        const QLocale locale(language == QLatin1String("en") ? QStringLiteral("en_US") : language.replace(QLatin1Char('-'), QLatin1Char('_')));
        const bool today = at.date() == QDateTime::fromMSecsSinceEpoch(nowMs).date();
        const auto text = today ? locale.toString(at.time(), QLocale::ShortFormat) : locale.toString(at, QLocale::ShortFormat);
        lines << strings_.fmt(QStringLiteral("remote.status.lastError"), {text});
    }
    if (facts.exitStatus) lines << strings_.fmt(QStringLiteral("remote.status.exitCode"), {*facts.exitStatus});
    return lines.join(QLatin1Char('\n'));
}

void RemoteHostsView::refresh() {
    statuses_ = remote_.statuses();
    const auto& document = remote_.document();
    const qint64 now = QDateTime::currentMSecsSinceEpoch();
    const bool shown = !document.hosts().empty();
    const auto names = document.displayNames();

    QVariantList rows;
    for (const auto& h : document.hosts()) {
        const auto status = statusOf(h.id);
        const auto id = qt(h.id);
        const auto name = names.contains(h.id) ? qt(names.at(h.id)) : qt(h.automaticName());
        const auto destination = qt(h.destination);
        QVariantMap row;
        row.insert(QStringLiteral("hostId"), id);
        row.insert(QStringLiteral("name"), name);
        row.insert(QStringLiteral("destination"), destination);
        row.insert(QStringLiteral("showDestination"), name != destination);
        row.insert(QStringLiteral("hostEnabled"), h.enabled);
        row.insert(QStringLiteral("customName"), h.name ? qt(*h.name) : QString());
        row.insert(QStringLiteral("relayFolder"), h.relayFolder ? qt(*h.relayFolder) : QString());
        row.insert(QStringLiteral("effectiveRelayFolder"), qt(h.effectiveRelayFolder()));
        row.insert(QStringLiteral("status"), qt(remote::statusName(status.status.code)));
        row.insert(QStringLiteral("dot"), qt(ui::dotName(ui::dotOf(status.status.code))));
        row.insert(QStringLiteral("line"), lineText(strings_, ui::line(status, h.destination, sessionsOf(id), now)));
        row.insert(QStringLiteral("tooltip"), tooltipText(status, now));
        const auto clock = ui::clockNote(status);
        row.insert(QStringLiteral("clockNote"), clock ? phrase(strings_, *clock) : QString());
        const auto history = ui::historyNote(status);
        row.insert(QStringLiteral("historyNote"), history ? phrase(strings_, *history) : QString());
        row.insert(QStringLiteral("canRetry"), ui::canRetry(status.status.code));
        row.insert(QStringLiteral("connected"), status.status.code == remote::StatusCode::Connected);
        row.insert(QStringLiteral("word"), wordOf(h.id));
        rows.push_back(row);
    }

    QVariantList choices;
    for (const auto& c : hostChoices()) {
        const bool local = c.id == kLocal;
        choices.push_back(QVariantMap{{QStringLiteral("id"), qt(c.id)},
                                      {QStringLiteral("name"), qt(c.name)},
                                      {QStringLiteral("local"), local},
                                      {QStringLiteral("dot"), local ? QStringLiteral("connected") : dotOf(c.id)},
                                      {QStringLiteral("word"), local ? QString() : wordOf(c.id)}});
    }

    QVariantList dashboard;
    if (shown) {
        for (const auto& item : state_.snapshot().value(QStringLiteral("hosts")).toList()) {
            auto row = item.toMap();
            if (!row.value(QStringLiteral("listed")).toBool()) continue;
            const auto id = row.value(QStringLiteral("id")).toString().toStdString();
            const bool local = id == kLocal;
            const auto code = local ? remote::StatusCode::Connected : statusOf(id).status.code;
            row.insert(QStringLiteral("key"), qt(id));
            row.insert(QStringLiteral("name"), nameOf(qt(id)));
            row.insert(QStringLiteral("dot"), qt(ui::dotName(ui::dotOf(code))));
            row.insert(QStringLiteral("connected"), code == remote::StatusCode::Connected);
            row.insert(QStringLiteral("word"), local ? QString() : wordOf(id));
            dashboard.push_back(row);
        }
    }

    QStringList nameKeys;
    for (const auto& c : hostChoices()) nameKeys << qt(c.id) + QLatin1Char('=') + qt(c.name);
    const bool renamed = nameKeys != names_;
    names_ = nameKeys;
    const bool changedNow = shown != shown_ || choices != choices_;
    const bool rowsNow = rows != rows_;
    shown_ = shown;
    rows_ = rows;
    choices_ = choices;
    if (changedNow) emit changed();
    if (rowsNow) emit rowsChanged();
    if (renamed) emit namesChanged();
    if (dashboard != dashboard_) {
        dashboard_ = dashboard;
        emit dashboardChanged();
    }
}

QString RemoteHostsView::nameOf(const QString& id) const {
    if (id == QLatin1String(kLocal)) return localName();
    if (const auto name = remote_.document().displayName(id.toStdString())) return qt(*name);
    for (const auto& item : history_.hosts()) {
        const auto host = item.toMap();
        if (host.value(QStringLiteral("id")).toString() == id) return host.value(QStringLiteral("name")).toString();
    }
    for (const auto& item : state_.snapshot().value(QStringLiteral("hosts")).toList()) {
        const auto host = item.toMap();
        if (host.value(QStringLiteral("id")).toString() == id) return host.value(QStringLiteral("name")).toString();
    }
    return id;
}

QVariantList RemoteHostsView::entries(const QVariant& value) const {
    const auto selection = selectionOf(value);
    QVariantList out;
    for (const auto& e : selection.entries(hostChoices())) {
        const bool local = e.id == kLocal;
        out.push_back(QVariantMap{{QStringLiteral("id"), qt(e.id)},
                                  {QStringLiteral("name"), qt(e.name)},
                                  {QStringLiteral("checked"), e.checked},
                                  {QStringLiteral("enabled"), e.enabled},
                                  {QStringLiteral("local"), local},
                                  {QStringLiteral("dot"), local ? QStringLiteral("connected") : dotOf(e.id)},
                                  {QStringLiteral("word"), local ? QString() : wordOf(e.id)}});
    }
    return out;
}

QVariant RemoteHostsView::toggled(const QVariant& value, const QString& id) const {
    const auto next = selectionOf(value).toggling(id.toStdString(), hostOrder());
    return next ? variantOf(*next) : QVariant();
}

QVariant RemoteHostsView::allHosts(bool on) const {
    return on ? QVariant(QStringLiteral("all")) : variantOf(hosts::HostSelection::normalized(hostOrder()));
}

QString RemoteHostsView::nameSuffix(const QVariant& value) const {
    const auto suffix = hosts::nameSuffix(selectionOf(value), hostChoices());
    if (!suffix) return {};
    const auto first = qt(suffix->first);
    return QStringLiteral(" · ") +
           (suffix->others == 0 ? first : strings_.fmt(QStringLiteral("orbs.name.more"), {first, static_cast<qlonglong>(suffix->others)}));
}

QString RemoteHostsView::nameSuffix(const std::optional<QStringList>& value) const {
    return value ? nameSuffix(QVariant(*value)) : QString();
}

QString RemoteHostsView::destinationError(const QString& destination) const {
    const auto issue = remote::validation::destinationIssue(destination.trimmed().toStdString());
    return issue ? phrase(strings_, ui::fieldError(*issue, false)) : QString();
}

QString RemoteHostsView::folderError(const QString& folder) const {
    const auto trimmed = folder.trimmed();
    if (trimmed.isEmpty()) return {};
    const auto issue = remote::validation::folderIssue(trimmed.toStdString());
    return issue ? phrase(strings_, ui::fieldError(*issue, true)) : QString();
}

QString RemoteHostsView::nameError(const QString& name) const {
    const auto error = ui::nameError(name.toStdString());
    return error ? phrase(strings_, *error) : QString();
}

QString RemoteHostsView::automaticName(const QString& destination, const QString& editingId) const {
    const auto clean = destination.trimmed().toStdString();
    if (remote::validation::destinationIssue(clean)) return {};
    auto preview = remote_.document();
    const auto* original = editingId.isEmpty() ? nullptr : preview.find(editingId.toStdString());
    auto host = remote::Host::make(original ? original->id : std::string(kPreviewId), clean);
    if (original) {
        host.enabled = original->enabled;
        host.relayFolder = original->relayFolder;
        preview.update(host);
    } else if (!preview.add(host)) {
        return qt(host.automaticName());
    }
    const auto name = preview.displayName(host.id);
    return qt(name ? *name : host.automaticName());
}

QVariantMap RemoteHostsView::testResult(const QVariantMap& result, const QString& destination) const {
    if (result.value(QStringLiteral("supported")).toBool()) {
        auto system = result.value(QStringLiteral("system")).toString();
        const auto arch = result.value(QStringLiteral("arch")).toString();
        if (!arch.isEmpty() && !system.contains(arch)) system += QLatin1Char(' ') + arch;
        return {{QStringLiteral("ok"), true},
                {QStringLiteral("attention"), false},
                {QStringLiteral("text"), strings_.fmt(QStringLiteral("remote.test.ok"), {system})}};
    }
    const auto code = ui::statusFromName(result.value(QStringLiteral("status")).toString().toStdString());
    auto status = remote::Status::of(code.value_or(remote::StatusCode::Exited));
    status.system = result.value(QStringLiteral("system")).toString().toStdString();
    status.exitStatus = result.value(QStringLiteral("exitStatus")).toInt();
    return {{QStringLiteral("ok"), false},
            {QStringLiteral("attention"), ui::dotOf(status.code) == ui::Dot::Attention},
            {QStringLiteral("text"), phrase(strings_, ui::sentence(status, destination.trimmed().toStdString()))}};
}

QString RemoteHostsView::removeFiles(const QString& id) const {
    const auto* host = remote_.document().find(id.toStdString());
    const auto folder = host ? qt(host->effectiveRelayFolder()) : QStringLiteral("~/.cache/agent-speed");
    auto text = strings_.fmt(QStringLiteral("remote.remove.files"), {folder});
    // REMOTE 2.4, 11.3: a host whose bridge is on restores its status line first when it can be reached.
    if (host && host->quotaClaudeCli) text += QStringLiteral("\n\n") + strings_.fmt(QStringLiteral("remote.quota.removeNote"), {folder});
    return text;
}

void RemoteHostsView::copySshCommand(const QString& id) const {
    const auto command = remote_.sshCommand(id);
    if (command.isEmpty()) return;
    if (auto* clipboard = QGuiApplication::clipboard()) clipboard->setText(command);
}
