#include "agentspeed/perf_history.hpp"

#include <algorithm>
#include <cmath>

#include "agentspeed/pricing.hpp"

namespace agentspeed::perf {

namespace {

constexpr std::int64_t kToolFloorMs = 50;
constexpr std::int64_t kDayMs = 86'400'000;

__extension__ typedef __int128 Wide;

std::array<std::int64_t, kBins + 1> makeEdges() {
    std::array<std::int64_t, kBins + 1> edges{};
    for (int j = 0; j <= kBins; ++j) {
        // The ceiling of the 4th root of 10^8 · 2^j, exactly.
        const Wide target = static_cast<Wide>(100'000'000) << j;
        auto c = static_cast<std::int64_t>(std::ceil(100 * std::pow(2.0, j / 4.0)));
        const auto fourth = [](std::int64_t v) {
            const Wide x = v;
            return x * x * x * x;
        };
        while (fourth(c) < target) ++c;
        while (c > 0 && fourth(c - 1) >= target) --c;
        edges[static_cast<std::size_t>(j)] = c;
    }
    return edges;
}

const std::array<std::int64_t, kBins + 1>& edges() {
    static const auto table = makeEdges();
    return table;
}

std::int64_t durationMs(const engine::Response& r) {
    const auto span = engine::interval(r);
    return span->second - span->first;
}

std::optional<std::int64_t> dayNumber(const std::string& day) {
    const auto ms = pricing::UtcDay::instantMs(day);
    if (!ms) return std::nullopt;
    return *ms / kDayMs;
}

std::size_t sourceRank(SourceKind source) { return static_cast<std::size_t>(source); }

}  // namespace

std::int64_t latencyEdge(int j) { return edges()[static_cast<std::size_t>(j)]; }

int latencyBin(std::int64_t ms) {
    int k = 0;
    for (int j = 1; j < kBins; ++j) {
        if (ms >= edges()[static_cast<std::size_t>(j)]) ++k;
    }
    return k;
}

double binCentre(int k) { return 100 * std::pow(2.0, (2.0 * k + 1) / 8); }

void Record::add(const Record& o) {
    n += o.n;
    outputSum += o.outputSum;
    durationMsSum += o.durationMsSum;
    for (std::size_t k = 0; k < latencyBins.size(); ++k) latencyBins[k] += o.latencyBins[k];
    reasonN += o.reasonN;
    reasonOutput += o.reasonOutput;
    reasonDurationMsSum += o.reasonDurationMsSum;
    cacheN += o.cacheN;
    cacheInput += o.cacheInput;
    cacheCached += o.cacheCached;
    toolN += o.toolN;
    toolLnSum = toolLnSum + o.toolLnSum;
    toolFailed += o.toolFailed;
    activeMs += o.activeMs;
}

Record& Recorder::record(const std::string& day, SourceKind source, const std::string& model) {
    return records_[{day, source, model}];
}

void Recorder::active(const std::string& agentId, SourceKind source, const std::string& model, std::int64_t start, std::int64_t end) {
    const auto key = std::make_pair(agentId, model);
    const auto it = covered_.find(key);
    const auto added = std::max<std::int64_t>(0, end - (it == covered_.end() ? start : std::max(start, it->second)));
    if (it == covered_.end()) {
        covered_.emplace(key, end);
    } else {
        it->second = std::max(it->second, end);
    }
    if (added == 0) return;
    if (const auto day = dayOf_(end)) record(*day, source, model).activeMs += added;
}

void Recorder::response(const std::string& agentId, SourceKind source, const engine::Response& r) {
    const auto span = engine::interval(r);
    if (!span || !r.final) return;
    const auto day = dayOf_(r.endMs);
    if (!day) return;
    const auto model = r.model.value_or("");
    auto& rec = record(*day, source, model);
    const auto duration = durationMs(r);
    ++rec.n;
    rec.outputSum += r.output;
    rec.durationMsSum += duration;
    if (r.firstMs && r.startMs && *r.firstMs >= *r.startMs) ++rec.latencyBins[static_cast<std::size_t>(latencyBin(*r.firstMs - *r.startMs))];
    const auto thought = std::min(r.reasoning, r.output);
    if (recordsReasoning(source) && thought > 0) {
        ++rec.reasonN;
        rec.reasonOutput += thought;
        rec.reasonDurationMsSum += duration;
    }
    if (recordsCache(source) && r.input > 0) {
        ++rec.cacheN;
        rec.cacheInput += r.input;
        rec.cacheCached += r.cachedInput;
    }
    active(agentId, source, model, span->first, span->second);
}

void Recorder::action(const std::string& agentId, SourceKind source, const work::Action& a) {
    if (!a.resultMs || *a.resultMs - a.callMs < 0 || *a.resultMs - a.callMs > kToolMaxMs) return;
    const auto day = dayOf_(*a.resultMs);
    if (!day) return;
    const auto model = a.model.value_or("");
    auto& rec = record(*day, source, model);
    ++rec.toolN;
    rec.toolLnSum = rec.toolLnSum + std::log(static_cast<double>(std::max(*a.resultMs - a.callMs, kToolFloorMs)));
    if (a.failed == true) ++rec.toolFailed;
    active(agentId, source, model, a.callMs, *a.resultMs);
}

std::vector<DayRecord> Recorder::rows() const {
    std::vector<DayRecord> out;
    out.reserve(records_.size());
    for (const auto& [key, rec] : records_) out.push_back({std::get<0>(key), std::get<1>(key), std::get<2>(key), rec});
    std::sort(out.begin(), out.end(), [](const DayRecord& a, const DayRecord& b) {
        return std::tie(a.day, a.source, a.model) < std::tie(b.day, b.source, b.model);
    });
    return out;
}

Pooled pooled(const std::vector<const Record*>& records, SourceKind source) {
    Record sum;
    for (const auto* r : records) sum.add(*r);
    return pooled(sum, source);
}

Pooled pooled(const Record& s, SourceKind source) {
    Measures m;
    const auto set = [&](Id id, std::optional<double> measure, std::int64_t samples) {
        m.measure[static_cast<std::size_t>(id)] = measure;
        m.samples[static_cast<std::size_t>(id)] = samples;
    };
    set(Id::T, s.n >= kMinSamples ? std::optional<double>(static_cast<double>(s.outputSum) / (static_cast<double>(s.durationMsSum) / 1000)) : std::nullopt,
        s.n);
    std::int64_t count = 0;
    for (const auto c : s.latencyBins) count += c;
    std::optional<double> latency;
    if (count >= kMinSamples) {
        const auto target = (count + 1) / 2;
        std::int64_t seen = 0;
        for (int k = 0; k < kBins; ++k) {
            seen += s.latencyBins[static_cast<std::size_t>(k)];
            if (seen >= target) {
                latency = binCentre(k);
                break;
            }
        }
    }
    set(Id::L, latency, count);
    set(Id::R,
        s.reasonN >= kMinSamples ? std::optional<double>(static_cast<double>(s.reasonOutput) / (static_cast<double>(s.reasonDurationMsSum) / 1000))
                                 : std::nullopt,
        s.reasonN);
    set(Id::H, s.cacheN >= kMinSamples ? std::optional<double>(static_cast<double>(s.cacheCached) / static_cast<double>(s.cacheInput)) : std::nullopt,
        s.cacheN);
    const auto k = s.toolN;
    if (k >= kMinSamples) {
        set(Id::N, s.activeMs > 0 ? std::optional<double>(static_cast<double>(k) / (static_cast<double>(s.activeMs) / 60'000)) : std::nullopt, k);
        set(Id::U, std::exp(s.toolLnSum / static_cast<double>(k)), k);
        set(Id::S, 1 - static_cast<double>(s.toolFailed) / static_cast<double>(k), k);
    } else {
        for (const auto id : {Id::N, Id::U, Id::S}) set(id, std::nullopt, k);
    }
    auto [components, score] = combine(source, m);
    if (s.n < kPooledMinN) score.reset();
    Pooled out;
    out.n = s.n;
    out.tools = k;
    out.activeMs = s.activeMs;
    out.score = score;
    if (score) out.shown = roundHalfAway(*score);
    out.components = components;
    return out;
}

std::string dayMinus(const std::string& day, int days) {
    const auto number = dayNumber(day);
    if (!number) return day;
    return pricing::UtcDay((*number - days) * kDayMs).label();
}

std::optional<double> baseline(const std::vector<DayRecord>& records, SourceKind source, const std::string& model,
                               const std::string& today) {
    const auto first = dayMinus(today, kBaselineDays), last = dayMinus(today, 1);
    std::vector<const Record*> chosen;
    for (const auto& r : records) {
        if (r.source == source && r.model == model && first <= r.day && r.day <= last) chosen.push_back(&r.record);
    }
    return pooled(chosen, source).score;
}

std::vector<RangeRow> rangeValues(const std::vector<DayRecord>& records, const std::string& first, const std::string& last,
                                  std::optional<SourceKind> source) {
    std::map<std::pair<SourceKind, std::string>, std::vector<const DayRecord*>> groups;
    for (const auto& r : records) {
        if (first <= r.day && r.day <= last && (!source || r.source == *source)) groups[{r.source, r.model}].push_back(&r);
    }
    std::vector<std::string> days;
    for (auto d = first; d <= last; d = dayMinus(d, -1)) {
        days.push_back(d);
        if (days.size() > 4000) break;
    }
    std::vector<RangeRow> rows;
    for (const auto& [key, part] : groups) {
        RangeRow row;
        row.source = key.first;
        row.model = key.second;
        std::vector<const Record*> all;
        for (const auto* r : part) all.push_back(&r->record);
        row.pooled = pooled(all, key.first);
        for (const auto& day : days) {
            std::vector<const Record*> ofDay;
            for (const auto* r : part) {
                if (r->day == day) ofDay.push_back(&r->record);
            }
            row.trend.emplace_back(day, pooled(ofDay, key.first).score);
        }
        rows.push_back(std::move(row));
    }
    std::sort(rows.begin(), rows.end(), [](const RangeRow& a, const RangeRow& b) {
        if (a.source != b.source) return sourceRank(a.source) < sourceRank(b.source);
        if (a.pooled.n != b.pooled.n) return a.pooled.n > b.pooled.n;
        return a.model < b.model;
    });
    return rows;
}

}  // namespace agentspeed::perf
