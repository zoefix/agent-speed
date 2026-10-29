#include <algorithm>
#include <cmath>
#include <tuple>

#include "agentspeed/task_score.hpp"
#include "task_eval.hpp"

// Predictions, task records, calibration and comparison (METRICS 6.11).
namespace agentspeed::tasks {

namespace {

using detail::Evaluation;
using detail::RunResult;

constexpr double kEtaLimit = 35.0;
constexpr double kL2 = 4.0;
constexpr int kIterations = 25;
constexpr std::size_t kMinTasks = 40, kMaxTasks = 2000;
constexpr double kMinClassWeight = 5.0;
constexpr std::int64_t kCompleteIdleMs = 30 * 60'000;
constexpr int kSnapshots[] = {1, 2, 4, 8, 16, 32, 64, 128, 256, 512};
constexpr double kHistogramEdges[] = {10, 20, 30, 40, 50, 60, 70, 80, 90};

const std::vector<std::string> kSuccessFeatures = {"passing", "failing", "conv", "ig", "loop", "osc", "size"};
const std::vector<std::string> kStallFeatures = {"ig", "loop", "osc", "failing", "size"};
const std::vector<std::string> kFixFeatures = {"attempts", "errors", "trend", "osc", "ig"};
const std::vector<std::string> kSnapshotFields = {"n", "passing", "failing", "conv", "ig", "loop", "osc", "size", "yStall"};
const std::vector<std::string> kFixFields = {"attempts", "errors", "trend", "osc", "ig", "yFix"};
const std::vector<double> kSuccessPrior = {-1.5, 3.0, -0.5, 1.0, 1.0, -2.0, -1.5, -0.5};
const std::vector<double> kStallPrior = {-1.0, -2.0, 3.0, 2.0, 0.8, 0.5};
const std::vector<double> kFixPrior = {-0.5, 1.5, 1.0, 1.0, 1.5, -1.0};

const std::vector<std::string>& featureNames(std::string_view model) {
    return model == "success" ? kSuccessFeatures : model == "stall" ? kStallFeatures : kFixFeatures;
}

const std::vector<double>& prior(std::string_view model) {
    return model == "success" ? kSuccessPrior : model == "stall" ? kStallPrior : kFixPrior;
}

double sigmoid(double eta) {
    eta = std::max(-kEtaLimit, std::min(kEtaLimit, eta));
    return 1.0 / (1.0 + std::exp(-eta));
}

double linear(const std::vector<double>& c, const Features& features, const std::vector<std::string>& names) {
    double sum = 0;
    for (std::size_t k = 0; k < names.size(); ++k) sum = sum + c[k + 1] * features.at(names[k]);
    return c[0] + sum;
}

std::vector<std::size_t> actionIndices(const std::vector<Event>& events, std::optional<std::int64_t> now, bool countedOnly) {
    std::vector<std::size_t> out;
    for (std::size_t j = 0; j < events.size(); ++j) {
        const auto& e = events[j];
        if (e.type != Event::Type::Action || (now && e.t > *now)) continue;
        if (countedOnly && e.kind == "other") continue;
        out.push_back(j);
    }
    return out;
}

struct Row {
    Features features;
    double y = 0;
    double weight = 0;
};

std::pair<std::vector<Row>, std::size_t> modelRows(const std::vector<const TaskRecord*>& records, std::string_view name) {
    std::vector<Row> rows;
    std::size_t tasks = 0;
    for (const auto* rec : records) {
        std::vector<std::pair<Features, double>> taskRows;
        const auto& source = name == "fix" ? rec->fixRows : rec->snapshots;
        const auto& fields = name == "fix" ? kFixFields : kSnapshotFields;
        for (const auto& r : source) {
            Features f;
            for (std::size_t k = 0; k < fields.size() && k < r.size(); ++k) f[fields[k]] = r[k];
            const double y = name == "success" ? (rec->success ? 1.0 : 0.0) : r.back();
            taskRows.emplace_back(std::move(f), y);
        }
        if (taskRows.empty()) continue;
        ++tasks;
        const double weight = 1.0 / static_cast<double>(taskRows.size());
        for (auto& [f, y] : taskRows) rows.push_back({std::move(f), y, weight});
    }
    return {rows, tasks};
}

std::vector<double> choleskySolve(const std::vector<std::vector<double>>& matrix, const std::vector<double>& vector) {
    const auto d = vector.size();
    std::vector<std::vector<double>> lower(d, std::vector<double>(d, 0.0));
    for (std::size_t j = 0; j < d; ++j) {
        double s = 0;
        for (std::size_t k = 0; k < j; ++k) s = s + lower[j][k] * lower[j][k];
        lower[j][j] = std::sqrt(matrix[j][j] - s);
        for (std::size_t i = j + 1; i < d; ++i) {
            double t = 0;
            for (std::size_t k = 0; k < j; ++k) t = t + lower[i][k] * lower[j][k];
            lower[i][j] = (matrix[i][j] - t) / lower[j][j];
        }
    }
    std::vector<double> z(d, 0.0), x(d, 0.0);
    for (std::size_t i = 0; i < d; ++i) {
        double t = 0;
        for (std::size_t k = 0; k < i; ++k) t = t + lower[i][k] * z[k];
        z[i] = (vector[i] - t) / lower[i][i];
    }
    for (std::size_t i = d; i-- > 0;) {
        double t = 0;
        for (std::size_t k = i + 1; k < d; ++k) t = t + lower[k][i] * x[k];
        x[i] = (z[i] - t) / lower[i][i];
    }
    return x;
}

std::vector<double> fit(const std::vector<Row>& rows, const std::vector<std::string>& names, const std::vector<double>& priorBeta) {
    auto beta = priorBeta;
    const auto d = beta.size();
    for (int iteration = 0; iteration < kIterations; ++iteration) {
        std::vector<double> gradient(d);
        std::vector<std::vector<double>> hessian(d, std::vector<double>(d, 0.0));
        for (std::size_t k = 0; k < d; ++k) {
            gradient[k] = kL2 * (beta[k] - priorBeta[k]);
            hessian[k][k] = kL2;
        }
        for (const auto& row : rows) {
            std::vector<double> x{1.0};
            for (const auto& n : names) x.push_back(row.features.at(n));
            double eta = 0;
            for (std::size_t k = 0; k < d; ++k) eta = eta + beta[k] * x[k];
            const double p = sigmoid(eta);
            for (std::size_t i = 0; i < d; ++i) {
                gradient[i] += row.weight * (p - row.y) * x[i];
                for (std::size_t j = 0; j < d; ++j) hessian[i][j] += row.weight * p * (1 - p) * x[i] * x[j];
            }
        }
        const auto step = choleskySolve(hessian, gradient);
        for (std::size_t k = 0; k < d; ++k) beta[k] = beta[k] - step[k];
    }
    return beta;
}

std::optional<double> quantile(std::vector<double> values, double p) {
    if (values.empty()) return std::nullopt;
    std::sort(values.begin(), values.end());
    const double h = static_cast<double>(values.size() - 1) * p;
    const auto i = static_cast<std::size_t>(std::floor(h));
    const double fraction = h - static_cast<double>(i);
    return fraction == 0 ? values[i] : values[i] + fraction * (values[i + 1] - values[i]);
}

}  // namespace

Models priorModels() { return {{kSuccessPrior, false, 0}, {kStallPrior, false, 0}, {kFixPrior, false, 0}}; }

double probability(const std::vector<double>& coefficients, const Features& features, std::string_view model) {
    return sigmoid(linear(coefficients, features, featureNames(model)));
}

std::optional<Predictions> predictions(const std::vector<Event>& events, std::optional<std::int64_t> now, const Models& models) {
    const auto task = currentTask(events, now);
    if (!task) return std::nullopt;
    const Evaluation ev(task->events, now);
    if (!ev.score) return std::nullopt;
    Predictions out;
    out.features = detail::snapshotFeatures(ev);
    out.success = sigmoid(linear(models.success.coefficients, out.features, kSuccessFeatures));
    out.stall = sigmoid(linear(models.stall.coefficients, out.features, kStallFeatures));
    if (const auto* open = ev.state == "failing" ? ev.openFailure() : nullptr) {
        const auto actions = actionIndices(task->events, now, false);
        auto fixf = detail::fixFeatures(task->events.data(), actions[open->index], now);
        out.fix = sigmoid(linear(models.fix.coefficients, fixf, kFixFeatures));
        out.fixFeatures = std::move(fixf);
    }
    out.successShown = roundHalfAway(out.success * 100);
    out.stallShown = roundHalfAway(out.stall * 100);
    if (out.fix) out.fixShown = roundHalfAway(*out.fix * 100);
    return out;
}

std::vector<TaskRecord> taskRecords(const std::vector<Event>& events, std::int64_t analysisMs, std::int64_t lastLineMs,
                                    const std::vector<ModelSpan>& models) {
    const auto tasks = splitTasks(events);
    std::vector<TaskRecord> records;
    for (std::size_t i = 0; i < tasks.size(); ++i) {
        const auto& evs = tasks[i].events;
        if (std::none_of(evs.begin(), evs.end(), [](const Event& e) { return e.type == Event::Type::Action; })) continue;
        const Evaluation ev(evs);
        TaskRecord rec;
        rec.ordinal = static_cast<std::int64_t>(records.size());
        rec.startMs = tasks[i].startMs;
        rec.endMs = taskEnd(tasks[i]);
        for (const auto& span : models) {
            if (span.t <= rec.endMs) {
                rec.model = span.model;
                rec.effort = span.effort;
            }
        }
        const auto counted = actionIndices(evs, std::nullopt, true);
        for (const int n : kSnapshots) {
            if (static_cast<std::size_t>(n) > counted.size()) break;
            const Evaluation prefix(evs.data(), evs.data() + counted[n - 1] + 1);
            if (prefix.state == "none") continue;
            const auto f = detail::snapshotFeatures(prefix);
            std::vector<double> row{static_cast<double>(n)};
            for (std::size_t k = 1; k + 1 < kSnapshotFields.size(); ++k) row.push_back(f.at(kSnapshotFields[k]));
            row.push_back(std::any_of(ev.stallEnds.begin(), ev.stallEnds.end(), [&](std::int64_t e) { return e > n; }) ? 1.0 : 0.0);
            rec.snapshots.push_back(std::move(row));
        }
        const auto actions = actionIndices(evs, std::nullopt, false);
        for (const auto* run : ev.runs) {
            if (run->result != RunResult::Fail) continue;
            const auto f = detail::fixFeatures(evs.data(), actions[run->index], std::nullopt);
            std::vector<double> row;
            for (std::size_t k = 0; k + 1 < kFixFields.size(); ++k) row.push_back(f.at(kFixFields[k]));
            const auto it = ev.nextInSeq.find(run->index);
            row.push_back(detail::fixLabel(it == ev.nextInSeq.end() ? nullptr : it->second));
            rec.fixRows.push_back(std::move(row));
        }
        rec.complete = i + 1 < tasks.size() || analysisMs - lastLineMs >= kCompleteIdleMs;
        rec.state = ev.state;
        rec.score = ev.score;
        rec.dims = ev.dims();
        rec.counts = ev.counts();
        rec.firstPassMs = ev.firstPassMs();
        rec.success = ev.state == "passing";
        rec.stalled = !ev.stallEnds.empty();
        records.push_back(std::move(rec));
    }
    return records;
}

Models calibrate(const std::vector<TaskRecord>& records) {
    std::vector<const TaskRecord*> chosen;
    for (const auto& r : records) {
        if (r.complete) chosen.push_back(&r);
    }
    std::stable_sort(chosen.begin(), chosen.end(), [](const TaskRecord* a, const TaskRecord* b) {
        if (a->endMs != b->endMs) return a->endMs < b->endMs;
        if (a->startMs != b->startMs) return a->startMs < b->startMs;
        if (a->agentId != b->agentId) return ordinalLess(a->agentId, b->agentId);
        return a->ordinal < b->ordinal;
    });
    if (chosen.size() > kMaxTasks) chosen.erase(chosen.begin(), chosen.end() - kMaxTasks);
    Models out;
    const std::pair<const char*, Model*> slots[] = {{"success", &out.success}, {"stall", &out.stall}, {"fix", &out.fix}};
    for (const auto& [name, model] : slots) {
        const auto [rows, tasks] = modelRows(chosen, name);
        double positive = 0, negative = 0;
        for (const auto& r : rows) {
            if (r.y == 1) positive = positive + r.weight;
            if (r.y == 0) negative = negative + r.weight;
        }
        model->tasks = static_cast<std::int64_t>(tasks);
        if (tasks >= kMinTasks && positive >= kMinClassWeight && negative >= kMinClassWeight) {
            model->coefficients = fit(rows, featureNames(name), prior(name));
            model->calibrated = true;
        } else {
            model->coefficients = prior(name);
        }
    }
    return out;
}

Aggregate aggregate(const std::vector<const TaskRecord*>& records) {
    Aggregate a;
    a.tasks = static_cast<std::int64_t>(records.size());
    std::vector<double> scores, firstPass;
    std::int64_t recoverAttempts = 0;
    for (const auto* r : records) {
        if (r->score) {
            scores.push_back(*r->score);
            if (r->success) ++a.successes;
            std::size_t bin = 0;
            for (const double edge : kHistogramEdges) bin += *r->score >= edge ? 1 : 0;
            ++a.histogram[bin];
        }
        a.fixAttempts += r->counts.fixAttempts;
        a.recovered += r->counts.recovered;
        a.fixFailures += r->counts.fixFailures;
        recoverAttempts += r->counts.recoverAttempts;
        if (r->firstPassMs) firstPass.push_back(static_cast<double>(*r->firstPassMs - r->startMs));
        if (r->stalled) ++a.stalls;
    }
    a.scoredTasks = static_cast<std::int64_t>(scores.size());
    a.medianScore = quantile(scores, 0.5);
    if (a.medianScore) a.medianShown = roundHalfAway(*a.medianScore);
    a.lowerQuartile = quantile(scores, 0.25);
    a.upperQuartile = quantile(scores, 0.75);
    if (a.scoredTasks) a.successRate = static_cast<double>(a.successes) / static_cast<double>(a.scoredTasks);
    if (a.tasks) a.stallRate = static_cast<double>(a.stalls) / static_cast<double>(a.tasks);
    if (a.fixAttempts) a.fixFailureRate = static_cast<double>(a.fixFailures) / static_cast<double>(a.fixAttempts);
    if (a.recovered) a.recoverAttempts = static_cast<double>(recoverAttempts) / static_cast<double>(a.recovered);
    a.timeToFirstPassMs = quantile(firstPass, 0.5);
    return a;
}

Comparison compare(const std::vector<TaskRecord>& records, const std::string& firstDay, const std::string& lastDay,
                   std::optional<SourceKind> source) {
    std::vector<const TaskRecord*> chosen;
    for (const auto& r : records) {
        if (r.complete && firstDay <= r.day && r.day <= lastDay && (!source || r.source == *source)) chosen.push_back(&r);
    }
    Comparison out;
    for (int s = 0; s <= static_cast<int>(SourceKind::Droid); ++s) {
        const auto kind = static_cast<SourceKind>(s);
        std::vector<const TaskRecord*> part;
        for (const auto* r : chosen) {
            if (r->source == kind) part.push_back(r);
        }
        if (!part.empty()) out.bySource.push_back({kind, aggregate(part)});
    }
    std::vector<std::pair<std::string, std::vector<const TaskRecord*>>> byModel;
    for (const auto* r : chosen) {
        const auto model = r->model.value_or("");
        auto it = std::find_if(byModel.begin(), byModel.end(), [&](const auto& entry) { return entry.first == model; });
        if (it == byModel.end()) it = byModel.insert(byModel.end(), {model, {}});
        it->second.push_back(r);
    }
    for (const auto& [model, part] : byModel) {
        ModelRow row{model, {}, aggregate(part)};
        for (int s = 0; s <= static_cast<int>(SourceKind::Droid); ++s) {
            const auto kind = static_cast<SourceKind>(s);
            if (std::any_of(part.begin(), part.end(), [&](const TaskRecord* r) { return r->source == kind; })) row.sources.push_back(kind);
        }
        out.byModel.push_back(std::move(row));
    }
    std::stable_sort(out.byModel.begin(), out.byModel.end(), [](const ModelRow& a, const ModelRow& b) {
        if (a.aggregate.tasks != b.aggregate.tasks) return a.aggregate.tasks > b.aggregate.tasks;
        return a.model < b.model;
    });
    return out;
}

}  // namespace agentspeed::tasks
