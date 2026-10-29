#include <QDateTime>
#include <QTimer>

#include "preferences.hpp"
#include "report/report_controller.hpp"
#include "strings.hpp"

// Discovery, consent, registration, renames, key rotation and forgetting (REPORTING 3, 4; DESIGN 11).
namespace {

constexpr qint64 kDayMs = 24LL * 3600 * 1000;
constexpr qint64 kHourMs = 3600LL * 1000;
constexpr qint64 kRotationAgeMs = 365 * kDayMs;
constexpr int kInviteDoneMs = 2400;
constexpr const char* kPresetDomain = "codexradar.com";

QString qs(const std::string& text) { return QString::fromStdString(text); }
std::string sv(const QString& text) { return text.toStdString(); }
QString nfkc(const QString& text) { return text.normalized(QString::NormalizationForm_KC); }

bool supportsThisClient(const agentspeed::report::Discovery& d) {
    return std::find(d.versions.begin(), d.versions.end(), 1) != d.versions.end();
}

}  // namespace

void ReportController::fetchDiscovery(bool userAction) {
    if (!service_ || !networkEnabled() || fetching_) return;
    fetching_ = true;
    if (userAction) fetchFailure_.reset();
    notify();
    const auto service = *service_;
    http_.discover(service, [this, service, userAction](ReportFetch fetch) { applyFetch(service, std::move(fetch), userAction); });
}

void ReportController::applyFetch(const agentspeed::report::ServiceUrl& service, ReportFetch fetch, bool userAction) {
    fetching_ = false;
    if (!service_ || !(*service_ == service)) {
        notify();
        return;
    }
    const auto now = QDateTime::currentMSecsSinceEpoch();
    if (fetch.kind == ReportFetch::Kind::Ok && fetch.discovery) {
        discovery_ = fetch.discovery;
        fetchFailure_.reset();
        if (identity_) {
            identity_->discovery = qs(agentspeed::report::discoveryJson(*discovery_));
            identity_->fetchedMs = now;
            saveIdentity();
        }
        if (!supportsThisClient(*discovery_)) problem_ = agentspeed::report::Problem::Upgrade;
        else if (problem_ == agentspeed::report::Problem::Upgrade) problem_ = agentspeed::report::Problem::None;
        if (run_) run_->scheduler->update(discovery_->limits);
        if (stream_) applyStream(stream_->scheduler->settingsChanged(steadyMs(), discovery_->websocket), lastSample_.get());
        if (consent_ && consent_->service == service) {
            consent_->discovery = discovery_;
            if (settings_.username().isEmpty() && !(identity_ && identity_->registered)) consent_->username = defaultUsername();
        }
        if (inviteSetup_) finishInviteSetup();
        if (enabled()) refreshTimer_.start(static_cast<int>(kDayMs));
    } else {
        fetchFailure_ = fetch.kind;
        if (fetch.kind == ReportFetch::Kind::Gone && identity_ && enabled()) {
            problem_ = agentspeed::report::Problem::Gone;
            settings_.setEnabled(false);
            endRun();
        }
        if (enabled()) refreshTimer_.start(static_cast<int>(kHourMs));
        // A failed refresh while the stream waits for refreshed settings stops it (METRICS 11.9, ready).
        if (stream_ && stream_->scheduler->awaitingSettings()) applyStream(stream_->scheduler->settingsChanged(steadyMs(), std::nullopt), lastSample_.get());
        if (consent_ && consent_->service == service && !consent_->discovery) consent_->failure = fetchText(fetch.kind);
    }
    Q_UNUSED(userAction);
    notify();
}

void ReportController::openConsent(bool review) {
    if (!service_) return;
    Draft draft{*service_, discovery_, {}, apiKey_, switches(), review, false, false, {}, {}, {}};
    draft.username = identity_ && identity_->registered ? identity_->username : nextUsername();
    consent_ = std::move(draft);
    fetchDiscovery(true);
    emit consentRequested();
    notify();
}

QString ReportController::consentTitle() const {
    if (!consent_) return {};
    QString name = consent_->discovery ? qs(consent_->discovery->name) : serviceName();
    return fmt(QStringLiteral("report.consent.title"), {name});
}

