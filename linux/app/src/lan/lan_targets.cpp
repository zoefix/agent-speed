#include "lan/lan_targets.hpp"

#include <QDateTime>
#include <QHostInfo>
#include <QNetworkProxy>

#include "lan/lan_http_post.hpp"
#include "lan/lan_net.hpp"

using namespace agentspeed::lan;

namespace {

constexpr qint64 kUdpResolveMs = 30'000;

QHostAddress pick(const std::vector<QHostAddress>& addresses) {
    for (const auto& a : addresses) {
        if (lan_net::plain(a).protocol() == QAbstractSocket::IPv4Protocol) return lan_net::plain(a);
    }
    return addresses.empty() ? QHostAddress() : addresses.front();
}

QByteArray bytes(const std::string& text) { return QByteArray(text.data(), static_cast<qsizetype>(text.size())); }

}  // namespace

struct LanTargets::Runner {
    explicit Runner(const Target& t) : target(t), scheduler(pushRule(t.kind == TargetKind::Udp ? Receiver::Udp : Receiver::Http)) {
        const auto parsed = parseTarget(t.kind, t.address);
        if (std::holds_alternative<TargetAddress>(parsed)) address = std::get<TargetAddress>(parsed);
    }
    bool udp() const { return target.kind == TargetKind::Udp; }
    void reset() {
        scheduler.restart();
        status = {};
        failures = 0;
        retryDue = false;
        resolvedAtMs = -1;
    }
    void cancel() {
        if (request) {
            request->cancel();
            request->deleteLater();
        }
        request = nullptr;
        busy = false;
    }

    Target target;
    std::optional<TargetAddress> address;
    PushScheduler scheduler;
    Status status;
    int failures = 0;
    bool retryDue = false, busy = false;
    std::vector<QHostAddress> addresses;
    qint64 resolvedAtMs = -1;
    Resolution resolution = Resolution::Ok;
    QPointer<LanHttpPost> request;
    std::shared_ptr<const Frame> frame;
    Done done;  // Send Test
};

LanTargets::LanTargets(QObject* parent) : QObject(parent) {
    udp4_.setProxy(QNetworkProxy::NoProxy);
    udp6_.setProxy(QNetworkProxy::NoProxy);
}

LanTargets::~LanTargets() {
    for (const auto& r : runners_) r->cancel();
}

std::shared_ptr<LanTargets::Runner> LanTargets::makeRunner(const Target& target) const { return std::make_shared<Runner>(target); }

void LanTargets::configure(const std::vector<Target>& targets, bool networkEnabled) {
    const bool networkBack = networkEnabled && !networkEnabled_;
    networkEnabled_ = networkEnabled;
    std::vector<std::shared_ptr<Runner>> next;
    for (const auto& t : targets) {
        const auto it = std::find_if(runners_.begin(), runners_.end(), [&](const auto& r) { return r->target.id == t.id; });
        std::shared_ptr<Runner> runner;
        if (it != runners_.end() && (*it)->target.kind == t.kind && (*it)->target.address == t.address && (*it)->target.key == t.key) {
            runner = *it;
            const bool enabling = t.enabled && !runner->target.enabled;
            runner->target = t;
            if (enabling || networkBack) runner->reset();
        } else {
            runner = makeRunner(t);
        }
        if (!t.enabled || !networkEnabled) runner->cancel();
        next.push_back(std::move(runner));
    }
    for (const auto& r : runners_) {
        if (std::find(next.begin(), next.end(), r) == next.end()) r->cancel();
    }
    runners_ = std::move(next);
    emit changed();
}

bool LanTargets::anyEnabled() const {
    return std::any_of(runners_.begin(), runners_.end(), [](const auto& r) { return r->target.enabled; });
}

LanTargets::Status LanTargets::status(const std::string& id) const {
    for (const auto& r : runners_) {
        if (r->target.id != id) continue;
        if (!networkEnabled_) return {TargetStatus::NetworkOff, r->status.lastSuccessWallMs, 0, 0};
        if (!r->target.enabled) return {TargetStatus::Off, r->status.lastSuccessWallMs, 0, 0};
        return r->status;
    }
    return {TargetStatus::Off, 0, 0, 0};
}

void LanTargets::publish(const std::shared_ptr<const Frame>& frame, bool changed, qint64 nowMs) {
    latest_ = frame;
    if (!networkEnabled_ || !frame) return;
    const auto runners = runners_;
    for (const auto& r : runners) {
        if (!r->target.enabled || !r->address) continue;
        const bool due = r->scheduler.due(nowMs, changed);
        if (r->busy) continue;
        if (r->retryDue) {
            if (nowMs < r->status.retryAtMs) continue;
        } else if (!due) {
            continue;
        }
        r->frame = frame;
        attempt(r, nowMs);
    }
}

void LanTargets::test(TargetKind kind, const std::string& address, const std::optional<std::string>& key,
                      const std::shared_ptr<const Frame>& frame, Done done) {
    Target target;
    target.id = "test";
    target.kind = kind;
    target.address = address;
    target.key = key;
    auto runner = makeRunner(target);
    if (!networkEnabled_ || !runner->address || !frame) {
        done({networkEnabled_ ? TargetStatus::Failed : TargetStatus::NetworkOff, 0, 0, 0});
        return;
    }
    runner->frame = frame;
    // The runner lives in its own callback until the outcome is known.
    runner->done = [runner, done = std::move(done)](const Status& status) { done(status); };
    attempt(runner, lan_net::steadyMs());
}

