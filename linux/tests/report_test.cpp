#include <gtest/gtest.h>

#include "agentspeed/report.hpp"
#include "fixture_support.hpp"

using namespace agentspeed;
using namespace agentspeed::report;
using namespace fixtures;

namespace {

struct Vectors {
    std::string text = readText(fixtureDir() / "report-vectors.json");
    json::Document doc;
    Vectors() { EXPECT_TRUE(doc.parse(text)); }
    json::Value operator[](std::string_view key) const { return doc.root()[key]; }
};

const Vectors& vectors() {
    static const Vectors v;
    return v;
}

std::string unhex(std::string_view hex) {
    std::string out;
    for (std::size_t i = 0; i + 1 < hex.size(); i += 2) out += static_cast<char>(std::stoi(std::string(hex.substr(i, 2)), nullptr, 16));
    return out;
}

PublicKey keyNamed(std::string_view name) { return PrivateKey::fromHex(*vectors()["keys"][name]["d"].string())->publicKey(); }

std::map<std::string, std::string> lowered(const json::Value& headers) {
    std::map<std::string, std::string> out;
    for (const auto& [name, value] : headers.members()) {
        auto key = name;
        std::transform(key.begin(), key.end(), key.begin(), [](unsigned char c) { return static_cast<char>(std::tolower(c)); });
        out[key] = *value.string();
    }
    return out;
}

UsernameRules rulesOf(const json::Value& value) {
    UsernameRules rules;
    rules.minLength = static_cast<int>(*value["minLength"].truncated());
    rules.maxLength = static_cast<int>(*value["maxLength"].truncated());
    rules.ascii = value["charset"].equals("ascii");
    rules.spaces = value["spaces"].isTrue();
    return rules;
}

}  // namespace

TEST(Report, Base64UrlIsStrict) {
    EXPECT_EQ(b64url("\xfb\xff"), "-_8");
    EXPECT_EQ(b64urlDecode("-_8"), std::optional<std::string>("\xfb\xff"));
    EXPECT_FALSE(b64urlDecode("-_9"));   // non-zero trailing bits
    EXPECT_FALSE(b64urlDecode("-_8="));  // padding
    EXPECT_FALSE(b64urlDecode("+/8"));   // standard alphabet
    EXPECT_FALSE(b64urlDecode("A"));
    EXPECT_EQ(sha256Hex(""), "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855");
}

TEST(Report, KeysMatchTheirEncodings) {
    const auto rfc = vectors()["rfc6979"];
    const auto d = *PrivateKey::fromHex(*rfc["d"].string());
    const auto pub = d.publicKey();
    EXPECT_EQ(PrivateKey::fromHex(d.hex())->d, d.d);
    EXPECT_EQ(pub.x, PrivateKey::fromHex(*rfc["x"].string())->d);
    EXPECT_EQ(pub.y, PrivateKey::fromHex(*rfc["y"].string())->d);
    for (const auto& message : rfc["messages"].elements()) {
        const auto text = *message["message"].string();
        EXPECT_TRUE(verify(pub, text, unhex(*message["r"].string() + *message["s"].string()))) << text;
        EXPECT_TRUE(verify(pub, text, sign(d, text))) << text;
        EXPECT_FALSE(verify(pub, text + "!", sign(d, text))) << text;
    }
    for (const auto& [name, key] : vectors()["keys"].members()) {
        SCOPED_TRACE(name);
        const auto p = PrivateKey::fromHex(*key["d"].string())->publicKey();
        EXPECT_EQ(jwk(p), compactJson(key["jwk"]));
        EXPECT_EQ(b64url(spki(p)), *key["spki"].string());
        EXPECT_EQ(thumbprint(p), *key["thumbprint"].string());
        EXPECT_EQ(parseJwk(key["jwk"]), std::optional<PublicKey>(p));
    }
    const auto fresh = PrivateKey::generate();
    EXPECT_TRUE(verify(fresh.publicKey(), "hello", sign(fresh, "hello")));
    EXPECT_NE(fresh.d, PrivateKey::generate().d);
}

