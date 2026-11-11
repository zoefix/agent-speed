#pragma once

#include <cstdint>
#include <functional>
#include <map>
#include <memory>
#include <optional>
#include <set>
#include <string>

#include "agentspeed/quota/codex.hpp"
#include "agentspeed/quota/paths.hpp"
#include "agentspeed/quota/routes/antigravity.hpp"
#include "agentspeed/quota/routes/environment.hpp"
#include "agentspeed/remote_messages.hpp"
#include "channel.hpp"
#include "net/https_client.hpp"
#include "protocol.hpp"

namespace agentspeed::quota {
class RunningSources;
class SignInMarker;
class VendorService;
namespace cli {
class FileReader;
}
}  // namespace agentspeed::quota
namespace agentspeed::traffic::sampler {
class ProcessReader;
}

// REMOTE 11 on the host: what the app's quota config asks for, read with the CLI routes of QUOTAS (Codex's rollouts,
// the host's Claude Code bridge, Grok Build's log and live request, Antigravity's loopback server, Kimi, Gemini and
// Droid with the host's own sign-ins) and sent as `quota`, `quotaRemoved` and `quotaBridge` lines. Nothing is read
// for a source that is not in the config's `sources` and enabled in `start`, and nothing asks the network unless the
// config's `network` is on. Driven by the relay's loop; requests run on the transport's own thread.
namespace agentspeed::relay {

class RelayTransport;
class HostBridge;

struct QuotaOptions {
    std::string folder;
    // The app's version, for the requests' `User-Agent`.
    std::string version;
    // The SSH command's environment (HOME, CODEX_HOME, CLAUDE_CONFIG_DIR, …); null: the process's.
    quota::Environment environment;
    // The vendor readers' view of the host; nullopt: the system's.
    std::optional<quota::VendorEnvironment> vendors;
    // The executable the bridge copies (REMOTE 11.3).
    std::string self = "/proc/self/exe";
    // CA bundles, proxy variables and test hooks; the user agent is filled in from `version`.
    net::ClientOptions client;
    // Tests: the running sources instead of the `/proc` listing (REMOTE 11.2).
    std::function<std::set<SourceKind>()> running;
    // Tests: Antigravity's servers; null reads `/proc`.
    std::shared_ptr<quota::AntigravityLocator> antigravity;
};

class QuotaSession {
public:
    using Clock = std::function<std::int64_t()>;

    QuotaSession(Output& out, std::set<SourceKind> enabled, QuotaOptions options, Clock clock);
    ~QuotaSession();
    QuotaSession(const QuotaSession&) = delete;
    QuotaSession& operator=(const QuotaSession&) = delete;

    // For the monitor's Codex parsers (QUOTAS 3); it keeps the collector alive.
    quota::CodexCollector::Observer codexObserver() const;
    // `start.quota` or a `quota` line: applied in order, so an off-and-on pair runs the bridge's 5.3 then 5.2. Answers
    // with the bridge's state; the next tick sends the reports that changed.
    void setConfig(const QuotaConfig& config);
    // One turn of the relay's loop: whatever is due.
    void tick();
    // Tests: no request queued, running or waiting to be applied.
    bool idle() const;

private:
    struct Sent {
        std::optional<quota::Snapshot> snapshot;
        bool running = false, signedIn = false;
        remote::HostQuota::Status status = remote::HostQuota::Status::Ok;
    };

    void build();
    void refresh(std::int64_t nowMs, std::int64_t steady);
    void report(std::int64_t nowMs);
    remote::HostQuota reportOf(SourceKind source) const;
    void sendBridge(remote::HostQuotaBridge::State state);

    Output& out_;
    const std::set<SourceKind> enabled_;
    QuotaOptions options_;
    Clock clock_;
    quota::Environment environment_;
    std::shared_ptr<quota::CodexCollector> codex_;
    std::unique_ptr<HostBridge> bridge_;
    std::unique_ptr<quota::cli::FileReader> cliFile_;
    std::unique_ptr<quota::SignInMarker> codexMarker_, claudeMarker_;
    std::unique_ptr<quota::RunningSources> runningSources_;
    std::unique_ptr<traffic::sampler::ProcessReader> processes_;
    std::unique_ptr<RelayTransport> transport_;
    std::unique_ptr<quota::VendorService> vendors_;

    std::optional<QuotaConfig> config_;
    std::set<SourceKind> running_;
    // Monotonic times of the relay's own intervals.
    std::optional<std::int64_t> listedMs_, cliCheckedMs_, bridgeCheckedMs_, reportedMs_;
    std::optional<remote::HostQuotaBridge::State> bridgeState_;
    std::map<SourceKind, Sent> sent_;
    bool reportDue_ = false;
};

}  // namespace agentspeed::relay
