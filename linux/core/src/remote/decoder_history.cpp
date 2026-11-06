#include <limits>

#include "fields.hpp"

// REMOTE 5.4: the history lines, their aggregate and performance record, and the stored texts of both (8.3).
namespace agentspeed::remote::detail {

namespace {

constexpr ModeKind kModeKinds[] = {ModeKind::ServiceTier, ModeKind::Accelerator, ModeKind::ModelVariant, ModeKind::Routing,
                                   ModeKind::Workflow,    ModeKind::Effort,      ModeKind::Speed};

// A fixed-length JSON array row of the aggregate encoding.
class Row {
public:
    Row(const json::Value& value, std::uint32_t size, std::string name) : name_(std::move(name)) {
        if (!value.isArray()) fail(LineError::Kind::WrongType, name_);
        if (value.size() != size) fail(LineError::Kind::OutOfRange, name_);
        value_ = value;
    }
    std::int64_t count(std::uint32_t i) const {
        const auto v = checkedInteger(value_.at(i), name_);
        if (v < 0) fail(LineError::Kind::OutOfRange, name_);
        return v;
    }
    std::optional<std::int64_t> optionalCount(std::uint32_t i) const {
        if (value_.at(i).isNull()) return std::nullopt;
        return count(i);
    }
    std::optional<std::int64_t> optionalInteger(std::uint32_t i) const {
        if (value_.at(i).isNull()) return std::nullopt;
        return checkedInteger(value_.at(i), name_);
    }
    bool boolean(std::uint32_t i) const {
        if (!value_.at(i).isBool()) fail(LineError::Kind::WrongType, name_);
        return value_.at(i).isTrue();
    }
    std::string string(std::uint32_t i, std::size_t maxBytes, TextRule rule) const { return checkedString(value_.at(i), name_, maxBytes, rule); }
    std::optional<std::string> optionalString(std::uint32_t i, std::size_t maxBytes, TextRule rule) const {
        if (value_.at(i).isNull()) return std::nullopt;
        return string(i, maxBytes, rule);
    }

private:
    json::Value value_;
    std::string name_;
};

}  // namespace

history::DayAggregate aggregate(const Fields& f) {
    history::DayAggregate d;
    const auto models = f.members("models");
    if (models.size() > limits::kHistoryList) fail(LineError::Kind::TooMany, "aggregate.models");
    for (const auto& [model, value] : models.members()) {
        if (model.size() > limits::kModelBytes || !allows(TextRule::Model, model)) fail(LineError::Kind::BadCharacter, "aggregate.models");
        const Row row(value, 6, "aggregate.models");
        history::ModelTotals t;
        t.requests = row.count(0);
        t.output = row.count(1);
        t.measuredOutput = row.count(2);
        t.measuredMs = row.count(3);
        t.costMicroUsd = row.optionalInteger(4);
        t.unpricedRequests = row.optionalCount(5);
        d.models[model] = t;
    }
    for (const auto& item : f.array("unpriced", limits::kHistoryList)) {
        const Row row(item, 5, "aggregate.unpriced");
        history::UnpricedFact fact;
        fact.model = row.string(0, limits::kModelBytes, TextRule::Model);
        fact.provider = row.optionalString(1, limits::kProviderBytes, TextRule::Model);
        fact.fast = row.boolean(2);
        fact.noDate = row.boolean(3);
        auto& count = d.unpriced[fact];
        count = std::min(count + row.count(4), kIntegerLimit);
    }
    for (const auto& item : f.array("modes", limits::kHistoryList)) {
        const Row row(item, 9, "aggregate.modes");
        const auto model = row.string(0, limits::kModelBytes, TextRule::Model);
        const auto kindText = row.string(1, 32, TextRule::Identifier);
        const auto mode = row.string(2, 64, TextRule::Identifier);
        const auto evidence = row.string(3, 64, TextRule::Identifier);
        const auto provider = row.optionalString(4, 32, TextRule::Identifier);
        history::ModeTotals totals;
        totals.requests = row.count(5);
        totals.output = row.count(6);
        totals.measuredOutput = row.count(7);
        totals.measuredMs = row.count(8);
        std::optional<ModeKind> kind;
        for (const auto k : kModeKinds) {
            if (modeKindName(k) == kindText) kind = k;
        }
        // A kind of a newer version is skipped, as the store's decoder does.
        if (!kind) continue;
        if (!ExecutionModes::isIdentifier(mode, 64) || !ExecutionModes::isIdentifier(evidence, 64) ||
            (provider && !ExecutionModes::isIdentifier(*provider, 32))) {
            fail(LineError::Kind::BadCharacter, "aggregate.modes");
        }
        d.modes[history::ModeKey{model, *kind, mode, evidence, provider}] = totals;
    }
    d.requests = f.count("requests");
    d.input = f.count("input");
    d.cachedInput = f.count("cachedInput");
    d.cacheWriteInput = f.count("cacheWriteInput");
    d.output = f.count("output");
    d.reasoning = f.count("reasoning");
    d.measuredOutput = f.count("measuredOutput");
    d.measuredMs = f.count("measuredMs");
    d.unfinalized = f.count("unfinalized");
    d.agents = static_cast<int>(f.count("agents", std::numeric_limits<int>::max()));
    d.costMicroUsd = f.optionalInteger("costMicroUsd");
    d.unpricedRequests = f.optionalInteger("unpricedRequests");
    if (d.unpricedRequests && *d.unpricedRequests < 0) fail(LineError::Kind::OutOfRange, "unpricedRequests");
    return d;
}

perf::Record perfRecord(const Fields& f) {
    perf::Record r;
    const auto bins = f.array("latencyBins", perf::kBins);
    if (bins.size() != perf::kBins) fail(LineError::Kind::OutOfRange, "record.latencyBins");
    for (std::size_t k = 0; k < bins.size(); ++k) {
        const auto value = checkedInteger(bins[k], "record.latencyBins");
        if (value < 0) fail(LineError::Kind::OutOfRange, "record.latencyBins");
        r.latencyBins[k] = value;
    }
    r.n = f.count("n");
    r.outputSum = f.count("outputSum");
    r.durationMsSum = f.count("durationMsSum");
    r.reasonN = f.count("reasonN");
    r.reasonOutput = f.count("reasonOutput");
    r.reasonDurationMsSum = f.count("reasonDurationMsSum");
    r.cacheN = f.count("cacheN");
    r.cacheInput = f.count("cacheInput");
    r.cacheCached = f.count("cacheCached");
    r.toolN = f.count("toolN");
    r.toolLnSum = f.number("toolLnSum");
    r.toolFailed = f.count("toolFailed");
    r.activeMs = f.count("activeMs");
    return r;
}

HistoryDay historyDay(const Fields& f) {
    HistoryDay day;
    const auto object = f.object("aggregate");
    day.aggregate = aggregate(object);
    day.aggregateJson = std::string(object.value().raw());
    day.day = f.day("day");
    day.source = f.source("source");
    day.digest = f.hex("digest", 16);
    return day;
}

HistoryPerf historyPerf(const Fields& f) {
    HistoryPerf perf;
    const auto object = f.object("record");
    perf.record = perfRecord(object);
    perf.recordJson = std::string(object.value().raw());
    perf.day = f.day("day");
    perf.source = f.source("source");
    perf.model = f.string("model", limits::kModelBytes, TextRule::Model);
    return perf;
}

}  // namespace agentspeed::remote::detail

namespace agentspeed::remote {

namespace {

template <typename T, typename Decode>
std::optional<T> decodeStored(std::string_view json, Decode decode) {
    json::Document document;
    if (!document.parse(json, detail::kMaxLineDepth) || !document.root().isObject()) return std::nullopt;
    try {
        return decode(detail::Fields(document.root(), ""));
    } catch (const LineError&) {
        return std::nullopt;
    }
}

}  // namespace

std::optional<history::DayAggregate> decodeAggregateText(std::string_view json) {
    return decodeStored<history::DayAggregate>(json, [](const detail::Fields& f) { return detail::aggregate(f); });
}

std::optional<perf::Record> decodePerfRecordText(std::string_view json) {
    return decodeStored<perf::Record>(json, [](const detail::Fields& f) { return detail::perfRecord(f); });
}

}  // namespace agentspeed::remote
