#pragma once

#include <array>
#include <cstdint>
#include <deque>
#include <optional>
#include <string>
#include <string_view>
#include <vector>

#include "agentspeed/actions.hpp"
#include "agentspeed/engine.hpp"
#include "agentspeed/model.hpp"

// The performance score (METRICS 6.9, spec/tools/perf_score.py): an absolute, benchmark-style number per agent, 1000
// the fixed reference level and no upper limit, the weighted geometric mean of seven components.
namespace agentspeed::perf {

enum class Id : std::uint8_t { T, L, N, R, H, U, S };
inline constexpr std::array<Id, 7> kIds{Id::T, Id::L, Id::N, Id::R, Id::H, Id::U, Id::S};
std::string_view idName(Id id);

enum class Reason : std::uint8_t { None, NotRecorded, FewSamples };
std::string_view reasonId(Reason reason);

struct Component {
    Id id = Id::T;
    bool present = false;
    Reason reason = Reason::None;
    std::int64_t samples = 0;
    std::optional<double> measure, ratio, subScore;
};
using Components = std::array<Component, 7>;

enum class Form : std::uint8_t { None, Live, Resting };
std::string_view formId(Form form);

inline constexpr std::int64_t kWindowMs = 30 * 60'000;
inline constexpr std::size_t kSample = 20;
inline constexpr std::int64_t kMinSamples = 3;
inline constexpr std::int64_t kProvisionalBelow = 10;
inline constexpr std::int64_t kToolMaxMs = 15 * 60'000;
inline constexpr std::int64_t kPooledMinN = 20;
inline constexpr int kBaselineDays = 14;

// Whether the source never records what the component measures (6.9 Absent components).
bool notRecorded(Id id, SourceKind source);
bool recordsLatency(SourceKind source);
bool recordsReasoning(SourceKind source);
bool recordsCache(SourceKind source);

// The present measures in T, L, N, R, H, U, S order (nullopt: absent) with each component's samples → components
// and score (nullopt when nothing is present).
struct Measures {
    std::array<std::optional<double>, 7> measure;
    std::array<std::int64_t, 7> samples{};
};
std::pair<Components, std::optional<double>> combine(SourceKind source, const Measures& measures);

std::int64_t roundHalfAway(double value);
// Ordinal (UTF-16 code unit) order.
bool ordinalLess(std::string_view a, std::string_view b);

// 6.7 step 1: a phase older than 300 s without a new line is idle.
Phase effectivePhase(Phase phase, std::optional<std::int64_t> lastLineMs, std::int64_t now);

struct Evaluation {
    Form form = Form::None;
    std::optional<double> score;
    std::optional<std::int64_t> shown;
    bool provisional = false;
    std::int64_t n = 0, tools = 0, activeMs = 0;
    std::optional<std::int64_t> spanStartMs;
    std::optional<std::string> model;  // of the newest sample response, as logged
    Components components;
    std::optional<double> baseline, vsUsual;
    std::optional<std::int64_t> vsUsualPercent;
};

// 6.9 for one agent at `now` from its responses (3.3) and its stored actions (6.10, the last 64 in file order);
// `phase` before the staleness rule.
Evaluation evaluate(SourceKind source, Phase phase, std::optional<std::int64_t> lastLineMs,
                    const std::vector<const engine::Response*>& responses, const std::deque<work::Action>& actions, std::int64_t now);
// 比平常: the baseline of the agent's (source, model) and the comparison with it.
void applyBaseline(Evaluation& evaluation, std::optional<double> baseline);

// 6.9 Sets and groups: the highest live value, else the highest resting value, else none; `agent` names the
// supplier (latest lastLineMs, then the smallest id, ordinal).
struct Valued {
    std::string id;
    std::optional<std::int64_t> lastLineMs;
    Form form = Form::None;
    std::optional<double> score;
};
struct Highest {
    Form form = Form::None;
    std::optional<double> score;
    std::optional<std::int64_t> shown;
    std::optional<std::string> agent;
};
Highest highest(const std::vector<Valued>& agents);

}  // namespace agentspeed::perf