QString ReportController::consentBody() const {
    const int interval = consent_ && consent_->discovery ? consent_->discovery->limits.intervalSeconds : 5;
    return fmt(QStringLiteral("report.consent.body"), {QString::number(interval)});
}

QString ReportController::consentUsername() const { return consent_ ? consent_->username : QString(); }

QString ReportController::consentUsernameError() const {
    if (!consent_) return {};
    if (!consent_->usernameError.isEmpty()) return consent_->usernameError;
    const auto r = consent_->discovery ? consent_->discovery->username : agentspeed::report::UsernameRules{};
    return usernameMessage(agentspeed::report::validateUsername(sv(consent_->username), r), r);
}

bool ReportController::consentApiKeyVisible() const { return consent_ && consent_->discovery && consent_->discovery->apiKeyMode != "none"; }
bool ReportController::consentApiKeyRequired() const { return consent_ && consent_->discovery && consent_->discovery->apiKeyMode == "required"; }
QString ReportController::consentApiKeyError() const { return consent_ ? consent_->apiKeyError : QString(); }
QVariantList ReportController::consentSections() const { return consent_ ? sectionRows(consent_->sections, consent_->discovery) : QVariantList(); }
QString ReportController::consentFailure() const { return consent_ ? consent_->failure : QString(); }

bool ReportController::consentReady() const {
    if (!consent_ || !consent_->discovery || consent_->starting || !networkEnabled() || !supportsThisClient(*consent_->discovery)) return false;
    if (agentspeed::report::validateUsername(sv(consent_->username), consent_->discovery->username)) return false;
    return !(consentApiKeyRequired() && consent_->apiKey.trimmed().isEmpty());
}

void ReportController::setConsentUsername(const QString& name) {
    if (!consent_) return;
    consent_->username = nfkc(name);
    consent_->usernameError.clear();
    consent_->offersTransfer = false;
    notify();
}

void ReportController::randomizeConsentUsername() { setConsentUsername(qs(agentspeed::report::randomUsername())); }

void ReportController::setConsentApiKey(const QString& key) {
    if (!consent_) return;
    consent_->apiKey = key.trimmed();
    consent_->apiKeyError.clear();
    notify();
}

void ReportController::setConsentSection(const QString& key, bool on) {
    if (!consent_) return;
    if (const auto section = agentspeed::report::sectionFromName(sv(key))) {
        if (on) consent_->sections.insert(*section);
        else consent_->sections.erase(*section);
    }
    notify();
}

void ReportController::cancelConsent() {
    consent_.reset();
    notify();
}

void ReportController::ensureIdentity(const agentspeed::report::ServiceUrl& service) {
    if (identity_ && identity_->origin == qs(service.origin()) && key_) return;
    if (identity_) keys_.forget(identity_->deviceId);
    ReportIdentity id;
    id.origin = qs(service.origin());
    id.service = qs(service.text());
    id.deviceId = qs(agentspeed::report::newDeviceId());
    id.keyCreatedMs = QDateTime::currentMSecsSinceEpoch();
    const auto key = agentspeed::report::PrivateKey::generate();
    if (!keys_.saveKey(id.deviceId, key)) {
        qWarning("report: the device key could not be stored");
        return;
    }
    identity_ = id;
    key_ = key;
    pendingKey_.reset();
    apiKey_.clear();
    saveIdentity();
}

void ReportController::startReporting() { transferConsent(); }

