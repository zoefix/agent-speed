#include "quota/quota_worker.hpp"

#include <QDateTime>
#include <algorithm>
#include <QMetaObject>

#include "agentspeed/quota/bridge.hpp"
#include "agentspeed/quota/cli_quota.hpp"
#include "agentspeed/quota/codex.hpp"
#include "agentspeed/quota/desktop_reader.hpp"
#include "agentspeed/quota/paths.hpp"
#include "agentspeed/quota/presence.hpp"
#include "agentspeed/quota/sources.hpp"
#include "agentspeed/quota/vendor_service.hpp"
#include "agentspeed/traffic_linux.hpp"
#include "quota/quota_transport.hpp"
#include "quota/secret_service.hpp"

namespace quota = agentspeed::quota;
using agentspeed::SourceKind;

namespace {

constexpr std::int64_t kBridgeCheckMs = 60'000;

// The home as the readers see it, with the user's own environment.
agentspeed::traffic::Env linuxEnv() {
    agentspeed::traffic::Env env;
    env.platform = agentspeed::traffic::Platform::Linux;
    if (const char* home = std::getenv("HOME")) env.home = home;
    return env;
}

QString code(const std::optional<quota::VendorStatus>& status) {
    return status ? QString::fromUtf8(quota::vendorStatusId(status->kind)) : QString();
}

// The settings file as Settings shows it, `~` for the home.
QString abbreviated(const std::string& path) {
    const auto home = quota::homeFolder(quota::processEnvironment());
    if (!home.empty() && home != "/" && path.rfind(home + "/", 0) == 0) return QStringLiteral("~") + QString::fromStdString(path.substr(home.size()));
    return QString::fromStdString(path);
}

}  // namespace

QuotaWorker::QuotaWorker(std::shared_ptr<quota::CodexCollector> codex, QString helperSource)
    : codex_(std::move(codex)), helperSource_(std::move(helperSource)) {}

QuotaWorker::~QuotaWorker() {
    if (transport_) transport_->abortAll();
}

void QuotaWorker::start() {
    const auto environment = quota::processEnvironment();
    transport_ = std::make_unique<QuotaTransport>();
    secrets_ = std::make_unique<SecretService>();
    quota::VendorServiceOptions options;
    options.wake = [this] { QMetaObject::invokeMethod(this, &QuotaWorker::tick, Qt::QueuedConnection); };
    options.log = [](std::string_view host, int status) { qInfo("quota: %.*s %d", static_cast<int>(host.size()), host.data(), status); };
    vendors_ = std::make_unique<quota::VendorService>(quota::VendorEnvironment::system(), *transport_, *secrets_, std::move(options));
    desktop_ = std::make_unique<quota::desktop::Reader>(quota::desktopCacheFolder(environment));
    const auto paths = quota::bridge::Paths::forComputer(environment);
    cliFile_ = std::make_unique<quota::cli::FileReader>(paths.quotaFile());
    bridge_ = std::make_unique<quota::bridge::Bridge>(paths);
    codexMarker_ = std::make_unique<quota::SignInMarker>(quota::SignInKind::Codex, quota::codexAuthFile(environment));
    claudeMarker_ = std::make_unique<quota::SignInMarker>(quota::SignInKind::ClaudeCode, quota::claudeAccountFile(environment));
    running_ = std::make_unique<quota::RunningSources>(linuxEnv());
    processes_ = std::make_unique<agentspeed::traffic::sampler::ProcessReader>();
    timer_ = new QTimer(this);
    timer_->setInterval(1000);
    connect(timer_, &QTimer::timeout, this, &QuotaWorker::tick);
    timer_->start();
    tick();
}

bool QuotaWorker::on(std::string_view key) const { return settings_.switchesOn.contains(key); }

bool QuotaWorker::enabled(SourceKind source) const { return settings_.enabled.contains(source); }

void QuotaWorker::setSettings(QuotaWorkerSettings settings) {
    if (settings == settings_) return;
    // A switch turned on reads its local files at once.
    for (const auto& key : settings.switchesOn) {
        if (!settings_.switchesOn.contains(key)) readLocalNow_ = true;
    }
    settings_ = std::move(settings);
    if (timer_) tick();
}

void QuotaWorker::checkNow() {
    readLocalNow_ = true;
    bridgeCheckedMs_.reset();
    cliCheckedMs_.reset();
    if (timer_) tick();
}

