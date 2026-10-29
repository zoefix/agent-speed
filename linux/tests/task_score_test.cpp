#include <gtest/gtest.h>

#include <set>

#include "agentspeed/engine.hpp"
#include "agentspeed/task_score.hpp"
#include "fixture_support.hpp"

using namespace agentspeed;

namespace {

constexpr double kTolerance = 1e-9;

Phase phaseOf(const json::Value& v) {
    for (const auto p : {Phase::Idle, Phase::Generating, Phase::Tools}) {
        if (v.equals(phaseId(p))) return p;
    }
    return Phase::Idle;
}

std::optional<bool> optBool(const json::Value& v) {
    if (!v.isBool()) return std::nullopt;
    return v.isTrue();
}

std::vector<std::string> strings(const json::Value& v) {
    std::vector<std::string> out;
    for (const auto& e : v.elements()) out.push_back(*e.string());
    return out;
}

tasks::Event eventOf(const json::Value& v) {
    tasks::Event e;
    e.type = v["type"].equals("prompt") ? tasks::Event::Type::Prompt : tasks::Event::Type::Action;
    e.t = *v["t"].truncated();
    e.continuation = v["continuation"].isTrue();
    if (const auto kind = v["kind"].string()) e.kind = *kind;
    e.tool = fixtures::optString(v["tool"]);
    e.digest = fixtures::optString(v["digest"]);
    if (const auto c = v["command"]; c.isObject()) {
        tasks::CommandInfo info;
        info.cls = *c["class"].string();
        info.suite = fixtures::optString(c["suite"]);
        info.targeted = c["targeted"].isTrue();
        info.key = *c["key"].string();
        e.command = info;
    }
    if (const auto o = v["outcome"]; o.isObject()) {
        e.outcome = tasks::Outcome{fixtures::optInt(o["exitCode"]), optBool(o["failed"]), fixtures::optInt(o["t"])};
    }
    if (const auto f = v["failures"]; f.isObject()) {
        tasks::FailureReport report;
        report.fingerprints = strings(f["fingerprints"]);
        report.failed = fixtures::optInt(f["failedCount"]);
        report.passed = fixtures::optInt(f["passedCount"]);
        report.framework = fixtures::optString(f["framework"]);
        e.failures = report;
    }
    if (const auto h = v["hunks"]; h.isArray()) {
        std::vector<tasks::EditHunk> hunks;
        for (const auto& x : h.elements()) hunks.push_back({*x["file"].string(), fixtures::optString(x["old"]), fixtures::optString(x["new"])});
        e.hunks = hunks;
    }
    return e;
}

std::vector<tasks::Event> eventsOf(const json::Value& v) {
    std::vector<tasks::Event> out;
    for (const auto& e : v.elements()) out.push_back(eventOf(e));
    return out;
}

void expectNumber(std::optional<double> got, const json::Value& want, const std::string& label) {
    SCOPED_TRACE(label);
    if (want.isNull()) {
        EXPECT_FALSE(got.has_value());
        return;
    }
    ASSERT_TRUE(got.has_value());
    EXPECT_NEAR(*got, *want.number(), kTolerance);
}

void expectInt(std::optional<std::int64_t> got, const json::Value& want, const std::string& label) {
    SCOPED_TRACE(label);
    EXPECT_EQ(got, fixtures::optInt(want));
}

void expectDims(const tasks::Dims& d, const json::Value& want) {
    expectNumber(d.V, want["V"], "V");
    expectNumber(d.C, want["C"], "C");
    expectNumber(d.R, want["R"], "R");
    expectNumber(d.E, want["E"], "E");
    expectNumber(d.K, want["K"], "K");
}

void expectCounts(const tasks::TaskCounts& c, const json::Value& w) {
    const std::pair<const char*, std::int64_t> fields[] = {
        {"actions", c.actions},     {"counted", c.counted},         {"edits", c.edits},   {"oscillations", c.oscillations},
        {"loopActions", c.loopActions}, {"runs", c.runs},           {"failingRuns", c.failingRuns}, {"episodes", c.episodes},
        {"recovered", c.recovered}, {"recoverAttempts", c.recoverAttempts}, {"fixAttempts", c.fixAttempts}, {"fixFailures", c.fixFailures}};
    for (const auto& [name, value] : fields) {
        if (w[name].valid()) expectInt(value, w[name], name);
    }
}

void expectResult(const tasks::TaskResult& r, const json::Value& w) {
    EXPECT_EQ(r.state, *w["state"].string());
    expectNumber(r.score, w["score"], "score");
    expectInt(r.shown, w["shown"], "shown");
    expectDims(r.dims, w["dims"]);
    expectNumber(r.ig, w["ig"], "ig");
    expectNumber(r.loop, w["loop"], "loop");
    expectNumber(r.osc, w["osc"], "osc");
    expectCounts(r.counts, w["counts"]);
    expectInt(r.firstPassMs, w["firstPassMs"], "firstPassMs");
    std::vector<std::int64_t> ends;
    for (const auto& e : w["stallEnds"].elements()) ends.push_back(*e.truncated());
    EXPECT_EQ(r.stallEnds, ends);
    expectNumber(r.vHeld, w["vHeld"], "vHeld");
    const auto of = w["openFailure"];
    ASSERT_EQ(r.openFailure.has_value(), !of.isNull());
    if (r.openFailure) {
        EXPECT_EQ(r.openFailure->suite, fixtures::optString(of["suite"]));
        EXPECT_EQ(r.openFailure->cls, fixtures::optString(of["class"]));
        EXPECT_EQ(r.openFailure->key, *of["key"].string());
        EXPECT_EQ(r.openFailure->errors, *of["errors"].truncated());
        EXPECT_EQ(r.openFailure->peak, *of["peak"].truncated());
        EXPECT_EQ(r.openFailure->counted, of["counted"].isTrue());
        EXPECT_EQ(r.openFailure->edited, of["edited"].isTrue());
    }
    const auto lp = w["lastPass"];
    ASSERT_EQ(r.lastPass.has_value(), !lp.isNull());
    if (r.lastPass) {
        EXPECT_EQ(r.lastPass->suite, fixtures::optString(lp["suite"]));
        EXPECT_EQ(r.lastPass->cls, fixtures::optString(lp["class"]));
        EXPECT_EQ(r.lastPass->key, *lp["key"].string());
        EXPECT_EQ(r.lastPass->buildOnly, lp["buildOnly"].isTrue());
    }
}

void expectFeatures(const tasks::Features& got, const json::Value& want) {
    ASSERT_EQ(got.size(), want.size());
    for (const auto& [name, value] : want.members()) {
        ASSERT_TRUE(got.contains(name)) << name;
        EXPECT_NEAR(got.at(name), *value.number(), kTolerance) << name;
    }
}

std::vector<std::vector<double>> rowsOf(const json::Value& v) {
    std::vector<std::vector<double>> rows;
    for (const auto& r : v.elements()) {
        std::vector<double> row;
        for (const auto& x : r.elements()) row.push_back(*x.number());
        rows.push_back(row);
    }
    return rows;
}

void expectRows(const std::vector<std::vector<double>>& got, const json::Value& want) {
    const auto rows = rowsOf(want);
    ASSERT_EQ(got.size(), rows.size());
    for (std::size_t i = 0; i < rows.size(); ++i) {
        ASSERT_EQ(got[i].size(), rows[i].size());
        for (std::size_t k = 0; k < rows[i].size(); ++k) EXPECT_NEAR(got[i][k], rows[i][k], kTolerance) << i << "," << k;
    }
}

tasks::TaskRecord recordOf(const json::Value& v) {
    tasks::TaskRecord r;
    if (const auto s = v["source"].string()) r.source = *sourceFromId(*s);
    r.agentId = fixtures::optString(v["agentId"]).value_or("");
    r.ordinal = fixtures::optInt(v["ordinal"]).value_or(0);
    r.startMs = fixtures::optInt(v["startMs"]).value_or(0);
    r.endMs = fixtures::optInt(v["endMs"]).value_or(0);
    r.complete = v["complete"].isTrue();
    r.success = v["success"].isTrue();
    r.stalled = v["stalled"].isTrue();
    r.model = fixtures::optString(v["model"]);
    r.day = fixtures::optString(v["day"]).value_or("");
    if (v["score"].isNumber()) r.score = *v["score"].number();
    r.firstPassMs = fixtures::optInt(v["firstPassMs"]);
    const auto c = v["counts"];
    r.counts.fixAttempts = fixtures::optInt(c["fixAttempts"]).value_or(0);
    r.counts.fixFailures = fixtures::optInt(c["fixFailures"]).value_or(0);
    r.counts.recovered = fixtures::optInt(c["recovered"]).value_or(0);
    r.counts.recoverAttempts = fixtures::optInt(c["recoverAttempts"]).value_or(0);
    r.snapshots = rowsOf(v["snapshots"]);
    r.fixRows = rowsOf(v["fixRows"]);
    return r;
}

void expectAggregate(const tasks::Aggregate& a, const json::Value& w) {
    expectInt(a.tasks, w["tasks"], "tasks");
    expectInt(a.scoredTasks, w["scoredTasks"], "scoredTasks");
    expectNumber(a.medianScore, w["medianScore"], "medianScore");
    expectInt(a.medianShown, w["medianShown"], "medianShown");
    expectNumber(a.lowerQuartile, w["lowerQuartile"], "lowerQuartile");
    expectNumber(a.upperQuartile, w["upperQuartile"], "upperQuartile");
    const auto bins = w["histogram"].elements();
    ASSERT_EQ(bins.size(), a.histogram.size());
    for (std::size_t i = 0; i < bins.size(); ++i) EXPECT_EQ(a.histogram[i], *bins[i].truncated());
    expectInt(a.successes, w["successes"], "successes");
    expectInt(a.stalls, w["stalls"], "stalls");
    expectInt(a.fixFailures, w["fixFailures"], "fixFailures");
    expectInt(a.fixAttempts, w["fixAttempts"], "fixAttempts");
    expectInt(a.recovered, w["recovered"], "recovered");
    expectNumber(a.successRate, w["successRate"], "successRate");
    expectNumber(a.stallRate, w["stallRate"], "stallRate");
    expectNumber(a.fixFailureRate, w["fixFailureRate"], "fixFailureRate");
    expectNumber(a.recoverAttempts, w["recoverAttempts"], "recoverAttempts");
    expectNumber(a.timeToFirstPassMs, w["timeToFirstPassMs"], "timeToFirstPassMs");
}

engine::Response responseOf(const json::Value& v) {
    engine::Response r;
    r.key = *v["key"].string();
    r.agentId = *v["agentId"].string();
    r.source = *sourceFromId(*v["source"].string());
    r.model = fixtures::optString(v["model"]);
    r.startMs = fixtures::optInt(v["startMs"]);
    r.endMs = *v["endMs"].truncated();
    r.input = v["input"].count();
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

void expectWindow(const engine::ThinkingWindow& w, const json::Value& want, const std::string& label) {
    expectNumber(w.liveThinking(), want["liveThinking"], label + " liveThinking");
    expectNumber(w.thinkingShare(), want["thinkingShare"], label + " thinkingShare");
    expectNumber(w.weight, want["weight"], label + " weight");
}

class TaskScore : public ::testing::Test {
protected:
    static void SetUpTestSuite() {
        file_ = new fixtures::JsonFile(fixtures::fixtureDir() / "task-score-cases.json");
        ASSERT_TRUE(file_->ok);
    }
    static void TearDownTestSuite() {
        delete file_;
        file_ = nullptr;
    }
    static json::Value section(std::string_view name) { return file_->root()[name]; }
    static fixtures::JsonFile* file_;
};
fixtures::JsonFile* TaskScore::file_ = nullptr;

}  // namespace

TEST_F(TaskScore, Continuations) {
    for (const auto& c : section("continuations").elements()) EXPECT_EQ(tasks::isContinuation(*c["text"].string()), c["expect"].isTrue()) << *c["text"].string();
}

TEST_F(TaskScore, PromptLines) {
    for (const auto& c : section("promptLines").elements()) {
        SCOPED_TRACE(*c["name"].string());
        const auto got = tasks::promptLine(*sourceFromId(*c["source"].string()), c["line"]);
        const auto want = c["expect"];
        ASSERT_EQ(got.has_value(), !want.isNull());
        if (!got) continue;
        const char* kinds[] = {"prompt", "command", "commandOutput"};
        EXPECT_TRUE(want["kind"].equals(kinds[static_cast<int>(got->kind)]));
        if (want["continuation"].valid()) {
            EXPECT_EQ(got->continuation, want["continuation"].isTrue());
        }
        EXPECT_EQ(got->t, fixtures::optInt(want["t"]));
    }
}

TEST_F(TaskScore, Segmentation) {
    for (const auto& c : section("segmentation").elements()) {
        SCOPED_TRACE(*c["name"].string());
        std::vector<tasks::ClassifiedLine> lines;
        for (const auto& l : c["lines"].elements()) lines.push_back({*l["line"].string(), *l["t"].truncated(), l["continuation"].isTrue(), l["duplicate"].isTrue()});
        const auto got = tasks::segment(lines);
        const auto want = c["expect"].elements();
        ASSERT_EQ(got.size(), want.size());
        for (std::size_t i = 0; i < want.size(); ++i) {
            EXPECT_EQ(got[i].t, *want[i]["t"].truncated());
            EXPECT_EQ(got[i].continuation, want[i]["continuation"].isTrue());
        }
    }
}

TEST_F(TaskScore, Tasks) {
    int queries = 0;
    for (const auto& c : section("tasks").elements()) {
        SCOPED_TRACE(*c["name"].string());
        const auto events = eventsOf(c["events"]);
        for (const auto& q : c["queries"].elements()) {
            const auto now = fixtures::optInt(q["nowMs"]);
            const auto task = tasks::currentTask(events, now);
            ASSERT_TRUE(task.has_value());
            expectResult(tasks::evaluateTask(task->events, now), q["expect"]);
            ++queries;
        }
    }
    EXPECT_GT(queries, 20);
}

TEST_F(TaskScore, Values) {
    for (const auto& c : section("values").elements()) {
        SCOPED_TRACE(*c["name"].string());
        const auto now = *c["nowMs"].truncated();
        std::vector<tasks::ValuedAgent> valued;
        std::vector<engine::Agent> agents;
        for (const auto& a : c["agents"].elements()) {
            const auto events = eventsOf(a["events"]);
            valued.push_back({*a["id"].string(), fixtures::optInt(a["lastLineMs"]).value_or(0), tasks::agentValue(events, phaseOf(a["phase"]), now)});
            engine::Agent agent;
            agent.id = *a["id"].string();
            agent.parentId = fixtures::optString(a["parentId"]);
            agents.push_back(agent);
        }
        const auto expect = c["expect"];
        for (const auto& v : valued) {
            const auto w = expect["agents"][v.id];
            EXPECT_TRUE(w["form"].equals(v.value.form)) << v.id;
            expectNumber(v.value.score, w["score"], v.id + " score");
            expectInt(v.value.shown, w["shown"], v.id + " shown");
            EXPECT_EQ(v.value.state, fixtures::optString(w["state"])) << v.id;
        }
        const auto expectHighest = [](const tasks::Highest& h, const json::Value& w) {
            EXPECT_TRUE(w["form"].equals(h.form));
            expectNumber(h.score, w["score"], "score");
            expectInt(h.shown, w["shown"], "shown");
            EXPECT_EQ(h.agent, fixtures::optString(w["agent"]));
        };
        const auto groups = engine::boardGroups(agents);
        EXPECT_EQ(groups.size(), expect["groups"].size());
        for (const auto& [root, members] : groups) {
            const auto w = expect["groups"][root];
            EXPECT_EQ(members, strings(w["members"]));
            std::vector<tasks::ValuedAgent> part;
            for (const auto& m : members) {
                for (const auto& v : valued) {
                    if (v.id == m) part.push_back(v);
                }
            }
            expectHighest(tasks::highest(part), w);
        }
        expectHighest(tasks::highest(valued), expect["all"]);
    }
}

TEST_F(TaskScore, Predictions) {
    for (const auto& c : section("predictions").elements()) {
        SCOPED_TRACE(*c["name"].string());
        auto models = tasks::priorModels();
        if (const auto m = c["models"]; m.isObject()) {
            for (auto [name, slot] : {std::pair<const char*, tasks::Model*>{"success", &models.success}, {"stall", &models.stall}, {"fix", &models.fix}}) {
                slot->coefficients.clear();
                for (const auto& x : m[name]["coefficients"].elements()) slot->coefficients.push_back(*x.number());
            }
        }
        const auto got = tasks::predictions(eventsOf(c["events"]), fixtures::optInt(c["nowMs"]), models);
        const auto w = c["expect"];
        ASSERT_EQ(got.has_value(), !w.isNull());
        if (!got) continue;
        expectNumber(got->success, w["success"], "success");
        expectNumber(got->stall, w["stall"], "stall");
        expectNumber(got->fix, w["fix"], "fix");
        expectFeatures(got->features, w["features"]);
        ASSERT_EQ(got->fixFeatures.has_value(), !w["fixFeatures"].isNull());
        if (got->fixFeatures) expectFeatures(*got->fixFeatures, w["fixFeatures"]);
        expectInt(got->successShown, w["successShown"], "successShown");
        expectInt(got->stallShown, w["stallShown"], "stallShown");
        expectInt(got->fixShown, w["fixShown"], "fixShown");
    }
}

TEST_F(TaskScore, Records) {
    for (const auto& c : section("records").elements()) {
        SCOPED_TRACE(*c["name"].string());
        std::vector<tasks::ModelSpan> spans;
        for (const auto& m : c["models"].elements()) spans.push_back({*m.at(0).truncated(), fixtures::optString(m.at(1)), fixtures::optString(m.at(2))});
        const auto got = tasks::taskRecords(eventsOf(c["events"]), *c["analysisMs"].truncated(), *c["lastLineMs"].truncated(), spans);
        const auto want = c["expect"].elements();
        ASSERT_EQ(got.size(), want.size());
        for (std::size_t i = 0; i < want.size(); ++i) {
            const auto& r = got[i];
            const auto w = want[i];
            EXPECT_EQ(r.ordinal, *w["ordinal"].truncated());
            EXPECT_EQ(r.startMs, *w["startMs"].truncated());
            EXPECT_EQ(r.endMs, *w["endMs"].truncated());
            EXPECT_EQ(r.complete, w["complete"].isTrue());
            EXPECT_EQ(r.model, fixtures::optString(w["model"]));
            EXPECT_EQ(r.effort, fixtures::optString(w["effort"]));
            EXPECT_EQ(r.state, *w["state"].string());
            expectNumber(r.score, w["score"], "score");
            expectDims(r.dims, w["dims"]);
            expectCounts(r.counts, w["counts"]);
            expectInt(r.firstPassMs, w["firstPassMs"], "firstPassMs");
            EXPECT_EQ(r.success, w["success"].isTrue());
            EXPECT_EQ(r.stalled, w["stalled"].isTrue());
            expectRows(r.snapshots, w["snapshots"]);
            expectRows(r.fixRows, w["fixRows"]);
        }
    }
}

TEST_F(TaskScore, Calibration) {
    for (const auto& c : section("calibration").elements()) {
        SCOPED_TRACE(*c["name"].string());
        for (const auto& [source, recs] : c["records"].members()) {
            SCOPED_TRACE(source);
            std::vector<tasks::TaskRecord> records;
            for (const auto& r : recs.elements()) records.push_back(recordOf(r));
            const auto models = tasks::calibrate(records);
            const auto w = c["expect"][source];
            for (auto [name, model] : {std::pair<const char*, const tasks::Model*>{"success", &models.success}, {"stall", &models.stall}, {"fix", &models.fix}}) {
                SCOPED_TRACE(name);
                const auto wm = w["models"][name];
                EXPECT_EQ(model->calibrated, wm["calibrated"].isTrue());
                EXPECT_EQ(model->tasks, *wm["tasks"].truncated());
                const auto coefficients = wm["coefficients"].elements();
                ASSERT_EQ(model->coefficients.size(), coefficients.size());
                for (std::size_t k = 0; k < coefficients.size(); ++k) EXPECT_NEAR(model->coefficients[k], *coefficients[k].number(), kTolerance);
            }
            const auto probes = c["probes"].elements();
            const auto wantProbes = w["probes"].elements();
            ASSERT_EQ(probes.size(), wantProbes.size());
            for (std::size_t i = 0; i < probes.size(); ++i) {
                const auto name = *probes[i]["model"].string();
                const auto& model = name == "success" ? models.success : name == "stall" ? models.stall : models.fix;
                tasks::Features f;
                for (const auto& [k, v] : probes[i]["features"].members()) f[k] = *v.number();
                const double p = tasks::probability(model.coefficients, f, name);
                EXPECT_NEAR(p, *wantProbes[i]["p"].number(), kTolerance);
                EXPECT_EQ(tasks::roundHalfAway(p * 100), *wantProbes[i]["shown"].truncated());
            }
        }
    }
}

TEST_F(TaskScore, Comparison) {
    for (const auto& c : section("comparison").elements()) {
        SCOPED_TRACE(*c["name"].string());
        std::vector<tasks::TaskRecord> records;
        for (const auto& r : c["records"].elements()) records.push_back(recordOf(r));
        std::optional<SourceKind> source;
        if (const auto s = c["source"].string()) source = sourceFromId(*s);
        const auto got = tasks::compare(records, *c["first"].string(), *c["last"].string(), source);
        const auto bySource = c["expect"]["bySource"].elements();
        ASSERT_EQ(got.bySource.size(), bySource.size());
        for (std::size_t i = 0; i < bySource.size(); ++i) {
            EXPECT_TRUE(bySource[i]["source"].equals(sourceId(got.bySource[i].source)));
            expectAggregate(got.bySource[i].aggregate, bySource[i]);
        }
        const auto byModel = c["expect"]["byModel"].elements();
        ASSERT_EQ(got.byModel.size(), byModel.size());
        for (std::size_t i = 0; i < byModel.size(); ++i) {
            EXPECT_EQ(got.byModel[i].model, *byModel[i]["model"].string());
            const auto sources = strings(byModel[i]["sources"]);
            ASSERT_EQ(got.byModel[i].sources.size(), sources.size());
            for (std::size_t k = 0; k < sources.size(); ++k) EXPECT_EQ(sourceId(got.byModel[i].sources[k]), sources[k]);
            expectAggregate(got.byModel[i].aggregate, byModel[i]);
        }
    }
}

TEST_F(TaskScore, LiveThinking) {
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
                ASSERT_TRUE(got.perModel.contains(model)) << model;
                expectWindow(got.perModel.at(model), w, model);
            }
            ++queries;
        }
    }
    EXPECT_GT(queries, 20);
}
