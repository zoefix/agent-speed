#include <gtest/gtest.h>

#include <cmath>
#include <set>

#include "agentspeed/engine.hpp"
#include "agentspeed/perf.hpp"
#include "agentspeed/perf_history.hpp"
#include "agentspeed/pricing.hpp"
#include "fixture_support.hpp"

using namespace agentspeed;

// spec/fixtures/perf-score-cases.json (METRICS 6.9, 6.11; spec/tools/perf_score.py): unrounded values within 10⁻⁹,
// relative above 1.
namespace {

Phase phaseOf(const json::Value& v) {
    for (const auto p : {Phase::Idle, Phase::Generating, Phase::Tools}) {
        if (v.equals(phaseId(p))) return p;
    }
    return Phase::Idle;
}

std::vector<std::string> strings(const json::Value& v) {
    std::vector<std::string> out;
    for (const auto& s : v.elements()) out.push_back(*s.string());
    return out;
}

void expectNumber(std::optional<double> got, const json::Value& want, const std::string& label) {
    SCOPED_TRACE(label);
    if (!want.valid() || want.isNull()) {
        EXPECT_FALSE(got.has_value());
        return;
    }
    ASSERT_TRUE(got.has_value());
    const double w = *want.number();
    EXPECT_LE(std::abs(*got - w), 1e-9 * std::max({1.0, std::abs(*got), std::abs(w)})) << *got << " vs " << w;
}

engine::Response responseOf(const json::Value& v) {
    engine::Response r;
    r.key = *v["key"].string();
    r.agentId = v["agentId"].string().value_or("");
    r.source = *sourceFromId(*v["source"].string());
    r.model = fixtures::optString(v["model"]);
    r.startMs = fixtures::optInt(v["startMs"]);
    r.endMs = *v["endMs"].truncated();
    r.firstMs = fixtures::optInt(v["firstMs"]);
    r.input = v["input"].count();
    r.cachedInput = v["cachedInput"].count();
    r.output = v["output"].count();
    r.reasoning = v["reasoning"].count();
    r.final = !v["final"].valid() || v["final"].isTrue();
    return r;
}

engine::Agent agentOf(const json::Value& v) {
    engine::Agent a;
    a.id = *v["id"].string();
    a.source = *sourceFromId(*v["source"].string());
    a.model = fixtures::optString(v["model"]);
    a.phase = phaseOf(v["phase"]);
    a.inFlightStartMs = fixtures::optInt(v["inFlightStartMs"]);
    a.lastLineMs = fixtures::optInt(v["lastLineMs"]);
    a.parentId = fixtures::optString(v["parentId"]);
    return a;
}

work::Action actionOf(const json::Value& v) {
    work::Action a;
    a.callMs = *v["callMs"].truncated();
    a.resultMs = fixtures::optInt(v["resultMs"]);
    a.tool = v["tool"].string().value_or("");
    a.digest = v["digest"].string().value_or("");
    a.failed = v["failed"].isBool() ? std::optional<bool>(v["failed"].isTrue()) : std::nullopt;
    a.model = fixtures::optString(v["model"]);
    return a;
}

perf::DayRecord recordOf(const json::Value& v) {
    perf::DayRecord row;
    row.day = v["day"].string().value_or("");
    if (const auto source = v["source"].string()) row.source = *sourceFromId(*source);
    row.model = v["model"].string().value_or("");
    auto& r = row.record;
    r.n = v["n"].count();
    r.outputSum = v["outputSum"].count();
    r.durationMsSum = v["durationMsSum"].count();
    std::size_t k = 0;
    for (const auto& c : v["latencyBins"].elements()) r.latencyBins[k++] = c.count();
    r.reasonN = v["reasonN"].count();
    r.reasonOutput = v["reasonOutput"].count();
    r.reasonDurationMsSum = v["reasonDurationMsSum"].count();
    r.cacheN = v["cacheN"].count();
    r.cacheInput = v["cacheInput"].count();
    r.cacheCached = v["cacheCached"].count();
    r.toolN = v["toolN"].count();
    r.toolLnSum = v["toolLnSum"].number().value_or(0);
    r.toolFailed = v["toolFailed"].count();
    r.activeMs = v["activeMs"].count();
    return row;
}

std::vector<perf::DayRecord> recordsOf(const json::Value& list) {
    std::vector<perf::DayRecord> out;
    for (const auto& r : list.elements()) out.push_back(recordOf(r));
    return out;
}

void expectComponents(const perf::Components& got, const json::Value& want) {
    ASSERT_EQ(want.size(), got.size());
    for (std::size_t i = 0; i < got.size(); ++i) {
        const auto& c = got[i];
        const auto w = want.at(static_cast<std::uint32_t>(i));
        SCOPED_TRACE(std::string(perf::idName(c.id)));
        EXPECT_EQ(perf::idName(c.id), *w["id"].string());
        EXPECT_EQ(c.present, w["present"].isTrue());
        EXPECT_EQ(std::string(perf::reasonId(c.reason)), w["reason"].string().value_or(""));
        EXPECT_EQ(c.samples, *w["samples"].truncated());
        expectNumber(c.measure, w["measure"], "measure");
        expectNumber(c.ratio, w["ratio"], "ratio");
        expectNumber(c.subScore, w["subScore"], "subScore");
    }
}

void expectEvaluation(const perf::Evaluation& got, const json::Value& want) {
    EXPECT_EQ(perf::formId(got.form), *want["form"].string());
    expectNumber(got.score, want["score"], "score");
    EXPECT_EQ(got.shown, fixtures::optInt(want["shown"]));
    EXPECT_EQ(got.provisional, want["provisional"].isTrue());
    EXPECT_EQ(got.n, *want["n"].truncated());
    EXPECT_EQ(got.tools, *want["tools"].truncated());
    EXPECT_EQ(got.activeMs, *want["activeMs"].truncated());
    EXPECT_EQ(got.spanStartMs, fixtures::optInt(want["spanStartMs"]));
    EXPECT_EQ(got.model, fixtures::optString(want["model"]));
    expectComponents(got.components, want["components"]);
    expectNumber(got.baseline, want["baseline"], "baseline");
    expectNumber(got.vsUsual, want["vsUsual"], "vsUsual");
    EXPECT_EQ(got.vsUsualPercent, fixtures::optInt(want["vsUsualPercent"]));
}

void expectHighest(const perf::Highest& got, const json::Value& want) {
    EXPECT_EQ(perf::formId(got.form), *want["form"].string());
    expectNumber(got.score, want["score"], "score");
    EXPECT_EQ(got.shown, fixtures::optInt(want["shown"]));
    EXPECT_EQ(got.agent, fixtures::optString(want["agent"]));
}

void expectRecord(const perf::DayRecord& got, const json::Value& want) {
    SCOPED_TRACE(got.day + " " + std::string(sourceId(got.source)) + " " + got.model);
    const auto w = recordOf(want);
    EXPECT_EQ(got.day, w.day);
    EXPECT_EQ(got.source, w.source);
    EXPECT_EQ(got.model, w.model);
    const auto& a = got.record;
    const auto& b = w.record;
    EXPECT_EQ(a.n, b.n);
    EXPECT_EQ(a.outputSum, b.outputSum);
    EXPECT_EQ(a.durationMsSum, b.durationMsSum);
    EXPECT_EQ(a.latencyBins, b.latencyBins);
    EXPECT_EQ(a.reasonN, b.reasonN);
    EXPECT_EQ(a.reasonOutput, b.reasonOutput);
    EXPECT_EQ(a.reasonDurationMsSum, b.reasonDurationMsSum);
    EXPECT_EQ(a.cacheN, b.cacheN);
    EXPECT_EQ(a.cacheInput, b.cacheInput);
    EXPECT_EQ(a.cacheCached, b.cacheCached);
    EXPECT_EQ(a.toolN, b.toolN);
    expectNumber(a.toolLnSum, want["toolLnSum"], "toolLnSum");
    EXPECT_EQ(a.toolFailed, b.toolFailed);
    EXPECT_EQ(a.activeMs, b.activeMs);
}

void expectPooled(const perf::Pooled& got, const json::Value& want) {
    EXPECT_EQ(got.n, *want["n"].truncated());
    EXPECT_EQ(got.tools, *want["tools"].truncated());
    EXPECT_EQ(got.activeMs, *want["activeMs"].truncated());
    expectNumber(got.score, want["score"], "score");
    EXPECT_EQ(got.shown, fixtures::optInt(want["shown"]));
    expectComponents(got.components, want["components"]);
}

void expectWindow(const engine::ThinkingWindow& w, const json::Value& want, const std::string& label) {
    expectNumber(w.liveThinking(), want["liveThinking"], label + " liveThinking");
    expectNumber(w.thinkingShare(), want["thinkingShare"], label + " thinkingShare");
    expectNumber(w.weight, want["weight"], label + " weight");
}

class PerfScore : public ::testing::Test {
protected:
    static void SetUpTestSuite() {
        file_ = new fixtures::JsonFile(fixtures::fixtureDir() / "perf-score-cases.json");
        ASSERT_TRUE(file_->ok);
    }
    static void TearDownTestSuite() {
        delete file_;
        file_ = nullptr;
    }
    static json::Value section(std::string_view name) { return file_->root()[name]; }
    static fixtures::JsonFile* file_;
};
fixtures::JsonFile* PerfScore::file_ = nullptr;

}  // namespace

