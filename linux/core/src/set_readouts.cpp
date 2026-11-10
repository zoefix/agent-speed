#include "agentspeed/set_readouts.hpp"

#include <algorithm>
#include <cmath>
#include <map>

#include "agentspeed/perf.hpp"

namespace agentspeed::perf {

namespace {

constexpr std::int64_t kRecentMs = 10 * 60'000;
constexpr std::size_t kRecentResponses = 3;

}  // namespace

std::optional<std::int64_t> RunningModel::shown() const {
    if (!speed) return std::nullopt;
    return roundHalfAway(*speed);
}

std::optional<double> recentSpeed(const std::vector<const engine::Response*>& own, std::int64_t now) {
    std::vector<const engine::Response*> chosen;
    for (const auto* r : own) {
        if (r->final && engine::interval(*r) && now - kRecentMs <= r->endMs && r->endMs <= now) chosen.push_back(r);
    }
    std::sort(chosen.begin(), chosen.end(), [](const engine::Response* a, const engine::Response* b) {
        if (a->endMs != b->endMs) return a->endMs > b->endMs;
        return ordinalLess(a->key, b->key);
    });
    if (chosen.size() > kRecentResponses) chosen.resize(kRecentResponses);
    if (chosen.empty()) return std::nullopt;
    double output = 0, durationMs = 0;
    for (const auto* r : chosen) {
        const auto span = *engine::interval(*r);
        output = output + static_cast<double>(r->output);
        durationMs = durationMs + static_cast<double>(span.second - span.first);
    }
    return output / (durationMs / 1000);
}

SetLatency setFirstLatency(const std::vector<ReadoutAgent>& agents) {
    std::vector<std::int64_t> values;
    for (const auto& a : agents) values.insert(values.end(), a.latencies.begin(), a.latencies.end());
    SetLatency out;
    out.n = static_cast<std::int64_t>(values.size());
    if (out.n < kMinSamples) return out;
    std::sort(values.begin(), values.end());
    const auto mid = values.size() / 2;
    out.ms = values.size() % 2 ? static_cast<double>(values[mid]) : static_cast<double>(values[mid - 1] + values[mid]) / 2;
    return out;
}

std::vector<RunningModel> runningModels(const std::vector<ReadoutAgent>& agents) {
    std::map<std::string, RunningModel> rows;
    for (const auto& a : agents) {
        if (a.phase == Phase::Idle || !a.model || a.model->empty()) continue;
        const auto speed = a.recentSpeed && std::isfinite(*a.recentSpeed) ? a.recentSpeed : std::nullopt;
        auto [it, created] = rows.try_emplace(*a.model);
        auto& row = it->second;
        if (created) {
            row.model = *a.model;
            row.source = a.source;
        }
        ++row.agents;
        if (speed && (!row.speed || *speed > *row.speed)) {
            row.speed = speed;
            row.source = a.source;
        }
    }
    std::vector<RunningModel> ordered;
    ordered.reserve(rows.size());
    for (auto& [model, row] : rows) ordered.push_back(std::move(row));
    std::sort(ordered.begin(), ordered.end(), [](const RunningModel& a, const RunningModel& b) {
        if (a.speed.has_value() != b.speed.has_value()) return a.speed.has_value();
        if (a.speed && *a.speed != *b.speed) return *a.speed > *b.speed;
        return ordinalLess(a.model, b.model);
    });
    return ordered;
}

SetReadouts readoutsOf(const std::vector<ReadoutAgent>& agents) { return {setFirstLatency(agents), runningModels(agents)}; }

}  // namespace agentspeed::perf
