#pragma once

#include <QObject>
#include <QTimer>
#include <QVariantList>
#include <array>
#include <map>
#include <memory>
#include <optional>
#include <set>
#include <tuple>
#include <vector>

#include "agentspeed/report_body.hpp"
#include "agentspeed/report_scheduler.hpp"
#include "agentspeed/report_profile.hpp"
#include "agentspeed/report_service.hpp"
#include "agentspeed/report_stream.hpp"
#include "monitor_worker.hpp"
#include "report/report_http.hpp"
#include "report/report_keys.hpp"
#include "report/report_settings.hpp"
#include "report/report_socket.hpp"

class Preferences;
class Strings;
namespace agentspeed::pricing {
class PriceBook;
}

// Live reporting (DESIGN 11, METRICS 11, REPORTING): the API pane, the consent sheet, the preview, the dashboard
// invitation and indicator, and the run that sends reports. Lives on the UI thread; every QML-facing value is a
// property that changes with `changed`.
class ReportController : public QObject {
    Q_OBJECT
    // Master and service.
    Q_PROPERTY(bool enabled READ enabled NOTIFY changed)
    Q_PROPERTY(bool networkEnabled READ networkEnabled NOTIFY changed)
    Q_PROPERTY(QStringList presetNames READ presetNames NOTIFY changed)
    Q_PROPERTY(int serviceIndex READ serviceIndex NOTIFY changed)
    Q_PROPERTY(QString customUrl READ customUrl NOTIFY changed)
    Q_PROPERTY(QString urlNote READ urlNote NOTIFY changed)
    Q_PROPERTY(QString urlNoteLevel READ urlNoteLevel NOTIFY changed)
    Q_PROPERTY(bool serviceKnown READ serviceKnown NOTIFY changed)
    Q_PROPERTY(QString serviceName READ serviceName NOTIFY changed)
    Q_PROPERTY(QString serviceHost READ serviceHost NOTIFY changed)
    Q_PROPERTY(QString serviceDescription READ serviceDescription NOTIFY changed)
    Q_PROPERTY(QString serviceRetention READ serviceRetention NOTIFY changed)
    Q_PROPERTY(QString termsUrl READ termsUrl NOTIFY changed)
    Q_PROPERTY(QString privacyUrl READ privacyUrl NOTIFY changed)
    Q_PROPERTY(QString leaderboardUrl READ leaderboardUrl NOTIFY changed)
    Q_PROPERTY(bool checking READ checking NOTIFY changed)
    Q_PROPERTY(QString fetchMessage READ fetchMessage NOTIFY changed)
    Q_PROPERTY(QString fetchLevel READ fetchLevel NOTIFY changed)
    Q_PROPERTY(bool needsReview READ needsReview NOTIFY changed)
    // Transport.
    Q_PROPERTY(bool webSocket READ webSocket NOTIFY changed)
    Q_PROPERTY(QString transportNote READ transportNote NOTIFY changed)
    Q_PROPERTY(bool transportUnsupported READ transportUnsupported NOTIFY changed)
    Q_PROPERTY(QString throttledNote READ throttledNote NOTIFY changed)
    // Username and API key.
    Q_PROPERTY(QString username READ username NOTIFY changed)
    Q_PROPERTY(QString usernameError READ usernameError NOTIFY changed)
    Q_PROPERTY(QString usernameNote READ usernameNote NOTIFY changed)
    Q_PROPERTY(bool usernameNoteError READ usernameNoteError NOTIFY changed)
    Q_PROPERTY(bool transferOffered READ transferOffered NOTIFY changed)
    Q_PROPERTY(bool apiKeyVisible READ apiKeyVisible NOTIFY changed)
    Q_PROPERTY(bool apiKeyRequired READ apiKeyRequired NOTIFY changed)
    Q_PROPERTY(QString apiKeyMasked READ apiKeyMasked NOTIFY changed)
    Q_PROPERTY(QString apiKeyUrl READ apiKeyUrl NOTIFY changed)
    // What's sent.
    Q_PROPERTY(QVariantList sections READ sections NOTIFY changed)
    Q_PROPERTY(bool scoreCaption READ scoreCaption NOTIFY changed)
    // Status.
    Q_PROPERTY(QString statusText READ statusText NOTIFY changed)
    Q_PROPERTY(QString statusLevel READ statusLevel NOTIFY changed)
    Q_PROPERTY(QString statusFootnote READ statusFootnote NOTIFY changed)
    Q_PROPERTY(QString rankText READ rankText NOTIFY changed)
    Q_PROPERTY(bool paused READ paused NOTIFY changed)
    Q_PROPERTY(bool canPause READ canPause NOTIFY changed)
    Q_PROPERTY(bool showRetry READ showRetry NOTIFY changed)
    Q_PROPERTY(bool showReview READ showReview NOTIFY changed)
    Q_PROPERTY(QString wsStats READ wsStats NOTIFY changed)
    Q_PROPERTY(QString wsCounters READ wsCounters NOTIFY changed)
    // This device and other services.
    Q_PROPERTY(bool deviceVisible READ deviceVisible NOTIFY changed)
    Q_PROPERTY(QString deviceDetail READ deviceDetail NOTIFY changed)
    Q_PROPERTY(QString profileUrl READ profileUrl NOTIFY changed)
    Q_PROPERTY(QString fingerprint READ fingerprint NOTIFY changed)
    Q_PROPERTY(QString forgetConfirm READ forgetConfirm NOTIFY changed)
    Q_PROPERTY(QString forgetState READ forgetState NOTIFY changed)
    Q_PROPERTY(QString forgetMessage READ forgetMessage NOTIFY changed)
    Q_PROPERTY(bool resetVisible READ resetVisible NOTIFY changed)
    Q_PROPERTY(QVariantList others READ others NOTIFY changed)
    // The consent sheet.
    Q_PROPERTY(bool consentOpen READ consentOpen NOTIFY changed)
    Q_PROPERTY(QString consentTitle READ consentTitle NOTIFY changed)
    Q_PROPERTY(QString consentBody READ consentBody NOTIFY changed)
    Q_PROPERTY(QString consentUsername READ consentUsername NOTIFY changed)
    Q_PROPERTY(QString consentUsernameError READ consentUsernameError NOTIFY changed)
    Q_PROPERTY(bool consentApiKeyVisible READ consentApiKeyVisible NOTIFY changed)
    Q_PROPERTY(bool consentApiKeyRequired READ consentApiKeyRequired NOTIFY changed)
    Q_PROPERTY(QString consentApiKeyError READ consentApiKeyError NOTIFY changed)
    Q_PROPERTY(QVariantList consentSections READ consentSections NOTIFY changed)
    Q_PROPERTY(bool consentStarting READ consentStarting NOTIFY changed)
    Q_PROPERTY(QString consentFailure READ consentFailure NOTIFY changed)
    Q_PROPERTY(bool consentTransfer READ consentTransfer NOTIFY changed)
    Q_PROPERTY(bool consentReady READ consentReady NOTIFY changed)
    // The preview.
    Q_PROPERTY(QString previewBody READ previewBody NOTIFY previewChanged)
    Q_PROPERTY(QString previewRequest READ previewRequest NOTIFY previewChanged)
    Q_PROPERTY(QString previewFooter READ previewFooter NOTIFY previewChanged)
    Q_PROPERTY(QString previewHint READ previewHint NOTIFY previewChanged)
    // The dashboard: invitation, indicator; the tray menu; About and the traffic pane.
    Q_PROPERTY(bool inviteVisible READ inviteVisible NOTIFY changed)
    Q_PROPERTY(QString inviteBody READ inviteBody NOTIFY changed)
    Q_PROPERTY(QString inviteUsername READ inviteUsername NOTIFY changed)
    Q_PROPERTY(bool inviteDone READ inviteDone NOTIFY changed)
    Q_PROPERTY(bool indicatorVisible READ indicatorVisible NOTIFY changed)
    Q_PROPERTY(bool indicatorPaused READ indicatorPaused NOTIFY changed)
    Q_PROPERTY(bool indicatorAttention READ indicatorAttention NOTIFY changed)
    Q_PROPERTY(QString indicatorTip READ indicatorTip NOTIFY changed)
    Q_PROPERTY(bool menuVisible READ menuVisible NOTIFY changed)
    Q_PROPERTY(QString menuText READ menuText NOTIFY changed)
    Q_PROPERTY(bool reporting READ reporting NOTIFY changed)
    Q_PROPERTY(QString trafficNote READ trafficNote NOTIFY changed)
    // The profile extensions (REPORTING-EXTENSIONS 4–6).
    Q_PROPERTY(bool profileAvailable READ profileAvailable NOTIFY changed)
    Q_PROPERTY(bool avatarSupported READ avatarSupported NOTIFY changed)
    Q_PROPERTY(bool identitySupported READ identitySupported NOTIFY changed)
    Q_PROPERTY(bool profileBound READ profileBound NOTIFY changed)
    Q_PROPERTY(QString profileName READ profileName NOTIFY changed)
    Q_PROPERTY(QString avatarSource READ avatarSource NOTIFY changed)
    Q_PROPERTY(bool avatarPending READ avatarPending NOTIFY changed)
    Q_PROPERTY(bool profileBusy READ profileBusy NOTIFY changed)
    Q_PROPERTY(QString profileError READ profileError NOTIFY changed)
    Q_PROPERTY(QVariantList profileDevices READ profileDevices NOTIFY changed)
    Q_PROPERTY(QString pairingState READ pairingState NOTIFY changed)
    Q_PROPERTY(QString pairingCode READ pairingCode NOTIFY changed)
    Q_PROPERTY(QString pairingClaimant READ pairingClaimant NOTIFY changed)
    Q_PROPERTY(bool pairingInviter READ pairingInviter NOTIFY changed)

public:
    ReportController(Preferences& prefs, const Strings& strings, std::shared_ptr<agentspeed::pricing::PriceBook> prices, QString dataDir,
                     QObject* parent = nullptr);
    ~ReportController() override;
    void start();
    // Quitting: a leaving report when one can go out within 1 s; `done` runs once it is answered or the time is up.
    void quit(std::function<void()> done);

