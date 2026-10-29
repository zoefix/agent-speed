#include <gtest/gtest.h>

#include <ctime>

#include "agentspeed/report_body.hpp"
#include "fixture_support.hpp"

using namespace agentspeed;
using namespace agentspeed::report;
using namespace fixtures;

namespace {

std::int64_t parseInstant(const std::string& text) {
    std::tm tm{};
    strptime(text.substr(0, 19).c_str(), "%Y-%m-%dT%H:%M:%S", &tm);
    return static_cast<std::int64_t>(timegm(&tm)) * 1000 + std::stoi(text.substr(20, 3));
}

double num(const json::Value& v) { return *v.number(); }
std::int64_t integer(const json::Value& v) { return *v.truncated(); }

SourceKind sourceOf(const json::Value& v) { return *sourceFromId(*v.string()); }

Usage usageOf(const json::Value& delta, const json::Value& cost) {
    return {integer(delta["requests"]),       integer(delta["output"]),     integer(delta["input"]),          integer(delta["cachedInput"]),
            integer(delta["cacheWriteInput"]), integer(delta["reasoning"]), integer(delta["measuredOutput"]), integer(delta["measuredMs"]),
            cost.isObject() ? integer(cost["deltaMicroUsd"]) : 0, cost.isObject() ? integer(cost["unpricedRequests"]) : 0};
}

Envelope linuxEnvelope() {
    Envelope envelope;
    envelope.client = ClientInfo("1.5.0", "Linux", 6);
    return envelope;
}

struct Built {
    Envelope envelope;
    WindowContent content;
    Sample sample;
    Options options;
};

// Each report example rebuilt from its own numbers (the C# test of the same name does the same).
Built fromExample(const json::Value& report) {
    Built b;
    const auto client = report["client"];
    const auto title = report["title"];
    const auto activity = *report["activity"].string();
    b.envelope = Envelope{ClientInfo(*client["version"].string(), *client["platform"].string(), static_cast<int>(integer(client["osMajor"]))),
                          *report["deviceId"].string(),
                          *report["username"].string(),
                          *report["run"]["id"].string(),
                          static_cast<int>(integer(report["run"]["seq"])),
                          parseInstant(*report["sentAt"].string()),
                          integer(report["windowMs"]),
                          activity == "active" ? Activity::Active : activity == "idle" ? Activity::Idle : Activity::Leaving,
                          static_cast<int>(integer(title["level"]))};
    for (const auto section : kAllSections)
        if (report[sectionName(section)].valid()) b.options.sections.insert(section);
    const auto cost = report["cost"];
    b.options.totalMetric = title["metric"].equals("total");
    b.options.redline = num(title["redline"]);
    b.options.priceVersion = cost.isObject() ? static_cast<int>(integer(cost["priceVersion"])) : 1;
    if (const auto rt = report["realtime"]; rt.isObject()) {
        b.options.stream = StreamBody{rt["observationAgeMs"].isNumber() ? std::optional<std::int64_t>(integer(rt["observationAgeMs"])) : std::nullopt, 65'536};
    }

    auto counters = std::make_shared<Counters>();
    Facts facts;
    TrafficSample traffic;
    const auto models = report["models"].elements();
    for (const auto& model : models) {
        const auto count = static_cast<int>(integer(model["generating"]));
        if (count > 0) facts.generatingModels[{*model["id"].string(), sourceOf(model["sources"].at(0))}] = count;
    }
    for (const auto& entry : report["sources"].elements()) {
        const auto kind = sourceOf(entry["source"]);
        const auto live = entry["live"];
        const auto entryCost = entry["cost"];
        b.sample.sources[kind] = {{num(live["outputTokS"]), num(live["totalTokS"])},
                                  num(live["estimatedShare"]),
                                  entryCost.isObject() ? num(entryCost["liveMicroUsdPerSecond"]) : 0,
                                  entryCost.isObject() ? num(entryCost["knownShare"]) : 1};
        const auto a = entry["agents"];
        facts.agents[kind] = {static_cast<int>(integer(a["generating"])), static_cast<int>(integer(a["tools"])), static_cast<int>(integer(a["active"]))};
        std::string own = "unknown";
        for (const auto& model : models)
            for (const auto& s : model["sources"].elements())
                if (s.equals(sourceId(kind))) own = *model["id"].string();
        counters->add(usageOf(entry["delta"], entryCost), {own, kind});
        if (const auto t = entry["traffic"]; t.isObject()) {
            if (t["rxBytesPerSecond"].isNumber()) traffic.sourceRates[kind] = {num(t["rxBytesPerSecond"]), num(t["txBytesPerSecond"])};
            traffic.totals.sources[kind] = {static_cast<std::uint64_t>(integer(t["rxBytes"])), static_cast<std::uint64_t>(integer(t["txBytes"])), 0, 0};
            traffic.enabled.insert(kind);
        }
    }
    const auto live = report["live"];
    const auto liveAgents = live["agents"];
    int listed = 0;
    for (const auto& [_, c] : facts.agents) listed += c.active;
    if (const auto unlisted = static_cast<int>(integer(liveAgents["active"])) - listed; unlisted > 0) facts.agents[SourceKind::Gemini] = {0, 0, unlisted};
    if (!report["sources"].valid()) {
        counters->add(usageOf(report["delta"], cost), {"unknown", SourceKind::Codex});
        facts.agents = {{SourceKind::Codex,
                         {static_cast<int>(integer(liveAgents["generating"])), static_cast<int>(integer(liveAgents["tools"])),
                          static_cast<int>(integer(liveAgents["active"]))}}};
    }

    std::optional<traffic::Rate> peakRate;
    if (const auto t = report["traffic"]; t.isObject()) {
        traffic.status = t["status"].equals("ok") ? TrafficStatus::Ok : t["status"].equals("off") ? TrafficStatus::Off : TrafficStatus::Unavailable;
        traffic.includesTools = traffic.status != TrafficStatus::Ok || t["includesTools"].isTrue();
        if (traffic.status == TrafficStatus::Ok && t["rxBytesPerSecond"].isNumber()) traffic.rate = traffic::Rate{num(t["rxBytesPerSecond"]), num(t["txBytesPerSecond"])};
        if (traffic.status == TrafficStatus::Ok && t["peakRxBytesPerSecond"].isNumber())
            peakRate = traffic::Rate{num(t["peakRxBytesPerSecond"]), num(t["peakTxBytesPerSecond"])};
    }
    traffic.totals.epoch = 9;
    facts.counters = counters;
    facts.epoch = 2;
    b.sample.live = {num(live["outputTokS"]), num(live["totalTokS"])};
    b.sample.estimatedShare = num(live["estimatedShare"]);
    b.sample.liveCost = cost.isObject() ? num(cost["liveMicroUsdPerSecond"]) : 0;
    b.sample.knownShare = cost.isObject() ? num(cost["knownShare"]) : 1;
    b.sample.facts = facts;
    b.sample.traffic = traffic;

    // The performance score's live values and rows (METRICS 11.10): one agent per model entry with its value, the entries'
    // thinking; the retired taskScore carries the thinking only.
    PerfRows perfRows;
    const auto perfTop = report["perfScore"], legacyTop = report["taskScore"];
    if (perfTop.isObject() || legacyTop.isObject()) {
        const bool legacy = !perfTop.isObject();
        (legacy ? b.options.extensions.taskScore : b.options.extensions.perfScore) = true;
        auto perf = std::make_shared<PerfFacts>();
        perf->thinking = num((legacy ? legacyTop : perfTop)["thinkingTokS"]);
        for (const auto& model : models) {
            const auto value = model[legacy ? "taskScore" : "perfScore"];
            const auto id = *model["id"].string();
            for (const auto form : {perf::Form::Live, perf::Form::Resting}) {
                if (value["form"].equals(perf::formId(form))) perf->agents.push_back({sourceOf(model["sources"].at(0)), id, form, value["score"].number()});
            }
            perf->modelThinking[id] = num(value["thinkingTokS"]);
            for (const auto& row : model["perfStats"].elements()) {
                PerfRow r;
                auto& rec = r.record;
                rec.n = integer(row["n"]);
                rec.outputSum = integer(row["outputSum"]);
                rec.durationMsSum = integer(row["durationMsSum"]);
                for (const auto& bin : row["latencyBins"].elements()) rec.latencyBins[static_cast<std::size_t>(integer(bin.at(0)))] += integer(bin.at(1));
                rec.reasonN = integer(row["reasonN"]);
                rec.reasonOutput = integer(row["reasonOutput"]);
                rec.reasonDurationMsSum = integer(row["reasonDurationMsSum"]);
                rec.cacheN = integer(row["cacheN"]);
                rec.cacheInput = integer(row["cacheInput"]);
                rec.cacheCached = integer(row["cacheCached"]);
                rec.toolN = integer(row["toolN"]);
                rec.toolLnSum = num(row["toolLnSum"]);
                rec.toolFailed = integer(row["toolFailed"]);
                rec.activeMs = integer(row["activeMs"]);
                perfRows[{id, sourceOf(row["source"])}] += r;
            }
        }
        b.sample.facts.perf = perf;
    }

    Sample baseline;
    baseline.facts.epoch = 2;
    baseline.traffic = traffic;
    baseline.traffic.totals = {9, {}};
    b.content = Window(baseline).content(b.sample);
    b.content.peak = {num(report["peak"]["outputTokS"]), num(report["peak"]["totalTokS"])};
    b.content.peakCost = cost.isObject() ? num(cost["peakMicroUsdPerSecond"]) : 0;
    b.content.peakRate = peakRate;
    b.content.perf = perfRows;
    return b;
}

// The first place two documents differ (numbers by value, member order checked separately), or empty.
std::string difference(const json::Value& want, const json::Value& got, const std::string& path) {
    if (want.isNumber() && got.isNumber()) return std::abs(num(want) - num(got)) < 1e-9 ? "" : path + ": " + std::string(want.raw()) + " != " + std::string(got.raw());
    if (want.type() != got.type()) return path + ": types differ";
    if (want.isObject()) {
        std::vector<std::string> a, b;
        for (const auto& [name, _] : want.members()) a.push_back(name);
        for (const auto& [name, _] : got.members()) b.push_back(name);
        if (a != b) return path + ": members differ";
        for (const auto& [name, value] : want.members())
            if (auto inner = difference(value, got[name], path + "." + name); !inner.empty()) return inner;
        return "";
    }
    if (want.isArray()) {
        if (want.size() != got.size()) return path + ": sizes differ";
        for (std::uint32_t i = 0; i < want.size(); ++i)
            if (auto inner = difference(want.at(i), got.at(i), path + "[" + std::to_string(i) + "]"); !inner.empty()) return inner;
        return "";
    }
    return want.raw() == got.raw() ? "" : path + ": " + std::string(want.raw()) + " != " + std::string(got.raw());
}

}  // namespace

class ReportExamples : public testing::TestWithParam<const char*> {};

TEST_P(ReportExamples, AreRebuiltExactly) {
    const auto text = readText(fixtureDir() / "report-examples" / GetParam());
    json::Document example;
    ASSERT_TRUE(example.parse(text));
    const auto b = fromExample(example.root());
    const auto built = fitted(b.envelope, b.content, b.sample, b.options).data;
    json::Document doc;
    ASSERT_TRUE(doc.parse(built));
    EXPECT_EQ(difference(example.root(), doc.root(), "$"), "");
}

INSTANTIATE_TEST_SUITE_P(Report, ReportExamples,
                         testing::Values("report-active.json", "report-idle.json", "report-leaving.json", "report-minimal.json", "report-websocket.json",
                                         "report-perfscore.json", "report-perfscore-websocket.json", "report-taskscore.json",
                                         "report-taskscore-websocket.json"));

TEST(ReportBody, NumbersAreRoundedHalfAwayFromZero) {
    EXPECT_EQ(Json::number(0.25, 1), "0.3");
    EXPECT_EQ(Json::number(2.5, 0), "3");
    EXPECT_EQ(Json::number(0.0005, 3), "0.001");
    EXPECT_EQ(Json::number(655.0, 1), "655");
    EXPECT_EQ(Json::number(1e-9, 1), "0");
    EXPECT_EQ(Json::quoted("a\"b\\c\n\x01"), "\"a\\\"b\\\\c\\n\\u0001\"");
    EXPECT_EQ(formatInstant(1'792'656'930'500), "2026-10-22T08:15:30.500Z");
}

TEST(ReportBody, ModelsBeyondTheLimitMergeIntoOther) {
    Sample sample;
    auto counters = std::make_shared<Counters>();
    for (int i = 0; i < 5; ++i) counters->add({1, 100 - i, 0, 0, 0, 0, 0, 0, 10, 0}, {"model-" + std::to_string(i), SourceKind::Codex});
    sample.facts.counters = counters;
    Window window(Sample{});
    const auto content = window.content(sample);
    Options options;
    options.sections = {Section::Models, Section::Cost};
    options.maxModels = 3;
    const auto json = body(linuxEnvelope(), content, sample, options);
    const auto* models = json.find("models");
    ASSERT_TRUE(models);
    ASSERT_EQ(models->items().size(), 3U);
    EXPECT_EQ(models->items()[2].find("id")->compact(), "\"other\"");
    EXPECT_EQ(models->items()[2].find("requests")->compact(), "3");
    EXPECT_EQ(models->items()[0].find("id")->compact(), "\"model-0\"");
}

TEST(ReportBody, OversizeDropsModelsThenSources) {
    Sample sample;
    auto counters = std::make_shared<Counters>();
    for (int i = 0; i < 16; ++i) counters->add({1, 100, 0, 0, 0, 0, 0, 0, 0, 0}, {"model-" + std::to_string(i), static_cast<SourceKind>(i)});
    sample.facts.counters = counters;
    const auto content = Window(Sample{}).content(sample);
    Options options;
    options.sections = {Section::Models, Section::Sources};
    options.bodyLimit = 2000;
    const auto result = fitted(linuxEnvelope(), content, sample, options);
    EXPECT_LE(result.data.size(), 2000U);
    EXPECT_FALSE(result.json.find("models"));
    options.bodyLimit = 900;
    const auto smaller = fitted(linuxEnvelope(), content, sample, options);
    EXPECT_FALSE(smaller.json.find("sources"));
}

TEST(ReportBody, TrafficIsAStatusWhenNotOk) {
    Sample sample;
    sample.traffic.status = TrafficStatus::Unavailable;
    Options options;
    options.sections = {Section::Traffic, Section::Sources};
    const auto json = body(linuxEnvelope(), Window(Sample{}).content(sample), sample, options);
    EXPECT_EQ(json.find("traffic")->compact(), R"({"status":"unavailable"})");
}

TEST(ReportBody, ClientInfo) {
    EXPECT_TRUE(ClientInfo::isVersion("1.5.0"));
    EXPECT_TRUE(ClientInfo::isVersion("1.5.0-dev.1"));
    EXPECT_FALSE(ClientInfo::isVersion("1.5"));
    EXPECT_FALSE(ClientInfo::isVersion("1.5.0-"));
    EXPECT_EQ(ClientInfo("1.5", "Linux", 6).version, "0.0.0");
    EXPECT_EQ(ClientInfo("1.5.0", "Linux", 6).json().compact(), R"({"app":"AgentSpeed","version":"1.5.0","platform":"Linux","osMajor":6})");
}

TEST(ReportBody, TitleTrackerFollowsDesign1) {
    TitleTracker title;
    for (double t = 0; t < 2; t += 0.5) title.update(1000, 2000, t);
    EXPECT_EQ(title.level(), 11);  // 0.5 R
    for (double t = 2; t < 4; t += 0.5) title.update(0, 2000, t);
    EXPECT_EQ(title.level(), 1);
    for (double t = 4; t < 8; t += 0.5) title.update(0, 2000, t);
    EXPECT_EQ(title.level(), 0);
    EXPECT_NEAR(TitleTracker::heat(400, 2000), 0.381, 0.001);
}

TEST(ReportBody, ModeUsageKeepsEveryKindWhole) {
    Sample sample;
    auto counters = std::make_shared<Counters>();
    const auto fast = ExecutionModes::of(ModeFact::create(ModeKind::ServiceTier, std::string("priority"), ModeEvidence::Served, std::string("openai")));
    counters->add({2, 300, 0, 0, 0, 0, 300, 1000, 0, 0}, {"gpt-5.5", SourceKind::Codex}, &fast);
    counters->add({1, 100, 0, 0, 0, 0, 100, 500, 0, 0}, {"gpt-5.5", SourceKind::Codex}, nullptr);
    sample.facts.counters = counters;
    const auto content = Window(Sample{}).content(sample);
    Options options;
    options.sections = {Section::Models};
    options.extensions.executionModes = true;
    options.extensions.modelHarnessRequests = true;
    const auto json = body(linuxEnvelope(), content, sample, options);
    const auto& model = json.find("models")->items().at(0);
    ASSERT_TRUE(model.find("modeUsage"));
    std::int64_t tierRequests = 0;
    int tierRows = 0;
    for (const auto& row : model.find("modeUsage")->items()) {
        if (row.find("kind")->compact() != "\"serviceTier\"") continue;
        ++tierRows;
        tierRequests += std::stoll(row.find("requests")->compact());
    }
    EXPECT_EQ(tierRows, 2);  // fast (served by openai) and the unknown bucket
    EXPECT_EQ(tierRequests, 3);
    EXPECT_EQ(model.find("modeUsage")->items().size(), 6U);  // 2 tier rows and one unknown row for each other kind
    EXPECT_EQ(model.find("harnessRequests")->compact(), R"([{"source":"codex","requests":3}])");
}