// Starts from the consent sheet; `transfer` only after the user chose to move the name (REPORTING 4.7).
void ReportController::transferConsent() {
    if (!consentReady() && !(consent_ && consent_->offersTransfer)) return;
    const bool transfer = consent_->offersTransfer;
    consent_->starting = true;
    consent_->failure.clear();
    consent_->usernameError.clear();
    consent_->apiKeyError.clear();
    notify();
    const auto discovery = *consent_->discovery;
    const auto service = consent_->service;
    for (const auto section : agentspeed::report::kAllSections) settings_.setSection(section, consent_->sections.contains(section));
    settings_.setUsername(consent_->username == defaultUsername() ? QString() : consent_->username);
    // A review of an identity that is already registered confirms the document again; nothing is sent.
    if (consent_->review && identity_ && identity_->registered && key_) {
        identity_->pin = agentspeed::report::ConsentPin::of(discovery);
        identity_->discovery = qs(agentspeed::report::discoveryJson(discovery));
        identity_->fetchedMs = QDateTime::currentMSecsSinceEpoch();
        if (!consent_->apiKey.isEmpty() && keys_.saveApiKey(identity_->deviceId, consent_->apiKey)) apiKey_ = consent_->apiKey;
        saveIdentity();
        settings_.setEnabled(true);
        settings_.setInvite(QStringLiteral("accepted"));
        consent_.reset();
        inviteSetup_ = false;
        notify();
        return;
    }
    ensureIdentity(service);
    if (!identity_ || !key_) {
        consent_->starting = false;
        consent_->failure = failureText(ReportFailure::Key);
        notify();
        return;
    }
    if (!consent_->apiKey.isEmpty() && keys_.saveApiKey(identity_->deviceId, consent_->apiKey)) apiKey_ = consent_->apiKey;
    discovery_ = discovery;
    const auto name = consent_->username;
    registerDevice(transfer, [this, discovery, name](const ReportResponse& response) {
        if (!consent_) return;
        consent_->starting = false;
        const auto& reply = response.reply;
        if (reply && reply->success()) {
            identity_->registered = true;
            identity_->username = reply->username ? qs(*reply->username) : name;
            identity_->profileUrl = reply->profileUrl ? qs(*reply->profileUrl) : QString();
            identity_->pin = agentspeed::report::ConsentPin::of(discovery);
            identity_->discovery = qs(agentspeed::report::discoveryJson(discovery));
            identity_->fetchedMs = QDateTime::currentMSecsSinceEpoch();
            saveIdentity();
            settings_.setEnabled(true);
            settings_.setInvite(QStringLiteral("accepted"));
            problem_ = agentspeed::report::Problem::None;
            strikes_ = 0;
            inviteSetup_ = false;
            consent_.reset();
            refreshTimer_.start(static_cast<int>(kDayMs));
        } else if (reply && reply->code == "username_taken") {
            consent_->usernameError = t("report.username.taken");
            consent_->offersTransfer = discovery.apiKeyMode != "none" && !apiKey_.isEmpty();
        } else if (reply && reply->code == "username_invalid") {
            consent_->usernameError = usernameMessage(reply->reason.value_or("characters"), discovery.username);
        } else if (reply && (reply->code == "api_key_required" || reply->code == "api_key_invalid")) {
            consent_->apiKeyError = t("report.status.apiKey");
        } else if (reply && reply->code == "device_conflict") {
            consent_->failure = t("report.status.keyMismatch");
        } else if (reply) {
            consent_->failure = failureText(reply->status >= 500 ? ReportFailure::Unavailable : ReportFailure::Response);
        } else {
            consent_->failure = failureText(response.failure);
        }
        notify();
    });
}

static std::optional<agentspeed::report::ServiceUrl> apiOf(const std::optional<agentspeed::report::Discovery>& discovery, const agentspeed::report::ServiceUrl& service) {
    if (!discovery) return std::nullopt;
    return agentspeed::report::ServiceUrl::parseApi(discovery->api, service.loopbackHttp());
}

void ReportController::signedCall(const QByteArray& method, const std::string& path, const std::string& body, bool usePending, bool withNewKey,
                                  std::function<void(const ReportResponse&)> done, const char* contentType, qint64 cap) {
    ReportResponse failed;
    // The network master switch stops every request (NETWORK 1).
    if (!networkEnabled() || !service_ || !identity_) {
        failed.failure = ReportFailure::Unreachable;
        done(failed);
        return;
    }
    const auto& document = consent_ && consent_->discovery ? consent_->discovery : discovery_;
    const auto api = apiOf(document, *service_);
    const auto& key = usePending ? pendingKey_ : key_;
    if (!api || !key || (withNewKey && !pendingKey_)) {
        failed.failure = api ? ReportFailure::Key : ReportFailure::Response;
        done(failed);
        return;
    }
    agentspeed::report::SignOptions options;
    options.timestamp = correctedMs() / 1000;
    options.apiKey = sv(apiKey_);
    options.version = AGENTSPEED_VERSION;
    options.platform = "Linux";
    if (withNewKey) options.newKey = &*pendingKey_;
    const auto url = api->endpoint(path);
    auto headers = agentspeed::report::signedHeaders(*key, method.toStdString(), url, sv(identity_->deviceId), body, options);
    if (contentType) {
        for (auto& [name, value] : headers)
            if (name == "Content-Type") value = contentType;
    }
    http_.send(method, url, body, headers, std::move(done), cap);
}