    bool enabled() const { return settings_.enabled(); }
    bool networkEnabled() const;
    QStringList presetNames() const;
    int serviceIndex() const;
    QString customUrl() const { return customText_; }
    QString urlNote() const;
    QString urlNoteLevel() const;
    bool serviceKnown() const { return discovery_.has_value(); }
    QString serviceName() const;
    QString serviceHost() const;
    QString serviceDescription() const;
    QString serviceRetention() const;
    QString termsUrl() const;
    QString privacyUrl() const;
    QString leaderboardUrl() const;
    bool checking() const { return fetching_; }
    QString fetchMessage() const;
    QString fetchLevel() const;
    bool needsReview() const;
    bool webSocket() const;
    QString transportNote() const;
    bool transportUnsupported() const;
    QString throttledNote() const;
    QString username() const;
    QString usernameError() const;
    QString usernameNote() const { return usernameNote_; }
    bool usernameNoteError() const { return usernameNoteError_; }
    bool transferOffered() const { return transferOffered_; }
    bool apiKeyVisible() const;
    bool apiKeyRequired() const;
    QString apiKeyMasked() const;
    QString apiKeyUrl() const;
    QVariantList sections() const;
    bool scoreCaption() const;
    QString statusText() const;
    QString statusLevel() const;
    QString statusFootnote() const;
    QString rankText() const;
    bool paused() const;
    bool canPause() const;
    bool showRetry() const;
    bool showReview() const;
    QString wsStats() const;
    QString wsCounters() const;
    bool deviceVisible() const;
    QString deviceDetail() const;
    QString profileUrl() const;
    QString fingerprint() const;
    QString forgetConfirm() const;
    QString forgetState() const { return forgetState_; }
    QString forgetMessage() const;
    bool resetVisible() const { return problem_ == agentspeed::report::Problem::KeyMismatch; }
    QVariantList others() const;
    bool consentOpen() const { return consent_.has_value(); }
    QString consentTitle() const;
    QString consentBody() const;
    QString consentUsername() const;
    QString consentUsernameError() const;
    bool consentApiKeyVisible() const;
    bool consentApiKeyRequired() const;
    QString consentApiKeyError() const;
    QVariantList consentSections() const;
    bool consentStarting() const { return consent_ && consent_->starting; }
    QString consentFailure() const;
    bool consentTransfer() const { return consent_ && consent_->offersTransfer; }
    bool consentReady() const;
    QString previewBody() const { return previewBody_; }
    QString previewRequest() const { return previewRequest_; }
    QString previewFooter() const { return previewFooter_; }
    QString previewHint() const;
    bool inviteVisible() const;
    QString inviteBody() const;
    QString inviteUsername() const;
    bool inviteDone() const { return inviteDone_; }
    bool indicatorVisible() const { return enabled(); }
    bool indicatorPaused() const { return paused(); }
    bool indicatorAttention() const;
    QString indicatorTip() const;
    bool menuVisible() const;
    QString menuText() const;
    bool reporting() const { return enabled(); }
    QString trafficNote() const;
    bool profileAvailable() const;
    bool avatarSupported() const;
    bool identitySupported() const;
    bool profileBound() const { return profile_ && profile_->sharedUserId.has_value(); }
    QString profileName() const;
    QString avatarSource() const;
    bool avatarPending() const { return !pendingAvatar_.isEmpty(); }
    bool profileBusy() const { return profileBusy_; }
    QString profileError() const { return profileError_; }
    QVariantList profileDevices() const;
    QString pairingState() const { return pairing_ ? QString::fromStdString(pairing_->state) : QString(); }
    QString pairingCode() const { return pairingCode_; }
    QString pairingClaimant() const;
    bool pairingInviter() const { return pairingInviter_; }

