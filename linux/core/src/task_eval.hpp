#pragma once

#include <map>
#include <optional>
#include <set>
#include <string>
#include <utility>
#include <vector>

#include "agentspeed/task_score.hpp"

// Everything METRICS 6.9 derives from one task's events at one instant (task_score.py `Evaluation`).
namespace agentspeed::tasks::detail {

constexpr double kVTest = 40.0, kVBuild = 20.0;
constexpr double kCMax = 25.0, kRMax = 15.0, kEMax = 10.0;

enum class RunResult { None, Pass, Fail };

using ActKey = std::pair<std::optional<std::string>, std::optional<std::string>>;  // (tool, digest)

struct Act {
    std::size_t index = 0;
    const Event* event = nullptr;
    std::string kind;
    std::optional<Outcome> outcome;
    FailureReport failures;  // empty without an outcome
    int weight = 0;
    RunResult result = RunResult::None;
    std::int64_t errors = 0;
    std::set<std::string> fingerprints;
    std::string check;
    std::int64_t block = 0;
    bool effective = false, oscillating = false;
    int gain = 0;

    ActKey key() const { return {event->tool, event->digest}; }
    const CommandInfo* command() const { return kind == "command" && event->command ? &*event->command : nullptr; }
    bool targeted() const { return command() && command()->targeted; }
    std::optional<std::string> suite() const { return command() ? command()->suite : std::nullopt; }
    std::optional<std::string> runClass() const { return command() ? std::optional<std::string>(command()->cls) : std::nullopt; }
    bool isClass(std::string_view cls) const { return command() && command()->cls == cls; }
};

struct Episode {
    std::string check;
    std::vector<const Act*> fails;
    bool recovered = false;
};

class Evaluation {
public:
    Evaluation(const Event* begin, const Event* end, std::optional<std::int64_t> now = std::nullopt);
    explicit Evaluation(const std::vector<Event>& events, std::optional<std::int64_t> now = std::nullopt)
        : Evaluation(events.data(), events.data() + events.size(), now) {}
    Evaluation(const Evaluation&) = delete;
    Evaluation& operator=(const Evaluation&) = delete;

    std::vector<Act> acts;
    std::vector<const Act*> counted;
    std::int64_t edits = 0, oscillations = 0, loopActions = 0;
    double osc = 0, ig = 0, loop = 0;
    std::vector<std::int64_t> stallEnds;
    std::vector<const Act*> runs;
    std::vector<std::pair<std::string, std::vector<const Act*>>> seq;  // per check, in first-run order
    std::vector<Episode> episodes;
    std::map<std::size_t, const Act*> nextInSeq;  // failing run index → next run of its sequence (nullptr: none)
    std::vector<const Act*> currentBlock, latestBlock;
    std::string state;
    double V = 0, C = 0, R = 0, E = 0, K = 0;
    std::optional<double> score;

    const std::vector<const Act*>& seqOf(const std::string& check) const;
    TaskCounts counts() const;
    Dims dims() const { return {V, C, R, E, K}; }
    std::optional<std::int64_t> firstPassMs() const;
    const Act* openFailure() const;
    std::optional<double> heldV() const;
    std::optional<OpenFailure> openFailureFacts() const;
    std::optional<LastPass> lastPassFacts() const;
    TaskResult result() const;

private:
    void editsAndGain();
    void runsAndState();
    void dimensions();
    bool blockFails(const std::vector<const Act*>& block) const;
    double convergence() const;
};

int fixLabel(const Act* next);
double sizeFeature(std::size_t n);
Features snapshotFeatures(const Evaluation& ev);
Features fixFeatures(const Event* begin, std::size_t runEventIndex, std::optional<std::int64_t> now);
double seqSum(const std::vector<double>& values);

}  // namespace agentspeed::tasks::detail
