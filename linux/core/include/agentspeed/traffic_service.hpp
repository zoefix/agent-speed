#pragma once

#include <cstdint>
#include <map>
#include <memory>
#include <optional>
#include <string>
#include <vector>

#include "agentspeed/traffic.hpp"

namespace agentspeed::traffic {

namespace sampler {
class ProcessReader;
class SocketCounters;
}  // namespace sampler

// What the UI shows of the traffic at one instant (DESIGN 10).
struct View {
    bool enabled = false;
    bool available = true;  // per-process counters can be read
    std::optional<Rate> machine;
    std::map<SourceKind, RatePair> sources;
    MachineDay machineToday;
    std::map<SourceKind, SourceDay> sourcesToday;
    std::map<std::string, AgentDay> agentsToday;  // by agent tree root
    std::map<std::string, RatePair> agentRates;
    std::vector<std::pair<double, double>> machineSeries;  // the last 60 s, oldest first (rx, tx bytes per second)
};

// METRICS 10 on Linux: samples once a second while enabled, attributes, keeps today's totals.
class Service {
public:
    Service();
    ~Service();
    void setEnabled(bool enabled);
    bool enabled() const { return enabled_; }
    void tick(std::int64_t nowMs, const std::vector<AgentFacts>& agents);
    View view(std::int64_t nowMs) const;
    // Today's rows (METRICS 10.9) for the history store, and the saved ones restored at launch.
    std::vector<HistoryRow> todayRows(std::int64_t nowMs) const;
    void restore(const std::vector<HistoryRow>& rows);

private:
    std::string dateOf(std::int64_t ms) const;

    bool enabled_ = true;
    std::unique_ptr<Engine> engine_;
    std::unique_ptr<sampler::ProcessReader> reader_;
    std::unique_ptr<sampler::SocketCounters> counters_;
    std::vector<Process> processes_;
    std::map<Key, std::int64_t> lastSeen_;
    std::int64_t lastProcessRead_ = 0, lastOpenFiles_ = 0;
    int streams_ = 0;
    bool available_ = true;
};

}  // namespace agentspeed::traffic