    Q_INVOKABLE void setEnabledByUser(bool on);
    Q_INVOKABLE void chooseService(int index);
    Q_INVOKABLE void editCustomUrl(const QString& text);
    Q_INVOKABLE void commitCustomUrl();
    Q_INVOKABLE void retry();
    Q_INVOKABLE void review();
    Q_INVOKABLE void setWebSocket(bool on);
    Q_INVOKABLE void commitUsername(const QString& name);
    Q_INVOKABLE void randomizeUsername();
    Q_INVOKABLE void transferUsername();
    Q_INVOKABLE void saveApiKey(const QString& key);
    Q_INVOKABLE void clearApiKey();
    Q_INVOKABLE void setSection(const QString& key, bool on);
    Q_INVOKABLE void pause(int hours);
    Q_INVOKABLE void resume();
    Q_INVOKABLE void forget();
    Q_INVOKABLE void forgetLocalOnly();
    Q_INVOKABLE void resetDevice();
    Q_INVOKABLE void forgetOther(const QString& origin);
    Q_INVOKABLE void setConsentUsername(const QString& name);
    Q_INVOKABLE void randomizeConsentUsername();
    Q_INVOKABLE void setConsentApiKey(const QString& key);
    Q_INVOKABLE void setConsentSection(const QString& key, bool on);
    Q_INVOKABLE void startReporting();
    Q_INVOKABLE void transferConsent();
    Q_INVOKABLE void cancelConsent();
    // Opens the preview window; the window and the consent sheet watch the preview while they are visible.
    Q_INVOKABLE void openPreview() { emit previewRequested(); }
    Q_INVOKABLE void watchPreview(bool on);
    Q_INVOKABLE void copyPreview();
    Q_INVOKABLE void acceptInvite();
    Q_INVOKABLE void declineInvite();
    Q_INVOKABLE void openLink(const QString& url);
    Q_INVOKABLE void refreshProfile();
    Q_INVOKABLE void chooseAvatar();
    // The local preview of an image file (nothing is sent before uploadAvatar).
    void loadAvatar(const QString& path);
    Q_INVOKABLE void uploadAvatar();
    Q_INVOKABLE void cancelAvatar();
    Q_INVOKABLE void removeAvatar();
    Q_INVOKABLE void saveProfileName(const QString& name);
    Q_INVOKABLE void createIdentity();
    Q_INVOKABLE void unlinkDevice();
    Q_INVOKABLE void invitePairing();
    Q_INVOKABLE void joinPairing(const QString& code);
    Q_INVOKABLE void approvePairing();
    Q_INVOKABLE void cancelPairing();
    Q_INVOKABLE void checkPairing();
    Q_INVOKABLE void openDevices();
    Q_INVOKABLE void closeDevices();
    Q_INVOKABLE void showSettings() { emit settingsRequested(); }
    // Quits the app after the leaving report (METRICS 11.5: at most 1 s).
    Q_INVOKABLE void quitApp();
    // Development aid: reports to a local reference server (loopback http only, spec/tools/report_server.py) without
    // the consent clicks, and logs the status. Returns false for any other URL.
    bool startSelfTest(const QString& url);
    // Where the statistics watch goes (the monitor worker): on, and the responses and calls the reports took.
    void setWatchSink(std::function<void(bool, qint64, qint64)> sink) { watchSink_ = std::move(sink); }
    // The preview body with its keys in `keyColor` (DESIGN 11: keys secondary, values in the label color).
    Q_INVOKABLE QString previewRich(const QString& keyColor) const;

public slots:
    void onTick(const ReportTick& tick);

signals:
    void changed();
    void previewChanged();
    void previewRequested();
    void consentRequested();
    void settingsRequested();
    void forgetConfirmRequested();
    void devicesRequested();

private:
    struct Draft {
        agentspeed::report::ServiceUrl service;
        std::optional<agentspeed::report::Discovery> discovery;
        QString username, apiKey;
        std::set<agentspeed::report::Section> sections;
        bool review = false, starting = false, offersTransfer = false;
        QString usernameError, apiKeyError, failure;
    };
    // The WebSocket run (METRICS 11.9): its scheduler, window and the bodies awaiting an answer.
    struct Stream {
        QString id;
        std::unique_ptr<agentspeed::report::stream::StreamScheduler> scheduler;
        std::unique_ptr<agentspeed::report::Window> window;
        agentspeed::report::TitleTracker title;
        std::map<int, std::pair<std::string, std::string>> bodies;  // seq → body, its SHA-256
        std::optional<std::array<double, 6>> lastBuilt;             // the live values of the last built report, rounded as sent
        std::string lastScores;                                      // its perfScore objects (METRICS 11.10 WebSocket)
        bool lastActive = false;
    };
    struct Run {
        QString id;
        std::unique_ptr<agentspeed::report::Scheduler> scheduler;
        std::unique_ptr<agentspeed::report::Window> window;
        agentspeed::report::TitleTracker title;
        std::map<int, std::string> bodies;
        std::set<int> signWithPending;
    };
    enum class RenameKind { None, Working };

