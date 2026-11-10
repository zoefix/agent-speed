#include "agentspeed/remote_coordinator.hpp"

#include <time.h>

#include <chrono>

namespace agentspeed::remote {

namespace {

constexpr std::int64_t kSuspendGapMs = 5000;

std::int64_t clockMs(clockid_t clock) {
    timespec t{};
    ::clock_gettime(clock, &t);
    return static_cast<std::int64_t>(t.tv_sec) * 1000 + t.tv_nsec / 1'000'000;
}

// How long the computer was suspended since boot: CLOCK_BOOTTIME runs on while CLOCK_MONOTONIC stops.
std::int64_t suspendedMs() { return clockMs(CLOCK_BOOTTIME) - clockMs(CLOCK_MONOTONIC); }

}  // namespace

Coordinator::Coordinator(Options options) : options_(std::move(options)) {
    transport_ = options_.transport ? options_.transport(loop_) : std::make_unique<PipeTransport>(loop_);
    ConnectionEnvironment environment;
    environment.transport = transport_.get();
    environment.scheduler = &loop_;
    environment.relays = options_.relays.get();
    environment.zone = options_.zone;
    environment.processEnvironment = options_.processEnvironment;
    hub_ = std::make_unique<Hub>(environment, [this](const HostEvent& event) { receive(event); });
    suspendGapMs_ = suspendedMs();
    loop_.addTicker([this] { checkSuspend(); });
    thread_ = std::thread([this] { loop_.run(); });
}

Coordinator::~Coordinator() { stop(); }

void Coordinator::stop() {
    {
        std::lock_guard lock(mutex_);
        if (stopped_) return;
        stopped_ = true;
    }
    loop_.sync([this] {
        for (auto& [_, check] : checks_) check->cancel();
        checks_.clear();
        hub_->stopAll();
        applied_.reset();
    });
    // The ending processes exit on SIGTERM, or on the SIGKILL 2 s later, before the loop stops.
    const auto deadline = std::chrono::steady_clock::now() + std::chrono::milliseconds(Connection::kKillDelayMs + 500);
    while (std::chrono::steady_clock::now() < deadline) {
        bool idle = true;
        loop_.sync([&] { idle = transport_->idle(); });
        if (idle) break;
        std::this_thread::sleep_for(std::chrono::milliseconds(20));
    }
    loop_.stop();
    if (thread_.joinable()) thread_.join();
}

void Coordinator::update(const HostsDocument& document, bool networkEnabled, std::set<SourceKind> sources, std::string priceTable,
                         QuotaSwitches quota) {
    {
        std::lock_guard lock(mutex_);
        if (stopped_) return;
        document_ = document;
    }
    Applied next{document.hosts(), networkEnabled, std::move(sources), std::move(priceTable), std::move(quota)};
    const auto names = document.displayNames();
    loop_.post([this, next = std::move(next), names] {
        if (applied_ && *applied_ == next) return;
        applied_ = next;
        std::vector<HostInfo> hosts;
        for (const auto& h : next.hosts) hosts.push_back({h.id, names.contains(h.id) ? names.at(h.id) : h.automaticName()});
        if (options_.feed) options_.feed->setHosts(std::move(hosts));
        hub_->update(next.hosts, next.networkEnabled, next.sources, next.priceTable, next.quota);
        hub_->setWatch(watch_);
    });
}

void Coordinator::setWatch(bool on) {
    loop_.post([this, on] {
        watch_ = on;
        hub_->setWatch(on);
    });
}

void Coordinator::retryNow(const std::string& hostId) {
    loop_.post([this, hostId] { hub_->retryNow(hostId); });
}

void Coordinator::requestHistory(const std::string& hostId, std::map<std::string, std::string> digests) {
    loop_.post([this, hostId, digests = std::move(digests)] { hub_->requestHistory(hostId, digests); });
}

void Coordinator::noteWake() {
    loop_.post([this] { hub_->noteWake(); });
}

void Coordinator::noteTimeZoneChange() {
    loop_.post([this] { hub_->noteTimeZoneChange(); });
}

void Coordinator::setHistorySink(HistorySink sink) {
    std::lock_guard lock(mutex_);
    historySink_ = std::move(sink);
}

void Coordinator::setQuotaSink(HistorySink sink) {
    std::lock_guard lock(mutex_);
    quotaSink_ = std::move(sink);
}

std::uint64_t Coordinator::testConnection(Host host, std::function<void(TestOutcome)> done) {
    std::uint64_t id = 0;
    {
        std::lock_guard lock(mutex_);
        id = nextCheck_++;
    }
    loop_.post([this, id, host = std::move(host), done = std::move(done)] {
        ConnectionEnvironment environment;
        environment.transport = transport_.get();
        environment.scheduler = &loop_;
        environment.relays = options_.relays.get();
        environment.zone = options_.zone;
        environment.processEnvironment = options_.processEnvironment;
        checks_[id] = ConnectionCheck::start(host, environment, [this, id, done](TestOutcome outcome) {
            loop_.post([this, id] { checks_.erase(id); });
            if (done) done(std::move(outcome));
        });
    });
    return id;
}

void Coordinator::cancelTest(std::uint64_t id) {
    loop_.post([this, id] {
        if (const auto it = checks_.find(id); it != checks_.end()) {
            it->second->cancel();
            checks_.erase(it);
        }
    });
}

HostsDocument Coordinator::document() const {
    std::lock_guard lock(mutex_);
    return document_;
}

std::map<std::string, HostStatus> Coordinator::statuses() const {
    std::lock_guard lock(mutex_);
    return statuses_;
}

std::set<std::string> Coordinator::connectedHostIds() const {
    std::set<std::string> ids;
    const_cast<EventLoop&>(loop_).sync([&] { ids = hub_->connectedHostIds(); });
    return ids;
}

Coordinator::Route Coordinator::route(const HostEvent& event) {
    if (std::holds_alternative<HostStatus>(event.kind)) return Route::Both;
    if (std::holds_alternative<HostEvent::HistoryDue>(event.kind)) return Route::History;
    const auto& message = std::get<HostEvent::Message>(event.kind).message;
    if (std::holds_alternative<HistoryDay>(message) || std::holds_alternative<HistoryPerf>(message) ||
        std::holds_alternative<HistoryDone>(message) || std::holds_alternative<HistoryProgress>(message)) {
        return Route::History;
    }
    if (const auto* error = std::get_if<RelayError>(&message); error && error->code == RelayError::Code::HistoryFailed) return Route::History;
    if (std::holds_alternative<HostQuota>(message) || std::holds_alternative<HostQuotaRemoved>(message) ||
        std::holds_alternative<HostQuotaBridge>(message)) {
        return Route::Quota;
    }
    return Route::Live;
}

void Coordinator::setHistoryTooLarge(std::set<std::string> hosts) {
    loop_.post([this, hosts = std::move(hosts)] {
        std::vector<HostEvent> changed;
        {
            std::lock_guard lock(mutex_);
            for (auto& [id, status] : statuses_) {
                const bool flag = hosts.contains(id);
                if (status.historyTooLarge == flag) continue;
                status.historyTooLarge = flag;
                changed.push_back({id, 0, status});
            }
            tooLarge_ = hosts;
        }
        // Only the live views hear it: the history's batches follow the connection's own statuses.
        if (!options_.feed) return;
        const auto now = loop_.nowMs();
        for (const auto& event : changed) options_.feed->receive(event, now, todayStartMs(now));
    });
}

void Coordinator::receive(const HostEvent& event) {
    if (!std::holds_alternative<HostStatus>(event.kind)) {
        deliver(event);
        return;
    }
    auto flagged = event;
    auto& status = std::get<HostStatus>(flagged.kind);
    {
        std::lock_guard lock(mutex_);
        status.historyTooLarge = tooLarge_.contains(event.hostId);
        statuses_[event.hostId] = status;
    }
    deliver(flagged);
}

void Coordinator::deliver(const HostEvent& event) {
    HistorySink sink, quotaSink;
    {
        std::lock_guard lock(mutex_);
        sink = historySink_;
        quotaSink = quotaSink_;
    }
    const auto where = route(event);
    if ((where == Route::Quota || where == Route::Both) && quotaSink) quotaSink(event);
    if (where == Route::Quota) return;
    if (where != Route::History && options_.feed) {
        const auto now = loop_.nowMs();
        options_.feed->receive(event, now, todayStartMs(now));
    }
    if (where != Route::Live && sink) sink(event);
}

std::int64_t Coordinator::todayStartMs(std::int64_t nowMs) {
    if (nowMs >= todayUntilMs_ || nowMs < todayStartMs_ || nowMs - todayCheckedMs_ >= 60'000) {
        ConnectionEnvironment environment;
        environment.zone = options_.zone;
        todayStartMs_ = environment.todayStartMs(nowMs);
        todayUntilMs_ = environment.nextMidnightMs(nowMs);
        todayCheckedMs_ = nowMs;
    }
    return todayStartMs_;
}

void Coordinator::checkSuspend() {
    const auto gap = suspendedMs();
    if (gap - suspendGapMs_ >= kSuspendGapMs) hub_->noteWake();
    suspendGapMs_ = gap;
}

}  // namespace agentspeed::remote
