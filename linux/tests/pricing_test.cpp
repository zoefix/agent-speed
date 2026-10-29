#include <gtest/gtest.h>

#include "agentspeed/price_book.hpp"
#include "fixture_support.hpp"

using namespace agentspeed;
using namespace agentspeed::pricing;

namespace {

std::map<std::string, std::shared_ptr<const PriceTable>> fixtureTables(const json::Value& tables) {
    std::map<std::string, std::shared_ptr<const PriceTable>> out;
    for (const auto& [name, path] : tables.members()) {
        TableError error;
        auto table = PriceTable::load(fixtures::readText(fixtures::fixtureDir() / *path.string()), &error);
        EXPECT_TRUE(table) << name << ": " << error.describe();
        out[name] = table;
    }
    return out;
}

TokenCounts tokensOf(const json::Value& v) {
    TokenCounts t;
    t.input = *fixtures::optInt(v["input"]);
    t.cachedInput = *fixtures::optInt(v["cachedInput"]);
    t.cacheWriteInput = *fixtures::optInt(v["cacheWriteInput"]);
    t.cacheWrite1hInput = *fixtures::optInt(v["cacheWrite1hInput"]);
    t.output = *fixtures::optInt(v["output"]);
    t.reasoning = *fixtures::optInt(v["reasoning"]);
    return t;
}

std::optional<std::int64_t> instantOf(const json::Value& v) { return v.isString() ? UtcDay::instantMs(*v.string()) : std::nullopt; }

void checkCost(const json::Value& spec, const PriceTable& table) {
    SCOPED_TRACE(*spec["name"].string());
    const auto tokens = tokensOf(spec["tokens"]);
    const bool fast = spec["fast"].isTrue();
    const auto at = instantOf(spec["at"]);
    const auto model = fixtures::optString(spec["model"]);
    const auto provider = fixtures::optString(spec["provider"]);
    std::optional<std::int64_t> pico;
    std::optional<int> tier;
    const auto resolution = table.resolver().resolve(model, provider);
    if (resolution.kind == ResolutionKind::Priced) {
        if (const auto* pricing = table.models()[static_cast<std::size_t>(resolution.index)].pricingFor(at, resolution.fast || fast)) {
            const auto cost = picoUsd(tokens, *pricing);
            pico = cost.pico;
            tier = cost.tier;
        }
    } else if (resolution.kind == ResolutionKind::Free) {
        pico = 0;
    }
    const auto expect = spec["expect"];
    if (expect["costPicoUsd"].valid()) {
        EXPECT_EQ(pico, fixtures::optInt(expect["costPicoUsd"]));
    }
    EXPECT_EQ(table.cost(model, provider, tokens, at, fast), fixtures::optInt(expect["costMicroUsd"]));
    if (expect["tier"].valid()) {
        const auto want = fixtures::optInt(expect["tier"]);
        EXPECT_EQ(tier, want ? std::optional<int>(static_cast<int>(*want)) : std::nullopt);
    }
}

}  // namespace

TEST(Pricing, BundledTableValidatesWithItsOwnTests) {
    const auto table = PriceBook::bundled();
    ASSERT_TRUE(table);
    EXPECT_GE(table->version(), 1);
    EXPECT_GT(table->models().size(), 10u);
}

TEST(Pricing, ResolveCostAndDayCases) {
    fixtures::JsonFile cases(fixtures::fixtureDir() / "price-cases.json");
    ASSERT_TRUE(cases.ok);
    const auto tables = fixtureTables(cases.root()["tables"]);
    ASSERT_EQ(tables.at("v1")->version(), 1);
    ASSERT_EQ(tables.at("v2")->version(), 2);
    int index = 0;
    for (const auto& spec : cases.root()["resolve"].elements()) {
        const auto& table = *tables.at(*spec["table"].string());
        const auto got = table.label(table.resolver().resolve(fixtures::optString(spec["model"]), fixtures::optString(spec["provider"])));
        EXPECT_EQ(got, *spec["expect"].string()) << "resolve[" << index << "] " << fixtures::optString(spec["model"]).value_or("null");
        ++index;
    }
    EXPECT_GE(index, 40);
    for (const auto& spec : cases.root()["cost"].elements()) checkCost(spec, *tables.at(*spec["table"].string()));
    for (const auto& spec : cases.root()["bundled"]["cost"].elements()) {
        ASSERT_TRUE(spec["at"].valid());
        checkCost(spec, *PriceBook::bundled());
    }
    for (const auto& spec : cases.root()["days"].elements()) {
        SCOPED_TRACE(*spec["name"].string());
        std::map<std::string, int> bindings;
        for (const auto& [day, version] : spec["bindings"].members()) bindings[day] = static_cast<int>(*fixtures::optInt(version));
        std::vector<std::shared_ptr<const PriceTable>> all;
        for (const auto& [name, table] : tables) all.push_back(table);
        PriceBook book(all, bindings);
        const auto day = *spec["date"].string();
        EXPECT_EQ(book.cost(fixtures::optString(spec["model"]), fixtures::optString(spec["provider"]), tokensOf(spec["tokens"]),
                            instantOf(spec["at"]), false, day),
                  fixtures::optInt(spec["costMicroUsd"]));
        EXPECT_EQ(book.versionForDay(day), *fixtures::optInt(spec["version"]));
    }
}

TEST(Pricing, RejectsTablesThatBreakTheRules) {
    TableError error;
    EXPECT_FALSE(PriceTable::load("[1]", &error));
    EXPECT_EQ(error.kind, TableErrorKind::NotJson);
    EXPECT_FALSE(PriceTable::load(R"({"schema": 2})", &error));
    EXPECT_EQ(error.kind, TableErrorKind::UnsupportedSchema);
    std::string text(PriceBook::bundled()->data());
    const auto at = text.find("\"currency\"");
    ASSERT_NE(at, std::string::npos);
    text.replace(text.find("USD", at), 3, "EUR");
    EXPECT_FALSE(PriceTable::load(text, &error));
    EXPECT_EQ(error.kind, TableErrorKind::Invalid);
    EXPECT_EQ(error.errors.front(), "currency must be \"USD\" and unit \"perMillionTokens\"");
}