TEST_F(PerfScore, LatencyBins) {
    const auto edges = section("edges").elements();
    ASSERT_EQ(edges.size(), static_cast<std::size_t>(perf::kBins + 1));
    for (int j = 0; j <= perf::kBins; ++j) EXPECT_EQ(perf::latencyEdge(j), *edges[static_cast<std::size_t>(j)].truncated()) << j;
    for (const auto& c : section("latencyBins").elements()) {
        EXPECT_EQ(perf::latencyBin(*c["ms"].truncated()), *c["expect"].truncated()) << *c["ms"].truncated();
    }
}

TEST_F(PerfScore, Agents) {
    int queries = 0;
    for (const auto& c : section("agents").elements()) {
        SCOPED_TRACE(*c["name"].string());
        std::vector<engine::Response> responses;
        for (const auto& r : c["responses"].elements()) responses.push_back(responseOf(r));
        const auto history = recordsOf(c["history"]);
        const auto today = c["today"].string();
        for (const auto& q : c["queries"].elements()) {
            const auto now = *q["nowMs"].truncated();
            SCOPED_TRACE(std::to_string(now));
            for (const auto& a : c["agents"].elements()) {
                const auto id = *a["id"].string();
                SCOPED_TRACE(id);
                std::vector<const engine::Response*> own;
                for (const auto& r : responses) {
                    if (r.agentId == id) own.push_back(&r);
                }
                std::deque<work::Action> actions;
                for (const auto& x : c["actions"][id].elements()) actions.push_back(actionOf(x));
                const auto source = *sourceFromId(*a["source"].string());
                auto got = perf::evaluate(source, phaseOf(a["phase"]), fixtures::optInt(a["lastLineMs"]), own, actions, now);
                if (today && got.score) perf::applyBaseline(got, perf::baseline(history, source, got.model.value_or(""), *today));
                expectEvaluation(got, q["expect"][id]);
            }
            ++queries;
        }
    }
    EXPECT_GT(queries, 10);
}

