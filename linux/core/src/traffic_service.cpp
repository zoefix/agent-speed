#include "agentspeed/traffic_service.hpp"

#include <ctime>
#include <set>

#include "agentspeed/traffic_linux.hpp"

namespace agentspeed::traffic {

namespace {

constexpr std::int64_t kIdleListMs = 2'000;
constexpr std::int64_t kOpenFilesMs = 10'000;
constexpr std::int64_t kExitedKeepMs = 10'000;

Env linuxEnv() {
    Env env;
    env.platform = Platform::Linux;
    if (const char* home = std::getenv("HOME")) env.home = home;
    return env;
}

}  // namespace

Service::Service()
    : engine_(std::make_unique<Engine>(linuxEnv(), [this](std::int64_t ms) { return dateOf(ms); })),
      reader_(std::make_unique<sampler::ProcessReader>()) {}

Service::~Service() = default;

std::string Service::dateOf(std::int64_t ms) const {
    const std::time_t seconds = static_cast<std::time_t>(ms / 1000);
    std::tm tm{};
    localtime_r(&seconds, &tm);
    char buffer[16];
    std::strftime(buffer, sizeof buffer, "%Y-%m-%d", &tm);
    return buffer;
}

void Service::setEnabled(bool enabled) {
    if (enabled == enabled_) return;
    enabled_ = enabled;
    // Turning measuring back on starts a new stream: processes already running add no burst (10.4.3).
    counters_.reset();
}

void Service::tick(std::int64_t nowMs, const std::vector<AgentFacts>& agents) {
    if (!enabled_) return;
    Sample sample;
    sample.t = nowMs;
    sample.interfaces = sampler::machineCounters();
    // The process table: every tick while agent processes exist, else every 2 s only to notice them (10.10).
    std::vector<Key> attributed;
    for (const auto& [key, c] : engine_->table().known()) {
        if (c.role != Classification::Role::None) attributed.push_back(key);
    }
    if (!attributed.empty() || nowMs - lastProcessRead_ >= kIdleListMs) {
        processes_ = reader_->read();
        lastProcessRead_ = nowMs;
        std::set<Key> alive;
        for (const auto& p : processes_) {
            alive.insert({p.pid, p.start});
            lastSeen_[{p.pid, p.start}] = nowMs;
        }
        std::vector<Key> gone;
        for (auto it = lastSeen_.begin(); it != lastSeen_.end();) {
            if (!alive.contains(it->first) && nowMs - it->second > kExitedKeepMs) {
                gone.push_back(it->first);
                it = lastSeen_.erase(it);
            } else {
                ++it;
            }
        }
        if (!gone.empty()) engine_->forget(gone);
        if (nowMs - lastOpenFiles_ >= kOpenFilesMs) {
            std::set<Key> agentsOnly;
            for (const auto& [key, c] : engine_->table().known()) {
                if (c.role == Classification::Role::Agent || c.role == Classification::Role::Self) agentsOnly.insert(key);
            }
            reader_->refreshOpenFiles(processes_, agentsOnly);
            lastOpenFiles_ = nowMs;
        }
        sample.processes = processes_;
    }
    engine_->tick(sample, agents);
    attributed.clear();
    for (const auto& [key, c] : engine_->table().known()) {
        if (c.role != Classification::Role::None && lastSeen_.contains(key)) attributed.push_back(key);
    }
    if (attributed.empty()) {
        // No agent process: no counters are read, and the next ones start a new stream.
        counters_.reset();
        return;
    }
    if (!counters_) counters_ = std::make_unique<sampler::SocketCounters>("linux-" + std::to_string(++streams_));
    Sample counted;
    counted.t = nowMs;
    counted.counters = counters_->sample(attributed);
    available_ = counters_->available();
    engine_->tick(counted, agents);
}

View Service::view(std::int64_t nowMs) const {
    View v;
    v.enabled = enabled_;
    v.available = available_;
    if (!enabled_) return v;
    v.machine = engine_->machineRate();
    v.sources = engine_->sourceRates();
    v.agentRates = engine_->agentRates();
    const auto date = dateOf(nowMs);
    if (const auto it = engine_->days().find(date); it != engine_->days().end()) {
        v.machineToday = it->second.machine;
        v.sourcesToday = it->second.sources;
    }
    if (const auto it = engine_->agentDays().find(date); it != engine_->agentDays().end()) v.agentsToday = it->second;
    const std::int64_t last = (nowMs / 1000) * 1000;
    const auto& buckets = engine_->machineBuckets().values;
    for (std::int64_t k = last - 59'000; k <= last; k += 1000) {
        const auto it = buckets.find(k);
        v.machineSeries.push_back(it == buckets.end() ? std::pair<double, double>{0, 0} : it->second);
    }
    return v;
}

std::vector<HistoryRow> Service::todayRows(std::int64_t nowMs) const {
    std::vector<HistoryRow> out;
    const auto date = dateOf(nowMs);
    for (const auto& row : engine_->history()) {
        if (row.day == date) out.push_back(row);
    }
    return out;
}

void Service::restore(const std::vector<HistoryRow>& rows) {
    std::map<std::string, Day> days;
    for (const auto& r : rows) {
        auto& day = days[r.day];
        if (r.key == "machine") {
            day.machine = {r.rx, r.tx, r.sampledMs};
        } else if (const auto source = sourceFromId(r.key)) {
            day.sources[*source] = {r.rx, r.tx, r.rxTools, r.txTools, 0, 0};
            day.processSampledMs = r.sampledMs;
        }
    }
    for (const auto& [date, day] : days) engine_->restoreDay(date, day);
}

}  // namespace agentspeed::traffic
