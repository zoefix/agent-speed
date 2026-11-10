// METRICS 6.9 Readouts of a set beyond the fixture cases (spec/tools/perf_score.py set_first_latency, recent_speed,
// running_models): grouping by the exact id, the supplying agent's source, ordinal ties and the 3-value minimum.
#include <gtest/gtest.h>

#include "agentspeed/perf.hpp"
#include "agentspeed/set_readouts.hpp"

using namespace agentspeed;
using perf::ReadoutAgent;

namespace {

ReadoutAgent runner(std::optional<std::string> model, Phase phase, std::optional<double> speed, SourceKind source = SourceKind::Codex) {
    return {{}, std::move(model), phase, speed, source};
}

ReadoutAgent sampled(std::vector<std::int64_t> latencies) { return {std::move(latencies), std::nullopt, Phase::Idle, std::nullopt, SourceKind::Codex}; }

engine::Response response(const char* key, std::int64_t start, std::int64_t end, std::int64_t output, bool final = true) {
    engine::Response r;
    r.key = key;
    r.agentId = "a";
    r.startMs = start;
    r.endMs = end;
    r.output = output;
    r.final = final;
    return r;
}

std::vector<std::string> modelsOf(const std::vector<perf::RunningModel>& rows) {
    std::vector<std::string> out;
    for (const auto& r : rows) out.push_back(r.model);
    return out;
}

}  // namespace

TEST(SetReadouts, RunningModelsGroupByTheExactIdAndKeepTheFastestAgentsSource) {
    const auto rows = perf::runningModels({
        runner("glm-5", Phase::Tools, std::nullopt, SourceKind::ZCode),
        runner("glm-5", Phase::Generating, 80.5, SourceKind::OpenCode),
        runner("GLM-5", Phase::Generating, 12, SourceKind::ZCode),
        runner("glm-5", Phase::Generating, 40, SourceKind::Kimi),
        runner("", Phase::Generating, 300),
        runner(std::nullopt, Phase::Generating, 300),
        runner("b-model", Phase::Generating, std::nullopt, SourceKind::Gemini),
        runner("a-model", Phase::Tools, std::nullopt, SourceKind::Pi),
        runner("resting-model", Phase::Idle, 500),
    });
    EXPECT_EQ(modelsOf(rows), (std::vector<std::string>{"glm-5", "GLM-5", "a-model", "b-model"}));
    EXPECT_EQ(rows[0].speed, 80.5);
    EXPECT_EQ(rows[0].shown(), 81);
    EXPECT_EQ(rows[0].agents, 3);
    EXPECT_EQ(rows[0].source, SourceKind::OpenCode);
    EXPECT_EQ(rows[2].shown(), std::nullopt);
    EXPECT_EQ(rows[2].source, SourceKind::Pi);  // no speed: its first agent's source
    EXPECT_EQ(rows[3].source, SourceKind::Gemini);
}

TEST(SetReadouts, TiesGoToTheSmallerModelInUtf16Order) {
    // U+1D49C is a surrogate pair (0xD835 …), so it sorts before U+FF5E in UTF-16 though after it by code point.
    const auto rows = perf::runningModels({runner("\xEF\xBD\x9E", Phase::Generating, 50), runner("\xF0\x9D\x92\x9C", Phase::Generating, 50),
                                           runner("b", Phase::Generating, 50), runner("B", Phase::Generating, 50),
                                           runner("slow", Phase::Generating, 49.4)});
    EXPECT_EQ(modelsOf(rows), (std::vector<std::string>{"B", "b", "\xF0\x9D\x92\x9C", "\xEF\xBD\x9E", "slow"}));
    EXPECT_EQ(rows.back().shown(), 49);
}

TEST(SetReadouts, TheLatencyNeedsThreeValuesAcrossTheSetsAgents) {
    EXPECT_EQ(perf::setFirstLatency({sampled({800}), sampled({}), sampled({1200})}), (perf::SetLatency{std::nullopt, 2}));
    EXPECT_EQ(perf::setFirstLatency({sampled({1200, 600}), sampled({900})}), (perf::SetLatency{900.0, 3}));
    EXPECT_EQ(perf::setFirstLatency({sampled({1200, 600}), sampled({900, 1500})}), (perf::SetLatency{1050.0, 4}));
    EXPECT_EQ(perf::setFirstLatency({}), (perf::SetLatency{std::nullopt, 0}));
}

TEST(SetReadouts, AnAgentsSampleKeepsItsLatenciesWhateverItsSize) {
    engine::Response r = response("r1", 1000, 3000, 100);
    r.firstMs = 1400;
    engine::Response late = response("r2", 5000, 7000, 100);
    late.firstMs = 4000;  // before its start: left out
    const std::vector<const engine::Response*> own = {&r, &late};
    const auto e = perf::evaluate(SourceKind::Codex, Phase::Generating, 7000, own, {}, 8000);
    EXPECT_EQ(e.n, 2);
    EXPECT_FALSE(e.score.has_value());
    EXPECT_EQ(e.latencies, (std::vector<std::int64_t>{400}));
}

TEST(SetReadouts, RecentSpeedReadsTheNewestThreeMeasuredResponsesOfTenMinutes) {
    const std::int64_t now = 2'000'000;
    const auto a = response("a", now - 9000, now - 7000, 100);
    const auto b = response("b", now - 9000, now - 7000, 900);   // the same end as a and c: the larger key goes
    const auto c = response("c", now - 9000, now - 7000, 50000);
    const auto d = response("d", now - 4000, now - 2000, 200);
    const auto open = response("e", now - 3000, now - 1000, 999, false);
    const auto old = response("f", now - 601'000, now - 600'500, 999);
    const auto future = response("g", now - 1000, now + 1000, 999);
    EXPECT_EQ(perf::recentSpeed({&c, &b, &a, &d, &open, &old, &future}, now), (200.0 + 100 + 900) / 6.0);
    EXPECT_EQ(perf::recentSpeed({&open, &old, &future}, now), std::nullopt);
    // A short response counts at least 500 ms, as everywhere in the engine.
    const auto quick = response("q", now - 100, now, 50);
    EXPECT_EQ(perf::recentSpeed({&quick}, now), 100.0);
}