TEST_F(PerfScore, Sets) {
    for (const auto& c : section("sets").elements()) {
        SCOPED_TRACE(*c["name"].string());
        std::vector<perf::Valued> all;
        std::map<std::string, perf::Valued> byId;
        std::vector<engine::Agent> agents;
        for (const auto& a : c["agents"].elements()) {
            const auto id = *a["id"].string();
            const auto value = c["values"][id];
            perf::Valued v{id, fixtures::optInt(a["lastLineMs"]), perf::Form::None, value["score"].number()};
            for (const auto form : {perf::Form::Live, perf::Form::Resting}) {
                if (value["form"].equals(perf::formId(form))) v.form = form;
            }
            all.push_back(v);
            byId[id] = v;
            agents.push_back(agentOf(a));
        }
        expectHighest(perf::highest(all), c["expect"]["all"]);
        const auto groups = engine::boardGroups(agents);
        EXPECT_EQ(groups.size(), c["expect"]["groups"].size());
        for (const auto& [root, members] : groups) {
            SCOPED_TRACE(root);
            const auto want = c["expect"]["groups"][root];
            EXPECT_EQ(members, strings(want["members"]));
            std::vector<perf::Valued> group;
            for (const auto& m : members) group.push_back(byId[m]);
            expectHighest(perf::highest(group), want);
        }
    }
}

