#include <QDateTime>
#include <QUuid>

#include "preferences.hpp"
#include "report/report_controller.hpp"

// The WebSocket transport (METRICS 11.9): reports built when the data changes, written on one connection and
// acknowledged one by one. The stream scheduler decides; this file performs its actions.
namespace {

std::string sv(const QString& text) { return text.toStdString(); }

const char* eventName(agentspeed::report::Event event) {
    switch (event) {
    case agentspeed::report::Event::Pause: return "pause";
    case agentspeed::report::Event::Resume: return "resume";
    case agentspeed::report::Event::NetworkOff: return "networkOff";
    case agentspeed::report::Event::NetworkOn: return "networkOn";
    case agentspeed::report::Event::Stop: return "off";
    }
    return "";
}

}  // namespace

bool ReportController::usesWebSocket() const { return webSocket() && discovery_ && discovery_->websocket.has_value(); }

std::array<double, 6> ReportController::sentLive(const agentspeed::report::Sample& sample) const {
    agentspeed::report::AgentCounts agents;
    for (const auto& [_, c] : sample.facts.agents) {
        agents.generating += c.generating;
        agents.tools += c.tools;
        agents.active += c.active;
    }
    using J = agentspeed::report::Json;
    return {J::round(sample.live.output, 1), J::round(sample.live.total, 1), J::round(sample.estimatedShare, 3),
            static_cast<double>(agents.generating), static_cast<double>(agents.tools), static_cast<double>(agents.active)};
}

void ReportController::startStreamIfNeeded(const std::shared_ptr<const agentspeed::report::Sample>& sample, qint64 now) {
    if (stream_ || !enabled() || !key_ || !identity_ || !identity_->registered || !discovery_ || !discovery_->websocket) return;
    Stream stream;
    stream.id = QUuid::createUuid().toString(QUuid::WithoutBraces).toLower();
    stream.scheduler = std::make_unique<agentspeed::report::stream::StreamScheduler>(*discovery_->websocket, discovery_->limits.heartbeatSeconds * 1000LL);
    stream.window = std::make_unique<agentspeed::report::Window>(*sample);
    stream_ = std::move(stream);
    streamStop_.reset();
    renamedForMismatch_ = false;
    Q_UNUSED(now);
}

void ReportController::endStream() {
    if (!stream_) return;
    wakeTimer_.stop();
    socket_.abort();
    stream_.reset();
    notify();
}

void ReportController::streamTick(const std::shared_ptr<const agentspeed::report::Sample>& sample, qint64 now, std::optional<qint64> observed, bool wake) {
    startStreamIfNeeded(sample, now);
    if (!stream_) {
        pendingEvents_.clear();
        return;
    }
    std::vector<std::string> events;
    for (const auto event : std::exchange(pendingEvents_, {})) events.emplace_back(eventName(event));
    auto& s = *stream_;
    const bool active = wake ? s.lastActive : sample->working() || sample->live.output > 0 || s.window->hasTokenIncrement(*sample);
    s.lastActive = active;
    const bool carriesTraffic = discovery_ && acceptedSections(*discovery_, switches()).contains(agentspeed::report::Section::Traffic);
    const bool changed = !wake && (s.window->hasIncrement(*sample, carriesTraffic) || !s.lastBuilt || *s.lastBuilt != sentLive(*sample) ||
                                   s.lastTaskScores != taskSignature(*sample));
    s.title.update(sample->value(prefs_.metric() == QLatin1String("total")), prefs_.redline(), static_cast<double>(now) / 1000.0);
    s.scheduler->setReporting(ready());
    s.scheduler->setHeartbeat(discovery_->limits.heartbeatSeconds * 1000LL);
    const auto modeBefore = s.scheduler->mode();
    applyStream(s.scheduler->tick(now, active, changed, changed ? observed : std::nullopt, events), sample.get());
    if (stream_ && stream_->scheduler->mode() != modeBefore) notify();
}

