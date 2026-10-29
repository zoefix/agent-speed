#include "report/report_controller.hpp"

#include <QDateTime>
#include <QDesktopServices>
#include <QFile>
#include <QSysInfo>
#include <QUrl>

#include <sys/utsname.h>

#include "preferences.hpp"
#include "strings.hpp"

namespace {

struct Preset {
    const char* name;
    const char* url;
    const char* domain;
};
constexpr Preset kPresets[] = {{"Codex Radar", "https://deng.codexradar.com", "codexradar.com"}};
constexpr int kPresetCount = static_cast<int>(std::size(kPresets));
constexpr qint64 kStaleMs = 7LL * 24 * 3600 * 1000;

QString qs(const std::string& text) { return QString::fromStdString(text); }

// The computer's name as the user set it (hostnamectl's pretty name), else the host name (DESIGN 11, Default username).
QString computerName() {
    QFile file(QStringLiteral("/etc/machine-info"));
    if (file.open(QIODevice::ReadOnly)) {
        for (const auto& line : QString::fromUtf8(file.read(16384)).split(QLatin1Char('\n'))) {
            if (!line.startsWith(QLatin1String("PRETTY_HOSTNAME="))) continue;
            auto value = line.mid(16).trimmed();
            if (value.size() >= 2 && (value.front() == QLatin1Char('"') || value.front() == QLatin1Char('\'')) && value.back() == value.front())
                value = value.mid(1, value.size() - 2);
            if (!value.isEmpty()) return value;
        }
    }
    return QSysInfo::machineHostName();
}

int kernelMajor() {
    utsname name{};
    if (uname(&name) != 0) return 1;
    return std::clamp(std::atoi(name.release), 1, 99);
}

std::string sv(const QString& text) { return text.toStdString(); }

}  // namespace

ReportController::ReportController(Preferences& prefs, const Strings& strings, std::shared_ptr<agentspeed::pricing::PriceBook> prices,
                                   QString dataDir, QObject* parent)
    : QObject(parent), prefs_(prefs), strings_(strings), prices_(std::move(prices)), keys_(dataDir + QStringLiteral("/report")),
      fallbackUsername_(qs(agentspeed::report::randomUsername())), previewRunId_(qs(agentspeed::report::newDeviceId())),
      previewDeviceId_(qs(agentspeed::report::newDeviceId())) {
    refreshTimer_.setSingleShot(true);
    connect(&refreshTimer_, &QTimer::timeout, this, [this] { fetchDiscovery(false); });
    pauseTimer_.setSingleShot(true);
    connect(&pauseTimer_, &QTimer::timeout, this, [this] {
        if (settings_.pauseUntil() > 0 && settings_.pauseUntil() <= QDateTime::currentMSecsSinceEpoch()) resume();
    });
    statusTimer_.setInterval(1000);
    connect(&statusTimer_, &QTimer::timeout, this, [this] {
        if (enabled()) emit changed();
        if (previewWatchers_ > 0) refreshPreview();
    });
    connect(&prefs_, &Preferences::networkEnabledChanged, this, [this] {
        post(prefs_.networkEnabled() ? agentspeed::report::Event::NetworkOn : agentspeed::report::Event::NetworkOff);
        if (prefs_.networkEnabled() && enabled()) fetchDiscovery(false);
        if (!prefs_.networkEnabled()) pairingTimer_.stop();
        notify();
    });
    for (auto signal : {&Preferences::trafficChanged, &Preferences::metricChanged, &Preferences::redlineChanged})
        connect(&prefs_, signal, this, [this] { notify(); });
    connect(&strings_, &Strings::changed, this, [this] { notify(); });
}

ReportController::~ReportController() = default;

void ReportController::start() {
    loadSelection();
    if (const auto until = settings_.pauseUntil(); until > 0) {
        const auto left = until - QDateTime::currentMSecsSinceEpoch();
        if (left <= 0) settings_.setPauseUntil(0);
        else pauseTimer_.start(static_cast<int>(std::min<qint64>(left, INT32_MAX)));
    }
    statusTimer_.start();
    // REPORTING 3.4: while reporting is on, the document is fetched at launch.
    if (enabled() && networkEnabled()) fetchDiscovery(false);
    notify();
}