TEST_F(PerfScore, Days) {
    for (const auto& c : section("days").elements()) {
        SCOPED_TRACE(*c["name"].string());
        perf::Recorder recorder([](std::int64_t ms) { return pricing::UtcDay(ms).label(); });
        std::vector<engine::Response> keep;
        for (const auto& agent : c["agents"].elements()) {
            const auto id = *agent["id"].string();
            const auto source = *sourceFromId(*agent["source"].string());
            for (const auto& e : agent["events"].elements()) {
                if (e["response"].isObject()) {
                    recorder.response(id, source, responseOf(e["response"]));
                } else {
                    recorder.action(id, source, actionOf(e["action"]));
                }
            }
        }
        const auto rows = recorder.rows();
        const auto want = c["expect"].elements();
        ASSERT_EQ(rows.size(), want.size());
        for (std::size_t i = 0; i < rows.size(); ++i) expectRecord(rows[i], want[i]);
    }
}

TEST_F(PerfScore, Pooled) {
    for (const auto& c : section("pooled").elements()) {
        SCOPED_TRACE(*c["name"].string());
        const auto records = recordsOf(c["records"]);
        std::vector<const perf::Record*> all;
        for (const auto& r : records) all.push_back(&r.record);
        expectPooled(perf::pooled(all, *sourceFromId(*c["source"].string())), c["expect"]);
    }
}

TEST_F(PerfScore, Baseline) {
    for (const auto& c : section("baseline").elements()) {
        SCOPED_TRACE(*c["name"].string());
        const auto got = perf::baseline(recordsOf(c["records"]), *sourceFromId(*c["source"].string()), *c["model"].string(), *c["today"].string());
        expectNumber(got, c["expect"], "baseline");
    }
}

TEST_F(PerfScore, Range) {
    for (const auto& c : section("range").elements()) {
        SCOPED_TRACE(*c["name"].string());
        std::optional<SourceKind> source;
        if (const auto s = c["source"].string()) source = sourceFromId(*s);
        const auto rows = perf::rangeValues(recordsOf(c["records"]), *c["first"].string(), *c["last"].string(), source);
        const auto want = c["expect"].elements();
        ASSERT_EQ(rows.size(), want.size());
        for (std::size_t i = 0; i < rows.size(); ++i) {
            SCOPED_TRACE(rows[i].model);
            EXPECT_EQ(sourceId(rows[i].source), *want[i]["source"].string());
            EXPECT_EQ(rows[i].model, *want[i]["model"].string());
            expectPooled(rows[i].pooled, want[i]);
            const auto trend = want[i]["trend"].elements();
            ASSERT_EQ(rows[i].trend.size(), trend.size());
            for (std::size_t d = 0; d < trend.size(); ++d) {
                EXPECT_EQ(rows[i].trend[d].first, *trend[d]["day"].string());
                expectNumber(rows[i].trend[d].second, trend[d]["score"], rows[i].trend[d].first);
            }
        }
    }
}

