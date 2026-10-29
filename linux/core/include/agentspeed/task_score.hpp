#pragma once

#include <array>
#include <cstdint>
#include <map>
#include <optional>
#include <string>
#include <string_view>
#include <vector>

#include "agentspeed/json.hpp"
#include "agentspeed/model.hpp"
#include "agentspeed/task_parsers.hpp"

// The task score (METRICS 6.9) and task history, predictions and comparison (6.11), over one agent's normalised
// event stream; nothing here reads a log (spec/tools/task_score.py).
namespace agentspeed::tasks {

bool isScoredSource(SourceKind source);

// ---- prompts and segmentation (6.9 Tasks)

std::string normalizePrompt(std::string_view text);
bool isContinuation(std::string_view text);

struct PromptLine {
    enum class Kind { Prompt, Command, CommandOutput } kind = Kind::Prompt;
    bool continuation = false;
    std::optional<std::int64_t> t;  // a queued Claude message's own time
};
std::optional<PromptLine> promptLine(SourceKind source, const json::Value& line);

struct Event {
    enum class Type { Prompt, Action } type = Type::Action;
    std::int64_t t = 0;
    bool continuation = false;
    std::string kind = "other";
    std::optional<std::string> tool, digest;
    std::optional<CommandInfo> command;
    std::optional<Outcome> outcome;
    std::optional<FailureReport> failures;
    std::optional<std::vector<EditHunk>> hunks;
};

struct ClassifiedLine {
    std::string line;  // prompt, command, commandOutput, assistant
    std::int64_t t = 0;
    bool continuation = false;
    bool duplicate = false;
};
std::vector<Event> segment(const std::vector<ClassifiedLine>& lines);

struct Task {
    std::int64_t startMs = 0;
    std::vector<Event> events;
};
std::vector<Task> splitTasks(const std::vector<Event>& events);
std::int64_t taskEnd(const Task& task);
std::optional<Task> currentTask(const std::vector<Event>& events, std::optional<std::int64_t> now);

// ---- one task (6.9 Runs … Score)

struct Dims {
    double V = 0, C = 0, R = 0, E = 0, K = 0;
};
struct TaskCounts {
    std::int64_t actions = 0, counted = 0, edits = 0, oscillations = 0, loopActions = 0, runs = 0, failingRuns = 0, episodes = 0,
                 recovered = 0, recoverAttempts = 0, fixAttempts = 0, fixFailures = 0;
};
struct OpenFailure {
    std::optional<std::string> suite, cls;
    std::string key;
    std::int64_t errors = 0, peak = 0;
    bool counted = false, edited = false;
};
struct LastPass {
    std::optional<std::string> suite, cls;
    std::string key;
    bool buildOnly = false;
};
struct TaskResult {
    std::string state;  // none, unverified, pending, failing, passing
    std::optional<double> score;
    std::optional<std::int64_t> shown;
    Dims dims;
    double ig = 0, loop = 0, osc = 0;
    TaskCounts counts;
    std::optional<std::int64_t> firstPassMs;
    std::vector<std::int64_t> stallEnds;
    std::optional<double> vHeld;
    std::optional<OpenFailure> openFailure;
    std::optional<LastPass> lastPass;
};
TaskResult evaluateTask(const std::vector<Event>& events, std::optional<std::int64_t> now = std::nullopt);

std::int64_t roundHalfAway(double value);

// ---- values (6.9 Agent, group and set values)

struct AgentValue {
    std::string form = "none";  // live, resting, none
    std::optional<double> score;
    std::optional<std::int64_t> shown;
    std::optional<std::string> state;
};
AgentValue agentValue(const std::vector<Event>& events, Phase phase, std::int64_t now);

struct ValuedAgent {
    std::string id;
    std::int64_t lastLineMs = 0;
    AgentValue value;
};
struct Highest {
    std::string form = "none";
    std::optional<double> score;
    std::optional<std::int64_t> shown;
    std::optional<std::string> agent;
};
Highest highest(const std::vector<ValuedAgent>& agents);

// Ordinal (UTF-16 code unit) order of ids.
bool ordinalLess(std::string_view a, std::string_view b);

// ---- predictions (6.11)

struct Model {
    std::vector<double> coefficients;
    bool calibrated = false;
    std::int64_t tasks = 0;
};
struct Models {
    Model success, stall, fix;
};
Models priorModels();

using Features = std::map<std::string, double>;
struct Predictions {
    double success = 0, stall = 0;
    std::optional<double> fix;
    Features features;
    std::optional<Features> fixFeatures;
    std::int64_t successShown = 0, stallShown = 0;
    std::optional<std::int64_t> fixShown;
};
std::optional<Predictions> predictions(const std::vector<Event>& events, std::optional<std::int64_t> now, const Models& models);
double probability(const std::vector<double>& coefficients, const Features& features, std::string_view model);

// ---- task records (6.11 History)

struct ModelSpan {
    std::int64_t t = 0;
    std::optional<std::string> model, effort;
};
struct TaskRecord {
    std::int64_t ordinal = 0, startMs = 0, endMs = 0;
    bool complete = false;
    std::optional<std::string> model, effort;
    std::string state;
    std::optional<double> score;
    Dims dims;
    TaskCounts counts;
    std::optional<std::int64_t> firstPassMs;
    bool success = false, stalled = false;
    std::vector<std::vector<double>> snapshots;  // n, passing, failing, conv, ig, loop, osc, size, yStall
    std::vector<std::vector<double>> fixRows;    // attempts, errors, trend, osc, ig, yFix
    // Identity, filled by the history.
    std::string agentId;
    SourceKind source = SourceKind::Codex;
    std::string day;
};
std::vector<TaskRecord> taskRecords(const std::vector<Event>& events, std::int64_t analysisMs, std::int64_t lastLineMs,
                                    const std::vector<ModelSpan>& models = {});

// ---- calibration (6.11)

Models calibrate(const std::vector<TaskRecord>& records);

// ---- comparison (6.11)

struct Aggregate {
    std::int64_t tasks = 0, scoredTasks = 0;
    std::optional<double> medianScore;
    std::optional<std::int64_t> medianShown;
    std::optional<double> lowerQuartile, upperQuartile;
    std::array<std::int64_t, 10> histogram{};
    std::int64_t successes = 0, stalls = 0, fixFailures = 0, fixAttempts = 0, recovered = 0;
    std::optional<double> successRate, stallRate, fixFailureRate, recoverAttempts, timeToFirstPassMs;
};
struct SourceRow {
    SourceKind source;
    Aggregate aggregate;
};
struct ModelRow {
    std::string model;
    std::vector<SourceKind> sources;
    Aggregate aggregate;
};
struct Comparison {
    std::vector<SourceRow> bySource;
    std::vector<ModelRow> byModel;
};
Aggregate aggregate(const std::vector<const TaskRecord*>& records);
Comparison compare(const std::vector<TaskRecord>& records, const std::string& firstDay, const std::string& lastDay,
                   std::optional<SourceKind> source = std::nullopt);

}  // namespace agentspeed::tasks