void ReportController::applyStream(const std::vector<agentspeed::report::stream::Action>& actions, const agentspeed::report::Sample* sample) {
    using Kind = agentspeed::report::stream::Action::Kind;
    bool windowStarted = false;
    for (const auto& action : actions) {
        if (!stream_) return;
        auto& s = *stream_;
        switch (action.kind) {
        case Kind::Discard:
            if (sample) s.window->discard(*sample, action.value == 1);
            windowStarted = true;
            break;
        case Kind::Connect: streamConnect(); break;
        case Kind::Abort: socket_.abort(); break;
        case Kind::Close: socket_.close(); break;
        case Kind::Build: {
            if (!sample || !identity_ || !discovery_) break;
            const auto content = s.window->take(*sample);
            windowStarted = true;
            agentspeed::report::Envelope envelope;
            envelope.client = client();
            envelope.deviceId = sv(identity_->deviceId);
            envelope.username = sv(identity_->username);
            envelope.runId = sv(s.id);
            envelope.seq = action.seq;
            envelope.sentAtMs = correctedMs();
            envelope.windowMs = action.windowMs;
            envelope.activity = action.text == "leaving" ? agentspeed::report::Activity::Leaving
                                : action.text == "idle"  ? agentspeed::report::Activity::Idle
                                                         : agentspeed::report::Activity::Active;
            envelope.titleLevel = s.title.level();
            auto opts = options(*discovery_, switches());
            opts.stream = agentspeed::report::StreamBody{action.value < 0 ? std::nullopt : std::optional<std::int64_t>(action.value),
                                                         s.scheduler->limits().maxMessageBytes};
            const auto body = agentspeed::report::fitted(envelope, content, *sample, opts).data;
            s.bodies[action.seq] = {body, agentspeed::report::sha256Hex(body)};
            s.lastBuilt = sentLive(*sample);
            s.lastTaskScores = taskSignature(*sample);
            break;
        }
        case Kind::Write: streamWrite(action.seq); break;
        case Kind::Delivered:
            s.bodies.erase(action.seq);
            lastDeliveryMs_ = QDateTime::currentMSecsSinceEpoch();
            retryReason_.reset();
            break;
        case Kind::Dropped: s.bodies.erase(action.seq); break;
        case Kind::React: streamReact(action.text); break;
        case Kind::Stop: streamStopped(action.text); break;
        case Kind::Wake:
            wakeTimer_.stop();
            wakeTimer_.singleShot(static_cast<int>(std::max<qint64>(0, action.value - steadyMs())), this, [this, at = action.value] {
                if (!stream_ || stream_->scheduler->wakeAt() != at || !lastSample_) return;
                stream_->scheduler->clearWake();
                streamTick(lastSample_, steadyMs(), std::nullopt, true);
            });
            break;
        case Kind::Backoff:
        case Kind::Wait:
        case Kind::Saturated: break;
        }
    }
    if (stream_) {
        if (sample) stream_->window->observe(*sample, !windowStarted);
        streamCounters_ = stream_->scheduler->counters();
        streamLatency_ = stream_->scheduler->latency();
    }
}

void ReportController::streamConnect() {
    ReportSocket::disconnect(&socket_, nullptr, this, nullptr);
    if (!stream_ || !identity_ || !discovery_ || !key_ || !service_) return;
    const auto api = agentspeed::report::ServiceUrl::parseApi(discovery_->api, service_->loopbackHttp());
    if (!api) return;
    const auto url = agentspeed::report::websocketUrl(api->text());
    agentspeed::report::SignOptions options;
    options.timestamp = correctedMs() / 1000;
    options.apiKey = sv(apiKey_);
    options.version = AGENTSPEED_VERSION;
    options.platform = "Linux";
    const auto headers = agentspeed::report::signedHeaders(*key_, "GET", url, sv(identity_->deviceId), "", options);
    const auto& limits = stream_->scheduler->settings();
    connect(&socket_, &ReportSocket::opened, this, [this] {
        if (stream_) applyStream(stream_->scheduler->opened(steadyMs()), lastSample_.get());
    });
    connect(&socket_, &ReportSocket::message, this, &ReportController::onSocketMessage);
    connect(&socket_, &ReportSocket::closed, this, [this](int code) {
        if (stream_) applyStream(stream_->scheduler->closed(steadyMs(), code), lastSample_.get());
        notify();
    });
    connect(&socket_, &ReportSocket::refused, this, [this](int status) {
        if (!stream_) return;
        const auto rejection = agentspeed::report::stream::classifyRejection(status, std::nullopt, std::nullopt);
        applyStream(stream_->scheduler->rejected(steadyMs(), status, rejection.code.empty() ? std::nullopt : std::optional<std::string>(rejection.code),
                                                 std::nullopt),
                    lastSample_.get());
        notify();
    });
    qInfo("report: WebSocket connect %s", qPrintable(QUrl(QString::fromStdString(url)).host()));
    socket_.open(QString::fromStdString(url), headers, limits.pingIntervalSeconds, limits.pongTimeoutSeconds);
}

void ReportController::streamWrite(int seq) {
    if (!stream_ || !identity_ || !discovery_ || !key_ || !service_) return;
    const auto it = stream_->bodies.find(seq);
    const auto api = agentspeed::report::ServiceUrl::parseApi(discovery_->api, service_->loopbackHttp());
    if (it == stream_->bodies.end() || !api) return;
    agentspeed::report::SignOptions options;
    options.timestamp = correctedMs() / 1000;
    options.version = AGENTSPEED_VERSION;
    options.platform = "Linux";
    const auto headers = agentspeed::report::signedHeaders(*key_, "POST", api->endpoint("/v1/reports"), sv(identity_->deviceId), it->second.first, options);
    socket_.send(QString::fromStdString(agentspeed::report::reportMessage(headers, it->second.first)));
}

