#include <gtest/gtest.h>

#include <cmath>
#include <limits>

#include "agentspeed/json.hpp"

using agentspeed::json::Document;
using agentspeed::json::Type;

namespace {

Document parsed(std::string_view text) {
    Document doc;
    EXPECT_TRUE(doc.parse(text)) << text;
    return doc;
}

}  // namespace

TEST(Json, ParsesNestedObjectsAndArrays) {
    auto doc = parsed(R"( {"a": {"b": [1, "x", true, null]}, "c": false} )");
    auto root = doc.root();
    ASSERT_TRUE(root.isObject());
    EXPECT_EQ(root["a"]["b"].size(), 4u);
    EXPECT_EQ(root["a"]["b"].at(1).string(), "x");
    EXPECT_TRUE(root["a"]["b"].at(2).isTrue());
    EXPECT_TRUE(root["a"]["b"].at(3).isNull());
    EXPECT_EQ(root["c"].type(), Type::False);
    EXPECT_FALSE(root["missing"].valid());
}

TEST(Json, DuplicateKeysResolveToTheLast) {
    auto doc = parsed(R"({"k": 1, "k": 2})");
    EXPECT_EQ(doc.root()["k"].raw(), "2");
}

TEST(Json, RejectsInvalidText) {
    Document doc;
    for (std::string_view bad : {"", "{", "{\"a\":}", "{\"a\":1,}", "[1,]", "{\"a\":NaN}", "{\"a\":Infinity}",
                                 "{\"a\":-Infinity}", "{\"a\":01}", "{\"a\":1.}", "{\"a\":.5}", "{\"a\":\"\x01\"}",
                                 "{\"a\":\"\\x\"}", "{\"a\":\"\\u12\"}", "{} {}", "{\"a\":\"\xC0\xAF\"}",
                                 "{\"a\":\"\xED\xA0\x80\"}", "tru", "{'a':1}"}) {
        EXPECT_FALSE(doc.parse(bad)) << bad;
    }
}

TEST(Json, LimitsNestingDepth) {
    Document doc;
    std::string deep(Document::kMaxDepth, '[');
    deep += std::string(Document::kMaxDepth, ']');
    EXPECT_TRUE(doc.parse(deep));
    std::string tooDeep(Document::kMaxDepth + 1, '[');
    tooDeep += std::string(Document::kMaxDepth + 1, ']');
    EXPECT_FALSE(doc.parse(tooDeep));
}

TEST(Json, DecodesEscapesAndReplacesLoneSurrogates) {
    auto doc = parsed(R"({"s": "a\"b\\c\/\n\u00e9\ud83d\ude00", "lone": "x\ud800y", "low": "\udc00"})");
    EXPECT_EQ(doc.root()["s"].string(), "a\"b\\c/\n\xC3\xA9\xF0\x9F\x98\x80");
    EXPECT_EQ(doc.root()["lone"].string(), "x\xEF\xBF\xBDy");
    EXPECT_EQ(doc.root()["low"].string(), "\xEF\xBF\xBD");
    EXPECT_TRUE(doc.root()["s"].startsWith("a\"b"));
}

TEST(Json, EscapedKeysMatchTheirDecodedForm) {
    auto doc = parsed(R"({"ty\u0070e": "x"})");
    EXPECT_EQ(doc.root()["type"].string(), "x");
}

TEST(Json, CountsFollowTheTokenRules) {
    auto doc = parsed(R"({"a": 12.9, "b": -7, "c": -1e999, "d": 0.5, "e": 1e20, "f": 1e999,
        "g": 123456789012345678901234567890, "h": "5", "i": true, "j": null, "k": 1000000000001, "l": 42})");
    auto r = doc.root();
    EXPECT_EQ(r["a"].count(), 12);
    EXPECT_EQ(r["b"].count(), 0);
    EXPECT_EQ(r["c"].count(), 0);
    EXPECT_EQ(r["d"].count(), 0);
    EXPECT_EQ(r["e"].count(), 1'000'000'000'000);
    EXPECT_EQ(r["f"].count(), 1'000'000'000'000);
    EXPECT_EQ(r["g"].count(), 1'000'000'000'000);
    EXPECT_EQ(r["h"].count(), 0);
    EXPECT_EQ(r["i"].count(), 0);
    EXPECT_EQ(r["j"].count(), 0);
    EXPECT_EQ(r["k"].count(), 1'000'000'000'000);
    EXPECT_EQ(r["l"].count(), 42);
    EXPECT_EQ(r["missing"].count(), 0);
}

TEST(Json, OrdinalsAreNonNegativeIntegerLiterals) {
    auto doc = parsed(R"({"a": 0, "b": 9223372036854775807, "c": 9223372036854775808, "d": -1, "e": 1.0,
        "f": 1e2, "g": "3", "h": -0})");
    auto r = doc.root();
    EXPECT_EQ(r["a"].ordinal(), 0);
    EXPECT_EQ(r["b"].ordinal(), std::numeric_limits<std::int64_t>::max());
    EXPECT_FALSE(r["c"].ordinal());
    EXPECT_FALSE(r["d"].ordinal());
    EXPECT_FALSE(r["e"].ordinal());
    EXPECT_FALSE(r["f"].ordinal());
    EXPECT_FALSE(r["g"].ordinal());
    EXPECT_EQ(r["h"].ordinal(), 0);
}

TEST(Json, TruncationSaturatesFiniteNumbers) {
    auto doc = parsed(R"({"a": 1e19, "b": -1e19, "c": 2.9, "d": -2.9, "e": 1e999, "f": 99999999999999999999})");
    auto r = doc.root();
    EXPECT_EQ(r["a"].truncated(), std::numeric_limits<std::int64_t>::max());
    EXPECT_EQ(r["b"].truncated(), std::numeric_limits<std::int64_t>::min());
    EXPECT_EQ(r["c"].truncated(), 2);
    EXPECT_EQ(r["d"].truncated(), -2);
    EXPECT_FALSE(r["e"].truncated());
    EXPECT_EQ(r["f"].truncated(), std::numeric_limits<std::int64_t>::max());
    EXPECT_FALSE(r["e"].finiteNumber());
    EXPECT_TRUE(std::isinf(*r["e"].number()));
}

TEST(Json, BlankLines) {
    EXPECT_TRUE(agentspeed::json::isBlank(" \t\r\n\v\f"));
    EXPECT_FALSE(agentspeed::json::isBlank(" x "));
}
