#include <algorithm>
#include <cmath>

#include "agentspeed/report_body.hpp"

namespace agentspeed::report {

namespace {

std::optional<traffic::Rate> larger(const std::optional<traffic::Rate>& a, const std::optional<traffic::Rate>& b) {
    if (!a) return b;
    if (!b) return a;
    return traffic::Rate{std::max(a->rx, b->rx), std::max(a->tx, b->tx)};
}

std::optional<traffic::Rate> liveOf(const traffic::RatePair& pair, bool includeTools) { return includeTools ? pair.withTools : pair.selfOnly; }

const std::shared_ptr<const Counters>& zeroCounters() {
    static const auto zero = std::make_shared<const Counters>();
    return zero;
}

bool tasksOrUsageSince(const Counters& now, const Counters& before) { return !now.since(before).byModel().empty(); }

const TaskFacts& noTasks() {
    static const TaskFacts empty;
    return empty;
}

const TaskFacts& tasksOf(const Sample& sample) { return sample.facts.tasks ? *sample.facts.tasks : noTasks(); }

}  // namespace

TrafficSample TrafficSample::from(const traffic::View& view, bool includeTools, const std::set<SourceKind>& enabled, std::int64_t epoch) {
    TrafficSample sample;
    sample.includesTools = includeTools;
    sample.enabled = enabled;
    sample.totals = {epoch, view.reportTotals};
    if (!view.enabled) return sample;
    sample.status = view.available ? TrafficStatus::Ok : TrafficStatus::Unavailable;
    if (!view.available) return sample;
    for (const auto& [kind, pair] : view.sources) {
        if (!enabled.contains(kind)) continue;
        const auto rate = liveOf(pair, includeTools);
        if (!rate) continue;
        sample.sourceRates[kind] = *rate;
        sample.rate = traffic::Rate{(sample.rate ? sample.rate->rx : 0) + rate->rx, (sample.rate ? sample.rate->tx : 0) + rate->tx};
    }
    return sample;
}

std::map<SourceKind, Bytes> TrafficSample::agentBytes(const TrafficTotals& baseline) const {
    std::map<SourceKind, Bytes> out;
    const bool restarted = baseline.epoch != totals.epoch;
    const auto sum = [&](const std::array<std::uint64_t, 4>& v) {
        return Bytes{static_cast<std::int64_t>(v[0] + (includesTools ? v[2] : 0)), static_cast<std::int64_t>(v[1] + (includesTools ? v[3] : 0))};
    };
    for (const auto& [kind, split] : totals.sources) {
        if (!enabled.contains(kind)) continue;
        const auto now = sum(split);
        Bytes then;
        if (!restarted) {
            if (const auto it = baseline.sources.find(kind); it != baseline.sources.end()) then = sum(it->second);
        }
        const Bytes delta{std::max<std::int64_t>(0, now.rx - then.rx), std::max<std::int64_t>(0, now.tx - then.tx)};
        if (delta.rx || delta.tx) out[kind] = delta;
    }
    return out;
}

Sample Sample::from(const Snapshot& snapshot, TrafficSample traffic) {
    Sample sample;
    sample.live = {snapshot.liveOutput, snapshot.liveTotal};
    sample.estimatedShare = snapshot.estimatedShare;
    sample.liveCost = snapshot.liveCost;
    sample.knownShare = snapshot.liveCostKnownShare;
    for (const auto& row : snapshot.sources) {
        if (!row.enabled) continue;
        sample.sources[row.source] = {{row.liveOutput, row.liveTotal}, row.liveEstimatedShare, row.liveCost, row.liveCostKnownShare};
    }
    sample.facts = snapshot.report;
    sample.traffic = std::move(traffic);
    return sample;
}

bool Sample::working() const {
    return std::any_of(facts.agents.begin(), facts.agents.end(), [](const auto& entry) { return entry.second.generating + entry.second.tools > 0; });
}

Window::Window(const Sample& sample)
    : baseline_(sample.facts.counters), lastCounters_(sample.facts.counters), baselineEpoch_(sample.facts.epoch), lastEpoch_(sample.facts.epoch),
      trafficBaseline_(sample.traffic.totals), lastTraffic_(sample.traffic.totals), taskBaseline_(tasksOf(sample).rows),
      taskGeneration_(tasksOf(sample).generation), lastCompleted_(tasksOf(sample).completed), lastTaskGeneration_(tasksOf(sample).generation),
      takenCompleted_(tasksOf(sample).completed) {}

bool Window::hasTokenIncrement(const Sample& sample) const {
    return sample.facts.counters->hasTokensSince(sample.facts.epoch == lastEpoch_ ? *lastCounters_ : *zeroCounters());
}

bool Window::hasIncrement(const Sample& sample, bool traffic) const {
    const auto& counters = sample.facts.counters;
    const bool usage = sample.facts.epoch != lastEpoch_ ? !counters->byModel().empty()
                                                       : counters != lastCounters_ && tasksOrUsageSince(*counters, *lastCounters_);
    const auto& tasks = tasksOf(sample);
    const bool completed = tasks.generation != lastTaskGeneration_ ? tasks.completed > 0 : tasks.completed > lastCompleted_;
    return usage || completed || (traffic && !sample.traffic.agentBytes(lastTraffic_).empty());
}

void Window::discard(const Sample& sample, bool keepingTick) {
    if (keepingTick && sample.facts.epoch == lastEpoch_) {
        baseline_ = lastCounters_;
        baselineEpoch_ = lastEpoch_;
        trafficBaseline_ = lastTraffic_;
    } else if (keepingTick) {
        baseline_ = zeroCounters();
        baselineEpoch_ = sample.facts.epoch;
        trafficBaseline_ = lastTraffic_;
    } else {
        baseline_ = sample.facts.counters;
        baselineEpoch_ = sample.facts.epoch;
        trafficBaseline_ = sample.traffic.totals;
    }
    peak_ = {};
    peakCost_ = 0;
    peakRate_.reset();
}

void Window::observe(const Sample& sample, bool peaks) {
    if (peaks) {
        peak_ = {std::max(peak_.output, sample.live.output), std::max(peak_.total, sample.live.total)};
        peakCost_ = std::max(peakCost_, sample.liveCost);
        peakRate_ = larger(peakRate_, sample.traffic.status == TrafficStatus::Ok ? sample.traffic.rate : std::nullopt);
    }
    lastCounters_ = sample.facts.counters;
    lastEpoch_ = sample.facts.epoch;
    lastTraffic_ = sample.traffic.totals;
    lastCompleted_ = tasksOf(sample).completed;
    lastTaskGeneration_ = tasksOf(sample).generation;
}

WindowContent Window::content(const Sample& sample) const {
    const auto& baseline = sample.facts.epoch == baselineEpoch_ ? *baseline_ : *zeroCounters();
    WindowContent out;
    out.usage = sample.facts.counters->since(baseline);
    out.agentBytes = sample.traffic.agentBytes(trafficBaseline_);
    out.peak = {std::max(peak_.output, sample.live.output), std::max(peak_.total, sample.live.total)};
    out.peakCost = std::max(peakCost_, sample.liveCost);
    out.peakRate = larger(peakRate_, sample.traffic.status == TrafficStatus::Ok ? sample.traffic.rate : std::nullopt);
    // Task rows since the previous build; discards leave them alone (METRICS 11.10 Task ledger).
    const auto& tasks = tasksOf(sample);
    for (const auto& [key, row] : *tasks.rows) {
        TaskRow change = row;
        if (tasks.generation == taskGeneration_ && taskBaseline_) {
            if (const auto it = taskBaseline_->find(key); it != taskBaseline_->end()) change = row - it->second;
        }
        if (!change.zero()) out.tasks[key] = change;
    }
    return out;
}

WindowContent Window::take(const Sample& sample) {
    auto taken = content(sample);
    discard(sample, false);
    observe(sample, false);
    taskBaseline_ = tasksOf(sample).rows;
    taskGeneration_ = tasksOf(sample).generation;
    takenCompleted_ = tasksOf(sample).completed;
    return taken;
}

double TitleTracker::enterSpeed(int level, double redline) {
    static constexpr std::array kFractions = {0.02, 0.03, 0.04, 0.06, 0.08, 0.12, 0.17, 0.24, 0.34, 0.5, 0.7, 1.0};
    if (level <= 0) return 0;
    if (level == 1) return 0.5;
    return kFractions[static_cast<std::size_t>(std::min(level, kMaxLevel) - 2)] * redline;
}

void TitleTracker::update(double value, double redline, double now) {
    value = std::isfinite(value) ? std::max(0.0, value) : 0.0;
    const auto next = rawLevel(value, redline, now);
    if (next != candidate_) {
        candidate_ = next;
        candidateSince_ = now;
    }
    if (candidate_ != level_ && now - candidateSince_ >= 0.8) level_ = candidate_;
}

double TitleTracker::heat(double value, double redline) {
    return value > 0 && redline > 0 && std::isfinite(value) ? std::min(1.0, std::pow(value / redline, 0.6)) : 0;
}

int TitleTracker::rawLevel(double value, double redline, double now) {
    if (value < 0.5) {
        if (!belowSince_) belowSince_ = now;
    } else {
        belowSince_.reset();
    }
    if (belowSince_ && now - *belowSince_ >= 3.0) return 0;
    auto step = candidate_ == 0 ? (value >= 0.5 ? 1 : 0) : candidate_;
    if (step == 0) return 0;
    while (step < kMaxLevel && value >= enterSpeed(step + 1, redline)) ++step;
    while (step > 1 && value < enterSpeed(step, redline) * 0.9) --step;
    return step;
}

}  // namespace agentspeed::report
