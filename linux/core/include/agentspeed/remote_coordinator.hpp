#pragma once

#include <cstdint>
#include <functional>
#include <map>
#include <memory>
#include <mutex>
#include <optional>
#include <set>
#include <string>
#include <thread>
#include <vector>

#include "agentspeed/remote_connection_check.hpp"
#include "agentspeed/remote_feed.hpp"
#include "agentspeed/remote_host.hpp"
#include "agentspeed/remote_hub.hpp"
#include "agentspeed/remote_relay_bundle.hpp"
#include "agentspeed/remote_ssh.hpp"
#include "agentspeed/remote_transport.hpp"

// Connects the remote hosts of the settings to the live monitor and the history (REMOTE 2, 4.4, 7, 8): owns the hub on
// its own thread (an EventLoop: the pipes are read the moment a line arrives, pings get exact round trips, and nothing
// waits for the monitor's 500 ms tick), keeps it in step with the hosts, the master switch, the enabled sources, the
// report watch and the price table, routes live events to the feed and history events to the history sink, and
// retries at once after a suspend (CLOCK_BOOTTIME running ahead of CLOCK_MONOTONIC). Every method is thread-safe.
namespace agentspeed::remote {

class Coordinator {
public:
    using HistorySink = std::function<void(const HostEvent&)>;
    using TransportFactory = std::function<std::unique_ptr<Transport>(EventLoop&)>;

    struct Options {
        std::shared_ptr<Feed> feed;
        std::shared_ptr<const RelayBundle> relays;
        std::function<Zone()> zone = [] { return currentZone(); };
        std::vector<std::string> processEnvironment = ssh::processEnvironment();
        // Default: posix_spawn pipes.
        TransportFactory transport;
    };

    explicit Coordinator(Options options);
    ~Coordinator();
    Coordinator(const Coordinator&) = delete;
    Coordinator& operator=(const Coordinator&) = delete;

    // The settings: hosts, the network master switch (REMOTE 2.3), the monitor's enabled sources and the current price
    // table's JSON. Applied only when something changed; a change of hosts or sources may reconnect.
    // `quota`: the app's quota switches (REMOTE 11.1), sent to the running relays without a reconnect.
    void update(const HostsDocument& document, bool networkEnabled, std::set<SourceKind> sources, std::string priceTable,
                QuotaSwitches quota = {});
    // Live reporting's watch (METRICS 11.10): `tool` lines are sent only while on.
    void setWatch(bool on);
    void retryNow(const std::string& hostId);
    // Answers a historyDue with the digests the app holds for the host ("<day>/<source>" → digest).
    void requestHistory(const std::string& hostId, std::map<std::string, std::string> digests);
    void noteWake();
    void noteNetworkAvailable() { noteWake(); }
    void noteTimeZoneChange();
    // Receives status changes, historyDue and the history lines (REMOTE 8.2), on the coordinator's thread.
    void setHistorySink(HistorySink sink);
    // Receives the quota lines (REMOTE 11.1: `quota`, `quotaRemoved`, `quotaBridge`) and status changes, on the
    // coordinator's thread. They never reach the live feed.
    void setQuotaSink(HistorySink sink);
    // REMOTE 8.3 Bounds: the hosts whose last history batch was discarded; their statuses carry `historyTooLarge`.
    void setHistoryTooLarge(std::set<std::string> hosts);
    // Test Connection; `done` runs on the coordinator's thread. Returns an id for cancelTest.
    std::uint64_t testConnection(Host host, std::function<void(TestOutcome)> done);
    void cancelTest(std::uint64_t id);
    // Quitting: every relay stops and the thread ends; nothing reconnects afterwards.
    void stop();

    // The hosts last applied, and the last status of each host.
    HostsDocument document() const;
    std::map<std::string, HostStatus> statuses() const;
    std::set<std::string> connectedHostIds() const;
    EventLoop& loop() { return loop_; }

private:
    struct Applied {
        std::vector<Host> hosts;
        bool networkEnabled = true;
        std::set<SourceKind> sources;
        std::string priceTable;
        QuotaSwitches quota;
        bool operator==(const Applied&) const = default;
    };
    enum class Route : std::uint8_t { Live, History, Both, Quota };
    static Route route(const HostEvent& event);
    void receive(const HostEvent& event);
    void deliver(const HostEvent& event);
    void checkSuspend();
    std::int64_t todayStartMs(std::int64_t nowMs);

    Options options_;
    EventLoop loop_;
    std::unique_ptr<Transport> transport_;
    std::unique_ptr<Hub> hub_;
    std::thread thread_;
    std::optional<Applied> applied_;
    bool watch_ = false;
    std::int64_t suspendGapMs_ = 0;
    // Today's start for the feed, recomputed at midnight and once a minute (a zone change).
    std::int64_t todayStartMs_ = 0, todayUntilMs_ = 0, todayCheckedMs_ = 0;
    std::map<std::uint64_t, std::shared_ptr<ConnectionCheck>> checks_;
    std::set<std::string> tooLarge_;
    std::uint64_t nextCheck_ = 1;

    mutable std::mutex mutex_;
    HostsDocument document_;
    std::map<std::string, HostStatus> statuses_;
    HistorySink historySink_;
    HistorySink quotaSink_;
    bool stopped_ = false;
};

}  // namespace agentspeed::remote
