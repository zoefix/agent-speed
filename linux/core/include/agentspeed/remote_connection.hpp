#pragma once

#include <cstdint>
#include <fstream>
#include <functional>
#include <map>
#include <memory>
#include <optional>
#include <set>
#include <string>
#include <vector>

#include "agentspeed/model.hpp"
#include "agentspeed/remote_clock.hpp"
#include "agentspeed/remote_host.hpp"
#include "agentspeed/remote_messages.hpp"
#include "agentspeed/remote_protocol.hpp"
#include "agentspeed/remote_relay_bundle.hpp"
#include "agentspeed/remote_transport.hpp"

// One host's connection (REMOTE 4.2–4.4): probe → (upload) → run over /usr/bin/ssh, then the protocol of section 5 on
// the relay's stdin and stdout, with pings, the silence rule, back-off and the history schedule. Single-threaded: every
// call and callback happens on the loop's thread; events go to the sink in order.
namespace agentspeed::remote {

// What a connection sends with `start` and keeps in step afterwards.
struct SessionSettings {
    // The app's enabled sources (REMOTE 3.3); a change means a new connection.
    std::set<SourceKind> sources;
    // The current price table, compact (compactPriceTable).
    std::string prices;
    // Live reporting's watch (METRICS 11.10).
    bool watch = false;
    // The app's quota switches (REMOTE 11.1); each host adds its own Claude Code bridge.
    QuotaSwitches quota;
    bool operator==(const SessionSettings&) const = default;
};

// Everything a connection uses from the outside world, replaceable in tests.
struct ConnectionEnvironment {
    Transport* transport = nullptr;
    Scheduler* scheduler = nullptr;
    const RelayBundle* relays = nullptr;
    // The app's time zone (REMOTE 5.3).
    std::function<Zone()> zone = [] { return currentZone(); };
    std::vector<std::string> processEnvironment;

    // Local midnight of the day containing `nowMs` in the app's zone, and the next one.
    std::int64_t todayStartMs(std::int64_t nowMs) const;
    std::int64_t nextMidnightMs(std::int64_t nowMs) const;
};

// When a failed host tries again (REMOTE 4.4): 5, 10, 20, 40, 60 s, then every 60 s; reset after a connection that stayed
// up for 60 s. Errors that need the user retry every 5 minutes without advancing the back-off.
class Backoff {
public:
    static constexpr std::int64_t kDelaysMs[] = {5'000, 10'000, 20'000, 40'000, 60'000};
    static constexpr std::int64_t kUserRetryMs = 300'000;
    static constexpr std::int64_t kResetAfterMs = 60'000;

    // `upSinceMs`: when the ended connection received `hello` (nullopt when it never did).
    std::int64_t delayMs(const Status& status, std::optional<std::int64_t> upSinceMs, std::int64_t nowMs);
    void reset() { failures_ = 0; }
    int failures() const { return failures_; }

private:
    int failures_ = 0;
};

class Connection : public std::enable_shared_from_this<Connection> {
public:
    enum class Stage : std::uint8_t { Idle, Probe, Upload, Run };
    using Sink = std::function<void(const HostEvent&)>;

    static constexpr std::int64_t kSilenceLimitMs = 150'000;
    static constexpr std::int64_t kProbeLimitMs = 150'000;
    static constexpr std::int64_t kPingIntervalMs = 30'000;
    static constexpr std::int64_t kHistoryIntervalMs = 600'000;
    static constexpr std::int64_t kKillDelayMs = 2'000;
    static constexpr std::size_t kUploadChunkBytes = 256 * 1024;
    static constexpr std::size_t kPendingPingLimit = 8;

    static std::shared_ptr<Connection> make(Host host, SessionSettings settings, ConnectionEnvironment environment, Sink sink);
    ~Connection();

