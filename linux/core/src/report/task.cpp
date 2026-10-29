#include <algorithm>
#include <cmath>

#include "agentspeed/report_task.hpp"

namespace agentspeed::report {

namespace {

std::int64_t roundHalfAway(double value) { return static_cast<std::int64_t>(std::floor(value + 0.5)); }

}  // namespace

TaskRow& TaskRow::operator+=(const TaskRow& o) {
    completed += o.completed;
    scored += o.scored;
    scoreSum += o.scoreSum;
    for (std::size_t i = 0; i < scoreBins.size(); ++i) scoreBins[i] += o.scoreBins[i];
    successes += o.successes;
    stalls += o.stalls;
    fixAttempts += o.fixAttempts;
    fixFailures += o.fixFailures;
    recovered += o.recovered;
    recoverAttempts += o.recoverAttempts;
    return *this;
}

TaskRow operator-(const TaskRow& a, const TaskRow& b) {
    TaskRow out;
    const auto step = [](std::int64_t x, std::int64_t y) { return std::max<std::int64_t>(0, x - y); };
    out.completed = step(a.completed, b.completed);
    out.scored = step(a.scored, b.scored);
    out.scoreSum = step(a.scoreSum, b.scoreSum);
    for (std::size_t i = 0; i < out.scoreBins.size(); ++i) out.scoreBins[i] = step(a.scoreBins[i], b.scoreBins[i]);
    out.successes = step(a.successes, b.successes);
    out.stalls = step(a.stalls, b.stalls);
    out.fixAttempts = step(a.fixAttempts, b.fixAttempts);
    out.fixFailures = step(a.fixFailures, b.fixFailures);
    out.recovered = step(a.recovered, b.recovered);
    out.recoverAttempts = step(a.recoverAttempts, b.recoverAttempts);
    return out;
}

Json TaskRow::json(SourceKind source) const {
    std::vector<Json> bins;
    for (const auto b : scoreBins) bins.push_back(Json::of(b));
    return Json::object({{"source", Json::of(sourceId(source))},
                         {"completed", Json::of(completed)},
                         {"scored", Json::of(scored)},
                         {"scoreSum", Json::of(scoreSum)},
                         {"scoreBins", Json::array(std::move(bins))},
                         {"successes", Json::of(successes)},
                         {"stalls", Json::of(stalls)},
                         {"fixAttempts", Json::of(fixAttempts)},
                         {"fixFailures", Json::of(fixFailures)},
                         {"recovered", Json::of(recovered)},
                         {"recoverAttempts", Json::of(recoverAttempts)}});
}

std::optional<TaskRow> recordRow(const TaskOutcome& outcome) {
    if (outcome.actions < 1) return std::nullopt;
    TaskRow row;
    row.completed = 1;
    if (outcome.score) {
        const auto shown = roundHalfAway(*outcome.score);
        row.scored = 1;
        row.scoreSum = shown;
        row.scoreBins[static_cast<std::size_t>(std::clamp<std::int64_t>(shown / 10, 0, 9))] = 1;
        row.successes = outcome.success ? 1 : 0;
    }
    row.stalls = outcome.stalled ? 1 : 0;
    row.fixAttempts = outcome.fixAttempts;
    row.fixFailures = outcome.fixFailures;
    row.recovered = outcome.recovered;
    row.recoverAttempts = outcome.recoverAttempts;
    return row;
}

Json taskValue(const std::vector<const TaskAgent*>& agents, double thinking) {
    Json score = Json::null();
    std::string form = "none";
    for (const char* wanted : {"live", "resting"}) {
        std::optional<double> best;
        for (const auto* agent : agents)
            if (agent->form == wanted && agent->score) best = best ? std::max(*best, *agent->score) : *agent->score;
        if (best) {
            score = Json::of(roundHalfAway(*best));
            form = wanted;
            break;
        }
    }
    const double tokS = std::isfinite(thinking) ? std::max(0.0, thinking) : 0.0;
    return Json::object({{"score", score}, {"form", Json::of(form)}, {"thinkingTokS", Json::decimal(std::floor(tokS * 10 + 0.5) / 10, 1)}});
}

void TaskLedger::setWatch(bool on) {
    if (watch_ && !on) {
        watched_.clear();
        rows_.clear();
        completed_ = taken_ = 0;
        ++version_;
    }
    if (!watch_ && on) {
        ++generation_;
        rows_.clear();
        completed_ = taken_ = 0;
        ++version_;
    }
    watch_ = on;
}

void TaskLedger::start(const std::string& task, std::int64_t firstEventMs, std::int64_t now, bool seeding) {
    if (watch_ && !seeding && now - firstEventMs <= kFirstEventAgeMs) watched_.insert(task);
}

bool TaskLedger::complete(const std::string& task, const ModelKey& key, const std::optional<TaskRow>& row) {
    if (!watched_.erase(task)) return false;
    if (!row || completed_ - taken_ >= kMaxCompleted) return false;
    rows_[key] += *row;
    completed_ += row->completed;
    ++version_;
    return true;
}

}  // namespace agentspeed::report