void ReportController::loadSelection() {
    service_.reset();
    discovery_.reset();
    identity_.reset();
    key_.reset();
    pendingKey_.reset();
    apiKey_.clear();
    fetchFailure_.reset();
    customText_ = settings_.customUrl();
    service_ = agentspeed::report::ServiceUrl::parse(sv(settings_.custom() ? settings_.customUrl() : settings_.service()));
    if (!service_) return;
    identity_ = settings_.identity(originOf());
    if (identity_) {
        key_ = keys_.key(identity_->deviceId);
        pendingKey_ = keys_.pending(identity_->deviceId);
        apiKey_ = keys_.apiKey(identity_->deviceId);
        if (!identity_->discovery.isEmpty()) discovery_ = agentspeed::report::parseDiscoveryBody(sv(identity_->discovery), *service_);
    }
}

void ReportController::notify() {
    emit changed();
    if (previewWatchers_ > 0) refreshPreview();
}

QString ReportController::originOf() const { return service_ ? qs(service_->origin()) : QString(); }

agentspeed::report::ClientInfo ReportController::client() const {
    return agentspeed::report::ClientInfo(AGENTSPEED_VERSION, "Linux", kernelMajor());
}

agentspeed::report::UsernameRules ReportController::rules() const {
    if (consent_ && consent_->discovery) return consent_->discovery->username;
    return discovery_ ? discovery_->username : agentspeed::report::UsernameRules{};
}

QString ReportController::defaultUsername() const {
    if (const auto fitted = agentspeed::report::fitUsername(sv(computerName()), rules())) return qs(*fitted);
    return fallbackUsername_;
}

QString ReportController::nextUsername() const {
    const auto typed = settings_.username();
    return typed.isEmpty() ? defaultUsername() : typed;
}

QString ReportController::t(const char* key) const { return strings_.t(QLatin1String(key)); }

QString ReportController::fmt(const QString& key, const QStringList& args) const {
    QVariantList list;
    for (const auto& a : args) list.append(a);
    return strings_.fmt(key, list);
}

QString ReportController::usernameMessage(const std::optional<std::string>& reason, const agentspeed::report::UsernameRules& rules) const {
    if (!reason) return {};
    if (*reason == "length") return fmt(QStringLiteral("report.username.invalid.length"), {QString::number(rules.minLength), QString::number(rules.maxLength)});
    if (*reason == "start") return t("report.username.invalid.start");
    if (*reason == "spaces") return t(rules.spaces ? "report.username.invalid.spaces" : "report.username.invalid.noSpaces");
    if (*reason == "reserved") return t("report.username.invalid.reserved");
    return t(rules.ascii ? "report.username.invalid.ascii" : "report.username.invalid.characters");
}

QString ReportController::failureText(ReportFailure failure) const {
    switch (failure) {
    case ReportFailure::Unreachable: return t("report.error.unreachable");
    case ReportFailure::Insecure: return t("report.error.insecure");
    case ReportFailure::Unavailable: return t("report.error.unavailable");
    case ReportFailure::Response: return t("report.error.response");
    case ReportFailure::Key: return t("report.error.key");
    }
    return {};
}

QString ReportController::fetchText(ReportFetch::Kind kind) const {
    switch (kind) {
    case ReportFetch::Kind::NotSupported: return t("report.service.notSupported");
    case ReportFetch::Kind::Invalid: return t("report.service.invalid");
    case ReportFetch::Kind::Unreachable: return t("report.service.unreachable");
    case ReportFetch::Kind::Insecure: return t("report.service.insecure");
    case ReportFetch::Kind::Unavailable: return t("report.service.unavailable");
    case ReportFetch::Kind::Gone: return t("report.status.gone");
    case ReportFetch::Kind::Ok: break;
    }
    return {};
}

