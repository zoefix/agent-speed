#pragma once

#include <compare>
#include <cstdint>
#include <map>
#include <optional>
#include <string>
#include <vector>

#include "agentspeed/model.hpp"
#include "agentspeed/modes.hpp"

// One model's `models[].modeUsage` (EXECUTION-MODES, Report extension; `mode_rows` of execution_modes_reference.py):
// every kind of the negotiated version that is observed, plus `serviceTier` and (version 2) `effort`, complete per
// source with an unknown bucket, ordered by `source/kind/provider/mode/evidence`. Over 64 rows version 2 leaves `effort`
// out first; nullopt when the breakdown still does not fit or cannot be complete.
namespace agentspeed::report::mode_rows {

constexpr int kMaxRows = 64;
// The execution-modes versions this client knows; any other number is not advertised.
constexpr int kNewestVersion = 2;

struct Counts {
    std::int64_t requests = 0, output = 0, measuredOutput = 0, measuredMs = 0;
    bool operator==(const Counts&) const = default;
    bool any() const { return requests || output || measuredOutput || measuredMs; }
    Counts& operator+=(const Counts& o);
};

// A frozen attribution of one source's responses.
struct Group {
    SourceKind source = SourceKind::Codex;
    ModeKind kind = ModeKind::ServiceTier;
    std::string mode, evidence;
    std::optional<std::string> provider;
    auto operator<=>(const Group&) const = default;
};

struct Row {
    Group key;
    Counts counts;
};

// The kinds a report carries under `version`: version 1 the five service kinds, version 2 also `effort`; none else.
std::vector<ModeKind> reportKinds(int version);

// `sources`: each source's totals of the model; `groups`: the known attributions' usage.
std::optional<std::vector<Row>> build(const std::map<SourceKind, Counts>& sources, const std::map<Group, Counts>& groups, int version);

}  // namespace agentspeed::report::mode_rows