TEST_F(PerfScore, LiveThinking) {
    const auto thinking = section("thinking");
    int queries = 0;
    for (const auto& c : thinking["windows"].elements()) {
        SCOPED_TRACE(*c["name"].string());
        for (const auto& q : c["queries"].elements()) {
            std::set<std::string> subset;
            if (q["sources"].isArray()) {
                for (const auto& s : q["sources"].elements()) subset.insert(*s.string());
            } else {
                for (const auto& r : c["responses"].elements()) subset.insert(*r["source"].string());
                for (const auto& a : c["agents"].elements()) subset.insert(*a["source"].string());
            }
            std::vector<engine::Response> responses;
            std::vector<engine::Agent> agents;
            for (const auto& r : c["responses"].elements()) {
                if (subset.contains(*r["source"].string())) responses.push_back(responseOf(r));
            }
            for (const auto& a : c["agents"].elements()) {
                if (subset.contains(*a["source"].string())) agents.push_back(agentOf(a));
            }
            expectWindow(engine::thinking(responses, agents, *q["nowMs"].truncated()).total, q["expect"], "total");
            ++queries;
        }
    }
    const auto load = [](const json::Value& c, std::vector<engine::Response>& responses, std::vector<engine::Agent>& agents) {
        for (const auto& r : c["responses"].elements()) responses.push_back(responseOf(r));
        for (const auto& a : c["agents"].elements()) agents.push_back(agentOf(a));
    };
    for (const auto& c : thinking["agentWindows"].elements()) {
        SCOPED_TRACE(*c["name"].string());
        std::vector<engine::Response> responses;
        std::vector<engine::Agent> agents;
        load(c, responses, agents);
        for (const auto& q : c["queries"].elements()) {
            const auto got = engine::thinking(responses, agents, *q["nowMs"].truncated());
            expectWindow(got.total, q["expect"], "total");
            for (const auto& a : agents) {
                const auto it = got.perAgent.find(a.id);
                expectWindow(it == got.perAgent.end() ? engine::ThinkingWindow{} : it->second, q["agents"][a.id], a.id);
            }
            ++queries;
        }
    }
    for (const auto& c : thinking["groupWindows"].elements()) {
        SCOPED_TRACE(*c["name"].string());
        std::vector<engine::Response> responses;
        std::vector<engine::Agent> agents;
        load(c, responses, agents);
        const auto groups = engine::boardGroups(agents);
        for (const auto& q : c["queries"].elements()) {
            const auto got = engine::thinking(responses, agents, *q["nowMs"].truncated());
            expectWindow(got.total, q["expect"], "total");
            EXPECT_EQ(groups.size(), q["groups"].size());
            for (const auto& [root, members] : groups) {
                engine::ThinkingWindow w;
                for (const auto& m : members) {
                    if (const auto it = got.perAgent.find(m); it != got.perAgent.end()) w.merge(it->second);
                }
                EXPECT_EQ(members, strings(q["groups"][root]["members"]));
                expectWindow(w, q["groups"][root], root);
            }
            ++queries;
        }
    }
    for (const auto& c : thinking["modelWindows"].elements()) {
        SCOPED_TRACE(*c["name"].string());
        std::vector<engine::Response> responses;
        std::vector<engine::Agent> agents;
        load(c, responses, agents);
        for (const auto& q : c["queries"].elements()) {
            const auto got = engine::thinking(responses, agents, *q["nowMs"].truncated());
            expectWindow(got.total, q["expect"], "total");
            EXPECT_EQ(got.perModel.size(), q["models"].size());
            for (const auto& [model, w] : q["models"].members()) {
                ASSERT_TRUE(got.perModel.contains(std::string(model))) << model;
                expectWindow(got.perModel.at(std::string(model)), w, std::string(model));
            }
            ++queries;
        }
    }
    EXPECT_GT(queries, 20);
}
