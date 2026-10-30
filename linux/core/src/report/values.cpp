#include <algorithm>

#include "agentspeed/engine.hpp"
#include "agentspeed/report_values.hpp"

namespace agentspeed::report {

Usage& Usage::operator+=(const Usage& o) {
    requests += o.requests;
    output += o.output;
    input += o.input;
    cachedInput += o.cachedInput;
    cacheWriteInput += o.cacheWriteInput;
    reasoning += o.reasoning;
    measuredOutput += o.measuredOutput;
    measuredMs += o.measuredMs;
    costMicroUsd += o.costMicroUsd;
    unpricedRequests += o.unpricedRequests;
    return *this;
}

Usage operator-(const Usage& a, const Usage& b) {
    const auto step = [](std::int64_t now, std::int64_t before) { return std::max<std::int64_t>(0, now - before); };
    return {step(a.requests, b.requests),       step(a.output, b.output),
            step(a.input, b.input),             step(a.cachedInput, b.cachedInput),
            step(a.cacheWriteInput, b.cacheWriteInput), step(a.reasoning, b.reasoning),
            step(a.measuredOutput, b.measuredOutput),   step(a.measuredMs, b.measuredMs),
            step(a.costMicroUsd, b.costMicroUsd),       step(a.unpricedRequests, b.unpricedRequests)};
}

Usage Counters::total() const {
    Usage total;
    for (const auto& [_, usage] : byModel_) total += usage;
    return total;
}

Counters Counters::since(const Counters& baseline) const {
    Counters delta;
    for (const auto& [key, value] : byModel_) {
        const auto it = baseline.byModel_.find(key);
        const auto change = it == baseline.byModel_.end() ? value : value - it->second;
        if (!change.zero()) delta.byModel_[key] = change;
    }
    for (const auto& [key, value] : byMode_) {
        const auto it = baseline.byMode_.find(key);
        const auto change = it == baseline.byMode_.end() ? value : value - it->second;
        if (!change.zero()) delta.byMode_[key] = change;
    }
    return delta;
}

bool Counters::hasTokensSince(const Counters& baseline) const {
    for (const auto& [key, value] : byModel_) {
        const auto it = baseline.byModel_.find(key);
        if ((it == baseline.byModel_.end() ? value : value - it->second).hasTokens()) return true;
    }
    return false;
}

void Counters::add(const Usage& usage, const ModelKey& key, const ExecutionModes* modes) {
    if (usage.zero()) return;
    byModel_[key] += usage;
    static const ExecutionModes none;
    // The newest version's kinds, the effort included: the ledger freezes them all and a report sends those its service
    // negotiated (EXECUTION-MODES, Report extension).
    for (const auto kind : {ModeKind::ServiceTier, ModeKind::Accelerator, ModeKind::ModelVariant, ModeKind::Routing, ModeKind::Workflow, ModeKind::Effort}) {
        const auto mode = (modes ? *modes : none).attribute(kind);
        byMode_[{key.modelId, key.source, kind, mode.mode, mode.evidence, mode.provider}] += usage;
    }
    ++version_;
}

Ledger::Reported Ledger::valuesOf(const ParsedResponse& response, std::optional<std::int64_t> costMicroUsd) {
    engine::Response e;
    e.startMs = response.startMs;
    e.endMs = response.endMs;
    e.output = response.tokens.output;
    e.final = response.final;
    const auto measured = response.final ? engine::interval(e) : std::nullopt;
    Reported out;
    auto& u = out.usage;
    u.requests = response.requestCount;
    u.output = response.tokens.output;
    u.input = response.tokens.input;
    u.cachedInput = response.tokens.cachedInput;
    u.cacheWriteInput = response.tokens.cacheWriteInput;
    u.reasoning = response.tokens.reasoning;
    u.measuredOutput = measured ? response.tokens.output : 0;
    u.measuredMs = measured ? measured->second - measured->first : 0;
    u.costMicroUsd = costMicroUsd.value_or(0);
    u.unpricedRequests = costMicroUsd ? 0 : response.requestCount;
    out.endMs = response.endMs;
    out.modes = response.modes;
    return out;
}

void Ledger::count(const ParsedResponse& response, std::optional<std::int64_t> costMicroUsd, const std::string& modelId) {
    if (counted_.contains(response.key)) {
        grow(response, costMicroUsd, modelId);
        return;
    }
    const auto values = valuesOf(response, costMicroUsd);
    counters_.add(values.usage, {modelId, response.source}, &values.modes);
    counted_[response.key] = values;
    order_.push_back(response.key);
    while (counted_.size() > kMaxCountedKeys && !order_.empty()) {
        counted_.erase(order_.front());
        order_.pop_front();
    }
}

void Ledger::grow(const ParsedResponse& response, std::optional<std::int64_t> costMicroUsd, const std::string& modelId) {
    const auto it = counted_.find(response.key);
    if (it == counted_.end()) return;
    auto& reported = it->second;
    const auto now = valuesOf(response, costMicroUsd);
    const auto added = now.usage - reported.usage;
    auto& r = reported.usage;
    const auto& n = now.usage;
    r = {std::max(r.requests, n.requests),       std::max(r.output, n.output),
         std::max(r.input, n.input),             std::max(r.cachedInput, n.cachedInput),
         std::max(r.cacheWriteInput, n.cacheWriteInput), std::max(r.reasoning, n.reasoning),
         std::max(r.measuredOutput, n.measuredOutput),   std::max(r.measuredMs, n.measuredMs),
         std::max(r.costMicroUsd, n.costMicroUsd),       std::max(r.unpricedRequests, n.unpricedRequests)};
    reported.endMs = std::max(reported.endMs, now.endMs);
    counters_.add(added, {modelId, response.source}, &reported.modes);
}

void Ledger::prune(std::int64_t cutoffMs) {
    bool removed = false;
    for (auto it = counted_.begin(); it != counted_.end();) {
        if (it->second.endMs < cutoffMs) {
            it = counted_.erase(it);
            removed = true;
        } else {
            ++it;
        }
    }
    if (removed) std::erase_if(order_, [&](const std::string& key) { return !counted_.contains(key); });
}

}  // namespace agentspeed::report