void ReportController::registerDevice(bool transfer, std::function<void(const ReportResponse&)> done) {
    if (!identity_ || !key_) {
        ReportResponse failed;
        failed.failure = ReportFailure::Key;
        done(failed);
        return;
    }
    const auto name = consent_ ? consent_->username : identity_->registered && !identity_->username.isEmpty() ? identity_->username : nextUsername();
    const auto body = agentspeed::report::registrationBody(sv(identity_->deviceId), sv(name), key_->publicKey(), client(), transfer);
    registering_ = true;
    notify();
    signedCall("POST", "/v1/devices", body, false, false, [this, done = std::move(done)](const ReportResponse& response) {
        registering_ = false;
        done(response);
        notify();
    });
}

// unknown_device (at most once per hour), signature_invalid without a pending key, a new name or API key while waiting.
void ReportController::reRegister() {
    if (registering_ || !identity_ || !key_) return;
    registerDevice(false, [this](const ReportResponse& response) {
        const auto& reply = response.reply;
        if (reply && reply->success()) {
            identity_->registered = true;
            if (reply->username) identity_->username = qs(*reply->username);
            if (reply->profileUrl) identity_->profileUrl = qs(*reply->profileUrl);
            saveIdentity();
            problem_ = agentspeed::report::Problem::None;
            registrationFailure_.reset();
            strikes_ = 0;
        } else if (reply && reply->code == "device_conflict") {
            problem_ = agentspeed::report::Problem::KeyMismatch;
        } else if (reply && reply->code == "username_taken") {
            problem_ = agentspeed::report::Problem::UsernameTaken;
            transferOffered_ = discovery_ && discovery_->apiKeyMode != "none" && !apiKey_.isEmpty();
        } else if (reply && reply->code == "username_invalid") {
            problem_ = agentspeed::report::Problem::UsernameRejected;
        } else if (reply && (reply->code == "api_key_required" || reply->code == "api_key_invalid")) {
            problem_ = agentspeed::report::Problem::ApiKey;
        } else if (reply && reply->code == "gone") {
            problem_ = agentspeed::report::Problem::Gone;
            settings_.setEnabled(false);
            endRun();
        } else {
            registrationFailure_ = reply ? ReportFailure::Unavailable : response.failure;
            after(static_cast<int>(kHourMs), [this] {
                if (registrationFailure_ && enabled()) {
                    registrationFailure_.reset();
                    if (identity_ && identity_->registered) reRegister();
                    else finishInviteSetup();
                }
            });
        }
        notify();
    });
}

// The invitation's setup (DESIGN 11, Accept): confirm without a sheet when the document is covered, then register.
void ReportController::finishInviteSetup() {
    if (!enabled() || !service_ || !discovery_ || registering_) return;
    if (!supportsThisClient(*discovery_)) {
        problem_ = agentspeed::report::Problem::Upgrade;
        return;
    }
    if (!(identity_ && identity_->pin)) {
        if (!agentspeed::report::invitationCovers(*discovery_, *service_, kPresetDomain, sv(nextUsername()))) return;  // waits for Review
        ensureIdentity(*service_);
        if (!identity_) return;
        identity_->pin = agentspeed::report::ConsentPin::of(*discovery_);
        identity_->discovery = qs(agentspeed::report::discoveryJson(*discovery_));
        identity_->fetchedMs = QDateTime::currentMSecsSinceEpoch();
        saveIdentity();
    }
    if (!identity_->registered) {
        reRegister();
        return;
    }
    inviteSetup_ = false;
}

