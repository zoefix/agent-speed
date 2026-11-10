#include <gtest/gtest.h>

#include "agentspeed/quota/cookies/crypto.hpp"
#include "agentspeed/quota/cookies/header.hpp"
#include "agentspeed/quota/vendors/parsers.hpp"
#include "fixture_support.hpp"
#include "quota_vendor_support.hpp"

// QUOTAS 10 Phase 2: every case of `vendors` in spec/fixtures/quota-cases.json through the C++ readers.
namespace {

namespace vendors = agentspeed::quota::vendors;
namespace cookies = agentspeed::quota::cookies;
using agentspeed::json::Document;
using agentspeed::json::Value;

const fixtures::JsonFile& cases() {
    static const fixtures::JsonFile file(fixtures::fixtureDir() / "quota-cases.json");
    return file;
}

Value vendorCase(std::string_view name) {
    EXPECT_TRUE(cases().ok);
    const auto value = cases().root()["vendors"][name];
    EXPECT_TRUE(value.valid()) << name;
    return value;
}

// A fixture body as the vendor would send it: its compact JSON text, parsed again.
struct Body {
    std::string text;
    Document doc;
    explicit Body(const Value& value) : text(fixtures::compactJson(value)) { EXPECT_TRUE(doc.parse(text)); }
    Value root() const { return doc.root(); }
};

std::string unhex(std::string_view hex) {
    std::string out;
    for (std::size_t i = 0; i + 1 < hex.size(); i += 2) out += static_cast<char>(std::stoi(std::string(hex.substr(i, 2)), nullptr, 16));
    return out;
}

TEST(QuotaVendorFixtures, GrokLog) {
    const auto c = vendorCase("grokLog");
    std::vector<std::string> lines;
    for (const auto& line : c["lines"].elements()) lines.push_back(*line.string());
    const std::vector<std::string_view> views(lines.begin(), lines.end());
    const auto found = vendors::grokFromLog(views);
    ASSERT_TRUE(found.has_value());
    EXPECT_EQ(found->observedMs, *c["expected"]["observedMs"].truncated());
    quota2_test::expectWindows(found->windows, c["expected"]["windows"]);
}

TEST(QuotaVendorFixtures, GrokApiAndUnknown) {
    const Body api(vendorCase("grokApi")["body"]);
    const auto windows = vendors::grokFromResponse(api.root());
    ASSERT_TRUE(windows.has_value());
    quota2_test::expectWindows(*windows, vendorCase("grokApi")["expected"]);

    const Body unknown(vendorCase("grokUnknown")["config"]);
    quota2_test::expectWindows(vendors::grokWindows(unknown.root()), vendorCase("grokUnknown")["expected"]);
    EXPECT_FALSE(vendors::grokFromResponse(unknown.root()).has_value());
}

TEST(QuotaVendorFixtures, Devin) {
    const auto c = vendorCase("devin");
    std::vector<vendors::DevinRow> rows;
    for (const auto& row : c["rows"].elements()) rows.push_back({*row.at(0).string(), *row.at(1).string()});
    quota2_test::expectWindows(vendors::devinWindows(rows), c["expected"]);
}

TEST(QuotaVendorFixtures, AntigravitySummaryAndStatus) {
    const Body summary(vendorCase("antigravity")["body"]);
    quota2_test::expectWindows(vendors::antigravitySummaryWindows(summary.root()), vendorCase("antigravity")["expected"]);
    const Body status(vendorCase("antigravityStatus")["body"]);
    quota2_test::expectWindows(vendors::antigravityStatusWindows(status.root()), vendorCase("antigravityStatus")["expected"]);
}

TEST(QuotaVendorFixtures, KimiCurrentAndLegacy) {
    for (const auto* name : {"kimiCurrent", "kimiLegacy"}) {
        SCOPED_TRACE(name);
        const Body body(vendorCase(name)["body"]);
        quota2_test::expectWindows(vendors::kimiWindows(body.root()), vendorCase(name)["expected"]);
    }
}

TEST(QuotaVendorFixtures, GeminiProjectAndBuckets) {
    const Body load(vendorCase("geminiProject")["body"]);
    EXPECT_EQ(vendors::geminiProject(load.root()), *vendorCase("geminiProject")["expected"].string());
    const Body quota(vendorCase("gemini")["body"]);
    quota2_test::expectWindows(vendors::geminiWindows(quota.root()), vendorCase("gemini")["expected"]);
}

TEST(QuotaVendorFixtures, DroidEnvAndLimits) {
    for (const auto& pair : vendorCase("droidEnv").elements()) {
        const auto text = *pair.at(0).string();
        SCOPED_TRACE(text);
        if (pair.at(1).isNull()) {
            EXPECT_FALSE(vendors::droidKeyFromEnvFile(text).has_value());
        } else {
            EXPECT_EQ(vendors::droidKeyFromEnvFile(text), *pair.at(1).string());
        }
    }
    const auto c = vendorCase("droid");
    const Body body(c["body"]);
    quota2_test::expectWindows(vendors::droidWindows(body.root(), *c["nowMs"].truncated()), c["expected"]);
}

TEST(QuotaVendorFixtures, ZcodeLimitsAndFailure) {
    const auto c = vendorCase("zcode");
    const Body body(c["body"]);
    const auto windows = vendors::zaiWindows(body.root(), *c["nowMs"].truncated());
    ASSERT_TRUE(windows.has_value());
    quota2_test::expectWindows(*windows, c["expected"]);

    const Body failure(vendorCase("zcodeFailure")["body"]);
    EXPECT_TRUE(vendorCase("zcodeFailure")["expected"].isNull());
    EXPECT_FALSE(vendors::zaiWindows(failure.root(), *c["nowMs"].truncated()).has_value());
}

TEST(QuotaVendorFixtures, QoderAndWorkBuddy) {
    for (const auto* name : {"qoder", "qoderPercent"}) {
        SCOPED_TRACE(name);
        const Body body(vendorCase(name)["body"]);
        quota2_test::expectWindows(vendors::qoderWindows(body.root()), vendorCase(name)["expected"]);
    }
    const Body workbuddy(vendorCase("workbuddy")["body"]);
    const auto windows = vendors::workBuddyWindows(workbuddy.root());
    ASSERT_TRUE(windows.has_value());
    quota2_test::expectWindows(*windows, vendorCase("workbuddy")["expected"]);
}

TEST(QuotaVendorFixtures, UserAgent) {
    EXPECT_EQ(vendors::chromeUserAgent("Macintosh; Intel Mac OS X 10_15_7", 142), *vendorCase("userAgent").string());
}

TEST(QuotaVendorFixtures, CookieDecryptionVectors) {
    const auto c = vendorCase("cookies");
    for (const auto* name : {"macV10", "macV10HostHash", "linuxV10", "linuxV11HostHash"}) {
        SCOPED_TRACE(name);
        const auto v = c[name];
        const auto key = cookies::cbcKey(*v["password"].string(), static_cast<int>(*v["iterations"].truncated()));
        ASSERT_TRUE(key.has_value());
        const auto blob = unhex(*v["blob"].string());
        const int meta = static_cast<int>(*v["metaVersion"].truncated());
        EXPECT_EQ(cookies::decryptCbc(blob, *key, meta), *v["expected"].string());
        if (v["host"].isString()) {
            // The host hash is checked against the row's own host_key.
            EXPECT_EQ(cookies::decryptCbc(blob, *key, meta, *v["host"].string()), *v["expected"].string());
            EXPECT_FALSE(cookies::decryptCbc(blob, *key, meta, std::string_view("evil.example")).has_value());
        }
        const auto wrong = cookies::cbcKey("not-the-password", static_cast<int>(*v["iterations"].truncated()));
        EXPECT_NE(cookies::decryptCbc(blob, *wrong, meta), *v["expected"].string());
    }
    const auto gcm = c["windowsGcm"];
    const auto blob = std::string("v10") + unhex(*gcm["nonce"].string()) + unhex(*gcm["ciphertextAndTag"].string());
    EXPECT_EQ(cookies::decryptGcm(blob, unhex(*gcm["key"].string()), 0), *gcm["plaintext"].string());
    auto tampered = blob;
    tampered.back() = static_cast<char>(tampered.back() ^ 1);
    EXPECT_FALSE(cookies::decryptGcm(tampered, unhex(*gcm["key"].string()), 0).has_value());
    EXPECT_FALSE(cookies::decryptCbc(std::string("v20") + blob.substr(3), std::string(16, 'k'), 0).has_value());
}

TEST(QuotaVendorFixtures, CookieHeader) {
    const auto c = vendorCase("cookieHeader");
    std::vector<cookies::Cookie> rows;
    for (const auto& row : c["rows"].elements()) {
        cookies::Cookie cookie;
        cookie.host = *row.at(0).string();
        cookie.name = *row.at(1).string();
        cookie.value = *row.at(2).string();
        cookie.expiresS = *row.at(3).truncated();
        rows.push_back(cookie);
    }
    EXPECT_EQ(cookies::joinHeader(rows, *c["site"].string(), *c["nowS"].truncated()), *c["expected"].string());
}

}  // namespace