void QuotaWorker::refreshBridge(std::int64_t nowMs) {
    const bool bridgeOn = on("quotaClaudeCli");
    if (!bridgeOn) {
        cliState_ = cliFailure_.isEmpty() ? QStringLiteral("off") : cliFailure_;
        bridgeCheckedMs_.reset();
        helperInstalled_ = false;
        return;
    }
    // At every launch while it is on, the copy follows the helper beside the app (QUOTAS 5.1).
    if (!helperInstalled_) helperInstalled_ = bridge_->installHelper(helperSource_.toStdString());
    if (!bridgeCheckedMs_ || nowMs - *bridgeCheckedMs_ >= kBridgeCheckMs) {
        bridgeCheckedMs_ = nowMs;
        cliState_ = QString::fromUtf8(quota::bridge::statusId(bridge_->status()));
    }
    // Ours: waiting until the quota file exists, then ok, as soon as the 5 s reader sees it (QUOTAS 5.4).
    if (cliState_ == QLatin1String("waiting") || cliState_ == QLatin1String("ok")) {
        cliState_ = cliFile_->hasFile() ? QStringLiteral("ok") : QStringLiteral("waiting");
    }
}

void QuotaWorker::enableCli() {
    if (!bridge_) return;
    const auto result = bridge_->turnOn(helperSource_.toStdString());
    if (result == quota::bridge::TurnOnResult::On) {
        cliFailure_.clear();
        helperInstalled_ = true;
        bridgeCheckedMs_.reset();
        emit cliEnabled(true, {});
    } else {
        cliFailure_ = result == quota::bridge::TurnOnResult::Unreadable ? QStringLiteral("unreadable") : QStringLiteral("failed");
        emit cliEnabled(false, cliFailure_);
    }
    tick();
}

void QuotaWorker::disableCli() {
    if (!bridge_) return;
    bridge_->turnOff();
    cliFailure_.clear();
    emit cliDisabled();
    tick();
}

void QuotaWorker::saveZcodeKey(QString key) {
    if (!secrets_) return;
    auto bytes = key.toStdString();
    key.fill(QLatin1Char('\0'));
    key.clear();
    if (secrets_->store(quota::zcodeKeyAttributes(), "Agent Speed ZCode API key", bytes)) zcodeKeySaved_ = true;
    std::fill(bytes.begin(), bytes.end(), '\0');
    if (vendors_) vendors_->zcodeKeyChanged();
    tick();
}

void QuotaWorker::removeZcodeKey() {
    if (!secrets_) return;
    secrets_->remove(quota::zcodeKeyAttributes());
    zcodeKeySaved_ = secrets_->has(quota::zcodeKeyAttributes());
    if (vendors_) vendors_->zcodeKeyChanged();
    tick();
}

void QuotaWorker::tick() {
    if (!timer_) return;
    const auto nowMs = QDateTime::currentMSecsSinceEpoch();
    const bool codexOn = enabled(SourceKind::Codex) && on("quotaCodex");
    const bool desktopOn = enabled(SourceKind::ClaudeDesktop) && on("quotaClaudeDesktop");
    const bool cliOn = enabled(SourceKind::ClaudeCode) && on("quotaClaudeCli");
    bool anyOn = codexOn || desktopOn || cliOn;
    for (const auto& route : quota::sourceRoutes()) anyOn = anyOn || (enabled(route.source) && on(route.switchKey));

    // QUOTAS 6: a light listing every 15 s while any route reads (the card, the vendors' cadence, Desktop's start).
    if (anyOn && (!listedMs_ || nowMs - *listedMs_ >= quota::RunningSources::kListIntervalMs)) {
        listedMs_ = nowMs;
        running_->update(processes_->read());
    } else if (!anyOn) {
        listedMs_.reset();
    }
    const auto& running = running_->running();

    codex_->setEnabled(codexOn);
    desktop_->tick({nowMs, desktopOn, running_->runs(SourceKind::ClaudeDesktop), running_->claudeDesktopStarted()});
    // The quota file every 5 s while the bridge is on (QUOTAS 5.5 Reader), at once when it turns on.
    if (!cliOn) {
        cliCheckedMs_.reset();
        cliFile_->refresh(false);
    } else if (!cliCheckedMs_ || nowMs - *cliCheckedMs_ >= quota::cli::FileReader::kCheckIntervalMs) {
        cliCheckedMs_ = nowMs;
        cliFile_->refresh(true);
    }
    refreshBridge(nowMs);
    // Whether a ZCode key is saved, asked of the keyring only once its switch is on (nothing is unlocked or read).
    if (!zcodeKeyKnown_ && enabled(SourceKind::ZCode) && on("quotaZcode")) {
        zcodeKeyKnown_ = true;
        zcodeKeySaved_ = secrets_->has(quota::zcodeKeyAttributes());
    }
    codexMarker_->refresh(nowMs, codexOn);
    claudeMarker_->refresh(nowMs, cliOn);

    quota::VendorInputs inputs;
    inputs.nowMs = nowMs;
    inputs.networkEnabled = settings_.networkEnabled;
    inputs.enabledSources = settings_.enabled;
    inputs.switchesOn = settings_.switchesOn;
    inputs.qoderConsent = on("quotaQoder");
    inputs.workBuddyConsent = on("quotaWorkBuddy");
    inputs.zcodeRegion = quota::requests::zaiRegionFromId(settings_.zcodeRegion.toStdString()).value_or(quota::requests::ZaiRegion::Global);
    inputs.running = running;
    inputs.dashboardOpen = settings_.dashboardOpen;
    inputs.readLocalNow = readLocalNow_;
    readLocalNow_ = false;
    vendors_->tick(inputs);
    publish(nowMs);
}