void ReportController::onSocketMessage(const QByteArray& text, bool binary) {
    if (!stream_) return;
    const auto now = steadyMs();
    auto& s = *stream_;
    const auto message = binary ? std::nullopt
                                : agentspeed::report::stream::parseMessage(std::string_view(text.constData(), static_cast<std::size_t>(text.size())),
                                                                           static_cast<std::size_t>(s.scheduler->limits().maxMessageBytes));
    if (!message) {
        // An invalid or binary message counts as close 1002.
        socket_.abort();
        applyStream(s.scheduler->closed(now, 1002), lastSample_.get());
        notify();
        return;
    }
    using Type = agentspeed::report::stream::Message::Type;
    switch (message->type) {
    case Type::Ready: applyStream(s.scheduler->ready(now, message->limits), lastSample_.get()); break;
    case Type::Ack: {
        const auto it = s.bodies.find(static_cast<int>(message->seq));
        const bool matches = message->runId == sv(s.id) && it != s.bodies.end() && it->second.second == message->bodySha256;
        if (matches && message->rank) rank_ = static_cast<int>(*message->rank);
        applyStream(s.scheduler->ack(now, static_cast<int>(message->seq), message->status, matches), lastSample_.get());
        break;
    }
    case Type::Error: {
        streamServerTime_ = message->serverTime;
        const auto seq = message->runId == sv(s.id) && message->seq > 0 ? std::optional<int>(static_cast<int>(message->seq)) : std::nullopt;
        qInfo("report: WebSocket error %s (%d)", message->code.c_str(), message->httpStatus);
        applyStream(s.scheduler->error(now, seq, message->code, message->httpStatus, message->retryAfterSeconds, message->fatal), lastSample_.get());
        break;
    }
    case Type::Other: break;
    }
    notify();
}

void ReportController::streamReact(const std::string& name) {
    if (name == "correctClock") {
        if (streamServerTime_) {
            const auto correction = *streamServerTime_ - QDateTime::currentSecsSinceEpoch();
            if (std::abs(correction) <= 86'400) clockCorrectionSeconds_ = correction;
        }
    } else if (name == "reRegister") {
        const auto now = QDateTime::currentMSecsSinceEpoch();
        if (lastReRegisterMs_ == 0 || now - lastReRegisterMs_ >= 3'600'000) {
            lastReRegisterMs_ = now;
            reRegister();
        }
    } else if (name == "recheckRegistration") {
        if (pendingKey_) promotePendingKey();
        else reRegister();
    } else if (name == "rename") {
        if (!renamedForMismatch_ && identity_) {
            renamedForMismatch_ = true;
            rename(settings_.username().isEmpty() ? identity_->username : settings_.username());
        }
    } else if (name == "newRun") {
        if (stream_) {
            stream_->id = QUuid::createUuid().toString(QUuid::WithoutBraces).toLower();
            stream_->bodies.clear();
            if (lastSample_) stream_->window = std::make_unique<agentspeed::report::Window>(*lastSample_);
        }
    } else if (name == "refreshDiscovery") {
        pendingStreamSettings_.reset();
        fetchDiscovery(false);
    }
}

void ReportController::streamStopped(const std::string& reason) {
    using agentspeed::report::Problem;
    if (reason == "apiKey") problem_ = Problem::ApiKey;
    else if (reason == "forbidden") problem_ = Problem::Forbidden;
    else if (reason == "keyMismatch") problem_ = Problem::KeyMismatch;
    else if (reason == "upgrade") problem_ = Problem::Upgrade;
    else if (reason == "rejected") problem_ = Problem::Rejected;
    else if (reason == "gone") {
        problem_ = Problem::Gone;
        settings_.setEnabled(false);
    } else {
        streamStop_ = reason;  // mismatch, replaced, incompatible, nonce, clientState
    }
    notify();
}

QString ReportController::wsStats() const {
    if (!usesWebSocket() || !enabled()) return {};
    const auto& c = streamCounters_;
    const auto delivered = c.accepted + c.duplicate;
    const auto lost = c.rejected + c.stale + c.saturated + c.unknown;
    if (delivered + lost == 0) return {};
    const auto ms = [this](std::optional<std::int64_t> v) { return v ? QString::number(*v) + QStringLiteral(" ms") : t("report.ws.noLatency"); };
    return fmt(QStringLiteral("report.ws.stats"), {ms(agentspeed::report::stream::percentile(streamLatency_, 50)),
                                                   ms(agentspeed::report::stream::percentile(streamLatency_, 95)), QString::number(delivered),
                                                   QString::number(lost)});
}

QString ReportController::wsCounters() const {
    const auto& c = streamCounters_;
    return fmt(QStringLiteral("report.ws.counters"), {QString::number(c.built), QString::number(c.accepted), QString::number(c.duplicate),
                                                      QString::number(c.rejected), QString::number(c.stale), QString::number(c.saturated),
                                                      QString::number(c.unknown)});
}
