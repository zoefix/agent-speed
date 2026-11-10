#include "agentspeed/remote_connection.hpp"

#include <algorithm>
#include <atomic>

#include "agentspeed/remote_ssh.hpp"
#include "agentspeed/tz.hpp"

namespace agentspeed::remote {

namespace {

constexpr std::int64_t kDayMs = 86'400'000;

// Attempt numbers are unique across connections, so a host's replaced connection never repeats one.
int nextAttempt() {
    static std::atomic<int> attempts{0};
    return ++attempts;
}

std::int64_t floorDiv(std::int64_t a, std::int64_t b) { return a / b - ((a % b != 0) && ((a < 0) != (b < 0)) ? 1 : 0); }

// The instant of local midnight `localDayStart` (local milliseconds) in `zone`.
std::int64_t instantOf(const TimeZone& zone, std::int64_t localDayStart, std::int64_t hint) {
    auto at = localDayStart - zone.offsetMs(hint);
    at = localDayStart - zone.offsetMs(at);
    return localDayStart - zone.offsetMs(at);
}

}  // namespace

std::int64_t ConnectionEnvironment::todayStartMs(std::int64_t nowMs) const {
    const auto data = zone ? zone().tzif : std::string();
    const auto tz = TimeZone::fromData(data);
    const auto& z = tz ? *tz : *TimeZone::utc();
    const auto local = nowMs + z.offsetMs(nowMs);
    auto start = instantOf(z, floorDiv(local, kDayMs) * kDayMs, nowMs);
    if (start > nowMs) start = instantOf(z, (floorDiv(local, kDayMs) - 1) * kDayMs, nowMs - kDayMs);
    return start;
}

std::int64_t ConnectionEnvironment::nextMidnightMs(std::int64_t nowMs) const {
    const auto data = zone ? zone().tzif : std::string();
    const auto tz = TimeZone::fromData(data);
    const auto& z = tz ? *tz : *TimeZone::utc();
    const auto local = nowMs + z.offsetMs(nowMs);
    auto next = instantOf(z, (floorDiv(local, kDayMs) + 1) * kDayMs, nowMs + kDayMs);
    if (next <= nowMs) next = instantOf(z, (floorDiv(local, kDayMs) + 2) * kDayMs, nowMs + 2 * kDayMs);
    return next;
}

std::int64_t Backoff::delayMs(const Status& status, std::optional<std::int64_t> upSinceMs, std::int64_t nowMs) {
    if (status.needsUser()) return kUserRetryMs;
    if (upSinceMs && nowMs - *upSinceMs >= kResetAfterMs) failures_ = 0;
    const auto delay = kDelaysMs[std::min<std::size_t>(static_cast<std::size_t>(failures_), std::size(kDelaysMs) - 1)];
    ++failures_;
    return delay;
}

std::shared_ptr<Connection> Connection::make(Host host, SessionSettings settings, ConnectionEnvironment environment, Sink sink) {
    return std::shared_ptr<Connection>(new Connection(std::move(host), std::move(settings), std::move(environment), std::move(sink)));
}

Connection::Connection(Host host, SessionSettings settings, ConnectionEnvironment environment, Sink sink)
    : host_(std::move(host)), settings_(std::move(settings)), environment_(std::move(environment)), sink_(std::move(sink)) {}

Connection::~Connection() {
    cancelTimers();
    if (auto process = std::move(process_)) {
        process->closeInput();
        process->terminate();
        environment_.scheduler->schedule(now() + kKillDelayMs, [process] { process->kill(); });
    }
}

void Connection::start() {
    if (stopped_ || stage_ != Stage::Idle || timers_.contains(Slot::Retry)) return;
    begin();
}

void Connection::stop(std::optional<Status> finalStatus) {
    if (stopped_) return;
    stopped_ = true;
    cancelTimers();
    closeUpload();
    endProcess();
    if (finalStatus) report(*finalStatus);
}

void Connection::retryNow() {
    if (stopped_ || stage_ != Stage::Idle) return;
    backoff_.reset();
    begin();
}

void Connection::noteWake() {
    if (stopped_) return;
    if (stage_ == Stage::Idle && lastStatus_ && lastStatus_->status.retrying() && timers_.contains(Slot::Retry)) {
        begin();
    } else if (stage_ == Stage::Run) {
        session_.lastLineMs = now();
        if (session_.helloReceived) sendPing();
    }
}

void Connection::updatePrices(const std::string& compactPrices) {
    if (settings_.prices == compactPrices) return;
    settings_.prices = compactPrices;
    if (stage_ == Stage::Run) send(encode::prices(compactPrices));
}

void Connection::setWatch(bool on) {
    if (settings_.watch == on) return;
    settings_.watch = on;
    if (stage_ == Stage::Run) send(encode::watch(on));
}

void Connection::setQuota(const QuotaSwitches& switches, bool hostBridge) {
    const auto before = quotaConfig();
    settings_.quota = switches;
    host_.quotaClaudeCli = hostBridge;
    const auto after = quotaConfig();
    if (stage_ == Stage::Run && session_.decoder && after != before) send(encode::quota(after));
}

void Connection::begin() {
    cancelTimers();
    attempt_ = nextAttempt();
    stage_ = Stage::Idle;
    endReason_.reset();
    relay_.reset();
    session_ = Session{};
    if (!host_.connectable()) {
        report(Status::of(StatusCode::InvalidDestination));
        return;
    }
    if (!environment_.transport->available(std::string(ssh::kExecutable))) {
        fail(Status::of(StatusCode::SshMissing));
        return;
    }
    report(Status::of(StatusCode::Connecting));
    const auto command = command::probe(host_.effectiveRelayFolder());
    if (!command) {
        fail(Status::of(StatusCode::InvalidDestination));
        return;
    }
    launch(Stage::Probe, *command);
}

void Connection::launch(Stage next, const std::string& command) {
    const auto configFile = ssh::developmentConfigFile(environment_.processEnvironment);
    const auto arguments = ssh::arguments(host_.destination, command, configFile);
    if (!arguments) {
        fail(Status::of(StatusCode::InvalidDestination));
        return;
    }
    const int token = ++processToken_;
    stdout_.clear();
    stderr_.clear();
    std::weak_ptr<Connection> self = shared_from_this();
    ProcessHandlers handlers;
    handlers.stdoutData = [self, token](std::string_view data) {
        if (auto c = self.lock()) c->receivedStdout(data, token);
    };
    handlers.stderrData = [self, token](std::string_view data) {
        if (auto c = self.lock()) c->receivedStderr(data, token);
    };
    handlers.exit = [self, token](int status) {
        if (auto c = self.lock()) c->exited(status, token);
    };
    ProcessRequest request{std::string(ssh::kExecutable), *arguments, ssh::environment(environment_.processEnvironment)};
    process_ = environment_.transport->launch(request, std::move(handlers));
    if (!process_) {
        fail(environment_.transport->available(std::string(ssh::kExecutable)) ? Status::exited(127) : Status::of(StatusCode::SshMissing));
        return;
    }
    stage_ = next;
    switch (next) {
        case Stage::Probe:
            schedule(Slot::StageLimit, now() + kProbeLimitMs, [](Connection& c) { c.endProcess(Status::of(StatusCode::Unreachable)); });
            break;
        case Stage::Upload: streamUpload(); break;
        case Stage::Run: beginSession(); break;
        case Stage::Idle: break;
    }
}

void Connection::receivedStdout(std::string_view data, int token) {
    if (token != processToken_ || stopped_) return;
    switch (stage_) {
        case Stage::Probe:
        case Stage::Upload:
            if (stdout_.size() < ProbeResult::kMaxOutputBytes) stdout_.append(data.substr(0, ProbeResult::kMaxOutputBytes - stdout_.size()));
            break;
        case Stage::Run: receivedRelayOutput(data); break;
        case Stage::Idle: break;
    }
}

void Connection::receivedStderr(std::string_view data, int token) {
    if (token != processToken_ || stopped_) return;
    stderr_.append(data);
    if (stderr_.size() > classify::kMaxStderrBytes) stderr_.erase(0, stderr_.size() - classify::kMaxStderrBytes);
}

void Connection::exited(int status, int token) {
    if (token != processToken_) return;
    process_.reset();
    cancel(Slot::StageLimit);
    closeUpload();
    if (stopped_) return;
    const auto ended = stage_;
    stage_ = Stage::Idle;
    if (endReason_) {
        fail(*endReason_);
        return;
    }
    switch (ended) {
        case Stage::Probe: probeFinished(status); break;
        case Stage::Upload: uploadFinished(status); break;
        case Stage::Run: fail(classify::ended(status, stderr_, classify::Stage::Run)); break;
        case Stage::Idle: break;
    }
}

void Connection::probeFinished(int status) {
    if (status != 0) {
        fail(classify::ended(status, stderr_, classify::Stage::Probe));
        return;
    }
    const auto result = ProbeResult::parse(stdout_);
    switch (result.verdict()) {
        case ProbeResult::Verdict::Malformed: fail(Status::exited(status)); return;
        case ProbeResult::Verdict::UnsupportedSystem: fail(Status::unsupportedSystem(result.system)); return;
        case ProbeResult::Verdict::CannotVerify: fail(Status::of(StatusCode::CannotVerify)); return;
        case ProbeResult::Verdict::Supported: break;
    }
    relay_ = environment_.relays ? environment_.relays->executable(*result.arch()) : std::nullopt;
    if (!relay_) {
        fail(Status::of(StatusCode::UploadFailed));
        return;
    }
    if (result.hasInstalled(relay_->installedName, relay_->sha256)) {
        startRelay();
    } else if (const auto command = command::upload(host_.effectiveRelayFolder(), relay_->installedName, relay_->sha256)) {
        report(Status::installing(0));
        launch(Stage::Upload, *command);
    } else {
        fail(Status::of(StatusCode::UploadFailed));
    }
}

void Connection::uploadFinished(int status) {
    if (status != 0) {
        fail(classify::ended(status, stderr_, classify::Stage::Upload));
        return;
    }
    if (!relay_ || ProbeResult::uploadedHash(stdout_) != relay_->sha256) {
        fail(Status::of(StatusCode::UploadFailed));
        return;
    }
    startRelay();
}

void Connection::startRelay() {
    const auto command = relay_ ? command::run(host_.effectiveRelayFolder(), relay_->installedName) : std::nullopt;
    if (!command) {
        fail(Status::of(StatusCode::UploadFailed));
        return;
    }
    launch(Stage::Run, *command);
}

// Streams the bundled relay into the upload command's stdin, one chunk after the other, then closes it.
void Connection::streamUpload() {
    upload_ = relay_ ? std::make_unique<std::ifstream>(relay_->path, std::ios::binary) : nullptr;
    if (!upload_ || !*upload_) {
        upload_.reset();
        endProcess(Status::of(StatusCode::UploadFailed));
        return;
    }
    uploadedBytes_ = 0;
    reportedUploadFraction_ = 0;
    uploadNextChunk(processToken_);
}

void Connection::uploadNextChunk(int token) {
    if (token != processToken_ || stage_ != Stage::Upload || !process_ || !upload_) {
        closeUpload();
        return;
    }
    std::string chunk(kUploadChunkBytes, '\0');
    upload_->read(chunk.data(), static_cast<std::streamsize>(chunk.size()));
    chunk.resize(static_cast<std::size_t>(upload_->gcount()));
    if (chunk.empty()) {
        closeUpload();
        process_->closeInput();
        return;
    }
    const auto size = static_cast<std::int64_t>(chunk.size());
    std::weak_ptr<Connection> self = shared_from_this();
    process_->write(std::move(chunk), [self, token, size](bool written) {
        auto c = self.lock();
        if (!c || token != c->processToken_) return;
        if (!written) {
            c->closeUpload();
            return;
        }
        c->uploadedBytes_ += size;
        c->reportUploadProgress();
        c->uploadNextChunk(token);
    });
}

void Connection::closeUpload() { upload_.reset(); }

void Connection::reportUploadProgress() {
    const auto total = relay_ ? relay_->size : 0;
    const double fraction = total > 0 ? std::min(1.0, static_cast<double>(uploadedBytes_) / static_cast<double>(total)) : 1.0;
    if (fraction - reportedUploadFraction_ < 0.05 && !(fraction == 1.0 && reportedUploadFraction_ < 1.0)) return;
    reportedUploadFraction_ = fraction;
    report(Status::installing(fraction));
}

// Ends the running process: stdin closed, SIGTERM, SIGKILL after 2 s. Its exit then reports `reason`.
void Connection::endProcess(std::optional<Status> reason) {
    if (reason && !endReason_) endReason_ = reason;
    if (!process_) return;
    auto process = process_;
    process->closeInput();
    process->terminate();
    environment_.scheduler->schedule(now() + kKillDelayMs, [process] { process->kill(); });
    if (stopped_) process_.reset();
}

void Connection::fail(const Status& status) {
    cancelTimers();
    stage_ = Stage::Idle;
    const auto failedAt = now();
    lastErrorAtMs_ = failedAt;
    if (status.code == StatusCode::InvalidDestination) {
        report(status);
        return;
    }
    const auto delay = backoff_.delayMs(status, session_.helloAtMs, failedAt);
    const auto retryAt = failedAt + delay;
    report(status, retryAt);
    schedule(Slot::Retry, retryAt, [](Connection& c) { c.begin(); });
}

// Reports a status once; a live status carries the relay's hello facts and the clock offset.
void Connection::report(const Status& status, std::optional<std::int64_t> retryAtMs) {
    HostStatus value;
    value.status = status;
    value.retryAtMs = retryAtMs;
    value.lastErrorAtMs = lastErrorAtMs_;
    if (session_.helloReceived && (status.code == StatusCode::Connected || status.isSeeding())) {
        value.clockOffsetMs = session_.clock.offsetMs();
        value.relayVersion = session_.relayVersion;
        value.relayArch = session_.relayArch;
    }
    if (lastStatus_ && *lastStatus_ == value) return;
    lastStatus_ = value;
    notify(value);
}

void Connection::notify(std::variant<HostStatus, HostEvent::Message, HostEvent::HistoryDue> kind) {
    if (!sink_) return;
    sink_(HostEvent{host_.id, attempt_, std::move(kind)});
}

void Connection::send(const std::optional<std::string>& line) {
    if (line && process_) process_->write(*line);
}

void Connection::schedule(Slot slot, std::int64_t atMs, std::function<void(Connection&)> work) {
    cancel(slot);
    const int generation = attempt_;
    std::weak_ptr<Connection> self = shared_from_this();
    timers_[slot] = environment_.scheduler->schedule(atMs, [self, slot, generation, work = std::move(work)] {
        auto c = self.lock();
        if (!c || c->stopped_ || c->attempt_ != generation) return;
        c->timers_.erase(slot);
        work(*c);
    });
}

void Connection::cancel(Slot slot) {
    if (const auto it = timers_.find(slot); it != timers_.end()) {
        environment_.scheduler->cancel(it->second);
        timers_.erase(it);
    }
}

void Connection::cancelTimers() {
    for (const auto& [slot, id] : timers_) environment_.scheduler->cancel(id);
    timers_.clear();
}

}  // namespace agentspeed::remote