std::set<agentspeed::report::Section> ReportController::switches() const {
    std::set<agentspeed::report::Section> on;
    for (const auto section : agentspeed::report::kAllSections)
        if (settings_.sectionOn(section)) on.insert(section);
    return on;
}

std::set<agentspeed::report::Section> ReportController::acceptedSections(const agentspeed::report::Discovery& discovery,
                                                                       const std::set<agentspeed::report::Section>& on) const {
    std::set<agentspeed::report::Section> out;
    for (const auto section : on) {
        const auto name = std::string(agentspeed::report::sectionName(section));
        if (std::find(discovery.sections.begin(), discovery.sections.end(), name) != discovery.sections.end()) out.insert(section);
    }
    return out;
}

QVariantList ReportController::sectionRows(const std::set<agentspeed::report::Section>& on, const std::optional<agentspeed::report::Discovery>& discovery) const {
    QVariantList rows;
    for (const auto section : agentspeed::report::kAllSections) {
        const std::string id = agentspeed::report::sectionName(section);
        const auto name = QString::fromStdString(id);
        const bool accepted = !discovery || std::find(discovery->sections.begin(), discovery->sections.end(), id) != discovery->sections.end();
        const bool trafficOff = section == agentspeed::report::Section::Traffic && !prefs_.trafficEnabled();
        QString note;
        if (!accepted) note = t("report.section.notAccepted");
        else if (trafficOff) note = t("report.section.trafficOff");
        else if (section == agentspeed::report::Section::Models && (!discovery || discovery->has("perfScore"))) note = t("report.section.models.perfScore");
        else if (section == agentspeed::report::Section::Models && discovery->has("taskScore")) note = t("report.section.models.thinking");
        rows.append(QVariantMap{{QStringLiteral("key"), name},
                                {QStringLiteral("label"), strings_.t(QStringLiteral("report.section.") + name)},
                                {QStringLiteral("on"), accepted && on.contains(section)},
                                {QStringLiteral("enabled"), accepted && !trafficOff},
                                {QStringLiteral("note"), note},
                                {QStringLiteral("noteIsCaption"), accepted && !trafficOff}});
    }
    return rows;
}

bool ReportController::discoveryStale() const {
    return identity_ && identity_->fetchedMs > 0 && QDateTime::currentMSecsSinceEpoch() - identity_->fetchedMs > kStaleMs;
}

void ReportController::saveIdentity() {
    if (!identity_) return;
    identity_->lastUsedMs = QDateTime::currentMSecsSinceEpoch();
    dropIdentities(settings_.save(*identity_));
}

void ReportController::dropIdentities(const QStringList& deviceIds) {
    for (const auto& id : deviceIds) keys_.forget(id);
}

void ReportController::after(int ms, std::function<void()> action) { QTimer::singleShot(ms, this, std::move(action)); }

void ReportController::openLink(const QString& url) {
    if (agentspeed::report::httpsLink(sv(url))) QDesktopServices::openUrl(QUrl(url));
}

// ---- master, service and settings

bool ReportController::networkEnabled() const { return prefs_.networkEnabled(); }

QStringList ReportController::presetNames() const {
    QStringList names;
    for (const auto& preset : kPresets) names.append(QString::fromUtf8(preset.name));
    return names;
}

int ReportController::serviceIndex() const {
    if (settings_.custom()) return kPresetCount;
    for (int i = 0; i < kPresetCount; ++i)
        if (settings_.service() == QLatin1String(kPresets[i].url)) return i;
    return 0;
}

QString ReportController::urlNote() const {
    if (!settings_.custom() || customText_.trimmed().isEmpty()) return {};
    agentspeed::report::UrlProblem problem{};
    const auto url = agentspeed::report::ServiceUrl::parse(sv(customText_), &problem);
    if (!url) return t(problem == agentspeed::report::UrlProblem::NotHttps ? "report.url.https" : "report.url.invalid");
    return url->loopbackHttp() ? t("report.url.loopback") : QString();
}