TEST(Report, RequestsHaveTheirCanonicalStrings) {
    for (const auto& [name, request] : vectors()["requests"].members()) {
        SCOPED_TRACE(name);
        const auto headers = lowered(request["headers"]);
        const auto body = *request["body"].string();
        EXPECT_EQ(authorityOf(*request["url"].string()), *request["authority"].string());
        EXPECT_EQ(pathOf(*request["url"].string()), *request["path"].string());
        EXPECT_EQ(sha256Hex(body), *request["bodySha256"].string());
        EXPECT_EQ(canonicalString(*request["method"].string(), *request["authority"].string(), *request["path"].string(),
                                  headers.at("x-agentspeed-timestamp"), headers.at("x-agentspeed-nonce"), headers.at("x-agentspeed-device"), body),
                  *request["canonical"].string());

        // Signing the same request again gives the same headers apart from the (random-k) signatures, which verify.
        const auto device = *PrivateKey::fromHex(*vectors()["keys"]["device"]["d"].string());
        const auto rotated = *PrivateKey::fromHex(*vectors()["keys"]["rotated"]["d"].string());
        SignOptions options;
        options.timestamp = std::stoll(headers.at("x-agentspeed-timestamp"));
        options.nonce = headers.at("x-agentspeed-nonce");
        options.version = "1.3.0";
        options.platform = "macOS";
        if (headers.contains("authorization")) options.apiKey = headers.at("authorization").substr(7);
        if (headers.contains("x-agentspeed-new-key-signature")) options.newKey = &rotated;
        const auto signedOnes = signedHeaders(device, *request["method"].string(), *request["url"].string(), headers.at("x-agentspeed-device"),
                                              body, options);
        std::map<std::string, std::string> mine;
        for (const auto& [key, value] : signedOnes) {
            auto lower = key;
            std::transform(lower.begin(), lower.end(), lower.begin(), [](unsigned char c) { return static_cast<char>(std::tolower(c)); });
            mine[lower] = value;
        }
        ASSERT_EQ(mine.size(), headers.size());
        for (const auto& [key, value] : headers) {
            if (key.ends_with("signature")) continue;
            EXPECT_EQ(mine[key], value) << key;
        }
        EXPECT_EQ(verifyRequest(keyNamed("device"), *request["method"].string(), *request["authority"].string(), *request["path"].string(),
                                mine, body),
                  Verdict::Valid);
    }
}

TEST(Report, VerifiesLikeAService) {
    for (const auto& item : vectors()["verify"].elements()) {
        const auto name = *item["name"].string();
        SCOPED_TRACE(name);
        const auto request = vectors()["requests"][*item["request"].string()];
        auto headers = lowered(request["headers"]);
        auto method = *request["method"].string(), authority = *request["authority"].string(), path = *request["path"].string();
        auto body = *request["body"].string();
        const auto change = item["change"];
        for (const auto& [key, value] : lowered(change["headers"])) headers[key] = value;
        if (change["method"].valid()) method = *change["method"].string();
        if (change["authority"].valid()) authority = *change["authority"].string();
        if (change["path"].valid()) path = *change["path"].string();
        if (change["body"].valid()) body = *change["body"].string();
        auto header = item["header"].string().value_or("X-AgentSpeed-Signature");
        std::transform(header.begin(), header.end(), header.begin(), [](unsigned char c) { return static_cast<char>(std::tolower(c)); });
        const auto verdict = verifyRequest(keyNamed(*item["key"].string()), method, authority, path, headers, body, header);
        const auto expect = *item["expect"].string();
        EXPECT_EQ(verdict, expect == "valid" ? Verdict::Valid : expect == "invalid" ? Verdict::Invalid : Verdict::Malformed);
    }
}

TEST(Report, ValidatesPublicKeys) {
    for (const auto& item : vectors()["publicKeys"].elements()) {
        EXPECT_EQ(parseJwk(item["jwk"]).has_value(), item["expect"].equals("valid")) << *item["name"].string();
    }
}

