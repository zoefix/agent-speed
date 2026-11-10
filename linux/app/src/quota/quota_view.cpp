#include "quota/quota_view.hpp"

#include <QDateTime>

#include "agentspeed/quota/sources.hpp"
#include "quota/quota_text.hpp"
#include "strings.hpp"

namespace quota = agentspeed::quota;
namespace remote = agentspeed::remote;
using agentspeed::SourceKind;

namespace {

// DESIGN 4: a source with a quota observed within the last 24 hours is listed.
constexpr std::int64_t kListedWithinMs = 24 * 3'600'000;

QString key(SourceKind source) { return QString::fromUtf8(agentspeed::sourceId(source)); }

bool hasWindows(const std::optional<quota::Snapshot>& snapshot) { return snapshot && !snapshot->windows.empty(); }

}  // namespace

namespace quota_status {

QuotaStatusLine route(const Strings& strings, const QString& code, const QString& name, const QString& host, std::int64_t atMs,
                      std::int64_t nowMs) {
    if (code == QLatin1String("ok")) {
        return {strings.fmt(QStringLiteral("settings.quota.status.ok"), {quota_text::updatedTime(atMs, nowMs)}), false, true};
    }
    if (code == QLatin1String("noCredential")) return {strings.t(QStringLiteral("settings.quota.status.noCredential")), true};
    if (code == QLatin1String("noBrowser")) return {strings.fmt(QStringLiteral("settings.quota.status.noBrowser"), {name}), true};
    if (code == QLatin1String("expired")) return {strings.fmt(QStringLiteral("settings.quota.status.expired"), {name}), true};
    if (code == QLatin1String("denied")) return {strings.fmt(QStringLiteral("settings.quota.status.denied"), {host}), true};
    if (code == QLatin1String("failed")) return {strings.t(QStringLiteral("settings.quota.status.failed")), true};
    if (code == QLatin1String("networkOff")) return {strings.t(QStringLiteral("settings.quota.status.networkOff")), true};
    if (code == QLatin1String("noCertificates")) return {strings.t(QStringLiteral("settings.quota.status.noCertificates")), true};
    if (code == QLatin1String("notRunning")) return {strings.fmt(QStringLiteral("settings.quota.status.notRunning"), {name}), false};
    return {};
}

QuotaStatusLine bridge(const Strings& strings, const QString& state, const QString& path, std::optional<std::int64_t> atMs, std::int64_t nowMs) {
    if (state == QLatin1String("waiting")) return {strings.t(QStringLiteral("settings.quota.cli.waiting")), false};
    if (state == QLatin1String("ok")) {
        return {strings.fmt(QStringLiteral("settings.quota.cli.ok"), {atMs ? quota_text::updatedTime(*atMs, nowMs) : QString()}), false, true};
    }
    if (state == QLatin1String("replaced")) return {strings.t(QStringLiteral("settings.quota.cli.replaced")), true};
    if (state == QLatin1String("unreadable")) return {strings.fmt(QStringLiteral("settings.quota.cli.unreadable"), {path}), true};
    if (state == QLatin1String("failed")) return {strings.fmt(QStringLiteral("settings.quota.cli.failed"), {path}), true};
    return {};
}

}  // namespace quota_status