QString ReportController::urlNoteLevel() const {
    if (urlNote().isEmpty()) return {};
    const auto url = agentspeed::report::ServiceUrl::parse(sv(customText_));
    return url && url->loopbackHttp() ? QStringLiteral("amber") : QStringLiteral("error");
}

QString ReportController::serviceName() const {
    if (discovery_) return qs(discovery_->name);
    if (!settings_.custom()) return QString::fromUtf8(kPresets[serviceIndex()].name);
    return service_ ? qs(service_->host()) : QString();
}

QString ReportController::serviceHost() const {
    if (!service_) return {};
    QString host = qs(service_->host());
    if (discovery_) {
        if (const auto api = agentspeed::report::ServiceUrl::parseApi(discovery_->api, true)) host = qs(api->host());
    }
    return fmt(QStringLiteral("report.service.host"), {host});
}

QString ReportController::serviceDescription() const { return discovery_ && discovery_->description ? qs(*discovery_->description) : QString(); }

QString ReportController::serviceRetention() const {
    return discovery_ && discovery_->retentionDays ? fmt(QStringLiteral("report.service.retention"), {QString::number(*discovery_->retentionDays)}) : QString();
}

QString ReportController::termsUrl() const { return discovery_ && discovery_->terms ? qs(*discovery_->terms) : QString(); }
QString ReportController::privacyUrl() const { return discovery_ && discovery_->privacy ? qs(*discovery_->privacy) : QString(); }
QString ReportController::leaderboardUrl() const { return discovery_ && discovery_->leaderboard ? qs(*discovery_->leaderboard) : QString(); }

QString ReportController::fetchMessage() const {
    if (fetching_ || !fetchFailure_) return {};
    // While reporting is being set up after the invitation, the status line carries the sentence instead.
    if (inviteSetup_) return {};
    return fetchText(*fetchFailure_);
}

QString ReportController::fetchLevel() const {
    if (!fetchFailure_) return {};
    switch (*fetchFailure_) {
    case ReportFetch::Kind::NotSupported:
    case ReportFetch::Kind::Invalid:
    case ReportFetch::Kind::Gone: return QStringLiteral("red");
    default: return QStringLiteral("amber");
    }
}

bool ReportController::needsReview() const {
    return identity_ && identity_->pin && discovery_ && identity_->pin->needsReview(*discovery_, hasApiKey());
}

void ReportController::setEnabledByUser(bool on) {
    if (on == enabled() && !(on && !consent_ && (!identity_ || !identity_->registered))) return;
    if (settings_.invite() == QLatin1String("unanswered")) settings_.setInvite(QStringLiteral("accepted"));
    if (!on) {
        consent_.reset();
        inviteSetup_ = false;
        post(agentspeed::report::Event::Stop);
        settings_.setEnabled(false);
        fetchFailure_.reset();
        notify();
        return;
    }
    if (!networkEnabled() || !service_) return;
    if (identity_ && identity_->registered && identity_->pin && key_ && !needsReview()) {
        settings_.setEnabled(true);
        problem_ = agentspeed::report::Problem::None;
        strikes_ = 0;
        fetchDiscovery(false);
        notify();
        return;
    }
    openConsent(false);
}

void ReportController::chooseService(int index) {
    if (index < 0 || index > kPresetCount) return;
    const bool custom = index == kPresetCount;
    if (custom == settings_.custom() && (custom || settings_.service() == QLatin1String(kPresets[index].url))) return;
    // Switching services pauses the old one (its identity is kept) and starts a new run on the new one.
    if (enabled()) post(agentspeed::report::Event::Stop);
    endRun();
    endStream();
    settings_.setCustom(custom);
    if (!custom) settings_.setService(QLatin1String(kPresets[index].url));
    problem_ = agentspeed::report::Problem::None;
    retryReason_.reset();
    registrationFailure_.reset();
    loadSelection();
    const bool wasOn = enabled();
    if (wasOn && !(identity_ && identity_->registered && identity_->pin)) settings_.setEnabled(false);
    if (service_ && networkEnabled()) fetchDiscovery(true);
    if (wasOn && service_ && !(identity_ && identity_->registered && identity_->pin)) openConsent(false);
    notify();
}