    // report_controller.cpp: selection, state and presentation helpers.
    void loadSelection();
    void notify();
    std::optional<agentspeed::report::ServiceUrl> selectedService() const { return service_; }
    QString originOf() const;
    agentspeed::report::ClientInfo client() const;
    agentspeed::report::UsernameRules rules() const;
    QString defaultUsername() const;
    QString nextUsername() const;
    QString usernameMessage(const std::optional<std::string>& reason, const agentspeed::report::UsernameRules& rules) const;
    QString failureText(ReportFailure failure) const;
    QString fetchText(ReportFetch::Kind kind) const;
    QString fmt(const QString& key, const QStringList& args) const;
    QString t(const char* key) const;
    std::set<agentspeed::report::Section> acceptedSections(const agentspeed::report::Discovery& discovery,
                                                         const std::set<agentspeed::report::Section>& switches) const;
    std::set<agentspeed::report::Section> switches() const;
    QVariantList sectionRows(const std::set<agentspeed::report::Section>& on, const std::optional<agentspeed::report::Discovery>& discovery) const;
    bool hasApiKey() const { return !apiKey_.isEmpty(); }
    bool discoveryStale() const;
    void saveIdentity();
    void dropIdentities(const QStringList& deviceIds);
    void after(int ms, std::function<void()> action);

