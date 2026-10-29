#pragma once

#include <array>
#include <cstdint>
#include <map>
#include <optional>
#include <set>
#include <string>
#include <vector>

#include "agentspeed/model.hpp"
#include "agentspeed/report_json.hpp"
#include "agentspeed/report_values.hpp"

// The taskScore extension of live reporting (METRICS 11.10; spec/tools/report_task_score.py): live values, task
// statistics and the task ledger.
namespace agentspeed::report {

// One model and source's statistics of counted tasks. Every field adds, so rows merge and differences are exact.
struct TaskRow {
    std::int64_t completed = 0, scored = 0, scoreSum = 0;
    std::array<std::int64_t, 10> scoreBins{};
    std::int64_t successes = 0, stalls = 0, fixAttempts = 0, fixFailures = 0, recovered = 0, recoverAttempts = 0;

    TaskRow& operator+=(const TaskRow& o);
    friend TaskRow operator-(const TaskRow& a, const TaskRow& b);
    bool zero() const { return completed == 0; }
    Json json(SourceKind source) const;
};
using TaskRows = std::map<ModelKey, TaskRow>;

// What one completed task adds (METRICS 11.10 Counting); nullopt for a task without actions.
struct TaskOutcome {
    std::int64_t actions = 0;
    std::optional<double> score;
    bool success = false, stalled = false;
    std::int64_t fixAttempts = 0, fixFailures = 0, recovered = 0, recoverAttempts = 0;
};
std::optional<TaskRow> recordRow(const TaskOutcome& outcome);

// An agent of the set with its value (6.9 Values), under its current model's report id.
struct TaskAgent {
    SourceKind source = SourceKind::Codex;
    std::string modelId;
    std::string form = "none";  // live, resting, none
    std::optional<double> score;
};

// {score, form, thinkingTokS}: the highest live score, else the highest resting score, else none.
Json taskValue(const std::vector<const TaskAgent*>& agents, double thinking);

// The live values each snapshot carries for the extension, and the task ledger's cumulative rows of the current watch
// generation (a new generation, after a break, starts them over).
struct TaskFacts {
    std::vector<TaskAgent> agents;
    double thinking = 0;                         // all enabled sources' liveThinking
    std::map<std::string, double> modelThinking;  // per report id: the model sums of its raw models
    std::shared_ptr<const TaskRows> rows = std::make_shared<TaskRows>();
    std::uint64_t generation = 0;
    std::int64_t completed = 0;  // tasks counted in `rows`
};

// METRICS 11.10 Watch, Counting and the task ledger. Tasks are keyed by the owner (agent id and first event time).
class TaskLedger {
public:
    static constexpr std::int64_t kFirstEventAgeMs = 120'000;
    static constexpr std::int64_t kMaxCompleted = 1000;

    bool watching() const { return watch_; }
    std::uint64_t generation() const { return generation_; }
    // The watch turning off is a break: every task is unwatched and the ledger emptied.
    void setWatch(bool on);
    // How many of this generation's completions the owner's builds already took (the 1000 cap counts the rest).
    void setTaken(std::int64_t taken) { taken_ = taken; }
    // A task's first event, read at `now` (seeding: during the backlog).
    void start(const std::string& task, std::int64_t firstEventMs, std::int64_t now, bool seeding = false);
    void drop(const std::string& task) { watched_.erase(task); }
    bool watched(const std::string& task) const { return watched_.contains(task); }
    // A watched task completed; `row` nullopt for a task without actions. True when it was counted.
    bool complete(const std::string& task, const ModelKey& key, const std::optional<TaskRow>& row);
    const TaskRows& rows() const { return rows_; }
    std::int64_t completed() const { return completed_; }
    std::uint64_t version() const { return version_; }
    const std::set<std::string>& watchedTasks() const { return watched_; }

private:
    bool watch_ = false;
    std::uint64_t generation_ = 0, version_ = 0;
    std::int64_t taken_ = 0, completed_ = 0;
    std::set<std::string> watched_;
    TaskRows rows_;
};

}  // namespace agentspeed::report