TEST(Report, Usernames) {
    const auto usernames = vectors()["usernames"];
    for (const auto& item : usernames["validate"].elements()) {
        const auto rules = rulesOf(usernames["rules"][*item["rules"].string()]);
        const auto got = validateUsername(*item["name"].string(), rules);
        EXPECT_EQ(got, item["expect"].string()) << *item["name"].string();
    }
    for (const auto& item : usernames["fit"].elements()) {
        const auto rules = rulesOf(usernames["rules"][*item["rules"].string()]);
        EXPECT_EQ(fitUsername(*item["computerName"].string(), rules), item["expect"].string()) << *item["computerName"].string();
    }
    for (const auto& item : usernames["keys"].elements()) {
        EXPECT_EQ(usernameKey(*item["a"].string()) == usernameKey(*item["b"].string()), item["same"].isTrue()) << *item["a"].string();
    }
    const auto random = randomUsername();
    EXPECT_EQ(random.size(), 12U);
    EXPECT_FALSE(validateUsername(random, UsernameRules{}));
}

TEST(Report, ParsesDiscoveryDocuments) {
    for (const auto& item : vectors()["discovery"].elements()) {
        const auto name = *item["name"].string();
        SCOPED_TRACE(name);
        std::string field;
        const auto parsed = parseDiscovery(item["document"], *item["fetchedFrom"].string(), &field);
        const auto expect = item["expect"];
        if (expect["invalid"].valid()) {
            EXPECT_FALSE(parsed);
            EXPECT_EQ(field, *expect["invalid"].string());
            continue;
        }
        ASSERT_TRUE(parsed) << field;
        const auto optional = [](const std::optional<std::string>& value) { return value ? "\"" + *value + "\"" : std::string("null"); };
        for (const auto& [key, want] : expect.members()) {
            std::string got;
            if (key == "api") got = "\"" + parsed->api + "\"";
            else if (key == "apiKeyMode") got = "\"" + parsed->apiKeyMode + "\"";
            else if (key == "apiKeyUrl") got = optional(parsed->apiKeyUrl);
            else if (key == "homepage") got = optional(parsed->homepage);
            else if (key == "leaderboard") got = optional(parsed->leaderboard);
            else if (key == "description") got = optional(parsed->description);
            else if (key == "contact") got = optional(parsed->contact);
            else if (key == "name") got = "\"" + parsed->name + "\"";
            else if (key == "retentionDays") got = parsed->retentionDays ? std::to_string(*parsed->retentionDays) : "null";
            else if (key == "limits") {
                const auto& l = parsed->limits;
                got = "{\"intervalSeconds\":" + std::to_string(l.intervalSeconds) + ",\"heartbeatSeconds\":" + std::to_string(l.heartbeatSeconds) +
                      ",\"maxAgeSeconds\":" + std::to_string(l.maxAgeSeconds) + ",\"maxBodyBytes\":" + std::to_string(l.maxBodyBytes) +
                      ",\"maxModels\":" + std::to_string(l.maxModels) + "}";
            } else if (key == "username") {
                const auto& u = parsed->username;
                got = "{\"minLength\":" + std::to_string(u.minLength) + ",\"maxLength\":" + std::to_string(u.maxLength) + ",\"charset\":\"" +
                      (u.ascii ? "ascii" : "unicode") + "\",\"spaces\":" + (u.spaces ? "true" : "false") + "}";
            } else if (key == "sections") {
                got = "[";
                for (const auto& section : parsed->sections) got += (got.size() > 1 ? ",\"" : "\"") + section + "\"";
                got += "]";
            } else if (key == "versions") {
                got = "[";
                for (const auto version : parsed->versions) got += (got.size() > 1 ? "," : "") + std::to_string(version);
                got += "]";
            } else {
                ADD_FAILURE() << "unchecked key " << key;
                continue;
            }
            EXPECT_EQ(got, compactJson(want)) << key;
        }
    }
}