QuotaView::QuotaView(const Strings& strings, QObject* parent) : QObject(parent), strings_(strings) {
    refresh_.setInterval(15'000);
    connect(&refresh_, &QTimer::timeout, this, &QuotaView::rebuild);
    refresh_.start();
    connect(&strings_, &Strings::changed, this, &QuotaView::rebuild);
}

std::int64_t QuotaView::now() const { return QDateTime::currentMSecsSinceEpoch(); }

void QuotaView::setLocal(std::vector<QuotaSourceState> sources) {
    if (demo_) return;
    local_ = std::move(sources);
    rebuild();
}

void QuotaView::setPublication(const QuotaPublication& publication) {
    if (demo_) return;
    local_ = publication.sources;
    rawStatuses_ = publication.statuses;
    setCli(publication.cliPath.isEmpty() ? cliPath_ : publication.cliPath, publication.cliState);
    setZcodeKeySaved(publication.zcodeKeySaved);
    rebuild();
}

void QuotaView::setInvite(bool on) {
    if (demo_ || inviteOn_ == on) return;
    inviteOn_ = on;
    rebuild();
}

void QuotaView::applyHost(const remote::RelayMessage& message) {
    if (const auto* q = std::get_if<remote::HostQuota>(&message)) {
        hosts_[QString::fromStdString(q->hostId)].reports[q->snapshot.source] = *q;
    } else if (const auto* removed = std::get_if<remote::HostQuotaRemoved>(&message)) {
        hosts_[QString::fromStdString(removed->hostId)].reports.erase(removed->source);
    } else if (const auto* bridge = std::get_if<remote::HostQuotaBridge>(&message)) {
        hosts_[QString::fromStdString(bridge->hostId)].bridge = *bridge;
    } else {
        return;
    }
    rebuild();
}

void QuotaView::setHosts(std::map<QString, QString> names, QSet<QString> connected, std::map<QString, QSet<QString>> shown) {
    if (demo_) return;
    for (auto it = hosts_.begin(); it != hosts_.end();) {
        it = names.contains(it->first) ? std::next(it) : hosts_.erase(it);
    }
    hostNames_ = std::move(names);
    connected_ = std::move(connected);
    hostShown_ = std::move(shown);
    rebuild();
}

std::optional<remote::HostQuotaBridge> QuotaView::hostBridge(const QString& hostId) const {
    const auto it = hosts_.find(hostId);
    return it == hosts_.end() ? std::nullopt : it->second.bridge;
}

void QuotaView::setCliConfirmRequested(bool on) {
    if (cliConfirmRequested_ == on) return;
    cliConfirmRequested_ = on;
    emit settingsChanged();
}

void QuotaView::openCliSettings() {
    setCliConfirmRequested(true);
    emit cliSettingsRequested();
}

void QuotaView::setCli(const QString& path, const QString& state) {
    if (cliPath_ == path && cliState_ == state) return;
    cliPath_ = path;
    cliState_ = state;
    emit settingsChanged();
}

void QuotaView::setZcodeKeySaved(bool saved) {
    if (zcodeKeySaved_ == saved) return;
    zcodeKeySaved_ = saved;
    emit settingsChanged();
}

// A switch's status in words: the bridge's states, else the route's with the agent's or the site's name.
QuotaStatusLine QuotaView::statusLine(const QString& switchKey, const QuotaRawStatus& raw, std::int64_t nowMs) const {
    if (switchKey == QLatin1String("quotaClaudeCli")) {
        return quota_status::bridge(strings_, raw.code, cliPath_, raw.atMs > 0 ? std::optional<std::int64_t>(raw.atMs) : std::nullopt, nowMs);
    }
    QString name;
    if (switchKey == QLatin1String("quotaQoder")) {
        name = QStringLiteral("Qoder");
    } else if (switchKey == QLatin1String("quotaWorkBuddy") && raw.code == QLatin1String("noBrowser")) {
        name = QStringLiteral("WorkBuddy");
    } else {
        const auto key = switchKey == QLatin1String("quotaGrokBuildLive") ? QStringLiteral("quotaGrokBuild") : switchKey;
        for (const auto& route : quota::sourceRoutes()) {
            if (key.toStdString() == route.switchKey) name = strings_.t(QStringLiteral("source.") + QString::fromUtf8(agentspeed::sourceId(route.source)));
        }
    }
    return quota_status::route(strings_, raw.code, name, raw.host, raw.atMs, nowMs);
}

QVariantList QuotaView::demoHosts() const {
    QVariantList out;
    if (!demo_) return out;
    for (const auto& [id, name] : hostNames_) out.push_back(QVariantMap{{QStringLiteral("key"), id}, {QStringLiteral("name"), name}});
    return out;
}

QVariantMap QuotaView::hostBridgeLine(const QString& hostId) const {
    // The demo's host stands for any host the demo's editor opens.
    const auto it = demo_ && !hosts_.contains(hostId) ? hosts_.begin() : hosts_.find(hostId);
    if (it == hosts_.end() || !it->second.bridge) return {};
    const auto state = QString::fromUtf8(remote::hostBridgeStateId(it->second.bridge->state));
    std::optional<std::int64_t> at;
    if (const auto q = it->second.reports.find(SourceKind::ClaudeCode); q != it->second.reports.end()) at = q->second.snapshot.observedMs;
    const auto line = quota_status::bridge(strings_, state, hostBridgePath(hostId), at, now());
    return {{QStringLiteral("text"), line.text}, {QStringLiteral("warning"), line.warning}, {QStringLiteral("ok"), line.ok}};
}

QString QuotaView::hostBridgePath(const QString& hostId) const {
    const auto it = hosts_.find(hostId);
    if (it == hosts_.end() || !it->second.bridge || it->second.bridge->settingsPath.empty()) return QStringLiteral("~/.claude/settings.json");
    return QString::fromStdString(it->second.bridge->settingsPath);
}

void QuotaView::rebuild() {
    const auto nowMs = now();
    const auto format = quota_text::format(strings_);
    QVariantMap lines, spoken;
    QStringList listed;
    for (const auto& s : local_) {
        if (!hasWindows(s.snapshot)) continue;
        const auto l = quota_text::lines(*s.snapshot, nowMs, strings_, format);
        lines.insert(key(s.source), l);
        spoken.insert(key(s.source), quota_text::spoken(l, strings_));
        if (nowMs - s.snapshot->observedMs <= kListedWithinMs) listed.push_back(key(s.source));
    }
    QVariantMap hostLines;
    for (const auto& [hostId, host] : hosts_) {
        const auto shown = hostShown_.find(hostId);
        QVariantList rows;
        for (const auto& [source, report] : host.reports) {
            if (!demo_ && (shown == hostShown_.end() || !shown->second.contains(key(source)))) continue;
            if (report.snapshot.windows.empty() || nowMs - report.snapshot.observedMs > kListedWithinMs) continue;
            const auto l = quota_text::lines(report.snapshot, nowMs, strings_, format);
            rows.push_back(QVariantMap{{QStringLiteral("source"), key(source)},
                                       {QStringLiteral("name"), strings_.t(QStringLiteral("source.") + key(source))},
                                       {QStringLiteral("lines"), l},
                                       {QStringLiteral("spoken"), quota_text::spoken(l, strings_)}});
        }
        if (!rows.isEmpty()) hostLines.insert(hostId, rows);
    }
    QVariantMap statuses;
    for (const auto& [switchKey, raw] : rawStatuses_) {
        const auto line = statusLine(switchKey, raw, nowMs);
        if (line.text.isEmpty()) continue;
        statuses.insert(switchKey, QVariantMap{{QStringLiteral("text"), line.text},
                                               {QStringLiteral("warning"), line.warning},
                                               {QStringLiteral("ok"), line.ok}});
    }
    const bool invite = inviteOn_;
    if (lines == lines_ && spoken == spoken_ && listed == listed_ && hostLines == hostLines_ && statuses == statuses_ && invite == invite_) return;
    lines_ = std::move(lines);
    spoken_ = std::move(spoken);
    listed_ = std::move(listed);
    hostLines_ = std::move(hostLines);
    statuses_ = std::move(statuses);
    invite_ = invite;
    ++revision_;
    emit changed();
}

QVariantList QuotaView::cardItems(const QStringList& sources, const QStringList& hosts, bool allHosts) const {
    const auto nowMs = now();
    const auto format = quota_text::format(strings_);
    const auto within = [&](SourceKind source) { return sources.isEmpty() || sources.contains(key(source)); };
    QVariantList out;
    if (allHosts || hosts.contains(QStringLiteral("local"))) {
        for (const auto& s : local_) {
            if (!within(s.source) || !s.running || !s.signedIn || !hasWindows(s.snapshot)) continue;
            out.push_back(QVariantMap{{QStringLiteral("key"), key(s.source)},
                                      {QStringLiteral("source"), key(s.source)},
                                      {QStringLiteral("name"), strings_.t(QStringLiteral("source.") + key(s.source))},
                                      {QStringLiteral("lines"), quota_text::lines(*s.snapshot, nowMs, strings_, format)}});
        }
    }
    for (const auto& [hostId, name] : hostNames_) {
        if ((!allHosts && !hosts.contains(hostId)) || !connected_.contains(hostId)) continue;
        const auto host = hosts_.find(hostId);
        const auto shown = hostShown_.find(hostId);
        if (host == hosts_.end()) continue;
        for (const auto& [source, report] : host->second.reports) {
            if (!within(source) || !report.running || !report.signedIn || report.snapshot.windows.empty()) continue;
            if (!demo_ && (shown == hostShown_.end() || !shown->second.contains(key(source)))) continue;
            out.push_back(QVariantMap{{QStringLiteral("key"), key(source) + QLatin1Char('@') + hostId},
                                      {QStringLiteral("source"), key(source)},
                                      {QStringLiteral("name"), strings_.t(QStringLiteral("source.") + key(source)) + QStringLiteral(" · ") + name},
                                      {QStringLiteral("lines"), quota_text::lines(report.snapshot, nowMs, strings_, format)}});
        }
    }
    return out;
}

void QuotaView::loadDemo(std::int64_t nowMs) {
    demo_ = true;
    constexpr std::int64_t h = 3'600'000, m = 60'000;
    using quota::Kind;
    const auto w = [](Kind kind, double used, std::optional<std::int64_t> resets, std::optional<std::string> name = std::nullopt,
                      std::optional<std::int64_t> minutes = std::nullopt) { return quota::makeWindow(kind, used, resets, std::move(name), minutes); };
    const auto state = [&](SourceKind source, std::vector<quota::Window> windows, std::int64_t age = 240'000) -> QuotaSourceState {
        return QuotaSourceState{source, quota::Snapshot{source, nowMs - age, std::move(windows)}, true, true};
    };
    local_ = {
        state(SourceKind::Codex, {w(Kind::SevenDay, 23, nowMs + 4 * 24 * h + 5 * h)}),
        state(SourceKind::ClaudeCode, {w(Kind::FiveHour, 41, nowMs + 3 * h + 12 * m), w(Kind::SevenDay, 96, nowMs + 19 * h + 40 * m)}),
        state(SourceKind::ClaudeDesktop, {w(Kind::SevenDay, 86, nowMs + 2 * 24 * h + 6 * h), w(Kind::ModelWeek, 27, nowMs + 2 * 24 * h + 6 * h, "Fable")}),
        state(SourceKind::Gemini, {w(Kind::Other, 40, nowMs + 9 * h, "Pro", 1440), w(Kind::Other, 5, nowMs + 9 * h, "Flash", 1440)}),
        state(SourceKind::Antigravity, {w(Kind::FiveHour, 25, nowMs + 2 * h + 12 * m, "Gemini"), w(Kind::SevenDay, 60, nowMs + 3 * 24 * h, "Gemini"),
                                        w(Kind::SevenDay, 83, nowMs + 5 * 24 * h + 2 * h, "Claude/GPT")}),
        state(SourceKind::ZCode, {w(Kind::FiveHour, 30, nowMs + 2 * h), w(Kind::Month, 40, nowMs + 6 * 24 * h + 10 * h, "MCP")}),
        state(SourceKind::Kimi, {w(Kind::FiveHour, 23, nowMs + 3 * h + 40 * m), w(Kind::SevenDay, 41, nowMs + 4 * 24 * h + 8 * h),
                                 w(Kind::Month, 13, nowMs + 6 * 24 * h + 10 * h)}),
        state(SourceKind::GrokBuild, {w(Kind::Month, 64, nowMs + 5 * 24 * h)}, 3 * h),
        state(SourceKind::Droid, {w(Kind::FiveHour, 20, nowMs + 1 * h + 5 * m), w(Kind::SevenDay, 55, nowMs + 3 * 24 * h + 4 * h)}),
        state(SourceKind::Devin, {w(Kind::Other, 25, nowMs + 9 * h, std::nullopt, 1440), w(Kind::SevenDay, 60, nowMs + 4 * 24 * h + 4 * h)}),
        state(SourceKind::Qoder, {w(Kind::Credits, 37, nowMs + 6 * 24 * h + 10 * h)}),
        state(SourceKind::QoderCli, {w(Kind::Credits, 37, nowMs + 6 * 24 * h + 10 * h)}),
    };
    const QString host = QStringLiteral("8C0D6B0A-1111-4222-8333-444455556666");
    hostNames_ = {{host, QStringLiteral("devbox")}};
    connected_ = {host};
    auto& demoHost = hosts_[host];
    const auto report = [&](SourceKind source, std::vector<quota::Window> windows) {
        remote::HostQuota q;
        q.hostId = host.toStdString();
        q.snapshot = quota::Snapshot{source, nowMs - 3 * m, std::move(windows)};
        q.running = true;
        q.signedIn = true;
        demoHost.reports[source] = q;
    };
    report(SourceKind::Codex, {w(Kind::FiveHour, 12, nowMs + 1 * h + 50 * m), w(Kind::SevenDay, 58, nowMs + 3 * 24 * h + 2 * h)});
    report(SourceKind::ClaudeCode, {w(Kind::FiveHour, 67, nowMs + 2 * h + 5 * m), w(Kind::SevenDay, 34, nowMs + 5 * 24 * h)});
    demoHost.bridge = remote::HostQuotaBridge{host.toStdString(), remote::HostQuotaBridge::State::Ok, "~/.claude/settings.json"};
    // Every kind of status line once; AGENT_SPEED_QUOTA_DEMO=<cli state> picks the bridge's (ok by default).
    const auto at = nowMs - 4 * m;
    auto cliState = qEnvironmentVariable("AGENT_SPEED_QUOTA_DEMO");
    if (!QStringList{QStringLiteral("waiting"), QStringLiteral("replaced"), QStringLiteral("unreadable"), QStringLiteral("failed"),
                     QStringLiteral("off")}.contains(cliState)) {
        cliState = QStringLiteral("ok");
    }
    cliState_ = cliState;
    rawStatuses_ = {
        {QStringLiteral("quotaClaudeCli"), {cliState, {}, at}},
        {QStringLiteral("quotaGemini"), {QStringLiteral("expired"), {}, 0}},
        {QStringLiteral("quotaAntigravity"), {QStringLiteral("ok"), {}, at}},
        {QStringLiteral("quotaKimi"), {QStringLiteral("ok"), {}, at}},
        {QStringLiteral("quotaDroid"), {QStringLiteral("noCredential"), {}, 0}},
        {QStringLiteral("quotaZcode"), {QStringLiteral("ok"), {}, at}},
        {QStringLiteral("quotaGrokBuild"), {QStringLiteral("ok"), {}, nowMs - 3 * h}},
        {QStringLiteral("quotaGrokBuildLive"), {QStringLiteral("denied"), QStringLiteral("cli-chat-proxy.grok.com"), 0}},
        {QStringLiteral("quotaDevin"), {QStringLiteral("ok"), {}, at}},
        {QStringLiteral("quotaQoder"), {QStringLiteral("noBrowser"), {}, 0}},
        {QStringLiteral("quotaWorkBuddy"), {QStringLiteral("notRunning"), {}, 0}},
    };
    zcodeKeySaved_ = qEnvironmentVariableIsSet("AGENT_SPEED_QUOTA_DEMO_KEY");
    inviteOn_ = cliState == QLatin1String("off");
    if (inviteOn_) std::erase_if(local_, [](const QuotaSourceState& s) { return s.source == SourceKind::ClaudeCode; });
    rebuild();
}
