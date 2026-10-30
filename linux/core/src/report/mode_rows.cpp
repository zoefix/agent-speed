#include "agentspeed/report_mode_rows.hpp"

#include <algorithm>
#include <set>

namespace agentspeed::report::mode_rows {

namespace {

constexpr std::int64_t kMaxInteger = 9'007'199'254'740'991;

bool valid(const Group& g) {
    if (!ExecutionModes::isIdentifier(g.mode, 64) || (g.provider && !ExecutionModes::isIdentifier(*g.provider, 32))) return false;
    if (g.mode == "unknown") return g.evidence == "unknown" && !g.provider;
    return g.evidence == "served" || g.evidence == "recorded" || g.evidence == "selected";
}

bool fits(const Counts& part, const Counts& remaining) {
    const auto ok = [](std::int64_t v, std::int64_t room) { return v >= 0 && v <= std::min(room, kMaxInteger); };
    return ok(part.requests, remaining.requests) && ok(part.output, remaining.output) && ok(part.measuredOutput, remaining.measuredOutput) &&
           ok(part.measuredMs, remaining.measuredMs);
}

Counts minus(const Counts& a, const Counts& b) {
    return {a.requests - b.requests, a.output - b.output, a.measuredOutput - b.measuredOutput, a.measuredMs - b.measuredMs};
}

std::string order(const Group& g) {
    return std::string(sourceId(g.source)) + "/" + std::string(modeKindName(g.kind)) + "/" + g.provider.value_or("") + "/" + g.mode + "/" + g.evidence;
}

}  // namespace

Counts& Counts::operator+=(const Counts& o) {
    requests += o.requests;
    output += o.output;
    measuredOutput += o.measuredOutput;
    measuredMs += o.measuredMs;
    return *this;
}

std::vector<ModeKind> reportKinds(int version) {
    std::vector<ModeKind> kinds{ModeKind::ServiceTier, ModeKind::Accelerator, ModeKind::ModelVariant, ModeKind::Routing, ModeKind::Workflow};
    if (version == 2) kinds.push_back(ModeKind::Effort);
    return version == 1 || version == 2 ? kinds : std::vector<ModeKind>{};
}

std::optional<std::vector<Row>> build(const std::map<SourceKind, Counts>& sources, const std::map<Group, Counts>& groups, int version) {
    const auto reported = reportKinds(version);
    if (reported.empty()) return std::nullopt;
    std::set<ModeKind> observed;
    for (const auto& [g, _] : groups)
        if (g.mode != "unknown") observed.insert(g.kind);
    std::map<Group, Counts> complete;
    for (const auto kind : reported) {
        if (!observed.contains(kind) && kind != ModeKind::ServiceTier && kind != ModeKind::Effort) continue;
        for (const auto& [source, total] : sources) {
            Counts remaining = total;
            for (const auto& [g, part] : groups) {
                if (g.source != source || g.kind != kind) continue;
                if (!valid(g) || !fits(part, remaining)) return std::nullopt;
                remaining = minus(remaining, part);
                complete[g] += part;
            }
            if (remaining.any()) complete[Group{source, kind, "unknown", "unknown", std::nullopt}] += remaining;
        }
    }
    std::vector<Row> rows;
    for (const auto& [g, counts] : complete)
        if (counts.any()) rows.push_back({g, counts});
    std::sort(rows.begin(), rows.end(), [](const Row& a, const Row& b) { return order(a.key) < order(b.key); });
    if (static_cast<int>(rows.size()) <= kMaxRows) return rows;
    if (version != 2) return std::nullopt;
    std::map<Group, Counts> withoutEffort;
    for (const auto& [g, counts] : groups)
        if (g.kind != ModeKind::Effort) withoutEffort[g] = counts;
    return build(sources, withoutEffort, 1);
}

}  // namespace agentspeed::report::mode_rows
