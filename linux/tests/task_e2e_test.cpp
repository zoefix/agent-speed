#include <gtest/gtest.h>

#include <algorithm>
#include <map>
#include <memory>

#include "agentspeed/claude_parser.hpp"
#include "agentspeed/codex_parser.hpp"
#include "agentspeed/droid.hpp"
#include "agentspeed/file_reader.hpp"
#include "agentspeed/history.hpp"
#include "agentspeed/line_splitter.hpp"
#include "agentspeed/omp_parser.hpp"
#include "agentspeed/openclaw.hpp"
#include "agentspeed/parents.hpp"
#include "agentspeed/pi_parser.hpp"
#include "agentspeed/task_events.hpp"
#include "agentspeed/tz.hpp"
#include "fixture_support.hpp"

using namespace agentspeed;

namespace {

constexpr double kTolerance = 1e-9;
constexpr double kCalibrationTolerance = 1e-6;

struct Agent {
    std::string id;     // absolute path, `#<session>` for an OpenClaw window
    std::string label;  // relative to spec/fixtures
    TranscriptParser* parser = nullptr;
    std::unique_ptr<tasks::Composer> composer;
};

void expectNumber(std::optional<double> got, const json::Value& want, const std::string& label, double tolerance = kTolerance) {
    SCOPED_TRACE(label);
    if (want.isNull()) {
        EXPECT_FALSE(got.has_value());
        return;
    }
    ASSERT_TRUE(got.has_value());
    EXPECT_NEAR(*got, *want.number(), tolerance);
}

std::optional<bool> optBool(const json::Value& v) { return v.isBool() ? std::optional<bool>(v.isTrue()) : std::nullopt; }

void expectEvent(const tasks::Event& e, const json::Value& w) {
    EXPECT_EQ(e.t, *w["t"].truncated());
    EXPECT_TRUE(w["type"].equals(e.type == tasks::Event::Type::Prompt ? "prompt" : "action"));
    if (e.type == tasks::Event::Type::Prompt) {
        EXPECT_EQ(e.continuation, w["continuation"].isTrue());
        return;
    }
    EXPECT_EQ(e.kind, *w["kind"].string());
    EXPECT_EQ(e.tool, fixtures::optString(w["tool"]));
    EXPECT_EQ(e.digest, fixtures::optString(w["digest"]));
    const auto c = w["command"];
    ASSERT_EQ(e.command.has_value(), c.isObject());
    if (e.command) {
        EXPECT_EQ(e.command->cls, *c["class"].string());
        EXPECT_EQ(e.command->suite, fixtures::optString(c["suite"]));
        EXPECT_EQ(e.command->targeted, c["targeted"].isTrue());
        EXPECT_EQ(e.command->key, *c["key"].string());
    }
    const auto o = w["outcome"];
    ASSERT_EQ(e.outcome.has_value(), o.isObject());
    if (e.outcome) {
        EXPECT_EQ(e.outcome->exitCode, fixtures::optInt(o["exitCode"]));
        EXPECT_EQ(e.outcome->failed, optBool(o["failed"]));
        EXPECT_EQ(e.outcome->t, fixtures::optInt(o["t"]));
    }
    const auto f = w["failures"];
    ASSERT_EQ(e.failures.has_value(), f.isObject());
    if (e.failures) {
        std::vector<std::string> fingerprints;
        for (const auto& x : f["fingerprints"].elements()) fingerprints.push_back(*x.string());
        EXPECT_EQ(e.failures->fingerprints, fingerprints);
        EXPECT_EQ(e.failures->failed, fixtures::optInt(f["failedCount"]));
        EXPECT_EQ(e.failures->passed, fixtures::optInt(f["passedCount"]));
        EXPECT_EQ(e.failures->framework, fixtures::optString(f["framework"]));
    }
    const auto h = w["hunks"];
    ASSERT_EQ(e.hunks.has_value(), h.isArray());
    if (e.hunks) {
        const auto hunks = h.elements();
        ASSERT_EQ(e.hunks->size(), hunks.size());
        for (std::size_t i = 0; i < hunks.size(); ++i) {
            EXPECT_EQ((*e.hunks)[i].file, *hunks[i]["file"].string());
            EXPECT_EQ((*e.hunks)[i].oldDigest, fixtures::optString(hunks[i]["old"]));
            EXPECT_EQ((*e.hunks)[i].newDigest, fixtures::optString(hunks[i]["new"]));
        }
    }
}

void expectCounts(const tasks::TaskCounts& c, const json::Value& w) {
    const std::pair<const char*, std::int64_t> fields[] = {
        {"actions", c.actions},         {"counted", c.counted},   {"edits", c.edits},
        {"oscillations", c.oscillations}, {"loopActions", c.loopActions}, {"runs", c.runs},
        {"failingRuns", c.failingRuns}, {"episodes", c.episodes}, {"recovered", c.recovered},
        {"recoverAttempts", c.recoverAttempts}, {"fixAttempts", c.fixAttempts}, {"fixFailures", c.fixFailures}};
    for (const auto& [name, value] : fields) EXPECT_EQ(value, *w[name].truncated()) << name;
}

void expectDims(const tasks::Dims& d, const json::Value& w) {
    expectNumber(d.V, w["V"], "V");
    expectNumber(d.C, w["C"], "C");
    expectNumber(d.R, w["R"], "R");
    expectNumber(d.E, w["E"], "E");
    expectNumber(d.K, w["K"], "K");
}

void expectRows(const std::vector<std::vector<double>>& got, const json::Value& want) {
    const auto rows = want.elements();
    ASSERT_EQ(got.size(), rows.size());
    for (std::size_t i = 0; i < rows.size(); ++i) {
        const auto cells = rows[i].elements();
        ASSERT_EQ(got[i].size(), cells.size());
        for (std::size_t k = 0; k < cells.size(); ++k) EXPECT_NEAR(got[i][k], *cells[k].number(), kTolerance) << i << "," << k;
    }
}

void expectHighest(const tasks::Highest& h, const json::Value& w, const std::function<std::string(const std::string&)>& label) {
    EXPECT_TRUE(w["form"].equals(h.form));
    expectNumber(h.score, w["score"], "score");
    EXPECT_EQ(h.shown, fixtures::optInt(w["shown"]));
    EXPECT_EQ(h.agent ? std::optional<std::string>(label(*h.agent)) : std::nullopt, fixtures::optString(w["agent"]));
}

void expectAggregate(const tasks::Aggregate& a, const json::Value& w) {
    EXPECT_EQ(a.tasks, *w["tasks"].truncated());
    EXPECT_EQ(a.scoredTasks, *w["scoredTasks"].truncated());
    expectNumber(a.medianScore, w["medianScore"], "medianScore");
    EXPECT_EQ(a.medianShown, fixtures::optInt(w["medianShown"]));
    expectNumber(a.lowerQuartile, w["lowerQuartile"], "lowerQuartile");
    expectNumber(a.upperQuartile, w["upperQuartile"], "upperQuartile");
    const auto bins = w["histogram"].elements();
    for (std::size_t i = 0; i < bins.size(); ++i) EXPECT_EQ(a.histogram[i], *bins[i].truncated());
    EXPECT_EQ(a.successes, *w["successes"].truncated());
    EXPECT_EQ(a.stalls, *w["stalls"].truncated());
    EXPECT_EQ(a.fixFailures, *w["fixFailures"].truncated());
    EXPECT_EQ(a.fixAttempts, *w["fixAttempts"].truncated());
    EXPECT_EQ(a.recovered, *w["recovered"].truncated());
    expectNumber(a.successRate, w["successRate"], "successRate");
    expectNumber(a.stallRate, w["stallRate"], "stallRate");
    expectNumber(a.fixFailureRate, w["fixFailureRate"], "fixFailureRate");
    expectNumber(a.recoverAttempts, w["recoverAttempts"], "recoverAttempts");
    expectNumber(a.timeToFirstPassMs, w["timeToFirstPassMs"], "timeToFirstPassMs");
}

}  // namespace