void ReportController::editCustomUrl(const QString& text) {
    customText_ = text;
    notify();
}

void ReportController::commitCustomUrl() {
    const auto url = agentspeed::report::ServiceUrl::parse(sv(customText_));
    if (!url) return;
    if (service_ && *url == *service_ && settings_.custom()) return;
    if (enabled()) post(agentspeed::report::Event::Stop);
    endRun();
    endStream();
    settings_.setCustom(true);
    settings_.setCustomUrl(qs(url->text()));
    customText_ = qs(url->text());
    loadSelection();
    if (enabled() && !(identity_ && identity_->registered && identity_->pin)) settings_.setEnabled(false);
    if (networkEnabled()) fetchDiscovery(true);
    notify();
}

void ReportController::retry() {
    if (streamStop_ && (*streamStop_ == "mismatch" || *streamStop_ == "replaced")) {
        streamStop_.reset();
        if (stream_) applyStream(stream_->scheduler->retry(steadyMs()), lastSample_.get());
        notify();
        return;
    }
    if (registrationFailure_ && identity_ && !identity_->registered && enabled()) {
        registrationFailure_.reset();
        finishInviteSetup();
    } else if (problem_ == agentspeed::report::Problem::None || problem_ == agentspeed::report::Problem::Upgrade) {
        fetchDiscovery(true);
    }
    notify();
}

void ReportController::review() { openConsent(true); }

bool ReportController::webSocket() const { return service_ && !settings_.http(originOf()); }

bool ReportController::transportUnsupported() const { return webSocket() && discovery_ && !discovery_->websocket; }

QString ReportController::transportNote() const {
    if (!webSocket()) return t("report.transport.hint");
    return transportUnsupported() ? t("report.status.wsUnsupported") : t("report.transport.source");
}

QString ReportController::throttledNote() const {
    if (!webSocket() || !discovery_ || !discovery_->websocket || discovery_->websocket->minIntervalMs <= 50) return {};
    return fmt(QStringLiteral("report.transport.throttled"), {QString::number(discovery_->websocket->minIntervalMs)});
}

void ReportController::setWebSocket(bool on) {
    if (!service_ || on == webSocket()) return;
    settings_.setHttp(originOf(), !on);
    endRun();
    endStream();
    streamStop_.reset();
    notify();
}

QString ReportController::username() const {
    if (identity_ && identity_->registered && !identity_->username.isEmpty()) return identity_->username;
    return nextUsername();
}

QString ReportController::usernameError() const {
    if (identity_ && identity_->registered) return {};
    const auto name = nextUsername();
    return usernameMessage(agentspeed::report::validateUsername(sv(name), rules()), rules());
}

bool ReportController::apiKeyVisible() const {
    return identity_ && identity_->pin && discovery_ && (discovery_->apiKeyMode == "optional" || discovery_->apiKeyMode == "required");
}

bool ReportController::apiKeyRequired() const { return discovery_ && discovery_->apiKeyMode == "required"; }

QString ReportController::apiKeyMasked() const {
    if (apiKey_.isEmpty()) return {};
    return QStringLiteral("••••") + apiKey_.right(4);
}

QString ReportController::apiKeyUrl() const { return discovery_ && discovery_->apiKeyUrl ? qs(*discovery_->apiKeyUrl) : QString(); }

void ReportController::saveApiKey(const QString& key) {
    const auto trimmed = key.trimmed();
    if (!identity_ || trimmed.isEmpty() || trimmed.size() > 512) return;
    if (!keys_.saveApiKey(identity_->deviceId, trimmed)) return;
    apiKey_ = trimmed;
    if (problem_ == agentspeed::report::Problem::ApiKey) {
        problem_ = agentspeed::report::Problem::None;
        if (identity_ && !identity_->registered) finishInviteSetup();
        else reRegister();
    }
    notify();
}

