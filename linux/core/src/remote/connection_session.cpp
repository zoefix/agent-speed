#include "agentspeed/remote_connection.hpp"

// The protocol state of one running relay (REMOTE 5, 6, 8.2).
namespace agentspeed::remote {

// The relay runs: `start` is its first line, then the watch if it is on.
void Connection::beginSession() {
    session_.decoder.emplace(host_.id);
    session_.lastLineMs = now();
    scheduleSilenceCheck();
    const auto zone = environment_.zone ? environment_.zone() : Zone{"UTC", utcTzif()};
    send(encode::start(environment_.todayStartMs(now()), settings_.sources, zone, settings_.prices, quotaConfig()));
    if (settings_.watch) send(encode::watch(true));
}

void Connection::receivedRelayOutput(std::string_view data) {
    session_.lastLineMs = now();
    int oversized = 0;
    const auto lines = session_.framer.consume(data, oversized);
    for (const auto& line : lines) {
        if (stage_ != Stage::Run || endReason_) return;
        handle(line);
    }
    for (int k = 0; k < oversized && !endReason_; ++k) countInvalid();
}

void Connection::handle(const std::string& line) {
    if (!session_.decoder) return;
    auto outcome = session_.decoder->decode(line);
    if (std::holds_alternative<LineError>(outcome)) {
        countInvalid();
    } else if (auto* message = std::get_if<RelayMessage>(&outcome)) {
        apply(std::move(*message));
    }
}

// More than 100 invalid lines end the connection (REMOTE 5.6).
void Connection::countInvalid() {
    if (++session_.invalidLines > limits::kInvalidLines) endProcess(Status::of(StatusCode::ProtocolError));
}

void Connection::apply(RelayMessage message) {
    if (!session_.helloReceived) {
        if (const auto* hello = std::get_if<Hello>(&message)) {
            received(*hello);
        } else {
            countInvalid();
        }
        return;
    }
    const bool seeded = !session_.ready;
    if (std::holds_alternative<Hello>(message)) {
        countInvalid();
    } else if (const auto* clock = std::get_if<ClockReply>(&message)) {
        const auto it = session_.pendingPings.find(clock->id);
        if (it == session_.pendingPings.end()) return;
        const auto sentMs = it->second;
        session_.pendingPings.erase(it);
        session_.clock.addSample(sentMs, now(), clock->nowMs);
        reportClockIfChanged();
    } else if (const auto* progress = std::get_if<Progress>(&message)) {
        if (!session_.ready) report(Status::seeding(progress->value));
    } else if (std::holds_alternative<Ready>(message)) {
        if (session_.ready) return;
        session_.ready = true;
        notify(HostEvent::Message{Ready{}, true});
        report(Status::of(StatusCode::Connected));
        historyDue();
        schedule(Slot::History, now() + kHistoryIntervalMs, [](Connection& c) { c.historyTimerFired(); });
        scheduleMidnight();
    } else if (const auto* agent = std::get_if<RemoteAgent>(&message)) {
        if (!session_.agentIds.contains(agent->id) && session_.agentIds.size() >= limits::kAgents) return;
        const auto id = agent->id;
        if (emitConverted(message)) session_.agentIds.insert(id);
    } else if (const auto* removed = std::get_if<AgentRemoved>(&message)) {
        session_.agentIds.erase(removed->id);
        notify(HostEvent::Message{std::move(message), seeded});
    } else if (std::holds_alternative<RemoteResponse>(message) || std::holds_alternative<RemoteTool>(message)) {
        emitConverted(message);
    } else if (std::holds_alternative<HistoryDone>(message)) {
        notify(HostEvent::Message{std::move(message), seeded});
        historyRunEnded();
    } else if (const auto* error = std::get_if<RelayError>(&message); error && error->code == RelayError::Code::HistoryFailed) {
        notify(HostEvent::Message{std::move(message), seeded});
        historyRunEnded();
    } else {
        notify(HostEvent::Message{std::move(message), seeded});
    }
}

void Connection::received(const Hello& hello) {
    session_.helloReceived = true;
    if (hello.protocol != kProtocol) {
        endProcess(Status::versionMismatch(hello.protocol));
        return;
    }
    session_.helloAtMs = now();
    session_.relayVersion = hello.version;
    session_.relayArch = hello.arch;
    session_.clock.noteHello(hello.nowMs, now());
    if (const auto offset = session_.clock.offsetMs()) session_.reportedOffsetSeconds = *offset / 1000;
    notify(HostEvent::Message{hello, true});
    report(Status::seeding(0));
    sendPing();
}

// Converts the line's times with the current offset; a time that leaves 2000–2100 makes it invalid.
bool Connection::emitConverted(const RelayMessage& message) {
    auto local = converted(message, session_.clock.offsetMs().value_or(0));
    if (!local) {
        countInvalid();
        return false;
    }
    notify(HostEvent::Message{std::move(*local), !session_.ready});
    return true;
}

void Connection::reportClockIfChanged() {
    const auto offset = session_.clock.offsetMs();
    const std::optional<std::int64_t> seconds = offset ? std::optional<std::int64_t>(*offset / 1000) : std::nullopt;
    if (seconds == session_.reportedOffsetSeconds || !lastStatus_) return;
    session_.reportedOffsetSeconds = seconds;
    report(lastStatus_->status);
}

void Connection::sendPing() {
    const auto id = session_.nextPingId++;
    session_.pendingPings[id] = now();
    if (session_.pendingPings.size() > kPendingPingLimit) session_.pendingPings.erase(session_.pendingPings.begin());
    send(encode::ping(id));
    schedule(Slot::Ping, now() + kPingIntervalMs, [](Connection& c) { c.sendPing(); });
}

// 150 s without any line end the connection as unreachable (REMOTE 4.4).
void Connection::scheduleSilenceCheck() {
    schedule(Slot::Silence, session_.lastLineMs + kSilenceLimitMs, [](Connection& c) {
        if (c.stage_ != Stage::Run) return;
        if (c.now() - c.session_.lastLineMs >= kSilenceLimitMs) {
            c.endProcess(Status::of(StatusCode::Unreachable));
        } else {
            c.scheduleSilenceCheck();
        }
    });
}

void Connection::requestHistory(const std::map<std::string, std::string>& digests) {
    if (stage_ != Stage::Run || !session_.ready) return;
    const auto line = encode::history(digests);
    if (!line) return;
    session_.historyInFlight = true;
    send(line);
}

void Connection::noteTimeZoneChange() {
    if (stage_ != Stage::Run) return;
    if (environment_.zone) send(encode::zone(environment_.zone()));
    send(encode::today(environment_.todayStartMs(now())));
    if (session_.ready) scheduleMidnight();
}

// A historyDone or a historyFailed error ends the run: a sync that waited for it is due now.
void Connection::historyRunEnded() {
    session_.historyInFlight = false;
    if (!session_.historyDuePending) return;
    session_.historyDuePending = false;
    historyDue();
}

void Connection::historyDue() {
    if (session_.historyInFlight) {
        session_.historyDuePending = true;
    } else {
        notify(HostEvent::HistoryDue{});
    }
}

void Connection::historyTimerFired() {
    historyDue();
    schedule(Slot::History, now() + kHistoryIntervalMs, [](Connection& c) { c.historyTimerFired(); });
}

void Connection::scheduleMidnight() {
    schedule(Slot::Midnight, environment_.nextMidnightMs(now()), [](Connection& c) {
        c.send(encode::today(c.environment_.todayStartMs(c.now())));
        c.historyDue();
        c.scheduleMidnight();
    });
}

}  // namespace agentspeed::remote