// spec/fixtures/task-e2e-expected.json: the composer over every scored source, then records, calibration, values.
TEST(TaskE2E, MatchesTheReference) {
    fixtures::JsonFile file(fixtures::fixtureDir() / "task-e2e-expected.json");
    ASSERT_TRUE(file.ok);
    const auto want = file.root();
    const std::string prefix = fixtures::fixtureDir().string() + "/";
    const auto label = [&](const std::string& id) { return id.rfind(prefix, 0) == 0 ? id.substr(prefix.size()) : id; };

    Deduper deduper;
    std::vector<std::unique_ptr<TranscriptParser>> owned;
    std::vector<std::unique_ptr<openclaw::Parser>> windows;
    std::vector<Agent> agents;
    struct NullSink : ResponseSink {
        void onResponse(const ParsedResponse&, bool) override {}
    } sink;
    for (const auto& item : want["order"].elements()) {
        const auto source = *item["source"].string();
        const auto absolute = prefix + *item["path"].string();
        SCOPED_TRACE(absolute);
        if (source == "openclaw") {
            std::map<std::string, std::unique_ptr<tasks::Composer>> composers;
            auto parsers = openclaw::readAll(absolute, deduper, [&](openclaw::Parser& p) -> ResponseSink& {
                auto composer = std::make_unique<tasks::Composer>(p.agentId(), SourceKind::OpenClaw);
                p.setLineObserver(composer.get());
                composers[p.agentId()] = std::move(composer);
                return sink;
            });
            ASSERT_TRUE(parsers.has_value());
            for (auto& p : *parsers) {
                agents.push_back({p->agentId(), label(p->agentId()), p.get(), std::move(composers[p->agentId()])});
                windows.push_back(std::move(p));
            }
            continue;
        }
        std::unique_ptr<TranscriptParser> parser;
        if (source == "codex") {
            parser = std::make_unique<CodexParser>(absolute, deduper);
        } else if (source == "claudeCode" || source == "claudeDesktop") {
            parser = std::make_unique<ClaudeParser>(absolute, deduper);
        } else if (source == "omp") {
            parser = std::make_unique<OmpParser>(absolute, deduper, prefix + *want["roots"]["omp"].string());
        } else if (source == "pi") {
            parser = PiParser::forPath(absolute, deduper);
        } else if (source == "droid") {
            parser = std::make_unique<droid::Parser>(absolute, deduper);
        }
        ASSERT_TRUE(parser) << source;
        auto composer = std::make_unique<tasks::Composer>(absolute, *sourceFromId(source));
        parser->setLineObserver(composer.get());
        if (auto* d = dynamic_cast<droid::Parser*>(parser.get())) {
            d->beginRead(0);
            LineSplitter lines;
            lines.feedPositioned(fixtures::readText(absolute), 0,
                                 [&](std::string_view line, std::int64_t offset, std::int64_t length) { d->feedPositioned(line, offset, length); });
            d->applyCycle(droid::readSidecar(droid::sidecarOf(absolute)), sink);
        } else {
            ASSERT_TRUE(readWholeFile(absolute, [&](std::string_view line) { parser->feed(line, sink); }).ok);
        }
        agents.push_back({absolute, label(absolute), parser.get(), std::move(composer)});
        owned.push_back(std::move(parser));
    }

    // Composed streams.
    ASSERT_EQ(agents.size(), want["agents"].size());
    for (const auto& a : agents) {
        SCOPED_TRACE(a.label);
        const auto w = want["agents"][a.label];
        ASSERT_TRUE(w.isObject());
        EXPECT_TRUE(w["source"].equals(sourceId(a.parser->source())));
        EXPECT_EQ(a.composer->stats().stitched, *w["stitched"].truncated());
        const auto events = w["events"].elements();
        ASSERT_EQ(a.composer->events().size(), events.size());
        for (std::size_t i = 0; i < events.size(); ++i) {
            SCOPED_TRACE("event " + std::to_string(i));
            expectEvent(a.composer->events()[i], events[i]);
        }
    }

    // Records of every agent read whole, days in UTC.
    const history::DayLabeler days(TimeZone::utc());
    const auto analysisMs = *want["analysisMs"].truncated();
    std::vector<tasks::TaskRecord> records;
    for (const auto& a : agents) {
        for (auto& r : tasks::taskRecords(a.composer->events(), analysisMs, a.parser->lastLineMs().value_or(0), a.composer->models())) {
            r.agentId = a.label;
            r.source = a.parser->source();
            r.day = days.label(r.endMs).value_or("");
            records.push_back(std::move(r));
        }
    }
    std::stable_sort(records.begin(), records.end(), [](const tasks::TaskRecord& x, const tasks::TaskRecord& y) {
        if (x.agentId != y.agentId) return tasks::ordinalLess(x.agentId, y.agentId);
        return x.ordinal < y.ordinal;
    });
    const auto wantRecords = want["records"].elements();
    ASSERT_EQ(records.size(), wantRecords.size());
    for (std::size_t i = 0; i < records.size(); ++i) {
        const auto& r = records[i];
        const auto w = wantRecords[i];
        SCOPED_TRACE(r.agentId + " #" + std::to_string(r.ordinal));
        EXPECT_EQ(r.agentId, *w["agentId"].string());
        EXPECT_EQ(r.ordinal, *w["ordinal"].truncated());
        EXPECT_TRUE(w["source"].equals(sourceId(r.source)));
        EXPECT_EQ(r.model, fixtures::optString(w["model"]));
        EXPECT_EQ(r.effort, fixtures::optString(w["effort"]));
        EXPECT_EQ(r.startMs, *w["startMs"].truncated());
        EXPECT_EQ(r.endMs, *w["endMs"].truncated());
        EXPECT_EQ(r.day, *w["day"].string());
        EXPECT_EQ(r.complete, w["complete"].isTrue());
        EXPECT_EQ(r.state, *w["state"].string());
        expectNumber(r.score, w["score"], "score");
        expectDims(r.dims, w["dims"]);
        expectCounts(r.counts, w["counts"]);
        EXPECT_EQ(r.firstPassMs, fixtures::optInt(w["firstPassMs"]));
        EXPECT_EQ(r.success, w["success"].isTrue());
        EXPECT_EQ(r.stalled, w["stalled"].isTrue());
        expectRows(r.snapshots, w["snapshots"]);
        expectRows(r.fixRows, w["fixRows"]);
    }

    // Calibration per source.
    std::map<SourceKind, tasks::Models> calibration;
    for (int k = 0; k <= static_cast<int>(SourceKind::Droid); ++k) {
        const auto source = static_cast<SourceKind>(k);
        std::vector<tasks::TaskRecord> part;
        for (const auto& r : records) {
            if (r.source == source) part.push_back(r);
        }
        if (!part.empty()) calibration[source] = tasks::calibrate(part);
    }
    ASSERT_EQ(calibration.size(), want["calibration"].size());
    for (const auto& [source, models] : calibration) {
        SCOPED_TRACE(std::string(sourceId(source)));
        const auto w = want["calibration"][sourceId(source)];
        for (const auto& [name, model] : {std::pair<const char*, const tasks::Model*>{"success", &models.success}, {"stall", &models.stall}, {"fix", &models.fix}}) {
            SCOPED_TRACE(name);
            EXPECT_EQ(model->calibrated, w[name]["calibrated"].isTrue());
            EXPECT_EQ(model->tasks, *w[name]["tasks"].truncated());
            const auto coefficients = w[name]["coefficients"].elements();
            ASSERT_EQ(model->coefficients.size(), coefficients.size());
            for (std::size_t i = 0; i < coefficients.size(); ++i) EXPECT_NEAR(model->coefficients[i], *coefficients[i].number(), kCalibrationTolerance);
        }
    }

    // Values at nowMs.
    const auto now = *want["nowMs"].truncated();
    std::vector<AgentLink> links;
    for (const auto& a : agents) {
        if (a.parser->source() != SourceKind::OpenClaw) links.push_back(a.parser->link());
    }
    auto parents = parent_links::resolve(links);
    for (const auto& w : windows) {
        if (w->parentId()) parents[w->agentId()] = *w->parentId();
    }
    std::vector<tasks::AgentRow> rows;
    for (const auto& a : agents) {
        tasks::ScoredAgent scored;
        scored.id = a.id;
        scored.source = a.parser->source();
        scored.phase = tasks::effectivePhase(a.parser->phase(), a.parser->lastLineMs(), now);
        scored.lastLineMs = a.parser->lastLineMs();
        if (const auto it = parents.find(a.id); it != parents.end()) scored.parentId = it->second;
        scored.events = &a.composer->events();
        const auto it = calibration.find(scored.source);
        rows.push_back(tasks::agentRow(scored, now, it == calibration.end() ? tasks::priorModels() : it->second));
    }
    const auto wantValues = want["values"].elements();
    ASSERT_EQ(rows.size(), wantValues.size());
    for (const auto& row : rows) {
        const auto id = label(row.agent.id);
        SCOPED_TRACE(id);
        const auto it = std::find_if(wantValues.begin(), wantValues.end(), [&](const json::Value& v) { return v["agentId"].equals(id); });
        ASSERT_NE(it, wantValues.end());
        const auto& w = *it;
        EXPECT_TRUE(w["phase"].equals(phaseId(row.agent.phase)));
        EXPECT_EQ(row.agent.lastLineMs, fixtures::optInt(w["lastLineMs"]));
        EXPECT_EQ(row.agent.parentId ? std::optional<std::string>(label(*row.agent.parentId)) : std::nullopt, fixtures::optString(w["parentId"]));
        EXPECT_EQ(row.taskStartMs, fixtures::optInt(w["taskStartMs"]));
        EXPECT_TRUE(w["form"].equals(row.value.form));
        expectNumber(row.value.score, w["score"], "score");
        EXPECT_EQ(row.value.shown, fixtures::optInt(w["shown"]));
        EXPECT_EQ(row.value.state, fixtures::optString(w["state"]));
        ASSERT_EQ(row.task.has_value(), w["dims"].valid());
        if (row.task) {
            expectDims(row.task->dims, w["dims"]);
            expectNumber(row.task->ig, w["ig"], "ig");
            expectNumber(row.task->loop, w["loop"], "loop");
            expectNumber(row.task->osc, w["osc"], "osc");
            expectCounts(row.task->counts, w["counts"]);
            EXPECT_EQ(row.task->firstPassMs, fixtures::optInt(w["firstPassMs"]));
            expectNumber(row.task->vHeld, w["vHeld"], "vHeld");
            ASSERT_EQ(row.task->openFailure.has_value(), w["openFailure"].isObject());
            if (row.task->openFailure) {
                const auto& f = *row.task->openFailure;
                const auto of = w["openFailure"];
                EXPECT_EQ(f.suite, fixtures::optString(of["suite"]));
                EXPECT_EQ(f.cls, fixtures::optString(of["class"]));
                EXPECT_EQ(f.key, *of["key"].string());
                EXPECT_EQ(f.errors, *of["errors"].truncated());
                EXPECT_EQ(f.peak, *of["peak"].truncated());
                EXPECT_EQ(f.counted, of["counted"].isTrue());
                EXPECT_EQ(f.edited, of["edited"].isTrue());
            }
            ASSERT_EQ(row.task->lastPass.has_value(), w["lastPass"].isObject());
            if (row.task->lastPass) {
                const auto& p = *row.task->lastPass;
                const auto lp = w["lastPass"];
                EXPECT_EQ(p.suite, fixtures::optString(lp["suite"]));
                EXPECT_EQ(p.cls, fixtures::optString(lp["class"]));
                EXPECT_EQ(p.key, *lp["key"].string());
                EXPECT_EQ(p.buildOnly, lp["buildOnly"].isTrue());
            }
        }
        const auto wp = w["predictions"];
        ASSERT_EQ(row.predictions.has_value(), wp.isObject());
        if (row.predictions) {
            expectNumber(row.predictions->success, wp["success"], "success", kCalibrationTolerance);
            expectNumber(row.predictions->stall, wp["stall"], "stall", kCalibrationTolerance);
            expectNumber(row.predictions->fix, wp["fix"], "fix", kCalibrationTolerance);
            EXPECT_EQ(row.predictions->successShown, *wp["successShown"].truncated());
            EXPECT_EQ(row.predictions->stallShown, *wp["stallShown"].truncated());
            EXPECT_EQ(row.predictions->fixShown, fixtures::optInt(wp["fixShown"]));
        }
        for (const auto& [name, model] : {std::pair<const char*, const tasks::Model*>{"success", &row.models.success}, {"stall", &row.models.stall}, {"fix", &row.models.fix}}) {
            EXPECT_EQ(model->calibrated, w["models"][name]["calibrated"].isTrue()) << name;
            EXPECT_EQ(model->tasks, *w["models"][name]["tasks"].truncated()) << name;
        }
    }

    // Sets and board groups.
    std::vector<std::optional<std::vector<SourceKind>>> sets;
    for (const auto& s : want["sets"].elements()) {
        if (s.isNull()) {
            sets.push_back(std::nullopt);
            continue;
        }
        std::vector<SourceKind> sources;
        for (const auto& x : s.elements()) sources.push_back(*sourceFromId(*x.string()));
        sets.push_back(sources);
    }
    const auto values = tasks::setValues(rows, now, sets);
    const auto wantSets = want["taskSets"];
    std::vector<std::string> listed;
    for (const auto& id : values.listed) listed.push_back(label(id));
    std::vector<std::string> wantListed;
    for (const auto& x : wantSets["listed"].elements()) wantListed.push_back(*x.string());
    EXPECT_EQ(listed, wantListed);
    const auto setRows = wantSets["sets"].elements();
    ASSERT_EQ(values.sets.size(), setRows.size());
    for (std::size_t i = 0; i < setRows.size(); ++i) {
        SCOPED_TRACE("set " + std::to_string(i));
        expectHighest(values.sets[i].value, setRows[i], label);
    }
    ASSERT_EQ(values.groups.size(), wantSets["groups"].size());
    for (const auto& g : values.groups) {
        SCOPED_TRACE(label(g.root));
        const auto w = wantSets["groups"][label(g.root)];
        ASSERT_TRUE(w.isObject());
        std::vector<std::string> members, wantMembers;
        for (const auto& m : g.members) members.push_back(label(m));
        for (const auto& m : w["members"].elements()) wantMembers.push_back(*m.string());
        EXPECT_EQ(members, wantMembers);
        expectHighest(g.value, w, label);
    }

    // Comparison.
    const auto comparison = tasks::compare(records, *want["comparisonRange"]["first"].string(), *want["comparisonRange"]["last"].string());
    const auto bySource = want["comparison"]["bySource"].elements();
    ASSERT_EQ(comparison.bySource.size(), bySource.size());
    for (std::size_t i = 0; i < bySource.size(); ++i) {
        EXPECT_TRUE(bySource[i]["source"].equals(sourceId(comparison.bySource[i].source)));
        expectAggregate(comparison.bySource[i].aggregate, bySource[i]);
    }
    const auto byModel = want["comparison"]["byModel"].elements();
    ASSERT_EQ(comparison.byModel.size(), byModel.size());
    for (std::size_t i = 0; i < byModel.size(); ++i) {
        EXPECT_EQ(comparison.byModel[i].model, *byModel[i]["model"].string());
        expectAggregate(comparison.byModel[i].aggregate, byModel[i]);
    }
}
