#pragma once

#include <QHostAddress>
#include <QObject>
#include <QPointer>
#include <QUdpSocket>
#include <functional>
#include <map>
#include <memory>
#include <optional>
#include <string>
#include <vector>

#include "agentspeed/lan_address.hpp"
#include "agentspeed/lan_frame.hpp"
#include "agentspeed/lan_push.hpp"
#include "agentspeed/lan_settings.hpp"

class LanHttpPost;

// LAN 5: the targets the user added, sent every new frame by the push rules — UDP datagrams (broadcast allowed) and
// HTTP POST requests — only to addresses that are local after resolution.
class LanTargets : public QObject {
    Q_OBJECT
public:
    struct Status {
        agentspeed::lan::TargetStatus status = agentspeed::lan::TargetStatus::Waiting;
        qint64 lastSuccessWallMs = 0;  // 0: none since launch
        int httpCode = 0;
        qint64 retryAtMs = 0;  // HTTP: the next attempt after a failure, on the monotonic clock
    };
    using Done = std::function<void(const Status&)>;

    explicit LanTargets(QObject* parent = nullptr);
    ~LanTargets() override;

    // The targets as saved and the master switch, applied at once.
    void configure(const std::vector<agentspeed::lan::Target>& targets, bool networkEnabled);
    // A tick's frame.
    void publish(const std::shared_ptr<const agentspeed::lan::Frame>& frame, bool changed, qint64 nowMs);
    Status status(const std::string& id) const;
    // Send Test: one frame to an address now, whatever the push rules; `done` gets the outcome.
    void test(agentspeed::lan::TargetKind kind, const std::string& address, const std::optional<std::string>& key,
              const std::shared_ptr<const agentspeed::lan::Frame>& frame, Done done);
    bool anyEnabled() const;

signals:
    void changed();

private:
    struct Runner;
    std::shared_ptr<Runner> makeRunner(const agentspeed::lan::Target& target) const;
    void attempt(const std::shared_ptr<Runner>& runner, qint64 nowMs);
    void resolved(const std::shared_ptr<Runner>& runner, const std::vector<QHostAddress>& addresses, qint64 nowMs);
    void sendUdp(const std::shared_ptr<Runner>& runner, qint64 nowMs);
    void sendHttp(const std::shared_ptr<Runner>& runner, const QHostAddress& address, qint64 nowMs);
    void fail(const std::shared_ptr<Runner>& runner, agentspeed::lan::TargetStatus status, qint64 nowMs, int code = 0);
    void settle(const std::shared_ptr<Runner>& runner, const Status& status);
    QUdpSocket& udpFor(const QHostAddress& address);

    std::vector<std::shared_ptr<Runner>> runners_;
    bool networkEnabled_ = true;
    std::shared_ptr<const agentspeed::lan::Frame> latest_;
    QUdpSocket udp4_, udp6_;
};
