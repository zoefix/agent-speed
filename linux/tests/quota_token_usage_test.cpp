#include <gtest/gtest.h>

#include <limits>
#include <memory>
#include <string>
#include <vector>

#include "agentspeed/history.hpp"
#include "agentspeed/price_book.hpp"
#include "agentspeed/quota/estimate/estimator.hpp"
#include "agentspeed/quota/estimate/tokens.hpp"
#include "agentspeed/report_service.hpp"
#include "quota_estimate_support.hpp"

// The token counts of each quota entry's own spend (QUOTAS 10.3 Token counts, REPORTING-EXTENSIONS 12): what each
// source reports, the sums and entries of the fixtures, the spend's counts, and the `tokenUsage` member of reports.
namespace {

namespace est = agentspeed::quota::estimate;
namespace report = agentspeed::report;
using agentspeed::json::Value;
using report::Json;
using namespace estimate_fixture;

Value tokenCases() { return cases().root()["tokenUsage"]; }

Json usageJson(const std::optional<est::TokenUsage>& usage) { return usage ? usage->json() : Json::null(); }

Value pipelineCase(const std::string& name) {
    for (const auto& c : cases().root()["pipeline"]["tokens"].elements()) {
        if (*c["name"].string() == name) return c;
    }
    ADD_FAILURE() << "no pipeline case " << name;
    return {};
}

// The entries of a `pipeline.tokens` case, as the engine builds them for the case's service.
std::vector<est::Entry> pipelineEntries(const std::string& name) {
    const auto input = pipelineCase(name)["input"];
    const auto salt = saltFromHex(*input["salt"].string());
    est::Engine engine(salt, 8, *input["install"].string());
    for (const auto& e : input["events"].elements()) addEvent(engine, salt, e);
    const auto now = integer(input["now"]);
    engine.build(now);
    return engine.entries(now, *input["origin"].string());
}

// Fixtures

TEST(QuotaTokenUsage, EachSourceKeepsTheDetailsItsLogsReport) {
    const auto list = tokenCases()["sources"];
    ASSERT_EQ(list.size(), 7u);
    for (const auto& c : list.elements()) {
        SCOPED_TRACE(*c["why"].string());
        const auto u = c["usage"];
        agentspeed::TokenCounts usage;
        usage.input = integer(u["input"]);
        usage.cachedInput = integer(u["cachedInput"]);
        usage.cacheWriteInput = integer(u["cacheWriteInput"]);
        usage.output = integer(u["output"]);
        usage.reasoning = integer(u["reasoning"]);
        EXPECT_EQ(est::countsOf(*c["source"].string(), usage), countsFrom(c["expected"]));
    }
}

TEST(QuotaTokenUsage, SumsMatchTheReference) {
    const auto led = ledgerFrom(tokenCases()["rows"]);
    const auto sums = tokenCases()["sums"];
    ASSERT_EQ(sums.size(), 9u);
    for (const auto& c : sums.elements()) {
        SCOPED_TRACE(*c["name"].string());
        ASSERT_EQ(c["accounts"].size(), 1u);
        const auto got = led.tokenUsage(*c["accounts"].at(0).string(), textOf(c["window"]), integer(c["a"]), integer(c["b"]));
        expectJson(usageJson(got).compact(), c["expected"], "tokenUsage");
    }
}

TEST(QuotaTokenUsage, EntriesMatchTheReference) {
    const auto input = tokenCases()["input"];
    const auto in = reportInput(input);
    // The reference keys the ledger by `accounts` and the tag by `account`; the port keys both by the account.
    ASSERT_EQ(input["accounts"].size(), 1u);
    const auto led = ledgerFrom(tokenCases()["rows"], "a", {{*input["accounts"].at(0).string(), in.account}});
    const auto origin = *tokenCases()["origin"].string();
    const auto entries = tokenCases()["entries"];
    ASSERT_EQ(entries.size(), 2u);
    for (const auto& c : entries.elements()) {
        SCOPED_TRACE(*c["name"].string());
        const auto entry = est::reportEntry(in, integer(c["now"]), led, 8, origin);
        const auto sent = entry.json(true);
        expectJson(sent.compact(), c["expected"], "entry");
        EXPECT_EQ(sent.members().back().first, "tokenUsage");
        EXPECT_FALSE(entry.json().find("tokenUsage")) << "no member without the extension";
    }
}

// The spend's counts

TEST(QuotaTokenUsage, AResponseCarriesItsCountsAndWhetherItsModelIsPublic) {
    const auto table = agentspeed::pricing::PriceBook::bundled();
    agentspeed::ParsedResponse known;
    known.key = "a";
    known.source = agentspeed::SourceKind::ClaudeCode;
    known.agentId = "/a";
    known.model = "claude-opus-5-5";
    known.startMs = 1'000;
    known.endMs = 2'000;
    known.tokens.input = 1300;
    known.tokens.cachedInput = 1000;
    known.tokens.cacheWriteInput = 200;
    known.tokens.output = 80;
    known.tokens.reasoning = 30;
    const auto spend = agentspeed::history::estimateResponseOf(known, known.source, 5, *table, *table);
    EXPECT_EQ(spend.model, "claude-opus-5-5");
    EXPECT_TRUE(spend.publicModel);
    EXPECT_EQ(spend.counts, (est::Counts{1300, 80, 1000, 200, std::nullopt})) << "Claude's logs carry no thinking tokens";

    auto unknown = known;
    unknown.model = "Acme-Internal-7";
    const auto raw = agentspeed::history::estimateResponseOf(unknown, unknown.source, std::nullopt, *table, *table);
    EXPECT_EQ(raw.model, "acme-internal-7") << "kept locally to match, never sent";
    EXPECT_FALSE(raw.publicModel);
    auto unnamed = known;
    unnamed.model.reset();
    EXPECT_FALSE(agentspeed::history::estimateResponseOf(unnamed, unnamed.source, std::nullopt, *table, *table).publicModel);

    // The live engine's responses, this computer's and the hosts', take the same form.
    const auto live = est::liveResponseOf(known, "host-1", 5, *table);
    EXPECT_EQ(live.machine, "host-1");
    EXPECT_EQ(live.counts, spend.counts);
    EXPECT_TRUE(live.publicModel);
    EXPECT_EQ(est::responseOf(raw).counts, raw.counts);
    EXPECT_FALSE(est::responseOf(raw).publicModel);
}

TEST(QuotaTokenUsage, ARowKeepsAMissingDetailMissingAndAResponseWithoutCountsUnmeasured) {
    est::Ledger led;
    led.add("a", "codex", "gpt-5.5", 1'000, 1, 1, 12, est::Counts{10, 2, 4, std::nullopt, 1}, true);
    led.add("a", "codex", "gpt-5.5", 2'000, 1, 1, 6, est::Counts{5, 1, 1, std::nullopt, 0}, false);
    const auto summed = led.tokenUsage("a", std::nullopt, 0, 30'000);
    ASSERT_TRUE(summed);
    EXPECT_EQ(summed->counts, (est::Counts{15, 3, 5, std::nullopt, 1}));
    // Contributions that disagree on the model's id make the row not public: the null row.
    EXPECT_EQ(summed->models, (std::vector<est::ModelTokens>{{std::nullopt, 15, 3}}));

    led.add("b", "codex", "gpt-5.5", 61'000, 1, 1, 6, est::Counts{5, 1, 1, std::nullopt, 0}, true);
    led.add("b", "codex", "gpt-5.5", 62'000, 1);
    EXPECT_FALSE(led.tokenUsage("b", std::nullopt, 60'000, 90'000)) << "a response recorded without counts: unmeasured, never 0";
    const auto zero = led.tokenUsage("b", std::nullopt, 180'000, 240'000);
    ASSERT_TRUE(zero) << "no rows: a measured zero";
    EXPECT_EQ(zero->counts, est::Counts::zero());
    EXPECT_TRUE(zero->models.empty());

    EXPECT_FALSE(est::merged(est::Counts::zero(), std::nullopt));
    EXPECT_EQ(est::merged(est::Counts{1, 1, 0, 0, 0}, est::Counts{2, 2, 1, std::nullopt, 1}), (est::Counts{3, 3, 1, std::nullopt, 1}));
}

TEST(QuotaTokenUsage, AbsurdCountsEndUnsafeInsteadOfOverflowing) {
    constexpr auto kMax = std::numeric_limits<std::int64_t>::max();
    EXPECT_EQ(est::saturatingSum(kMax - 1, 5), kMax);
    est::Ledger led;
    const est::Counts huge{kMax - 1, 1, std::nullopt, std::nullopt, std::nullopt};
    for (std::int64_t minute = 0; minute < 3; ++minute) led.add("a", "codex", "gpt-5.5", minute * 60'000, 1, 1, 0, huge, true);
    EXPECT_FALSE(led.tokenUsage("a", std::nullopt, 0, 180'000));

    // 2^53 − 1 is still safe; one more token is not.
    est::Ledger edge;
    edge.add("a", "codex", "gpt-5.5", 0, 1, 1, 0, est::Counts{est::kMaxSafeInteger, 0, 0, std::nullopt, 0}, true);
    EXPECT_TRUE(edge.tokenUsage("a", std::nullopt, 0, 120'000));
    edge.add("a", "codex", "gpt-5.5", 60'000, 1, 1, 0, est::Counts{1, 0, 0, std::nullopt, 0}, true);
    EXPECT_FALSE(edge.tokenUsage("a", std::nullopt, 0, 120'000));
}

TEST(QuotaTokenUsage, UnmeasuredCountsAreNullNeverZero) {
    const auto entries = pipelineEntries("a response recorded without counts");
    ASSERT_EQ(entries.size(), 3u);
    std::vector<bool> unmeasured;
    for (const auto& e : entries) unmeasured.push_back(!e.tokenUsage);
    EXPECT_EQ(unmeasured, (std::vector<bool>{true, true, false}));
    EXPECT_EQ(entries[0].json(true).find("tokenUsage")->kind(), Json::Kind::Null);
    EXPECT_GT(entries[0].usedMicroUsd, 0) << "the spend still counts the response";
}

// Reports

std::optional<report::Discovery> discoveryWith(const std::string& extensions) {
    const auto service = *report::ServiceUrl::parse("https://radar.example");
    return report::parseDiscoveryBody(
        R"({"protocol":"agent-speed-report","versions":[1],"service":{"name":"Radar","terms":"https://radar.example/",)"
        R"("privacy":"https://radar.example/"},"api":"https://radar.example","extensions":)" + extensions + "}",
        service);
}

TEST(QuotaTokenUsageReport, TheExtensionNeedsTheIntegerOne) {
    const auto both = discoveryWith(R"({"quotaEstimate":1,"quotaTokenUsage":1})");
    ASSERT_TRUE(both);
    EXPECT_TRUE(both->has("quotaTokenUsage"));
    EXPECT_EQ(both->extensions.at("quotaTokenUsage"), 1);
    for (const auto* value : {"true", R"("1")", "2", "1.5", "null", "1.0", "1e0", "-1", "{}"}) {
        const auto other = discoveryWith(std::string(R"({"quotaTokenUsage":)") + value + "}");
        ASSERT_TRUE(other) << value;
        EXPECT_FALSE(other->has("quotaTokenUsage")) << value;
    }
    // Stored with the identity and read back.
    const auto stored = report::parseDiscoveryBody(report::discoveryJson(*both), *report::ServiceUrl::parse("https://radar.example"));
    ASSERT_TRUE(stored);
    EXPECT_TRUE(stored->has("quotaTokenUsage"));
    // The preview's assumed service advertises it (DESIGN 11, Preview window).
    const auto assumed = report::assumedDiscovery(*report::ServiceUrl::parse("https://radar.example"), "Radar");
    EXPECT_EQ(assumed.extensions, (std::map<std::string, int>{{"perfScore", 1}, {"quotaTokenUsage", 1}}));
}

report::Sample busySample() {
    report::Sample sample;
    auto counters = std::make_shared<report::Counters>();
    for (int i = 0; i < 3; ++i) counters->add({1, 100 - i, 0, 0, 0, 0, 0, 0, 10, 0}, {"model-" + std::to_string(i), agentspeed::SourceKind::Codex});
    sample.facts.counters = counters;
    return sample;
}

std::vector<Json> sentEntries(const std::vector<est::Entry>& entries) {
    std::vector<Json> out;
    for (const auto& e : entries) out.push_back(e.json(true));
    return out;
}

TEST(QuotaTokenUsageReport, ReportsCarryTheCountsOnlyWhenAdvertisedAndDropThemFirst) {
    const auto entries = pipelineEntries("Codex and Claude accounts");
    ASSERT_EQ(entries.size(), 5u);
    const auto sample = busySample();
    const auto content = report::Window(report::Sample{}).content(sample);
    report::Envelope envelope;
    envelope.client = report::ClientInfo("1.8.0", "Linux", 6);
    report::Options options;
    options.sections = {report::Section::Models, report::Section::Cost};
    options.quotas = sentEntries(entries);

    const auto plain = report::fitted(envelope, content, sample, options);
    const auto* without = plain.json.find("quotas");
    ASSERT_TRUE(without);
    ASSERT_EQ(without->items().size(), 5u);
    for (std::size_t i = 0; i < entries.size(); ++i) {
        EXPECT_FALSE(without->items()[i].find("tokenUsage"));
        EXPECT_EQ(without->items()[i].compact(), entries[i].json().compact());
    }

    options.extensions.quotaTokenUsage = true;
    const auto http = report::fitted(envelope, content, sample, options);
    const auto* carried = http.json.find("quotas");
    ASSERT_TRUE(carried);
    ASSERT_EQ(carried->items().size(), 5u);
    for (const auto& entry : carried->items()) EXPECT_EQ(entry.members().back().first, "tokenUsage");
    EXPECT_EQ(carried->items()[0].find("tokenUsage")->find("cacheWriteInputTokens")->kind(), Json::Kind::Null) << "Codex logs no cache writes";
    EXPECT_EQ(carried->items()[2].find("tokenUsage")->find("reasoningTokens")->kind(), Json::Kind::Null) << "Claude logs no thinking tokens";
    EXPECT_EQ(http.data.find("claude-mystery-1"), std::string::npos) << "raw names never leave";
    const auto expected = pipelineCase("Codex and Claude accounts")["expected"]["entries"];
    for (std::uint32_t i = 0; i < expected.size(); ++i) expectJson(carried->items().at(i).compact(), expected.at(i), "entry");

    // The WebSocket body (with `realtime` last) carries the same entries.
    auto ws = options;
    ws.stream = report::StreamBody{120, 65'536};
    const auto streamed = report::fitted(envelope, content, sample, ws);
    EXPECT_EQ(streamed.json.find("quotas")->compact(), carried->compact());
    EXPECT_EQ(streamed.json.members().back().first, "realtime");

    // Too large with the counts: they go first, the quotas stay.
    auto tight = options;
    tight.bodyLimit = static_cast<int>(plain.data.size());
    const auto fitted = report::fitted(envelope, content, sample, tight);
    EXPECT_FALSE(fitted.quotasDropped);
    ASSERT_TRUE(fitted.json.find("quotas"));
    EXPECT_EQ(fitted.json.find("quotas")->items().size(), 5u);
    EXPECT_EQ(fitted.data.find("tokenUsage"), std::string::npos);
    EXPECT_TRUE(fitted.json.find("models"));
    EXPECT_EQ(fitted.data, plain.data);
}

}  // namespace