void ReportController::commitUsername(const QString& input) {
    const auto name = nfkc(input.trimmed().isEmpty() ? input : input);
    usernameNote_.clear();
    usernameNoteError_ = false;
    transferOffered_ = false;
    if (identity_ && identity_->registered) {
        if (name == identity_->username) return;
        if (const auto reason = agentspeed::report::validateUsername(sv(name), rules())) {
            usernameNote_ = usernameMessage(reason, rules());
            usernameNoteError_ = true;
            notify();
            return;
        }
        settings_.setUsername(name);
        rename(name);
        return;
    }
    settings_.setUsername(name == defaultUsername() ? QString() : name);
    if (problem_ == agentspeed::report::Problem::UsernameTaken || problem_ == agentspeed::report::Problem::UsernameRejected) {
        problem_ = agentspeed::report::Problem::None;
        if (!agentspeed::report::validateUsername(sv(name), rules())) {
            if (identity_ && identity_->pin) reRegister();
            else finishInviteSetup();
        }
    }
    notify();
}

void ReportController::randomizeUsername() { commitUsername(qs(agentspeed::report::randomUsername())); }

void ReportController::transferUsername() {
    if (!identity_ || !key_ || registering_) return;
    transferOffered_ = false;
    registerDevice(true, [this](const ReportResponse& response) {
        if (response.reply && response.reply->success()) {
            identity_->registered = true;
            if (response.reply->username) identity_->username = qs(*response.reply->username);
            if (response.reply->profileUrl) identity_->profileUrl = qs(*response.reply->profileUrl);
            saveIdentity();
            problem_ = agentspeed::report::Problem::None;
        } else if (response.reply && response.reply->code == "username_taken") {
            usernameNote_ = t("report.username.taken");
            usernameNoteError_ = true;
        } else {
            registrationFailure_ = response.reply ? ReportFailure::Unavailable : response.failure;
        }
        notify();
    });
}

void ReportController::rename(const QString& name) {
    if (!identity_ || !identity_->registered) return;
    const auto path = "/v1/devices/" + sv(identity_->deviceId);
    signedCall("PATCH", path, agentspeed::report::renameBody(sv(name)), false, false, [this, name](const ReportResponse& response) {
        const auto& reply = response.reply;
        if (reply && reply->success()) {
            identity_->username = reply->username ? qs(*reply->username) : name;
            if (reply->profileUrl) identity_->profileUrl = qs(*reply->profileUrl);
            saveIdentity();
            usernameNote_ = t("report.username.updated");
            usernameNoteError_ = false;
            after(3000, [this] {
                if (usernameNote_ == t("report.username.updated")) {
                    usernameNote_.clear();
                    notify();
                }
            });
            if (problem_ == agentspeed::report::Problem::UsernameTaken) problem_ = agentspeed::report::Problem::None;
        } else if (reply && reply->code == "username_taken") {
            usernameNote_ = t("report.username.taken");
            usernameNoteError_ = true;
        } else if (reply && reply->code == "username_invalid") {
            usernameNote_ = usernameMessage(reply->reason.value_or("characters"), rules());
            usernameNoteError_ = true;
        } else {
            usernameNote_ = failureText(reply ? ReportFailure::Unavailable : response.failure);
            usernameNoteError_ = true;
        }
        notify();
    });
}

void ReportController::rotateKeyIfDue() {
    if (rotating_ || !identity_ || !identity_->registered || pendingKey_ || !key_) return;
    if (QDateTime::currentMSecsSinceEpoch() - identity_->keyCreatedMs < kRotationAgeMs) return;
    const auto next = agentspeed::report::PrivateKey::generate();
    if (!keys_.savePending(identity_->deviceId, next)) return;
    pendingKey_ = next;
    identity_->pendingKey = true;
    saveIdentity();
    rotating_ = true;
    const auto path = "/v1/devices/" + sv(identity_->deviceId) + "/key";
    signedCall("PUT", path, agentspeed::report::rotationBody(next.publicKey()), false, true, [this](const ReportResponse& response) {
        rotating_ = false;
        if (response.reply && response.reply->success()) promotePendingKey();
        notify();
    });
}

void ReportController::promotePendingKey() {
    if (!identity_ || !pendingKey_ || !keys_.promotePending(identity_->deviceId)) return;
    key_ = pendingKey_;
    pendingKey_.reset();
    identity_->pendingKey = false;
    identity_->keyCreatedMs = QDateTime::currentMSecsSinceEpoch();
    saveIdentity();
}