void ReportController::clearApiKey() {
    if (!identity_) return;
    keys_.saveApiKey(identity_->deviceId, QString());
    apiKey_.clear();
    notify();
}

QVariantList ReportController::sections() const { return sectionRows(switches(), discovery_); }

bool ReportController::scoreCaption() const { return !discovery_ || discovery_->has("perfScore") || discovery_->has("taskScore"); }

void ReportController::setSection(const QString& key, bool on) {
    if (const auto section = agentspeed::report::sectionFromName(sv(key))) settings_.setSection(*section, on);
    notify();
}

bool ReportController::paused() const { return settings_.pauseUntil() != 0; }

bool ReportController::canPause() const { return enabled() && identity_ && identity_->registered; }

void ReportController::pause(int hours) {
    const qint64 until = hours > 0 ? QDateTime::currentMSecsSinceEpoch() + hours * 3'600'000LL : -1;
    settings_.setPauseUntil(until);
    if (until > 0) pauseTimer_.start(hours * 3'600'000);
    post(agentspeed::report::Event::Pause);
    notify();
}

void ReportController::resume() {
    settings_.setPauseUntil(0);
    pauseTimer_.stop();
    post(agentspeed::report::Event::Resume);
    notify();
}

// ---- status

QString ReportController::statusText() const {
    using agentspeed::report::Problem;
    if (!enabled()) return t("report.status.off");
    if (!networkEnabled()) return t("settings.network.offHint");
    switch (problem_) {
    case Problem::ApiKey: return t("report.status.apiKey");
    case Problem::UsernameTaken: return t("report.status.usernameTaken");
    case Problem::UsernameRejected: return t("report.status.usernameRejected");
    case Problem::KeyMismatch: return t("report.status.keyMismatch");
    case Problem::Rejected: return t("report.status.rejected");
    case Problem::Forbidden: return t("report.status.forbidden");
    case Problem::Upgrade: return t("report.status.upgrade");
    case Problem::Gone: return t("report.status.gone");
    case Problem::None: break;
    }
    if (inviteSetup_ && fetchFailure_ && !fetching_) return fetchText(*fetchFailure_);
    if (inviteSetup_ && fetching_) return t("report.status.connecting");
    if (identity_ && identity_->pin && needsReview()) return t("report.service.changed");
    if (inviteSetup_ && !(identity_ && identity_->pin) && discovery_) return t("report.status.review");
    if (registering_) return t("report.status.registering");
    if (registrationFailure_) return t("report.status.registerFailed");
    if (paused()) {
        const auto until = settings_.pauseUntil();
        return until < 0 ? t("report.status.pausedIndefinitely")
                         : fmt(QStringLiteral("report.status.paused"), {QLocale().toString(QDateTime::fromMSecsSinceEpoch(until).time(), QLocale::ShortFormat)});
    }
    const auto now = QDateTime::currentMSecsSinceEpoch();
    if (rateLimitedUntilMs_ > now)
        return fmt(QStringLiteral("report.status.rateLimited"), {QLocale().toString(QDateTime::fromMSecsSinceEpoch(rateLimitedUntilMs_).time(), QLocale::ShortFormat)});
    if (retryReason_) return t("report.status.retrying");
    if (discoveryStale() || !identity_ || !identity_->registered) return t("report.status.connecting");
    if (usesWebSocket()) {
        if (streamStop_) {
            const auto& r = *streamStop_;
            return t(r == "mismatch" ? "report.status.wsMismatch" : r == "replaced" ? "report.status.wsReplaced" : r == "nonce" ? "report.status.wsNonce"
                     : r == "clientState" ? "report.status.wsClientState" : "report.status.wsIncompatible");
        }
        const auto when = [&](qint64 ms) {
            const auto ago = std::max<qint64>(0, (now - ms) / 1000);
            return ago < 60 ? fmt(QStringLiteral("time.secondsAgo"), {QString::number(ago)})
                            : QLocale().toString(QDateTime::fromMSecsSinceEpoch(ms).time(), QLocale::ShortFormat);
        };
        const bool open = stream_ && stream_->scheduler->connection() == agentspeed::report::stream::Connection::Open;
        const auto streamMode = stream_ ? stream_->scheduler->mode() : std::string("quiet");
        if (open && lastDeliveryMs_ > 0 && streamMode != "quiet") return fmt(QStringLiteral("report.status.streaming"), {when(lastDeliveryMs_)});
        if (stream_ && stream_->scheduler->failures() > 0)
            return lastDeliveryMs_ > 0 ? fmt(QStringLiteral("report.status.wsReconnecting"), {when(lastDeliveryMs_)}) : t("report.status.wsReconnectingNever");
        if (streamMode == "idle") return t("report.status.idle");
        if (streamMode == "active") return t("report.status.connecting");
        return lastDeliveryMs_ > 0 ? t("report.status.quiet") : t("report.status.never");
    }
    const auto mode = run_ ? run_->scheduler->mode() : agentspeed::report::Mode::Quiet;
    if (mode == agentspeed::report::Mode::Active && lastDeliveryMs_ > 0) {
        const auto ago = std::max<qint64>(0, (now - lastDeliveryMs_) / 1000);
        const auto when = ago < 60 ? fmt(QStringLiteral("time.secondsAgo"), {QString::number(ago)})
                                   : QLocale().toString(QDateTime::fromMSecsSinceEpoch(lastDeliveryMs_).time(), QLocale::ShortFormat);
        return fmt(QStringLiteral("report.status.reporting"), {when});
    }
    if (mode == agentspeed::report::Mode::Idle) return t("report.status.idle");
    return lastDeliveryMs_ > 0 ? t("report.status.quiet") : t("report.status.never");
}

