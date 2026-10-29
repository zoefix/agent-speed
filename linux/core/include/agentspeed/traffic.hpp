#pragma once

#include <array>
#include <cstdint>
#include <functional>
#include <map>
#include <optional>
#include <string>
#include <utility>
#include <vector>

#include "agentspeed/model.hpp"

// Network traffic (METRICS 10, spec/tools/traffic.py): byte formatting, process attribution, deltas, live rates, today
// totals and daily rows. Only byte counts are read; the samplers that read them live elsewhere (traffic_linux.hpp).
namespace agentspeed::traffic {

// METRICS 10.11: decimal units, three significant digits, a no-break space before the unit.
std::string formatRate(double bytesPerSecond, bool bits = false);
std::string formatTotal(double bytes);

enum class Platform { Linux, MacOS, Windows };

// Paths and names as the platform compares them (METRICS 10.5).
struct Env {
    Platform platform = Platform::Linux;
    std::string home;
    std::map<std::string, std::string> vars;  // Windows %VAR% values
    std::string norm(const std::string& path) const;
    std::string name(const std::string& exe) const;
};

struct Process {
    std::int64_t pid = 0, ppid = 0;
    std::int64_t start = 0;  // start time, any monotonic unit shared by the table
    std::string exe;
    std::vector<std::string> args;
    std::optional<std::string> bundle;  // macOS bundle id
    std::optional<std::string> cwd;
    std::vector<std::string> openFiles;
};

using Key = std::pair<std::int64_t, std::int64_t>;  // (pid, start)

std::optional<SourceKind> matchSource(const Env& env, const Process& process, const std::vector<const Process*>& ancestors);

struct Classification {
    enum class Role { None, Agent, Self, Tool } role = Role::None;
    std::optional<SourceKind> source;
    std::optional<Key> root;
};

// Sticky classification of process identities.
class ProcessTable {
public:
    explicit ProcessTable(Env env) : env_(std::move(env)) {}
    void refresh(const std::vector<Process>& processes);
    const Classification* classify(const Key& key) const;
    const Process* process(const Key& key) const;
    const std::map<Key, Classification>& known() const { return known_; }
    // Forgets identities absent from the table for a while (exited processes).
    void forget(const std::vector<Key>& keys);
    const Env& env() const { return env_; }

private:
    Env env_;
    std::map<Key, Classification> known_;
    std::map<Key, Process> processes_;
};

struct CounterRow {
    std::int64_t pid = 0, start = 0;
    std::uint64_t rx = 0, tx = 0;
};
struct Counters {
    std::string stream;
    bool continues = false;
    std::vector<CounterRow> rows;
};
using Interfaces = std::map<std::string, std::pair<std::uint64_t, std::uint64_t>>;

struct Sample {
    std::int64_t t = 0;
    std::optional<std::vector<Process>> processes;
    std::optional<Interfaces> interfaces;
    std::optional<Counters> counters;
};

// An agent of a scored-or-not source for per-agent attribution (10.6).
struct AgentFacts {
    std::string id;
    SourceKind source = SourceKind::Codex;
    std::optional<std::string> parent;
    std::optional<std::string> transcript;
    std::optional<std::string> cwdPath;
    std::vector<std::pair<std::int64_t, std::int64_t>> active;  // intervals in which it was active
};

struct Rate {
    double rx = 0, tx = 0;
};
struct RatePair {
    std::optional<Rate> withTools, selfOnly;
};
struct SourceDay {
    std::uint64_t rxSelf = 0, txSelf = 0, rxTools = 0, txTools = 0, unassignedRx = 0, unassignedTx = 0;
};
struct AgentDay {
    std::uint64_t rxSelf = 0, txSelf = 0, rxTools = 0, txTools = 0;
};
struct MachineDay {
    std::uint64_t rx = 0, tx = 0;
    std::int64_t sampledMs = 0;
};
struct Day {
    MachineDay machine;
    std::map<SourceKind, SourceDay> sources;
    std::int64_t processSampledMs = 0;
};
struct HistoryRow {
    std::string day, key;
    std::uint64_t rx = 0, tx = 0, rxTools = 0, txTools = 0;
    std::int64_t sampledMs = 0;
};

// One-second buckets of the last 300 s for sparklines (10.7).
struct Buckets {
    static constexpr int kSeconds = 300;
    std::map<std::int64_t, std::pair<double, double>> values;  // bucket start (ms) → bytes per second
    void add(std::int64_t t, std::int64_t intervalMs, std::uint64_t rx, std::uint64_t tx);
};

class Engine {
public:
    // `dateOf` gives the local date (YYYY-MM-DD) of a time.
    Engine(Env env, std::function<std::string(std::int64_t)> dateOf);
    void tick(const Sample& sample, const std::vector<AgentFacts>& agents);

    std::optional<Rate> machineRate() const;
    std::map<SourceKind, RatePair> sourceRates() const;
    std::map<std::string, RatePair> agentRates() const;
    const std::map<std::string, Day>& days() const { return days_; }
    const std::map<std::string, std::map<std::string, AgentDay>>& agentDays() const { return agentDays_; }
    std::vector<HistoryRow> history() const;
    const ProcessTable& table() const { return table_; }
    const Buckets& machineBuckets() const { return machineBuckets_; }
    const std::map<SourceKind, Buckets>& sourceBuckets() const { return sourceBuckets_; }
    // Adds today's totals saved earlier (a relaunch, 10.8).
    void restoreDay(const std::string& date, const Day& day);
    // Cumulative bytes per source since the engine started (rxSelf, txSelf, rxTools, txTools), without the deltas of a
    // sample after a gap: what live reporting takes its deltas from (METRICS 11.4).
    const std::map<SourceKind, std::array<std::uint64_t, 4>>& reportTotals() const { return reportTotals_; }
    // Exited processes leave the table.
    void forget(const std::vector<Key>& keys) { table_.forget(keys); }

    struct Series {
        std::vector<std::array<std::int64_t, 3>> points;  // (t, rx, tx)
        void add(std::int64_t t, std::int64_t rx, std::int64_t tx, bool gap);
        std::optional<Rate> rate() const;
    };
    struct SeriesPair {
        Series self, all;
        void add(std::int64_t t, const std::array<std::uint64_t, 4>& v, bool gap);
        RatePair rates() const;
    };

private:
    std::string rootOf(const std::string& id, const std::map<std::string, const AgentFacts*>& byId) const;
    std::vector<std::string> candidates(SourceKind source, const Key& root, std::int64_t start, std::int64_t end,
                                        const std::vector<AgentFacts>& agents) const;
    static std::uint64_t delta(std::uint64_t prev, std::uint64_t cur, std::optional<std::int64_t> dt);

    std::function<std::string(std::int64_t)> dateOf_;
    ProcessTable table_;
    std::optional<std::int64_t> prevT_, prevCounterT_;
    Interfaces interfaces_;
    std::optional<std::string> stream_;
    std::map<Key, std::pair<std::uint64_t, std::uint64_t>> counters_;
    std::map<Key, int> missing_;
    Series machine_;
    std::map<SourceKind, SeriesPair> sources_;
    std::map<std::string, SeriesPair> agents_;
    std::map<std::string, Day> days_;
    std::map<std::string, std::map<std::string, AgentDay>> agentDays_;
    Buckets machineBuckets_;
    std::map<SourceKind, Buckets> sourceBuckets_;
    std::map<SourceKind, std::array<std::uint64_t, 4>> reportTotals_;
};

}  // namespace agentspeed::traffic