void ReportController::endIdentity(bool deleteKeys) {
    endRun();
    endStream();
    if (identity_) {
        if (deleteKeys) keys_.forget(identity_->deviceId);
        settings_.remove(identity_->origin);
    }
    identity_.reset();
    key_.reset();
    pendingKey_.reset();
    apiKey_.clear();
    settings_.setEnabled(false);
    problem_ = agentspeed::report::Problem::None;
    retryReason_.reset();
    registrationFailure_.reset();
    rank_.reset();
    lastDeliveryMs_ = 0;
}

void ReportController::forget() {
    if (!identity_ || !identity_->registered) return;
    forgetState_ = QStringLiteral("working");
    forgetFailure_.reset();
    post(agentspeed::report::Event::Stop);
    notify();
    const auto path = "/v1/devices/" + sv(identity_->deviceId);
    signedCall("DELETE", path, "", false, false, [this](const ReportResponse& response) {
        const auto& reply = response.reply;
        // An unknown device counts as already deleted (REPORTING 4.6).
        if (reply && (reply->success() || reply->status == 404)) {
            endIdentity(true);
            forgetState_ = QStringLiteral("done");
        } else {
            forgetState_ = QStringLiteral("failed");
            forgetFailure_ = reply ? ReportFailure::Unavailable : response.failure;
        }
        notify();
    });
}

void ReportController::forgetLocalOnly() {
    endIdentity(true);
    forgetState_.clear();
    notify();
}

void ReportController::resetDevice() {
    if (!service_ || !identity_) return;
    const auto pin = identity_->pin;
    const auto stored = identity_->discovery;
    endIdentity(true);
    ensureIdentity(*service_);
    if (!identity_) return;
    identity_->pin = pin;
    identity_->discovery = stored;
    identity_->fetchedMs = QDateTime::currentMSecsSinceEpoch();
    saveIdentity();
    settings_.setEnabled(true);
    reRegister();
    notify();
}

void ReportController::forgetOther(const QString& origin) {
    const auto other = settings_.identity(origin);
    if (!other || origin == originOf()) return;
    keys_.forget(other->deviceId);
    settings_.remove(origin);
    notify();
}

void ReportController::acceptInvite() {
    settings_.setInvite(QStringLiteral("accepted"));
    settings_.setCustom(false);
    settings_.setService(QStringLiteral("https://deng.codexradar.com"));
    loadSelection();
    settings_.setEnabled(true);
    inviteSetup_ = !(identity_ && identity_->registered && identity_->pin);
    inviteDone_ = true;
    after(kInviteDoneMs, [this] {
        inviteDone_ = false;
        notify();
    });
    fetchDiscovery(true);
    notify();
}

void ReportController::declineInvite() {
    settings_.setInvite(QStringLiteral("declined"));
    notify();
}

bool ReportController::startSelfTest(const QString& url) {
    const auto service = agentspeed::report::ServiceUrl::parse(sv(url));
    if (!service || !service->loopbackHttp()) return false;
    editCustomUrl(qs(service->text()));
    commitCustomUrl();
    setEnabledByUser(true);
    auto* poll = new QTimer(this);
    // AGENT_SPEED_SELFTEST_AVATAR=<image>: once registered, also create a shared identity and upload that avatar.
    const auto avatarPath = qEnvironmentVariable("AGENT_SPEED_SELFTEST_AVATAR");
    auto profileStep = std::make_shared<int>(0);
    connect(poll, &QTimer::timeout, this, [this, poll, avatarPath, profileStep] {
        if (consent_ && consentReady()) startReporting();
        if (!avatarPath.isEmpty() && profileAvailable() && !profileBusy_) {
            switch ((*profileStep)++) {
            case 0: createIdentity(); break;
            case 1:
                loadAvatar(avatarPath);
                uploadAvatar();
                break;
            case 2: refreshProfile(); break;
            case 3:
                qInfo("report selftest: profile bound=%d avatar=%d devices=%d error=%s", profileBound(), !avatarImage_.isEmpty(),
                      static_cast<int>(profileDevices().size()), qPrintable(profileError_));
                break;
            default: break;
            }
        }
        static QString last;
        const auto line = statusLevel() + QStringLiteral(" ") + statusText();
        if (line != last) qInfo("report selftest: %s", qPrintable(line));
        last = line;
    });
    poll->start(500);
    return true;
}