    // report_account.cpp: discovery, consent, registration, renames, keys and forgetting.
    void fetchDiscovery(bool userAction);
    void applyFetch(const agentspeed::report::ServiceUrl& service, ReportFetch fetch, bool userAction);
    void openConsent(bool review);
    void ensureIdentity(const agentspeed::report::ServiceUrl& service);
    void registerDevice(bool transfer, std::function<void(const ReportResponse&)> done);
    void reRegister();
    void finishInviteSetup();
    void rename(const QString& name);
    void rotateKeyIfDue();
    void promotePendingKey();
    void signedCall(const QByteArray& method, const std::string& path, const std::string& body, bool usePending, bool withNewKey,
                    std::function<void(const ReportResponse&)> done, const char* contentType = nullptr, qint64 cap = 16'384);
    void scheduleRefresh();
    void endIdentity(bool deleteKeys);

    // report_delivery.cpp: runs, ticks, reports and reactions.
    void post(agentspeed::report::Event event);
    void tickRun(const std::shared_ptr<const agentspeed::report::Sample>& sample, qint64 now, std::optional<qint64> observed = std::nullopt);
    bool ready() const;
    void startRunIfNeeded(const std::shared_ptr<const agentspeed::report::Sample>& sample);
    void endRun();
    void apply(const agentspeed::report::Actions& actions, const agentspeed::report::Sample& sample);
    void build(const agentspeed::report::Ticket& ticket, const agentspeed::report::Sample& sample);
    void send(const agentspeed::report::Ticket& ticket);
    void complete(const agentspeed::report::Ticket& ticket, const QString& runId, const ReportResponse& response, bool usedPending);
    void react(const agentspeed::report::Reaction& reaction);
    agentspeed::report::Options options(const agentspeed::report::Discovery& discovery, const std::set<agentspeed::report::Section>& switches) const;
    qint64 correctedMs() const;

    // report_stream_run.cpp: the WebSocket transport.
    bool usesWebSocket() const;
    void streamTick(const std::shared_ptr<const agentspeed::report::Sample>& sample, qint64 now, std::optional<qint64> observed, bool wake);
    void startStreamIfNeeded(const std::shared_ptr<const agentspeed::report::Sample>& sample, qint64 now);
    void endStream();
    void applyStream(const std::vector<agentspeed::report::stream::Action>& actions, const agentspeed::report::Sample* sample);
    void streamConnect();
    void streamWrite(int seq);
    void streamReact(const std::string& name);
    void streamStopped(const std::string& reason);
    void onSocketMessage(const QByteArray& text, bool binary);
    std::array<double, 6> sentLive(const agentspeed::report::Sample& sample) const;

    // METRICS 11.10 Watch, sent to the engine when it changes.
    void updatePerfWatch();
    std::string scoreSignature(const agentspeed::report::Sample& sample) const;

    // report_profile_ui.cpp.
    void profileCall(const QByteArray& method, const std::string& path, const std::string& body, const char* contentType,
                     std::function<void(const ReportResponse&)> done);
    void applyProfile(const ReportResponse& response);
    void fetchAvatar();
    void pollPairing();

    // report_preview.cpp.
    void refreshPreview();

    Preferences& prefs_;
    const Strings& strings_;
    std::shared_ptr<agentspeed::pricing::PriceBook> prices_;
    ReportSettings settings_;
    ReportKeys keys_;
    ReportHttp http_;
    std::optional<agentspeed::report::ServiceUrl> service_;
    QString customText_;
    std::optional<agentspeed::report::Discovery> discovery_;  // the stored or fetched document of the selected service
    bool fetching_ = false;
    std::optional<ReportFetch::Kind> fetchFailure_;
    std::optional<ReportIdentity> identity_;
    std::optional<agentspeed::report::PrivateKey> key_, pendingKey_;
    QString apiKey_;
    std::optional<Draft> consent_;
    std::optional<Run> run_;
    std::optional<Stream> stream_;
    ReportSocket socket_;
    QTimer wakeTimer_;
    std::optional<std::string> streamStop_;
    std::optional<std::int64_t> streamServerTime_;
    agentspeed::report::stream::Counters streamCounters_;
    std::vector<std::int64_t> streamLatency_;
    std::optional<std::optional<agentspeed::report::StreamSettings>> pendingStreamSettings_;
    std::function<void(bool, qint64, qint64)> watchSink_;
    std::optional<std::tuple<bool, qint64, qint64>> sentWatch_;
    std::vector<agentspeed::report::Event> pendingEvents_;
    std::shared_ptr<const agentspeed::report::Sample> lastSample_;
    qint64 clockCorrectionSeconds_ = 0;
    int strikes_ = 0;
    qint64 lastReRegisterMs_ = 0;
    bool renamedForMismatch_ = false;
    bool rotating_ = false;
    bool registering_ = false;
    bool inviteSetup_ = false;
    bool inviteDone_ = false;
    agentspeed::report::Problem problem_ = agentspeed::report::Problem::None;
    std::optional<ReportFailure> retryReason_, registrationFailure_, forgetFailure_;
    qint64 rateLimitedUntilMs_ = 0;
    qint64 lastDeliveryMs_ = 0;
    std::optional<int> rank_;
    QString usernameNote_;
    bool usernameNoteError_ = false;
    bool transferOffered_ = false;
    QString forgetState_;
    QString fallbackUsername_;
    QTimer refreshTimer_, pauseTimer_, statusTimer_;
    std::function<void()> quitDone_;
    // The preview (DESIGN 11, Preview window): its own window of the current values while open.
    int previewWatchers_ = 0;
    std::unique_ptr<agentspeed::report::Window> previewWindow_;
    qint64 previewStartedMs_ = 0;
    QString previewRunId_, previewDeviceId_;
    QString previewBody_, previewRequest_, previewFooter_, previewCopy_;
    std::optional<agentspeed::report::profile::Profile> profile_;
    QByteArray pendingAvatar_, avatarImage_;
    bool profileBusy_ = false, pairingInviter_ = false, devicesOpen_ = false;
    QString profileError_, pairingCode_;
    std::optional<agentspeed::report::profile::Pairing> pairing_;
    QTimer pairingTimer_;
};
