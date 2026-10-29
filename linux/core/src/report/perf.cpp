#include "agentspeed/report_perf.hpp"

#include <algorithm>
#include <cmath>

namespace agentspeed::report {

namespace {

constexpr std::int64_t kToolFloorMs = 50;

std::int64_t roundHalfAway(double value) { return static_cast<std::int64_t>(std::floor(value + 0.5)); }

Json thinkingJson(double thinking) {
    const double tokS = std::isfinite(thinking) ? std::max(0.0, thinking) : 0.0;
    return Json::decimal(std::floor(tokS * 10 + 0.5) / 10, 1);
}

}  // namespace

PerfRow& PerfRow::operator+=(const PerfRow& o) {
    record.add(o.record);
    return *this;
}

PerfRow operator-(const PerfRow& a, const PerfRow& b) {
    const auto step = [](std::int64_t x, std::int64_t y) { return std::max<std::int64_t>(0, x - y); };
    const auto& x = a.record;
    const auto& y = b.record;
    PerfRow out;
    auto& r = out.record;
    r.n = step(x.n, y.n);
    r.outputSum = step(x.outputSum, y.outputSum);
    r.durationMsSum = step(x.durationMsSum, y.durationMsSum);
    for (std::size_t k = 0; k < r.latencyBins.size(); ++k) r.latencyBins[k] = step(x.latencyBins[k], y.latencyBins[k]);
    r.reasonN = step(x.reasonN, y.reasonN);
    r.reasonOutput = step(x.reasonOutput, y.reasonOutput);
    r.reasonDurationMsSum = step(x.reasonDurationMsSum, y.reasonDurationMsSum);
    r.cacheN = step(x.cacheN, y.cacheN);
    r.cacheInput = step(x.cacheInput, y.cacheInput);
    r.cacheCached = step(x.cacheCached, y.cacheCached);
    r.toolN = step(x.toolN, y.toolN);
    r.toolLnSum = r.toolN > 0 ? std::max(0.0, x.toolLnSum - y.toolLnSum) : 0.0;
    r.toolFailed = step(x.toolFailed, y.toolFailed);
    r.activeMs = step(x.activeMs, y.activeMs);
    return out;
}

Json PerfRow::json(SourceKind source) const {
    const auto& r = record;
    std::vector<Json> bins;
    for (std::size_t k = 0; k < r.latencyBins.size(); ++k) {
        if (r.latencyBins[k] > 0) bins.push_back(Json::array({Json::of(static_cast<std::int64_t>(k)), Json::of(r.latencyBins[k])}));
    }
    return Json::object({{"source", Json::of(sourceId(source))},
                         {"n", Json::of(r.n)},
                         {"outputSum", Json::of(r.outputSum)},
                         {"durationMsSum", Json::of(r.durationMsSum)},
                         {"latencyBins", Json::array(std::move(bins))},
                         {"reasonN", Json::of(r.reasonN)},
                         {"reasonOutput", Json::of(r.reasonOutput)},
                         {"reasonDurationMsSum", Json::of(r.reasonDurationMsSum)},
                         {"cacheN", Json::of(r.cacheN)},
                         {"cacheInput", Json::of(r.cacheInput)},
                         {"cacheCached", Json::of(r.cacheCached)},
                         {"toolN", Json::of(r.toolN)},
                         {"toolLnSum", Json::decimal(std::floor(r.toolLnSum * 1e6 + 0.5) / 1e6, 6)},
                         {"toolFailed", Json::of(r.toolFailed)},
                         {"activeMs", Json::of(r.activeMs)}});
}

PerfRow responseRow(SourceKind source, const engine::Response& response) {
    PerfRow row;
    auto& r = row.record;
    const auto span = engine::interval(response);
    const auto duration = span ? span->second - span->first : 0;
    r.n = 1;
    r.outputSum = response.output;
    r.durationMsSum = duration;
    if (response.firstMs && response.startMs && *response.firstMs >= *response.startMs) {
        r.latencyBins[static_cast<std::size_t>(perf::latencyBin(*response.firstMs - *response.startMs))] = 1;
    }
    const auto thought = std::min(response.reasoning, response.output);
    if (perf::recordsReasoning(source) && thought > 0) {
        r.reasonN = 1;
        r.reasonOutput = thought;
        r.reasonDurationMsSum = duration;
    }
    if (perf::recordsCache(source) && response.input > 0) {
        r.cacheN = 1;
        r.cacheInput = response.input;
        r.cacheCached = response.cachedInput;
    }
    return row;
}

PerfRow callRow(const work::Action& action) {
    PerfRow row;
    row.record.toolN = 1;
    row.record.toolLnSum = std::log(static_cast<double>(std::max(*action.resultMs - action.callMs, kToolFloorMs)));
    row.record.toolFailed = action.failed == true ? 1 : 0;
    return row;
}

Json perfValue(const std::vector<const PerfAgent*>& agents, double thinking) {
    Json score = Json::null();
    perf::Form form = perf::Form::None;
    for (const auto wanted : {perf::Form::Live, perf::Form::Resting}) {
        std::optional<double> best;
        for (const auto* agent : agents) {
            if (agent->form == wanted && agent->score) best = best ? std::max(*best, *agent->score) : *agent->score;
        }
        if (best) {
            score = Json::of(std::min(kMaxPerfScore, roundHalfAway(*best)));
            form = wanted;
            break;
        }
    }
    return Json::object({{"score", score}, {"form", Json::of(perf::formId(form))}, {"thinkingTokS", thinkingJson(thinking)}});
}

Json legacyValue(double thinking) {
    return Json::object({{"score", Json::null()}, {"form", Json::of("none")}, {"thinkingTokS", thinkingJson(thinking)}});
}

void PerfLedger::setWatch(bool on) {
    if (watch_ != on) {
        if (on) ++generation_;
        rows_.clear();
        responses_ = calls_ = takenResponses_ = takenCalls_ = 0;
        ++version_;
    }
    watch_ = on;
}

void PerfLedger::reset() {
    setWatch(false);
    covered_.clear();
}

void PerfLedger::add(const std::string& agentId, const ModelKey& key, PerfRow row, std::int64_t start, std::int64_t end) {
    const auto coverKey = std::make_pair(agentId, key.modelId);
    const auto it = covered_.find(coverKey);
    row.record.activeMs = std::max<std::int64_t>(0, end - (it == covered_.end() ? start : std::max(start, it->second)));
    if (it == covered_.end()) {
        covered_.emplace(coverKey, end);
    } else {
        it->second = std::max(it->second, end);
    }
    rows_[key] += row;
    ++version_;
}

bool PerfLedger::response(const std::string& agentId, const ModelKey& key, const engine::Response& response) {
    const auto span = engine::interval(response);
    if (!watch_ || !span || !response.final || responses_ - takenResponses_ >= kMaxResponses) return false;
    add(agentId, key, responseRow(key.source, response), span->first, span->second);
    ++responses_;
    return true;
}

bool PerfLedger::call(const std::string& agentId, const ModelKey& key, const work::Action& action, std::int64_t now, bool seeding) {
    if (!watch_ || seeding || !action.resultMs || now - *action.resultMs > kFirstSeenMs) return false;
    const auto d = *action.resultMs - action.callMs;
    if (d < 0 || d > perf::kToolMaxMs || calls_ - takenCalls_ >= kMaxCalls) return false;
    add(agentId, key, callRow(action), action.callMs, *action.resultMs);
    ++calls_;
    return true;
}

}  // namespace agentspeed::report
