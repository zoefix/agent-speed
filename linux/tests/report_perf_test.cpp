#include <gtest/gtest.h>

#include <cmath>
#include <functional>

#include "agentspeed/report_body.hpp"
#include "agentspeed/report_perf.hpp"
#include "fixture_support.hpp"

using namespace agentspeed;
using namespace agentspeed::report;
using namespace fixtures;

// spec/fixtures/report-vectors.json → perfScore (METRICS 11.10; spec/tools/report_perf_score.py).
namespace {

struct Vectors {
    std::string text = readText(fixtureDir() / "report-vectors.json");
    json::Document doc;
    Vectors() { EXPECT_TRUE(doc.parse(text)); }
    json::Value of(std::string_view key) const { return doc.root()["perfScore"][key]; }
};

const Vectors& vectors() {
    static const Vectors v;
    return v;
}

PerfRow rowOf(const json::Value& v) {
    PerfRow row;
    auto& r = row.record;
    r.n = *v["n"].truncated();
    r.outputSum = *v["outputSum"].truncated();
    r.durationMsSum = *v["durationMsSum"].truncated();
    for (const auto& bin : v["latencyBins"].elements()) r.latencyBins[static_cast<std::size_t>(*bin.at(0).truncated())] += *bin.at(1).truncated();
    r.reasonN = *v["reasonN"].truncated();
    r.reasonOutput = *v["reasonOutput"].truncated();
    r.reasonDurationMsSum = *v["reasonDurationMsSum"].truncated();
    r.cacheN = *v["cacheN"].truncated();
    r.cacheInput = *v["cacheInput"].truncated();
    r.cacheCached = *v["cacheCached"].truncated();
    r.toolN = *v["toolN"].truncated();
    r.toolLnSum = *v["toolLnSum"].number();
    r.toolFailed = *v["toolFailed"].truncated();
    r.activeMs = *v["activeMs"].truncated();
    return row;
}

std::string statsJson(const PerfRows& rows) {
    std::map<std::string, std::vector<std::pair<SourceKind, PerfRow>>> byModel;
    for (const auto& [key, row] : rows) byModel[key.modelId].emplace_back(key.source, row);
    std::string out = "{";
    for (auto& [model, list] : byModel) {
        out += (out.size() > 1 ? "," : "") + Json::quoted(model) + ":[";
        for (std::size_t i = 0; i < list.size(); ++i) out += (i ? "," : "") + list[i].second.json(list[i].first).compact();
        out += "]";
    }
    return out + "}";
}

// Equal as JSON values: numbers compare by value (Python writes 0.0 where the builder writes 0); with `anyOrder`, object
// members in any order (the ledger's per-model map).
bool sameJson(const std::string& a, const std::string& b, bool anyOrder = false) {
    json::Document x, y;
    if (!x.parse(a) || !y.parse(b)) return false;
    std::function<bool(const json::Value&, const json::Value&)> same = [&](const json::Value& p, const json::Value& q) {
        if (p.isNumber() && q.isNumber()) return std::abs(*p.number() - *q.number()) < 1e-9;
        if (p.type() != q.type()) return false;
        if (p.isObject()) {
            const auto pm = p.members(), qm = q.members();
            if (pm.size() != qm.size()) return false;
            for (std::size_t i = 0; i < pm.size(); ++i) {
                if (anyOrder) {
                    if (!q[pm[i].first].valid() || !same(pm[i].second, q[pm[i].first])) return false;
                } else if (pm[i].first != qm[i].first || !same(pm[i].second, qm[i].second)) {
                    return false;
                }
            }
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

TEST(ReportPerfScore, Edges) {
    const auto edges = vectors().of("edges").elements();
    ASSERT_EQ(edges.size(), static_cast<std::size_t>(perf::kBins + 1));
    for (int j = 0; j <= perf::kBins; ++j) EXPECT_EQ(perf::latencyEdge(j), *edges[static_cast<std::size_t>(j)].truncated());
}

TEST(ReportPerfScore, Capability) {
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
        options.extensions.perfScore = discovery->has("perfScore");
        options.extensions.taskScore = discovery->has("taskScore");
        const std::string got = options.perfScoreActive() ? "perfScore" : options.legacyTaskScoreActive() ? "taskScore" : "";
        EXPECT_EQ(got, item["expect"].string().value_or(""));
    }
}

TEST(ReportPerfScore, Assembly) {
    for (const auto& item : vectors().of("assembly").elements()) {
        SCOPED_TRACE(*item["name"].string());
        const auto input = item["input"];
        const bool legacy = input["extension"].equals("taskScore");
        auto facts = std::make_shared<PerfFacts>();
        for (const auto& agent : input["agents"].elements()) {
            PerfAgent a;
            a.source = *sourceFromId(*agent["source"].string());
            a.modelId = *agent["model"].string();
            for (const auto form : {perf::Form::Live, perf::Form::Resting}) {
                if (agent["form"].equals(perf::formId(form))) a.form = form;
            }
            if (agent["score"].isNumber()) a.score = agent["score"].number();
            facts->agents.push_back(a);
        }
        facts->thinking = *input["thinking"]["total"].number();
        for (const auto& [id, value] : input["thinking"]["models"].members()) facts->modelThinking[std::string(id)] = *value.number();
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
        sample.facts.perf = facts;
        auto content = Window(Sample{}).content(sample);
        for (const auto& [model, rows] : input["ledger"].members())
            for (const auto& row : rows.elements()) content.perf[{std::string(model), *sourceFromId(*row["source"].string())}] += rowOf(row);
        Options options;
        options.sections = {Section::Models};
        options.maxModels = static_cast<int>(input["maxModels"].truncated().value_or(16));
        (legacy ? options.extensions.taskScore : options.extensions.perfScore) = true;
        Envelope envelope;
        const auto body = report::body(envelope, content, sample, options);
        const char* top = legacy ? "taskScore" : "perfScore";
        ASSERT_TRUE(body.find(top));
        EXPECT_TRUE(sameJson(body.find(top)->compact(), compactJson(item["expect"][top]))) << body.find(top)->compact();
        EXPECT_TRUE(sameJson(body.find("models")->compact(), compactJson(item["expect"]["models"]))) << body.find("models")->compact();
    }
}

TEST(ReportPerfScore, Ledger) {
    for (const auto& item : vectors().of("ledger").elements()) {
        SCOPED_TRACE(*item["name"].string());
        PerfLedger ledger;
        PerfRows baseline;
        std::uint64_t baselineGeneration = 0;
        std::vector<std::string> builds;
        const auto pending = [&] {
            PerfRows out;
            for (const auto& [key, row] : ledger.rows()) {
                PerfRow change = row;
                if (ledger.generation() == baselineGeneration)
                    if (const auto it = baseline.find(key); it != baseline.end()) change = row - it->second;
                if (!change.zero()) out[key] = change;
            }
            return out;
        };
        for (const auto& event : item["events"].elements()) {
            const auto at = *event["at"].truncated();
            if (event["watch"].valid()) {
                ledger.setWatch(event["watch"].isTrue());
            } else if (event["response"].valid()) {
                if (event["counted"].valid() && !event["counted"].isTrue()) continue;
                const auto r = event["response"];
                engine::Response response;
                response.startMs = optInt(r["startMs"]);
                response.endMs = *r["endMs"].truncated();
                response.firstMs = optInt(r["firstMs"]);
                response.output = r["output"].count();
                response.reasoning = r["reasoning"].count();
                response.input = r["input"].count();
                response.cachedInput = r["cachedInput"].count();
                ledger.response(*r["agent"].string(), {*r["model"].string(), *sourceFromId(*r["source"].string())}, response);
            } else if (event["call"].valid()) {
                const auto c = event["call"];
                work::Action action;
                action.callMs = *c["callMs"].truncated();
                action.resultMs = optInt(c["resultMs"]);
                action.failed = c["failed"].isBool() ? std::optional<bool>(c["failed"].isTrue()) : std::nullopt;
                ledger.call(*c["agent"].string(), {*c["model"].string(), *sourceFromId(*c["source"].string())}, action, at, event["seeding"].isTrue());
            } else if (event["build"].valid()) {
                const auto taken = pending();
                baseline = ledger.rows();
                baselineGeneration = ledger.generation();
                ledger.setTaken(ledger.responses(), ledger.calls());
                const bool models = !event["models"].valid() || event["models"].isTrue();
                builds.push_back("{\"seq\":" + std::to_string(*event["build"].truncated()) + ",\"perfStats\":" + (models ? statsJson(taken) : "null") + "}");
            } else if (event["relaunch"].valid()) {
                ledger = PerfLedger{};
                baseline.clear();
                baselineGeneration = 0;
            }
        }
        std::string got = "[";
        for (std::size_t i = 0; i < builds.size(); ++i) got += (i ? "," : "") + builds[i];
        EXPECT_TRUE(sameJson(got + "]", compactJson(item["expect"]["builds"]), true)) << got << "]";
        EXPECT_TRUE(sameJson(statsJson(pending()), compactJson(item["expect"]["pending"]), true)) << statsJson(pending());
    }
}
