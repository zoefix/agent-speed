#include <gtest/gtest.h>

#include "agentspeed/unpriced.hpp"
#include "fixture_support.hpp"

using namespace agentspeed;

// spec/fixtures/unpriced-cases.json (METRICS 9.10, spec/tools/unpriced.py).
namespace {

std::shared_ptr<const pricing::PriceTable> tableAt(const std::string& relative) {
    return pricing::PriceTable::load(fixtures::readText(fixtures::fixtureDir() / relative));
}

unpriced::Stored storedOf(const json::Value& v) {
    unpriced::Stored s;
    s.fileVersion = static_cast<int>(*v["fileVersion"].truncated());
    s.requests = *v["requests"].truncated();
    s.unpriced = fixtures::optInt(v["unpriced"]);
    for (const auto& [model, n] : v["models"].members()) s.models[std::string(model)] = *n.truncated();
    for (const auto& r : v["side"].elements()) {
        s.side.push_back({*r["model"].string(), fixtures::optString(r["provider"]), r["fast"].isTrue(), *r["requests"].truncated(),
                          static_cast<int>(*r["version"].truncated())});
    }
    return s;
}

void expectFacts(const std::vector<unpriced::Fact>& got, const json::Value& want) {
    const auto list = want.elements();
    ASSERT_EQ(got.size(), list.size());
    for (std::size_t i = 0; i < list.size(); ++i) {
        EXPECT_EQ(got[i].model, *list[i]["model"].string());
        EXPECT_EQ(got[i].provider, fixtures::optString(list[i]["provider"]));
        EXPECT_EQ(got[i].fast, list[i]["fast"].isTrue());
        EXPECT_EQ(got[i].noDate, list[i]["noDate"].isTrue());
        EXPECT_EQ(got[i].requests, *list[i]["requests"].truncated());
    }
}

std::vector<unpriced::LiveResponse> liveOf(const json::Value& list) {
    std::vector<unpriced::LiveResponse> out;
    for (const auto& r : list.elements()) {
        unpriced::LiveResponse x;
        x.source = *sourceFromId(*r["source"].string());
        x.model = fixtures::optString(r["model"]);
        x.provider = fixtures::optString(r["provider"]);
        x.fast = r["fast"].isTrue();
        x.endMs = *r["endMs"].truncated();
        x.requestCount = r["requestCount"].valid() ? *r["requestCount"].truncated() : 1;
        x.costMicroUsd = fixtures::optInt(r["costMicroUsd"]);
        out.push_back(x);
    }
    return out;
}

}  // namespace

TEST(Unpriced, MatchesTheReferenceCases) {
    fixtures::JsonFile file(fixtures::fixtureDir() / "unpriced-cases.json");
    ASSERT_TRUE(file.ok);
    const auto root = file.root();
    const auto table = tableAt(*root["table"].string());
    ASSERT_TRUE(table);

    for (const auto& c : root["diagnose"].elements()) {
        const auto model = fixtures::optString(c["model"]);
        SCOPED_TRACE(model.value_or("<null>"));
        const bool fast = c["fast"].isTrue();
        const auto got = unpriced::diagnose(*table, model, fixtures::optString(c["provider"]), fast);
        TokenCounts tokens;
        tokens.input = 1000;
        tokens.output = 100;
        const auto cost = table->cost(model, fixtures::optString(c["provider"]), tokens, 1'793'016'000'000, fast);
        EXPECT_EQ(got.has_value(), !cost.has_value());
        if (c["expect"].isNull()) {
            EXPECT_FALSE(got.has_value());
            continue;
        }
        ASSERT_TRUE(got.has_value());
        EXPECT_EQ(unpriced::reasonId(got->reason), *c["expect"]["reason"].string());
        EXPECT_EQ(got->canonical, fixtures::optString(c["expect"]["canonical"]));
        EXPECT_EQ(got->rule, fixtures::optString(c["expect"]["rule"]));
    }

    for (const auto& c : root["read"].elements()) {
        SCOPED_TRACE(*c["name"].string());
        const auto got = unpriced::readPart(storedOf(c["stored"]));
        EXPECT_EQ(got.requests, *c["expect"]["requests"].truncated());
        EXPECT_EQ(got.noCostData, *c["expect"]["noCostData"].truncated());
        EXPECT_EQ(got.derived, *c["expect"]["derived"].truncated());
        expectFacts(got.facts, c["expect"]["facts"]);
    }

    for (const auto& c : root["quote"].elements()) {
        const bool redact = !c["redact"].valid() || c["redact"].isTrue();
        EXPECT_EQ(unpriced::quoted(fixtures::optString(c["text"]), redact), *c["expect"].string());
    }

    for (const auto& c : root["live"].elements()) {
        SCOPED_TRACE(*c["name"].string());
        const auto got = unpriced::liveParts(liveOf(c["responses"]), *c["today"].string());
        const auto want = c["expect"].elements();
        ASSERT_EQ(got.size(), want.size());
        for (std::size_t i = 0; i < want.size(); ++i) {
            EXPECT_EQ(got[i].day, *want[i]["day"].string());
            EXPECT_EQ(sourceId(got[i].source), *want[i]["source"].string());
            EXPECT_EQ(got[i].requests, *want[i]["requests"].truncated());
            EXPECT_EQ(got[i].noCostData, *want[i]["noCostData"].truncated());
            EXPECT_EQ(got[i].derived, *want[i]["derived"].truncated());
            expectFacts(got[i].facts, want[i]["facts"]);
        }
    }

    std::vector<unpriced::Part> stored;
    for (const auto& e : root["stored"].elements()) {
        auto part = unpriced::readPart(storedOf(e["stored"]));
        part.day = *e["day"].string();
        part.source = *sourceFromId(*e["source"].string());
        stored.push_back(std::move(part));
    }
    stored = unpriced::mergeParts(stored);
    for (const auto& c : root["reports"].elements()) {
        SCOPED_TRACE(*c["name"].string());
        const auto view = c["view"];
        std::vector<unpriced::Part> parts = stored;
        if (view["view"].equals("live")) {
            for (const auto& live : root["live"].elements()) {
                if (live["name"].equals(*c["input"].string())) parts = unpriced::liveParts(liveOf(live["responses"]), *live["today"].string());
            }
        }
        const auto reportTable = c["table"].string() ? tableAt(*c["table"].string()) : table;
        std::optional<SourceKind> source;
        if (const auto s = view["source"].string()) source = sourceFromId(*s);
        std::set<SourceKind> excluded;
        for (const auto& x : view["excluded"].elements()) excluded.insert(*sourceFromId(*x.string()));
        const auto groups = unpriced::aggregate(parts, *reportTable, *view["first"].string(), *view["last"].string(), source, excluded);
        const unpriced::App app{*c["app"]["version"].string(), *c["app"]["platform"].string(), *c["app"]["os"].string()};
        EXPECT_EQ(unpriced::render(groups, app, *reportTable, *view["view"].string(), *view["first"].string(), *view["last"].string(), source, excluded),
                  *c["expect"].string());
    }
}