void QuotaWorker::publish(std::int64_t nowMs) {
    QuotaPublication out;
    const auto& readings = vendors_->readings();
    const auto runs = [&](SourceKind source) { return running_->runs(source) || readings.runs(source); };
    if (enabled(SourceKind::Codex) && on("quotaCodex")) {
        out.sources.push_back({SourceKind::Codex, codex_->snapshot(), runs(SourceKind::Codex), codexMarker_->signedIn()});
    }
    if (enabled(SourceKind::ClaudeCode) && on("quotaClaudeCli")) {
        out.sources.push_back({SourceKind::ClaudeCode, cliFile_->snapshot(), runs(SourceKind::ClaudeCode), claudeMarker_->signedIn()});
    }
    if (enabled(SourceKind::ClaudeDesktop) && on("quotaClaudeDesktop")) {
        out.sources.push_back({SourceKind::ClaudeDesktop, desktop_->snapshot(), runs(SourceKind::ClaudeDesktop),
                               quota::desktopSignedIn(desktop_->snapshot(), nowMs)});
    }
    for (const auto route : quota::kVendorRoutes) {
        if (route == quota::VendorRoute::GrokBuildLive) continue;
        const auto source = quota::vendorRouteSource(route);
        const auto sources = route == quota::VendorRoute::Qoder ? std::vector<SourceKind>{SourceKind::Qoder, SourceKind::QoderCli}
                                                                : std::vector<SourceKind>{source};
        for (const auto s : sources) {
            if (!enabled(s) || !on(quota::routeOf(s)->switchKey)) continue;
            out.sources.push_back({s, readings.snapshot(s), runs(s), readings.signedIn(s)});
        }
    }
    std::sort(out.sources.begin(), out.sources.end(), [](const auto& a, const auto& b) { return a.source < b.source; });
    for (const auto route : quota::kVendorRoutes) {
        const auto key = route == quota::VendorRoute::GrokBuildLive ? std::string(quota::kGrokLiveSwitch)
                                                                    : std::string(quota::routeOf(quota::vendorRouteSource(route))->switchKey);
        const auto status = readings.status(route);
        if (!status) continue;
        out.statuses[QString::fromStdString(key)] = {code(status), QString::fromStdString(status->host), status->okMs};
    }
    out.cliState = cliState_;
    out.cliPath = abbreviated(bridge_->paths().settingsFile);
    if (cliState_ != QLatin1String("off")) {
        const auto& snapshot = cliFile_->snapshot();
        out.statuses[QStringLiteral("quotaClaudeCli")] = {cliState_, {}, snapshot ? snapshot->observedMs : 0};
    }
    out.zcodeKeySaved = zcodeKeySaved_;
    if (last_ && last_->sources == out.sources && last_->statuses == out.statuses && last_->cliState == out.cliState &&
        last_->cliPath == out.cliPath && last_->zcodeKeySaved == out.zcodeKeySaved) {
        return;
    }
    last_ = out;
    emit published(std::move(out));
}
