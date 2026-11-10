#pragma once

#include <functional>
#include <map>
#include <memory>
#include <optional>
#include <set>
#include <string>
#include <vector>

#include "agentspeed/remote_connection.hpp"
#include "agentspeed/remote_host.hpp"

// One connection per enabled host, kept in step with the settings (REMOTE 2.3, 4.4, 7 Sources). Loop thread only.
namespace agentspeed::remote {

class Hub {
public:
    using Sink = std::function<void(const HostEvent&)>;

    Hub(ConnectionEnvironment environment, Sink sink);
    ~Hub();
    Hub(const Hub&) = delete;
    Hub& operator=(const Hub&) = delete;

    // Starts the connections of enabled, valid hosts and stops the others (removed or disabled hosts; every host while
    // the master switch is off). A host whose destination or folder changed reconnects, and every host does when the
    // enabled sources change. A new price table goes to the running relays without a reconnect; an invalid one keeps
    // the previous (else the bundled) table.
    // The quota switches go to every running relay without a reconnect, with each host's bridge (REMOTE 11.1).
    void update(const std::vector<Host>& hosts, bool networkEnabled, const std::set<SourceKind>& sources, std::string_view priceTable,
                const QuotaSwitches& quota = {});
    void retryNow(const std::string& hostId);
    void requestHistory(const std::string& hostId, const std::map<std::string, std::string>& digests);
    void setWatch(bool on);
    // The computer woke from sleep or its network came back: hosts waiting for their back-off retry at once.
    void noteWake();
    void noteTimeZoneChange();
    // Quitting: every relay's stdin closes and every ssh ends.
    void stopAll();

    std::set<std::string> connectedHostIds() const;
    std::shared_ptr<Connection> connection(const std::string& hostId) const;

private:
    void removeConnection(const std::string& id, std::optional<Status> finalStatus);
    void reportIdle(const std::string& id, const Status& status);

    ConnectionEnvironment environment_;
    Sink sink_;
    std::map<std::string, std::shared_ptr<Connection>> connections_;
    std::map<std::string, Host> connectedHosts_;
    std::map<std::string, Status> idleStatuses_;
    std::optional<SessionSettings> settings_;
};

}  // namespace agentspeed::remote
