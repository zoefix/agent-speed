#include <gtest/gtest.h>

#include <functional>

#include "agentspeed/report_body.hpp"
#include "agentspeed/report_task.hpp"
#include "fixture_support.hpp"

using namespace agentspeed;
using namespace agentspeed::report;
using namespace fixtures;

namespace {

struct Vectors {
    std::string text = readText(fixtureDir() / "report-vectors.json");
    json::Document doc;
    Vectors() { EXPECT_TRUE(doc.parse(text)); }
    json::Value of(std::string_view key) const { return doc.root()["taskScore"][key]; }
};

const Vectors& vectors() {
    static const Vectors v;
    return v;
}

TaskOutcome outcomeOf(const json::Value& record) {
    TaskOutcome o;
    o.actions = record["actions"].truncated().value_or(0);
    if (record["score"].isNumber()) o.score = record["score"].number();
    o.success = record["success"].isTrue();
    o.stalled = record["stalled"].isTrue();
    const auto counts = record["counts"];
    o.fixAttempts = counts["fixAttempts"].truncated().value_or(0);
    o.fixFailures = counts["fixFailures"].truncated().value_or(0);
    o.recovered = counts["recovered"].truncated().value_or(0);
    o.recoverAttempts = counts["recoverAttempts"].truncated().value_or(0);
    return o;
}

TaskRow rowOf(const json::Value& v) {
    TaskRow r;
    r.completed = *v["completed"].truncated();
    r.scored = *v["scored"].truncated();
    r.scoreSum = *v["scoreSum"].truncated();
    for (std::uint32_t i = 0; i < 10; ++i) r.scoreBins[i] = *v["scoreBins"].at(i).truncated();
    r.successes = *v["successes"].truncated();
    r.stalls = *v["stalls"].truncated();
    r.fixAttempts = *v["fixAttempts"].truncated();
    r.fixFailures = *v["fixFailures"].truncated();
    r.recovered = *v["recovered"].truncated();
    r.recoverAttempts = *v["recoverAttempts"].truncated();
    return r;
}

std::string statsJson(const TaskRows& rows) {
    std::map<std::string, std::vector<std::pair<SourceKind, TaskRow>>> byModel;
    for (const auto& [key, row] : rows) byModel[key.modelId].emplace_back(key.source, row);
    std::string out = "{";
    for (auto& [model, list] : byModel) {
        std::sort(list.begin(), list.end(), [](const auto& a, const auto& b) { return a.first < b.first; });
        out += (out.size() > 1 ? "," : "") + Json::quoted(model) + ":[";
        for (std::size_t i = 0; i < list.size(); ++i) out += (i ? "," : "") + list[i].second.json(list[i].first).compact();
        out += "]";
    }
    return out + "}";
}

// Equal as JSON values: numbers compare by value (Python writes 0.0 where the builder writes 0).
bool sameJson(const std::string& a, const std::string& b) {
    json::Document x, y;
    if (!x.parse(a) || !y.parse(b)) return false;
    std::function<bool(const json::Value&, const json::Value&)> same = [&](const json::Value& p, const json::Value& q) {
        if (p.isNumber() && q.isNumber()) return std::abs(*p.number() - *q.number()) < 1e-9;
        if (p.type() != q.type()) return false;
        if (p.isObject()) {
            const auto pm = p.members(), qm = q.members();
            if (pm.size() != qm.size()) return false;
            for (std::size_t i = 0; i < pm.size(); ++i)
                if (pm[i].first != qm[i].first || !same(pm[i].second, qm[i].second)) return false;
            return true;
        }
        if (p.isArray()) {
            if (p.size() != q.size()) return false;
            for (std::uint32_t i = 0; i < p.size(); ++i)
                if (!same(p.at(i), q.at(i))) return false;
            return true;
        }
        return p.raw() == q.raw();
    };
    return same(x.root(), y.root());
}

}  // namespace

