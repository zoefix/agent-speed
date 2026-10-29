#include <gtest/gtest.h>

#include "agentspeed/money.hpp"
#include "fixture_support.hpp"

using namespace agentspeed;
using namespace agentspeed::money;

namespace {

std::map<std::string, Decimal> ratesOf(const json::Value& v) {
    std::map<std::string, Decimal> out;
    for (const auto& [code, value] : v.members()) out[code] = *Decimal::parse(*value.string());
    return out;
}

}  // namespace

TEST(Money, FormatCases) {
    fixtures::JsonFile cases(fixtures::fixtureDir() / "money-format.json");
    ASSERT_TRUE(cases.ok);
    const auto& table = CurrencyTable::bundled();
    for (const auto& c : cases.root()["cases"].elements()) {
        const auto form = c["form"].equals("rate") ? Form::Rate : Form::Amount;
        Decimal value;
        if (c["value"].isString()) {
            value = *Decimal::parse(*c["value"].string());
        } else {
            value = amount(*fixtures::optInt(c["costMicroUsd"]), *Decimal::parse(*c["rate"].string()));
        }
        const auto* currency = table.find(*c["currency"].string());
        ASSERT_NE(currency, nullptr);
        const auto got = format(value, *currency, *c["language"].string(), form);
        SCOPED_TRACE(*c["currency"].string() + " " + value.text());
        EXPECT_EQ(got.number, *c["number"].string());
        EXPECT_EQ(got.text, *c["text"].string());
    }
}

TEST(Money, DecimalsAndHelpers) {
    EXPECT_EQ(significant(*Decimal::parse("32.552")), "32.55");
    EXPECT_EQ(significant(*Decimal::parse("158.100039")), "158.1");
    EXPECT_EQ(significant(*Decimal::parse("0.890292")), "0.8903");
    EXPECT_EQ(significant(*Decimal::parse("25842.4")), "25,842");
    EXPECT_EQ(significant(*Decimal::parse("9.99999")), "10.00");
    EXPECT_EQ(csvUsd(1'234'567), "1.234567");
    EXPECT_EQ(csv(4200, *Decimal::parse("32.552"), 0), "0.14");
    EXPECT_EQ(Decimal::ofDouble(0.1).text(), "0.1");
    EXPECT_EQ(withSymbol("CHF", "0.42"), "CHF\xC2\xA0" "0.42");
    EXPECT_EQ(withSymbol("$", "<0.000001"), "<$0.000001");
    const auto common = CurrencyTable::bundled().commonGroup("zh-Hant");
    ASSERT_GE(common.size(), 4u);
    EXPECT_EQ(common[0]->code, "USD");
    EXPECT_EQ(common[1]->code, "TWD");
}

TEST(Money, RateParseAndAcceptCases) {
    fixtures::JsonFile cases(fixtures::fixtureDir() / "rates-cases.json");
    ASSERT_TRUE(cases.ok);
    std::set<std::string> supported;
    for (const auto& code : cases.root()["supported"].elements()) supported.insert(*code.string());
    for (const auto& c : cases.root()["parse"].elements()) {
        SCOPED_TRACE(*c["name"].string());
        const auto shape = rates::shapeOf(*c["provider"].string());
        ASSERT_TRUE(shape);
        const auto parsed = rates::parse(*shape, c["body"], supported);
        const auto expect = c["expect"];
        if (expect.isNull()) {
            EXPECT_FALSE(parsed);
            continue;
        }
        ASSERT_TRUE(parsed);
        EXPECT_EQ(parsed->dateMs, *fixtures::optInt(expect["dateMs"]));
        const auto want = ratesOf(expect["rates"]);
        ASSERT_EQ(parsed->rates.size(), want.size());
        for (const auto& [code, rate] : want) {
            ASSERT_TRUE(parsed->rates.contains(code)) << code;
            EXPECT_EQ(parsed->rates.at(code), rate) << code << " " << parsed->rates.at(code).text();
        }
    }
    for (const auto& c : cases.root()["accept"].elements()) {
        SCOPED_TRACE(*c["name"].string());
        std::optional<rates::Reference> last;
        if (c["last"].isObject()) {
            last = rates::Reference{*fixtures::optInt(c["last"]["dateMs"]), *fixtures::optInt(c["last"]["acceptedMs"]), ratesOf(c["last"]["rates"])};
        }
        const auto fallback = c["fallback"].isObject() ? std::optional(ratesOf(c["fallback"])) : std::nullopt;
        const auto other = c["other"].isObject() ? std::optional(ratesOf(c["other"])) : std::nullopt;
        const auto verdict = rates::check(*fixtures::optInt(c["dateMs"]), ratesOf(c["rates"]), *c["selected"].string(),
                                          *fixtures::optInt(c["nowMs"]), last, fallback ? &*fallback : nullptr, other ? &*other : nullptr);
        EXPECT_EQ(verdict == rates::Verdict::Accepted, c["accept"].isTrue());
    }
}
