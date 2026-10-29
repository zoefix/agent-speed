#include "agentspeed/perf.hpp"

#include <algorithm>
#include <cmath>

#include "agentspeed/text.hpp"

namespace agentspeed::perf {

namespace {

constexpr std::array<double, 7> kWeights{0.30, 0.20, 0.15, 0.10, 0.10, 0.10, 0.05};
constexpr double kTFloor = 0.01, kLow = 0.05, kHigh = 20.0;
constexpr std::int64_t kStaleMs = 300'000;
constexpr std::int64_t kToolFloorMs = 50;

std::size_t index(Id id) { return static_cast<std::size_t>(id); }

double ratioOf(Id id, double m) {
    switch (id) {
        case Id::T: return m / 50;
        case Id::L: return 5000 / std::max(m, 250.0);
        case Id::N: return m / 3;
        case Id::R: return m / 20;
        case Id::H: return m / 0.95;
        case Id::U: return 400 / m;
        case Id::S: return m / 0.95;
    }
    return m;
}

double clamp(Id id, double ratio) { return id == Id::T ? std::max(kTFloor, ratio) : std::min(kHigh, std::max(kLow, ratio)); }

std::u16string utf16(std::string_view s) {
    std::u16string out;
    for (std::size_t i = 0; i < s.size();) {
        const auto cp = text::nextCodePoint(s, i);
        if (cp < 0x10000) {
            out.push_back(static_cast<char16_t>(cp));
        } else {
            out.push_back(static_cast<char16_t>(0xD800 + ((cp - 0x10000) >> 10)));
            out.push_back(static_cast<char16_t>(0xDC00 + ((cp - 0x10000) & 0x3FF)));
        }
    }
    return out;
}

std::int64_t durationMs(const engine::Response& r) {
    const auto span = engine::interval(r);
    return span->second - span->first;
}

bool measured(const engine::Response& r) { return r.final && engine::interval(r).has_value(); }

std::int64_t unionLength(std::vector<std::pair<std::int64_t, std::int64_t>> intervals) {
    std::sort(intervals.begin(), intervals.end());
    std::int64_t total = 0;
    std::optional<std::pair<std::int64_t, std::int64_t>> current;
    for (const auto& [start, end] : intervals) {
        if (end <= start) continue;
        if (!current || start > current->second) {
            if (current) total += current->second - current->first;
            current = {start, end};
        } else {
            current->second = std::max(current->second, end);
        }
    }
    return total + (current ? current->second - current->first : 0);
}

double median(std::vector<std::int64_t> values) {
    std::sort(values.begin(), values.end());
    const auto mid = values.size() / 2;
    if (values.size() % 2) return static_cast<double>(values[mid]);
    return static_cast<double>(values[mid - 1] + values[mid]) / 2;
}

}  // namespace

std::string_view idName(Id id) {
    static constexpr std::array<std::string_view, 7> kNames{"T", "L", "N", "R", "H", "U", "S"};
    return kNames[index(id)];
}

std::string_view reasonId(Reason reason) {
    switch (reason) {
        case Reason::NotRecorded: return "notRecorded";
        case Reason::FewSamples: return "fewSamples";
        case Reason::None: break;
    }
    return {};
}

std::string_view formId(Form form) {
    switch (form) {
        case Form::Live: return "live";
        case Form::Resting: return "resting";
        case Form::None: break;
    }
    return "none";
}

bool recordsLatency(SourceKind source) {
    return source == SourceKind::Codex || source == SourceKind::ClaudeCode || source == SourceKind::ClaudeDesktop;
}
bool recordsReasoning(SourceKind source) { return source != SourceKind::Deepseek; }
bool recordsCache(SourceKind source) { return source != SourceKind::Hermes; }

bool notRecorded(Id id, SourceKind source) {
    switch (id) {
        case Id::L: return !recordsLatency(source);
        case Id::N:
        case Id::U:
        case Id::S: return !work::hasActions(source);
        case Id::R: return !recordsReasoning(source);
        case Id::H: return !recordsCache(source);
        case Id::T: break;
    }
    return false;
}

std::pair<Components, std::optional<double>> combine(SourceKind source, const Measures& measures) {
    Components components;
    double logSum = 0, weights = 0;
    for (const auto id : kIds) {
        auto& c = components[index(id)];
        c.id = id;
        c.samples = measures.samples[index(id)];
        const auto& m = measures.measure[index(id)];
        if (!m) {
            c.reason = notRecorded(id, source) ? Reason::NotRecorded : Reason::FewSamples;
            continue;
        }
        const double ratio = clamp(id, ratioOf(id, *m));
        c.present = true;
        c.measure = *m;
        c.ratio = ratio;
        c.subScore = 1000 * ratio;
        logSum = logSum + kWeights[index(id)] * std::log(ratio);
        weights = weights + kWeights[index(id)];
    }
    std::optional<double> score;
    if (weights > 0) score = 1000 * std::exp(logSum / weights);
    return {components, score};
}

std::int64_t roundHalfAway(double value) {
    return value >= 0 ? static_cast<std::int64_t>(std::floor(value + 0.5)) : -static_cast<std::int64_t>(std::floor(-value + 0.5));
}

bool ordinalLess(std::string_view a, std::string_view b) { return utf16(a) < utf16(b); }

Phase effectivePhase(Phase phase, std::optional<std::int64_t> lastLineMs, std::int64_t now) {
    if (phase != Phase::Idle && lastLineMs && now - *lastLineMs > kStaleMs) return Phase::Idle;
    return phase;
}

Evaluation evaluate(SourceKind source, Phase phase, std::optional<std::int64_t> lastLineMs,
                    const std::vector<const engine::Response*>& responses, const std::deque<work::Action>& actions, std::int64_t now) {
    std::vector<const engine::Response*> sample;
    for (const auto* r : responses) {
        if (measured(*r) && now - kWindowMs <= r->endMs && r->endMs <= now) sample.push_back(r);
    }
    std::sort(sample.begin(), sample.end(), [](const engine::Response* a, const engine::Response* b) {
        if (a->endMs != b->endMs) return a->endMs > b->endMs;
        return ordinalLess(a->key, b->key);
    });
    if (sample.size() > kSample) sample.resize(kSample);
    const auto n = static_cast<std::int64_t>(sample.size());

    Evaluation out;
    std::optional<std::int64_t> spanStart;
    std::vector<std::pair<std::int64_t, std::int64_t>> spans;
    for (const auto* r : sample) {
        const auto span = *engine::interval(*r);
        spans.push_back(span);
        spanStart = spanStart ? std::min(*spanStart, span.first) : span.first;
    }
    std::vector<const work::Action*> calls;
    if (n >= kMinSamples) {
        const auto from = actions.size() > work::ActionLog::kStored ? actions.size() - work::ActionLog::kStored : 0;
        for (auto i = from; i < actions.size(); ++i) {
            const auto& a = actions[i];
            if (a.resultMs && a.callMs >= *spanStart && *a.resultMs <= now && *a.resultMs - a.callMs >= 0 &&
                *a.resultMs - a.callMs <= kToolMaxMs) {
                calls.push_back(&a);
            }
        }
    }
    for (const auto* a : calls) spans.emplace_back(a->callMs, *a->resultMs);
    const auto active = unionLength(spans);

    Measures m;
    const auto set = [&](Id id, std::optional<double> measure, std::int64_t samples) {
        m.measure[index(id)] = measure;
        m.samples[index(id)] = samples;
    };
    if (n >= kMinSamples) {
        std::int64_t output = 0, duration = 0;
        std::vector<std::int64_t> latencies;
        std::int64_t reason = 0, reasonMs = 0, thought = 0, input = 0, cached = 0, withInput = 0;
        for (const auto* r : sample) {
            output += r->output;
            duration += durationMs(*r);
            if (r->firstMs && r->startMs && *r->firstMs >= *r->startMs) latencies.push_back(*r->firstMs - *r->startMs);
            if (const auto t = std::min(r->reasoning, r->output); t > 0) {
                ++thought;
                reason += t;
                reasonMs += durationMs(*r);
            }
            if (r->input > 0) {
                ++withInput;
                input += r->input;
                cached += r->cachedInput;
            }
        }
        set(Id::T, static_cast<double>(output) / (static_cast<double>(duration) / 1000), n);
        const auto latencyCount = static_cast<std::int64_t>(latencies.size());
        set(Id::L, latencyCount >= kMinSamples ? std::optional<double>(median(latencies)) : std::nullopt, latencyCount);
        if (recordsReasoning(source)) {
            set(Id::R, thought >= kMinSamples ? std::optional<double>(static_cast<double>(reason) / (static_cast<double>(reasonMs) / 1000)) : std::nullopt,
                thought);
        }
        if (recordsCache(source)) {
            set(Id::H, withInput >= kMinSamples ? std::optional<double>(static_cast<double>(cached) / static_cast<double>(input)) : std::nullopt,
                withInput);
        }
        const auto k = static_cast<std::int64_t>(calls.size());
        if (work::hasActions(source) && k >= kMinSamples) {
            set(Id::N, active > 0 ? std::optional<double>(static_cast<double>(k) / (static_cast<double>(active) / 60'000)) : std::nullopt, k);
            double logs = 0;
            std::int64_t failed = 0;
            for (const auto* a : calls) {
                logs = logs + std::log(static_cast<double>(std::max(*a->resultMs - a->callMs, kToolFloorMs)));
                if (a->failed == true) ++failed;
            }
            set(Id::U, std::exp(logs / static_cast<double>(k)), k);
            set(Id::S, 1 - static_cast<double>(failed) / static_cast<double>(k), k);
        } else {
            for (const auto id : {Id::N, Id::U, Id::S}) set(id, std::nullopt, k);
        }
    } else {
        set(Id::T, std::nullopt, n);
    }
    const auto [components, score] = combine(source, m);
    out.components = components;
    out.score = score;
    if (score) {
        out.shown = roundHalfAway(*score);
        out.form = effectivePhase(phase, lastLineMs, now) == Phase::Idle ? Form::Resting : Form::Live;
        out.provisional = n < kProvisionalBelow;
    }
    out.n = n;
    out.tools = static_cast<std::int64_t>(calls.size());
    out.activeMs = active;
    out.spanStartMs = spanStart;
    if (!sample.empty()) out.model = sample.front()->model;
    return out;
}

void applyBaseline(Evaluation& evaluation, std::optional<double> baseline) {
    evaluation.baseline.reset();
    evaluation.vsUsual.reset();
    evaluation.vsUsualPercent.reset();
    if (!evaluation.score || !baseline) return;
    evaluation.baseline = baseline;
    evaluation.vsUsual = *evaluation.score / *baseline - 1;
    evaluation.vsUsualPercent = roundHalfAway(100 * (*evaluation.score / *baseline - 1));
}

Highest highest(const std::vector<Valued>& agents) {
    for (const auto form : {Form::Live, Form::Resting}) {
        std::vector<const Valued*> candidates;
        for (const auto& a : agents) {
            if (a.form == form && a.score) candidates.push_back(&a);
        }
        if (candidates.empty()) continue;
        double best = *candidates.front()->score;
        for (const auto* a : candidates) best = std::max(best, *a->score);
        const Valued* pick = nullptr;
        for (const auto* a : candidates) {
            if (*a->score != best) continue;
            const auto last = a->lastLineMs.value_or(0), pickLast = pick ? pick->lastLineMs.value_or(0) : 0;
            if (!pick || last > pickLast || (last == pickLast && ordinalLess(a->id, pick->id))) pick = a;
        }
        return {form, best, roundHalfAway(best), pick->id};
    }
    return {};
}

}  // namespace agentspeed::perf