TEST(ReportTaskScore, Capability) {
    for (const auto& item : vectors().of("capability").elements()) {
        SCOPED_TRACE(*item["name"].string());
        auto text = compactJson(item["document"]);
        text.insert(text.size() - 1, std::string(text.size() > 2 ? "," : "") +
                                         R"("protocol":"agent-speed-report","versions":[1],"service":{"name":"E","terms":"https://e.com/t","privacy":"https://e.com/p"},"api":"https://api.e.com")");
        json::Document doc;
        ASSERT_TRUE(doc.parse(text));
        const auto discovery = parseDiscovery(doc.root(), "https://e.com/.well-known/agent-speed-report");
        ASSERT_TRUE(discovery);
        Options options;
        if (std::find(discovery->sections.begin(), discovery->sections.end(), "models") != discovery->sections.end() && item["modelsSwitch"].isTrue())
            options.sections.insert(Section::Models);
        options.maxModels = discovery->limits.maxModels;
        options.extensions.taskScore = discovery->has("taskScore");
        EXPECT_EQ(options.taskScoreActive(), item["expect"].isTrue());
    }
}

TEST(ReportTaskScore, Records) {
    for (const auto& item : vectors().of("records").elements()) {
        SCOPED_TRACE(*item["name"].string());
        const auto row = recordRow(outcomeOf(item["record"]));
        if (item["expect"].isNull()) {
            EXPECT_FALSE(row);
            continue;
        }
        ASSERT_TRUE(row);
        EXPECT_EQ(row->json(*sourceFromId(*item["record"]["source"].string())).compact(), compactJson(item["expect"]));
    }
}

TEST(ReportTaskScore, Assembly) {
    for (const auto& item : vectors().of("assembly").elements()) {
        SCOPED_TRACE(*item["name"].string());
        const auto input = item["input"];
        auto facts = std::make_shared<TaskFacts>();
        for (const auto& agent : input["agents"].elements()) {
            TaskAgent a;
            a.source = *sourceFromId(*agent["source"].string());
            a.modelId = *agent["model"].string();
            a.form = *agent["form"].string();
            if (agent["score"].isNumber()) a.score = agent["score"].number();
            facts->agents.push_back(a);
        }
        facts->thinking = *input["thinking"]["total"].number();
        for (const auto& [id, value] : input["thinking"]["models"].members()) facts->modelThinking[id] = *value.number();
        Sample sample;
        auto counters = std::make_shared<Counters>();
        for (const auto& model : input["models"].elements()) {
            const auto source = *sourceFromId(*model["sources"].at(0).string());
            Usage u;
            u.requests = *model["requests"].truncated();
            u.output = *model["output"].truncated();
            u.measuredOutput = *model["measuredOutput"].truncated();
            u.measuredMs = *model["measuredMs"].truncated();
            counters->add(u, {*model["id"].string(), source});
            if (const auto g = *model["generating"].truncated(); g > 0) sample.facts.generatingModels[{*model["id"].string(), source}] = static_cast<int>(g);
        }
        sample.facts.counters = counters;
        sample.facts.tasks = facts;
        auto content = Window(Sample{}).content(sample);
        for (const auto& [model, rows] : input["ledger"].members())
            for (const auto& row : rows.elements()) content.tasks[{model, *sourceFromId(*row["source"].string())}] += rowOf(row);
        Options options;
        options.sections = {Section::Models};
        options.maxModels = static_cast<int>(input["maxModels"].truncated().value_or(16));
        options.extensions.taskScore = true;
        Envelope envelope;
        const auto body = report::body(envelope, content, sample, options);
        EXPECT_TRUE(sameJson(body.find("taskScore")->compact(), compactJson(item["expect"]["taskScore"]))) << body.find("taskScore")->compact();
        EXPECT_TRUE(sameJson(body.find("models")->compact(), compactJson(item["expect"]["models"]))) << body.find("models")->compact();
    }
}

TEST(ReportTaskScore, Ledger) {
    for (const auto& item : vectors().of("ledger").elements()) {
        SCOPED_TRACE(*item["name"].string());
        TaskLedger ledger;
        TaskRows baseline;
        std::uint64_t baselineGeneration = 0;
        std::vector<std::string> builds;
        const auto pending = [&] {
            TaskRows out;
            for (const auto& [key, row] : ledger.rows()) {
                TaskRow change = row;
                if (ledger.generation() == baselineGeneration)
                    if (const auto it = baseline.find(key); it != baseline.end()) change = row - it->second;
                if (!change.zero()) out[key] = change;
            }
            return out;
        };
        for (const auto& event : item["events"].elements()) {
            const auto at = *event["at"].truncated();
            const auto ids = [&](const char* key) {
                std::vector<std::string> list;
                const auto name = *event[key].string();
                if (event["count"].valid()) {
                    for (std::int64_t i = 0; i < *event["count"].truncated(); ++i) list.push_back(name + "-" + std::to_string(i));
                } else {
                    list.push_back(name);
                }
                return list;
            };
            if (event["watch"].valid()) {
                ledger.setWatch(event["watch"].isTrue());
            } else if (event["start"].valid()) {
                for (const auto& task : ids("start")) ledger.start(task, *event["t"].truncated(), at, event["seeding"].isTrue());
            } else if (event["withdraw"].valid()) {
                ledger.drop(*event["withdraw"].string());
            } else if (event["drop"].valid()) {
                ledger.drop(*event["drop"].string());
            } else if (event["complete"].valid()) {
                const auto record = event["record"];
                const ModelKey key{*record["model"].string(), *sourceFromId(*record["source"].string())};
                for (const auto& task : ids("complete")) ledger.complete(task, key, recordRow(outcomeOf(record)));
            } else if (event["build"].valid()) {
                const auto taken = pending();
                baseline = ledger.rows();
                baselineGeneration = ledger.generation();
                ledger.setTaken(ledger.completed());
                const bool models = !event["models"].valid() || event["models"].isTrue();
                builds.push_back("{\"seq\":" + std::to_string(*event["build"].truncated()) + ",\"taskStats\":" + (models ? statsJson(taken) : "null") + "}");
            } else if (event["relaunch"].valid()) {
                ledger = TaskLedger{};
                baseline.clear();
                baselineGeneration = 0;
            }
        }
        std::string got = "[";
        for (std::size_t i = 0; i < builds.size(); ++i) got += (i ? "," : "") + builds[i];
        EXPECT_EQ(got + "]", compactJson(item["expect"]["builds"]));
        std::string watched = "[";
        for (const auto& task : ledger.watchedTasks()) watched += (watched.size() > 1 ? "," : "") + Json::quoted(task);
        EXPECT_EQ(watched + "]", compactJson(item["expect"]["watched"]));
        EXPECT_EQ(statsJson(pending()), compactJson(item["expect"]["pending"]));
    }
}