QString ReportController::statusLevel() const {
    using agentspeed::report::Problem;
    if (!enabled() || !networkEnabled()) return QStringLiteral("gray");
    if (problem_ != Problem::None) return QStringLiteral("red");
    if (inviteSetup_ && fetchFailure_ && !fetching_) return fetchLevel();
    if (identity_ && identity_->pin && needsReview()) return QStringLiteral("red");
    if (inviteSetup_ && !(identity_ && identity_->pin) && discovery_ && !fetching_ && !registering_) return QStringLiteral("red");
    if (registering_ || registrationFailure_ || (inviteSetup_ && fetching_)) return QStringLiteral("amber");
    if (paused()) return QStringLiteral("gray");
    if (rateLimitedUntilMs_ > QDateTime::currentMSecsSinceEpoch() || retryReason_) return QStringLiteral("amber");
    if (discoveryStale() || !identity_ || !identity_->registered) return QStringLiteral("amber");
    if (usesWebSocket()) {
        if (streamStop_) return QStringLiteral("red");
        const bool open = stream_ && stream_->scheduler->connection() == agentspeed::report::stream::Connection::Open;
        const auto streamMode = stream_ ? stream_->scheduler->mode() : std::string("quiet");
        if (open && lastDeliveryMs_ > 0 && streamMode != "quiet") return QStringLiteral("green");
        if ((stream_ && stream_->scheduler->failures() > 0) || streamMode == "active") return QStringLiteral("amber");
        return QStringLiteral("gray");
    }
    const auto mode = run_ ? run_->scheduler->mode() : agentspeed::report::Mode::Quiet;
    return mode == agentspeed::report::Mode::Active && lastDeliveryMs_ > 0 ? QStringLiteral("green") : QStringLiteral("gray");
}

QString ReportController::statusFootnote() const {
    if (!enabled()) return {};
    if (registrationFailure_ && problem_ == agentspeed::report::Problem::None) return failureText(*registrationFailure_);
    if (retryReason_ && problem_ == agentspeed::report::Problem::None) return failureText(*retryReason_);
    return {};
}