    void start();
    // Closes the relay's stdin, terminates ssh (SIGKILL after 2 s) and reports `finalStatus`, if any, as the last event.
    // The connection cannot be started again.
    void stop(std::optional<Status> finalStatus = std::nullopt);
    // Retry now: an attempt at once unless one is under way; the back-off starts over.
    void retryNow();
    // The computer woke or its network came back: a host waiting for its back-off retries at once; a running relay is
    // pinged and its silence clock restarts.
    void noteWake();
    void updatePrices(const std::string& compactPrices);
    void setWatch(bool on);
    // The quota switches or the host's bridge switch changed: a running relay gets the new `quota` config.
    void setQuota(const QuotaSwitches& switches, bool hostBridge);
    HostQuotaConfig quotaConfig() const { return HostQuotaConfig::forHost(host_.quotaClaudeCli, settings_.quota); }
    // Sends `history` with the digests the app holds; ignored before `ready`.
    void requestHistory(const std::map<std::string, std::string>& digests);
    // The zone changed: the relay gets it and the new local midnight.
    void noteTimeZoneChange();

    const Host& host() const { return host_; }
    Stage stage() const { return stage_; }
    int attempt() const { return attempt_; }
    bool stopped() const { return stopped_; }
    const std::optional<HostStatus>& lastStatus() const { return lastStatus_; }
    const SessionSettings& settings() const { return settings_; }

private:
    enum class Slot : std::uint8_t { Retry, StageLimit, Silence, Ping, History, Midnight };

    struct Session {
        LineFramer framer;
        std::optional<LineDecoder> decoder;
        int invalidLines = 0;
        bool helloReceived = false;
        std::optional<std::int64_t> helloAtMs;
        bool ready = false;
        std::optional<std::string> relayVersion, relayArch;
        ClockEstimator clock;
        std::map<std::int64_t, std::int64_t> pendingPings;
        std::int64_t nextPingId = 1;
        std::int64_t lastLineMs = 0;
        std::set<std::string> agentIds;
        bool historyInFlight = false, historyDuePending = false;
        std::optional<std::int64_t> reportedOffsetSeconds;
    };

    Connection(Host host, SessionSettings settings, ConnectionEnvironment environment, Sink sink);

    std::int64_t now() const { return environment_.scheduler->nowMs(); }
    void begin();
    void launch(Stage next, const std::string& command);
    void receivedStdout(std::string_view data, int token);
    void receivedStderr(std::string_view data, int token);
    void exited(int status, int token);
    void probeFinished(int status);
    void uploadFinished(int status);
    void startRelay();
    void streamUpload();
    void uploadNextChunk(int token);
    void closeUpload();
    void reportUploadProgress();
    void endProcess(std::optional<Status> reason = std::nullopt);
    void fail(const Status& status);
    void report(const Status& status, std::optional<std::int64_t> retryAtMs = std::nullopt);
    void notify(std::variant<HostStatus, HostEvent::Message, HostEvent::HistoryDue> kind);
    void send(const std::optional<std::string>& line);
    void schedule(Slot slot, std::int64_t atMs, std::function<void(Connection&)> work);
    void cancel(Slot slot);
    void cancelTimers();

    // The protocol (connection_session.cpp).
    void beginSession();
    void receivedRelayOutput(std::string_view data);
    void handle(const std::string& line);
    void countInvalid();
    void apply(RelayMessage message);
    void received(const Hello& hello);
    bool emitConverted(const RelayMessage& message);
    void reportClockIfChanged();
    void sendPing();
    void scheduleSilenceCheck();
    void historyRunEnded();
    void historyDue();
    void historyTimerFired();
    void scheduleMidnight();

    Host host_;
    SessionSettings settings_;
    ConnectionEnvironment environment_;
    Sink sink_;
    Stage stage_ = Stage::Idle;
    bool stopped_ = false;
    int attempt_ = 0;
    int processToken_ = 0;
    std::shared_ptr<Process> process_;
    std::optional<Status> endReason_;
    Backoff backoff_;
    std::optional<HostStatus> lastStatus_;
    std::optional<std::int64_t> lastErrorAtMs_;
    std::string stdout_, stderr_;
    std::optional<RelayExecutable> relay_;
    std::unique_ptr<std::ifstream> upload_;
    std::int64_t uploadedBytes_ = 0;
    double reportedUploadFraction_ = 0;
    Session session_;
    std::map<Slot, Scheduler::TimerId> timers_;
};

}  // namespace agentspeed::remote