void LanTargets::attempt(const std::shared_ptr<Runner>& runner, qint64 nowMs) {
    runner->scheduler.sent(nowMs);
    runner->retryDue = false;
    const auto& address = *runner->address;
    if (address.literal) {
        resolved(runner, {QHostAddress(QString::fromStdString(address.host))}, nowMs);
        return;
    }
    if (runner->udp() && runner->resolvedAtMs >= 0 && nowMs - runner->resolvedAtMs < kUdpResolveMs) {
        if (runner->resolution == Resolution::Ok) {
            sendUdp(runner, nowMs);
        } else {
            fail(runner, runner->resolution == Resolution::NotFound ? TargetStatus::NotFound : TargetStatus::NotLocal, nowMs);
        }
        return;
    }
    runner->busy = true;
    std::weak_ptr<Runner> weak = runner;
    QHostInfo::lookupHost(QString::fromStdString(address.host), this, [this, weak](const QHostInfo& info) {
        const auto r = weak.lock();
        if (!r || !r->busy) return;
        r->busy = false;
        std::vector<QHostAddress> found;
        if (info.error() == QHostInfo::NoError) {
            for (const auto& a : info.addresses()) found.push_back(a);
        }
        resolved(r, found, lan_net::steadyMs());
    });
}

void LanTargets::resolved(const std::shared_ptr<Runner>& runner, const std::vector<QHostAddress>& addresses, qint64 nowMs) {
    std::vector<std::string> texts;
    for (const auto& a : addresses) texts.push_back(a.toString().toStdString());
    const auto resolution = resolveCheck(texts, runner->udp());
    runner->addresses = addresses;
    if (runner->udp() && !runner->address->literal) {
        runner->resolvedAtMs = nowMs;
        runner->resolution = resolution;
    }
    if (resolution != Resolution::Ok) {
        fail(runner, resolution == Resolution::NotFound ? TargetStatus::NotFound : TargetStatus::NotLocal, nowMs);
        return;
    }
    if (runner->udp()) {
        sendUdp(runner, nowMs);
    } else {
        sendHttp(runner, pick(addresses), nowMs);
    }
}

QUdpSocket& LanTargets::udpFor(const QHostAddress& address) {
    const bool v4 = address.protocol() == QAbstractSocket::IPv4Protocol;
    auto& socket = v4 ? udp4_ : udp6_;
    if (socket.state() != QAbstractSocket::BoundState) socket.bind(v4 ? QHostAddress(QHostAddress::AnyIPv4) : QHostAddress(QHostAddress::AnyIPv6), 0);
    return socket;
}

void LanTargets::sendUdp(const std::shared_ptr<Runner>& runner, qint64 nowMs) {
    const auto d = datagram(*runner->frame, runner->target.key);
    const auto address = pick(runner->addresses);
    auto& socket = udpFor(address);
    const auto written = socket.writeDatagram(d.bytes.data(), static_cast<qint64>(d.bytes.size()), address, static_cast<quint16>(runner->address->port));
    if (written < 0) {
        if (runner->status.status != TargetStatus::Failed) qCInfo(lanLog).noquote() << "lan: udp target" << address.toString() << "failed:" << socket.errorString();
        runner->resolvedAtMs = -1;
        fail(runner, TargetStatus::Failed, nowMs);
        return;
    }
    settle(runner, {TargetStatus::Ok, QDateTime::currentMSecsSinceEpoch(), 0, 0});
}

void LanTargets::sendHttp(const std::shared_ptr<Runner>& runner, const QHostAddress& address, qint64 nowMs) {
    const auto body = runner->frame->wire();
    const auto signed_ = runner->target.key ? signature(*runner->target.key, body) : std::string();
    const auto& parsed = *runner->address;
    auto request = LanHttpPost::request(bytes(parsed.target), bytes(parsed.hostHeader), bytes(body), bytes(signed_));
    runner->busy = true;
    std::weak_ptr<Runner> weak = runner;
    runner->request = new LanHttpPost(
        address, static_cast<quint16>(parsed.port), std::move(request),
        [this, weak, nowMs, address](std::optional<int> code) {
            const auto r = weak.lock();
            if (!r) return;
            if (r->request) r->request->deleteLater();
            r->request = nullptr;
            r->busy = false;
            if (code && *code >= 200 && *code < 300) {
                r->failures = 0;
                settle(r, {TargetStatus::Ok, QDateTime::currentMSecsSinceEpoch(), *code, 0});
                return;
            }
            qCInfo(lanLog).noquote() << "lan: http target" << address.toString() << (code ? QString::number(*code) : QStringLiteral("unreachable"));
            fail(r, code ? TargetStatus::HttpStatus : TargetStatus::Unreachable, std::max(nowMs, lan_net::steadyMs()), code.value_or(0));
        },
        this);
}

void LanTargets::fail(const std::shared_ptr<Runner>& runner, TargetStatus status, qint64 nowMs, int code) {
    Status next = runner->status;
    next.status = status;
    next.httpCode = code;
    next.retryAtMs = 0;
    if (!runner->udp()) {
        ++runner->failures;
        next.retryAtMs = nowMs + httpBackoffMs(runner->failures);
        runner->retryDue = true;
    }
    settle(runner, next);
}

void LanTargets::settle(const std::shared_ptr<Runner>& runner, const Status& status) {
    if (status.status != runner->status.status && !runner->done && runner->address) {
        qCInfo(lanLog).noquote() << "lan:" << QString::fromLatin1(kindName(runner->target.kind).data()) << "target"
                                 << QString::fromStdString(runner->address->host) << QString::fromLatin1(statusName(status.status).data());
    }
    runner->status = status;
    if (runner->done) {
        const auto done = std::move(runner->done);
        runner->done = nullptr;
        done(status);
        return;
    }
    emit changed();
}