QString ReportController::rankText() const {
    return enabled() && rank_ ? fmt(QStringLiteral("report.status.rank"), {QString::number(*rank_)}) : QString();
}

bool ReportController::showRetry() const {
    if (enabled() && streamStop_ && (*streamStop_ == "mismatch" || *streamStop_ == "replaced")) return true;
    return enabled() && !fetching_ && ((inviteSetup_ && fetchFailure_) || registrationFailure_.has_value());
}

bool ReportController::showReview() const {
    return enabled() && ((identity_ && identity_->pin && needsReview()) || (inviteSetup_ && !(identity_ && identity_->pin) && discovery_ && !registering_));
}

// ---- device and other services

bool ReportController::deviceVisible() const { return identity_ && identity_->registered; }

QString ReportController::deviceDetail() const {
    if (!deviceVisible() || !service_) return {};
    return fmt(QStringLiteral("report.device.detail"), {identity_->username, qs(service_->host())});
}

QString ReportController::profileUrl() const { return deviceVisible() ? identity_->profileUrl : QString(); }

QString ReportController::fingerprint() const {
    if (!deviceVisible() || !key_) return {};
    return qs(agentspeed::report::thumbprint(key_->publicKey()).substr(0, 8));
}

QString ReportController::forgetConfirm() const { return service_ ? fmt(QStringLiteral("report.forget.confirm"), {qs(service_->host())}) : QString(); }

QString ReportController::forgetMessage() const {
    if (forgetState_ == QLatin1String("done")) return t("report.forget.done");
    if (forgetState_ == QLatin1String("failed") && forgetFailure_) return fmt(QStringLiteral("report.forget.failed"), {failureText(*forgetFailure_)});
    return {};
}

QVariantList ReportController::others() const {
    QVariantList rows;
    for (const auto& id : settings_.identities()) {
        if (id.origin == originOf() || !id.registered) continue;
        const auto url = agentspeed::report::ServiceUrl::parse(sv(id.service));
        rows.append(QVariantMap{{QStringLiteral("origin"), id.origin},
                                {QStringLiteral("host"), url ? qs(url->host()) : id.origin},
                                {QStringLiteral("username"), id.username}});
    }
    return rows;
}

// ---- dashboard, menu, About

bool ReportController::inviteVisible() const {
    if (inviteDone_) return true;
    if (settings_.invite() != QLatin1String("unanswered") || enabled() || !networkEnabled() || settings_.custom()) return false;
    for (const auto& id : settings_.identities())
        if (id.registered) return false;
    return true;
}

QString ReportController::inviteBody() const {
    return fmt(QStringLiteral("report.invite.body"), {QString::fromUtf8(kPresets[0].name), QString::fromUtf8(kPresets[0].domain)});
}

QString ReportController::inviteUsername() const {
    return fmt(QStringLiteral("report.invite.username"), {QChar(0x2068) + nextUsername() + QChar(0x2069)});
}

bool ReportController::indicatorAttention() const {
    return enabled() && (problem_ != agentspeed::report::Problem::None || showReview() || (inviteSetup_ && fetchFailure_ && !fetching_) ||
                         (usesWebSocket() && streamStop_.has_value()));
}

QString ReportController::indicatorTip() const {
    if (indicatorAttention()) return t("report.indicator.attention");
    if (paused()) return t("report.menu.paused");
    return fmt(QStringLiteral("report.indicator"), {username(), service_ ? qs(service_->host()) : QString()});
}

bool ReportController::menuVisible() const { return enabled() && identity_ && identity_->registered && !inviteSetup_; }

QString ReportController::menuText() const { return paused() ? t("report.menu.paused") : fmt(QStringLiteral("report.menu.on"), {serviceName()}); }

QString ReportController::trafficNote() const {
    if (!enabled() || !discovery_ || !service_) return {};
    if (!acceptedSections(*discovery_, switches()).contains(agentspeed::report::Section::Traffic)) return {};
    return fmt(QStringLiteral("report.traffic.note"), {qs(service_->host())});
}
