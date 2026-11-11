#include "quota_session.hpp"

#include <algorithm>
#include <chrono>

#include "agentspeed/quota/cli_quota.hpp"
#include "agentspeed/quota/presence.hpp"
#include "agentspeed/quota/vendor_service.hpp"
#include "agentspeed/traffic_linux.hpp"
#include "quota_bridge.hpp"
#include "quota_lines.hpp"
#include "quota_transport.hpp"

namespace agentspeed::relay {

namespace {

using Status = remote::HostQuota::Status;
using BridgeState = remote::HostQuotaBridge::State;

constexpr std::int64_t kBridgeCheckMs = 60'000;
constexpr std::int64_t kReportIntervalMs = 1'000;

Status statusOf(const std::optional<quota::VendorStatus>& status) {
    if (!status) return Status::Ok;
    switch (status->kind) {
        case quota::VendorStatusKind::Ok: return Status::Ok;
        case quota::VendorStatusKind::NoCredential:
        case quota::VendorStatusKind::NoBrowser: return Status::NoCredential;
        case quota::VendorStatusKind::Expired: return Status::Expired;
        case quota::VendorStatusKind::Denied: return Status::Denied;
        case quota::VendorStatusKind::Failed: return Status::Failed;
        case quota::VendorStatusKind::NetworkOff: return Status::NetworkOff;
        case quota::VendorStatusKind::NotRunning: return Status::NotRunning;
        case quota::VendorStatusKind::NoCertificates: return Status::NoCertificates;
    }
    return Status::Failed;
}

bool hasWindows(const std::optional<quota::Snapshot>& snapshot) { return snapshot && !snapshot->windows.empty(); }

// The relay's own intervals run on the monotonic clock; the readers get the host's wall clock.
std::int64_t steadyMs() {
    return std::chrono::duration_cast<std::chrono::milliseconds>(std::chrono::steady_clock::now().time_since_epoch()).count();
}

// Grok Build has two routes behind one line: the status of the one whose snapshot is shown, else the live request's
// while it is on (it says why the network gave nothing), else the log's.
std::optional<quota::VendorStatus> grokStatus(const quota::VendorReadings& readings, const std::optional<quota::Snapshot>& shown) {
    const auto live = readings.routes.find(quota::VendorRoute::GrokBuildLive);
    if (live == readings.routes.end()) return readings.status(quota::VendorRoute::GrokBuild);
    if (shown) {
        const auto& liveSnapshot = live->second.snapshot;
        return liveSnapshot && liveSnapshot->observedMs == shown->observedMs ? live->second.status
                                                                              : readings.status(quota::VendorRoute::GrokBuild);
    }
    return live->second.status ? live->second.status : readings.status(quota::VendorRoute::GrokBuild);
}

}  // namespace

QuotaSession::QuotaSession(Output& out, std::set<SourceKind> enabled, QuotaOptions options, Clock clock)
    : out_(out),
      enabled_(std::move(enabled)),
      options_(std::move(options)),
      clock_(std::move(clock)),
      environment_(options_.environment ? options_.environment : quota::processEnvironment()),
      codex_(std::make_shared<quota::CodexCollector>()) {
    // Nothing is read before the app's config says what (QUOTAS 9.1).
    codex_->setEnabled(false);
}

QuotaSession::~QuotaSession() = default;

quota::CodexCollector::Observer QuotaSession::codexObserver() const {
    return [codex = codex_](const json::Value& rateLimits, std::int64_t timestampMs) { codex->observe(rateLimits, timestampMs); };
}

// The readers, made with the first config: a relay of an app without quotas never starts the transport's thread.
void QuotaSession::build() {
    const auto folder = absoluteFolder(options_.folder);
    bridge_ = std::make_unique<HostBridge>(folder, environment_, options_.self);
    cliFile_ = std::make_unique<quota::cli::FileReader>(bridge_->paths().quotaFile());
    codexMarker_ = std::make_unique<quota::SignInMarker>(quota::SignInKind::Codex, quota::codexAuthFile(environment_));
    claudeMarker_ = std::make_unique<quota::SignInMarker>(quota::SignInKind::ClaudeCode, quota::claudeAccountFile(environment_));
    traffic::Env env;
    env.platform = traffic::Platform::Linux;
    env.home = quota::homeFolder(environment_);
    runningSources_ = std::make_unique<quota::RunningSources>(std::move(env));
    processes_ = std::make_unique<traffic::sampler::ProcessReader>();
    auto client = options_.client;
    if (client.userAgent.empty()) client.userAgent = "AgentSpeed/" + options_.version + " (Linux)";
    if (!client.variable) {
        client.variable = [environment = environment_](std::string_view name) { return environment(std::string(name)); };
    }
    if (!client.clock) client.clock = clock_;
    transport_ = std::make_unique<RelayTransport>(std::move(client));
    quota::VendorServiceOptions vendorOptions;
    vendorOptions.available = quota::hostRoutes();
    vendorOptions.antigravity = options_.antigravity;
    static quota::NoSecrets noSecrets;
    vendors_ = std::make_unique<quota::VendorService>(options_.vendors ? *options_.vendors : quota::VendorEnvironment::system(), *transport_,
                                                      noSecrets, std::move(vendorOptions));
}

void QuotaSession::setConfig(const QuotaConfig& config) {
    const bool first = !config_;
    if (first) build();
    QuotaConfig active = config;
    active.sources.clear();
    for (const auto source : config.sources) {
        if (enabled_.contains(source) && remote::quotas::hostReads(source)) active.sources.insert(source);
    }
    config_ = active;
    codex_->setEnabled(active.sources.contains(SourceKind::Codex));
    bridgeCheckedMs_ = steadyMs();
    // Every config is answered with the bridge's state: the app waits for `off` before it lets a removed host go.
    sendBridge(bridge_->apply(config.claudeBridge, first));
    // The next tick looks again at once: the processes, the quota file and every report.
    reportDue_ = true;
    cliCheckedMs_.reset();
    listedMs_.reset();
}

void QuotaSession::sendBridge(BridgeState state) {
    bridgeState_ = state;
    out_.line(quotaBridgeLine(state, bridge_->settingsPath()));
}

void QuotaSession::tick() {
    if (!config_) return;
    const auto nowMs = clock_();
    const auto steady = steadyMs();
    transport_->deliver();
    refresh(nowMs, steady);
    if (bridge_->on() && (!bridgeCheckedMs_ || steady - *bridgeCheckedMs_ >= kBridgeCheckMs)) {
        bridgeCheckedMs_ = steady;
        if (const auto state = bridge_->check(); state != bridgeState_) sendBridge(state);
    }
    if (reportDue_ || !reportedMs_ || steady - *reportedMs_ >= kReportIntervalMs) {
        reportedMs_ = steady;
        report(nowMs);
    }
}

// QUOTAS 6 and 9 on the host: the running agents (REMOTE 11.2), the local files, the sign-ins and the vendor round.
void QuotaSession::refresh(std::int64_t nowMs, std::int64_t steady) {
    const auto& sources = config_->sources;
    if (!sources.empty() && (!listedMs_ || steady - *listedMs_ >= quota::RunningSources::kListIntervalMs)) {
        listedMs_ = steady;
        if (options_.running) {
            running_ = options_.running();
        } else {
            runningSources_->update(processes_->read());
            running_ = runningSources_->running();
        }
    }
    const bool codexOn = sources.contains(SourceKind::Codex);
    const bool cliOn = sources.contains(SourceKind::ClaudeCode);
    if (!cliOn) {
        cliCheckedMs_.reset();
        cliFile_->refresh(false);
    } else if (!cliCheckedMs_ || steady - *cliCheckedMs_ >= quota::cli::FileReader::kCheckIntervalMs) {
        cliCheckedMs_ = steady;
        cliFile_->refresh(true);
    }
    codexMarker_->refresh(nowMs, codexOn);
    claudeMarker_->refresh(nowMs, cliOn);
    vendors_->tick(quota::hostInputs(sources, config_->network, config_->grokLive, running_, nowMs));
}

remote::HostQuota QuotaSession::reportOf(SourceKind source) const {
    remote::HostQuota r;
    std::optional<quota::Snapshot> snapshot;
    const auto& readings = vendors_->readings();
    switch (source) {
        case SourceKind::Codex:
            snapshot = codex_->snapshot();
            r.signedIn = codexMarker_->signedIn();
            break;
        case SourceKind::ClaudeCode:
            snapshot = cliFile_->snapshot();
            r.signedIn = claudeMarker_->signedIn();
            break;
        case SourceKind::GrokBuild:
            snapshot = readings.snapshot(source);
            r.signedIn = readings.signedIn(source);
            r.status = statusOf(grokStatus(readings, snapshot));
            break;
        default:
            snapshot = readings.snapshot(source);
            r.signedIn = readings.signedIn(source);
            r.status = statusOf(readings.status(source));
            break;
    }
    r.running = running_.contains(source) || readings.runs(source);
    r.snapshot = snapshot ? *snapshot : quota::Snapshot{source, 0, {}};
    r.snapshot.source = source;
    // An observation the app could not place in time is not one.
    if (!snapshot || !validTime(snapshot->observedMs)) r.snapshot.observedMs = 0;
    return r;
}

// One line per source whose report changed (REMOTE 11.1). A report without a snapshot is timed when it changed.
void QuotaSession::report(std::int64_t nowMs) {
    reportDue_ = false;
    LineBatch lines(out_);
    for (const auto source : remote::quotas::hostSources()) {
        const auto sent = sent_.find(source);
        if (!config_->sources.contains(source)) {
            if (sent != sent_.end()) {
                lines.add(quotaRemovedLine(source));
                sent_.erase(sent);
            }
            continue;
        }
        auto r = reportOf(source);
        std::optional<quota::Snapshot> snapshot;
        if (r.snapshot.observedMs != 0) snapshot = r.snapshot;
        if (sent != sent_.end() && sent->second.snapshot == snapshot && sent->second.running == r.running &&
            sent->second.signedIn == r.signedIn && sent->second.status == r.status) {
            continue;
        }
        if (sent != sent_.end() && hasWindows(sent->second.snapshot) && !hasWindows(snapshot)) lines.add(quotaRemovedLine(source));
        if (!snapshot) r.snapshot.observedMs = nowMs;
        auto text = quotaLine(r);
        if (text.empty()) continue;
        lines.add(std::move(text));
        sent_[source] = Sent{snapshot, r.running, r.signedIn, r.status};
    }
}

bool QuotaSession::idle() const { return !transport_ || transport_->idle(); }

}  // namespace agentspeed::relay
