#include <gtest/gtest.h>

#include "agentspeed/report_service.hpp"
#include "fixture_support.hpp"

using namespace agentspeed;
using namespace agentspeed::report;
using namespace fixtures;

TEST(ReportServiceUrl, Normalizes) {
    const std::vector<std::pair<std::string, std::string>> cases = {
        {"codexradar.com", "https://codexradar.com"},
        {"https://Example.com/", "https://example.com"},
        {"HTTPS://EXAMPLE.COM", "https://example.com"},
        {"  example.com  ", "https://example.com"},
        {"\texample.com\n", "https://example.com"},
        {"https://example.com/agentspeed/", "https://example.com/agentspeed"},
        {"https://example.com/AgentSpeed//", "https://example.com/AgentSpeed"},
        {"https://example.com:443", "https://example.com"},
        {"https://example.com:443/x", "https://example.com/x"},
        {"https://example.com:8443", "https://example.com:8443"},
        {"example.com:8443/board", "https://example.com:8443/board"},
        {"http://127.0.0.1:8787", "http://127.0.0.1:8787"},
        {"http://127.0.0.1:80/", "http://127.0.0.1"},
        {"http://[::1]:8787", "http://[::1]:8787"},
        {"http://LOCALHOST:8787/", "http://localhost:8787"},
        {"https://[2001:DB8::1]:8443/x", "https://[2001:db8::1]:8443/x"},
        {"https://bücher.example/x", "https://xn--bcher-kva.example/x"},
        {"https://BÜCHER.example", "https://xn--bcher-kva.example"},
        {"https://example.com/排行", "https://example.com/%E6%8E%92%E8%A1%8C"},
        {"https://example.com/a%2Fb", "https://example.com/a%2Fb"},
        {"localhost:8787", "https://localhost:8787"},
        {"https://deng.codexradar.com", "https://deng.codexradar.com"},
    };
    for (const auto& [input, expected] : cases) {
        const auto url = ServiceUrl::parse(input);
        ASSERT_TRUE(url) << input;
        EXPECT_EQ(url->text(), expected) << input;
    }
}

TEST(ReportServiceUrl, Rejects) {
    const std::vector<std::pair<std::string, UrlProblem>> cases = {
        {"http://example.com", UrlProblem::NotHttps},          {"http://127.0.0.2:8787", UrlProblem::NotHttps},
        {"http://localhost.example.com", UrlProblem::NotHttps}, {"ftp://example.com", UrlProblem::NotHttps},
        {"ws://example.com", UrlProblem::NotHttps},             {"https://user@example.com", UrlProblem::Invalid},
        {"https://user:secret@example.com", UrlProblem::Invalid}, {"https://example.com?q", UrlProblem::Invalid},
        {"https://example.com/?", UrlProblem::Invalid},         {"https://example.com#f", UrlProblem::Invalid},
        {"https://example.com/#", UrlProblem::Invalid},         {"", UrlProblem::Invalid},
        {"   ", UrlProblem::Invalid},                           {"https://", UrlProblem::Invalid},
        {"https:///path", UrlProblem::Invalid},                 {"https://exa mple.com", UrlProblem::Invalid},
        {"https://example.com/a b", UrlProblem::Invalid},       {"https://exa​mple.com", UrlProblem::Invalid},
        {"https://example.com/‮x", UrlProblem::Invalid},   {"https://example.com\\x", UrlProblem::Invalid},
        {"https://example.com/a\\b", UrlProblem::Invalid},      {"https://example.com/a%zz", UrlProblem::Invalid},
        {"https://example.com/../x", UrlProblem::Invalid},      {"https://example.com/a/%2e%2E", UrlProblem::Invalid},
        {"https://example.com/./x", UrlProblem::Invalid},       {"https://a..b", UrlProblem::Invalid},
        {"https://example.com./x", UrlProblem::Invalid},        {"https://exa_mple.com", UrlProblem::Invalid},
        {"https://-example.com", UrlProblem::Invalid},          {"https://ex%41mple.com", UrlProblem::Invalid},
        {"https://example.com:0", UrlProblem::Invalid},         {"https://example.com:99999", UrlProblem::Invalid},
        {"mailto:zoe@example.com", UrlProblem::Invalid},
    };
    for (const auto& [input, expected] : cases) {
        UrlProblem problem{};
        EXPECT_FALSE(ServiceUrl::parse(input, &problem)) << input;
        EXPECT_EQ(problem, expected) << input;
    }
    EXPECT_FALSE(ServiceUrl::parse("https://example.com/" + std::string(2100, 'a')));
}

TEST(ReportServiceUrl, DerivedParts) {
    const auto url = *ServiceUrl::parse("https://Example.com:8443/board/");
    EXPECT_EQ(url.authority(), "example.com:8443");
    EXPECT_EQ(url.origin(), "https://example.com:8443");
    EXPECT_EQ(url.discoveryUrl(), "https://example.com:8443/board/.well-known/agent-speed-report");
    EXPECT_EQ(url.endpoint("/v1/devices"), "https://example.com:8443/board/v1/devices");
    EXPECT_FALSE(url.loopbackHttp());
    EXPECT_TRUE(ServiceUrl::parse("http://127.0.0.1:8787")->loopbackHttp());
    EXPECT_FALSE(ServiceUrl::parseApi("http://127.0.0.1:8787", false));
    EXPECT_TRUE(ServiceUrl::parseApi("http://127.0.0.1:8787", true));
    EXPECT_FALSE(ServiceUrl::parseApi("example.com", false));
    EXPECT_EQ(httpsLink("https://example.com/terms"), std::optional<std::string>("https://example.com/terms"));
    EXPECT_FALSE(httpsLink("http://example.com/terms"));
    EXPECT_FALSE(httpsLink("https://user@example.com"));
    EXPECT_FALSE(httpsLink("https://example.com/a b"));
}

TEST(ReportReply, ResponseExamplesParse) {
    const auto text = readText(fixtureDir() / "report-examples" / "responses.json");
    json::Document doc;
    ASSERT_TRUE(doc.parse(text));
    int checked = 0;
    for (const auto& item : doc.root()["responses"].elements()) {
        const auto status = static_cast<int>(*item["status"].truncated());
        const auto body = compactJson(item["body"]);
        std::map<std::string, std::string> headers;
        if (item["for"].string()->find("Retry-After") != std::string::npos) headers["retry-after"] = "20";
        const auto reply = Reply::parse(status, headers, body, 1'792'656'931);
        SCOPED_TRACE(*item["for"].string());
        if (reply.success()) {
            EXPECT_EQ(reply.result, item["body"]["status"].string());
        } else {
            EXPECT_EQ(reply.code, item["body"]["error"]["code"].string());
        }
        ++checked;
    }
    EXPECT_EQ(checked, 16);
    const auto created = Reply::parse(201, {}, R"({"status":"created","deviceId":"6f1c2a9e-3b4d-4e5f-8a7b-9c0d1e2f3a4b","username":"zoe-mbp","profileUrl":"https://example.com/u/zoe"})", 0);
    EXPECT_EQ(created.deviceId, std::optional<std::string>("6f1c2a9e-3b4d-4e5f-8a7b-9c0d1e2f3a4b"));
    EXPECT_EQ(created.profileUrl, std::optional<std::string>("https://example.com/u/zoe"));
    const auto slower = Reply::parse(200, {}, R"({"status":"accepted","intervalSeconds":1000,"rank":12})", 0);
    EXPECT_EQ(slower.intervalSeconds, std::optional<int>(300));
    EXPECT_EQ(slower.rank, std::optional<int>(12));
    const auto clock = Reply::parse(401, {}, R"({"error":{"code":"timestamp_out_of_window","serverTime":1792656931,"message":"a\u0007b"}})", 0);
    EXPECT_EQ(clock.serverTime, std::optional<std::int64_t>(1'792'656'931));
    EXPECT_EQ(clock.logMessage, std::optional<std::string>("ab"));
    EXPECT_FALSE(Reply::parse(400, {}, R"({"error":{"code":"Bad-Code"}})", 0).code);
    EXPECT_EQ(Reply::parse(502, {}, "<html>", 0).status, 502);
}

TEST(ReportReply, RetryAfter) {
    EXPECT_EQ(retryAfter(" 20 ", 0), std::optional<int>(20));
    EXPECT_EQ(retryAfter("Sun, 06 Nov 1994 08:49:37 GMT", 784'111'770), std::optional<int>(7));
    EXPECT_EQ(retryAfter("Sunday, 06-Nov-94 08:49:37 GMT", 784'111'770), std::optional<int>(7));
    EXPECT_EQ(retryAfter("Sun Nov  6 08:49:37 1994", 784'111'770), std::optional<int>(7));
    EXPECT_EQ(retryAfter("Sun, 06 Nov 1994 08:49:37 GMT", 784'111'800), std::optional<int>(0));
    EXPECT_FALSE(retryAfter("soon", 0));
}

TEST(ReportReply, ClassifiesOutcomesAndReactions) {
    const auto of = [](int status, std::string body, std::map<std::string, std::string> headers = {}) {
        return Reply::parse(status, headers, body, 0);
    };
    EXPECT_EQ(classify(nullptr, false).first.kind, Outcome::Kind::Transient);
    auto ok = of(200, R"({"status":"accepted","intervalSeconds":10})");
    EXPECT_EQ(classify(&ok, false).first.intervalSeconds, std::optional<int>(10));
    auto limited = of(429, R"({"error":{"code":"rate_limited"}})");
    EXPECT_EQ(classify(&limited, false).first.retryAfterSeconds, 60);
    auto unavailable = of(503, "", {{"retry-after", "2"}});
    EXPECT_EQ(classify(&unavailable, false).first.kind, Outcome::Kind::RateLimited);
    EXPECT_EQ(classify(&unavailable, false).first.retryAfterSeconds, 5);
    auto server = of(500, "");
    EXPECT_EQ(classify(&server, false).first.kind, Outcome::Kind::Transient);
    auto redirect = of(302, "");
    EXPECT_EQ(classify(&redirect, false).first.kind, Outcome::Kind::Transient);
    auto signature = of(401, R"({"error":{"code":"signature_invalid"}})");
    EXPECT_EQ(classify(&signature, true).first.kind, Outcome::Kind::Again);
    EXPECT_EQ(classify(&signature, true).second.kind, Reaction::Kind::UsePendingKey);
    EXPECT_EQ(classify(&signature, false).second.kind, Reaction::Kind::RecheckRegistration);
    auto nonce = of(401, R"({"error":{"code":"nonce_replayed"}})");
    EXPECT_EQ(classify(&nonce, false).first.kind, Outcome::Kind::Again);
    auto mismatch = of(409, R"({"error":{"code":"username_mismatch","username":"zoe-mbp"}})");
    EXPECT_EQ(classify(&mismatch, false).second.registered, std::optional<std::string>("zoe-mbp"));
    auto gone = of(410, "");
    EXPECT_EQ(classify(&gone, false).second.problem, Problem::Gone);
    auto missing = of(404, "");
    EXPECT_TRUE(classify(&missing, false).second.misconfigured);
    auto unknown = of(418, R"({"error":{"code":"teapot"}})");
    EXPECT_EQ(classify(&unknown, false).second.kind, Reaction::Kind::Strike);
    auto version = of(400, R"({"error":{"code":"version_unsupported","supported":[1]}})");
    EXPECT_TRUE(classify(&version, false).second.upgrade);
}

TEST(ReportConsent, NeedsReviewOnlyForTheListedChanges) {
    const auto service = *ServiceUrl::parse("https://example.com");
    auto confirmed = assumedDiscovery(service, "Example");
    confirmed.sections = {"cost", "sources"};
    confirmed.retentionDays = 30;
    const auto pin = ConsentPin::of(confirmed);
    auto same = confirmed;
    same.limits.intervalSeconds = 10;
    same.description = "new words";
    EXPECT_FALSE(pin.needsReview(same, false));
    auto renamed = confirmed;
    renamed.name = "Other";
    EXPECT_TRUE(pin.needsReview(renamed, false));
    auto more = confirmed;
    more.sections = {"cost", "models", "sources"};
    EXPECT_TRUE(pin.needsReview(more, false));
    auto longer = confirmed;
    longer.retentionDays = 60;
    EXPECT_TRUE(pin.needsReview(longer, false));
    auto forever = confirmed;
    forever.retentionDays.reset();
    EXPECT_TRUE(pin.needsReview(forever, false));
    auto keyed = confirmed;
    keyed.apiKeyMode = "required";
    EXPECT_TRUE(pin.needsReview(keyed, false));
    EXPECT_FALSE(pin.needsReview(keyed, true));
}

TEST(ReportConsent, InvitationCoverage) {
    const auto service = *ServiceUrl::parse("https://deng.codexradar.com");
    auto document = assumedDiscovery(service, "Codex Radar");
    document.api = "https://speed-api.codexradar.com";
    EXPECT_TRUE(invitationCovers(document, service, "codexradar.com", "zoe-mbp"));
    EXPECT_FALSE(invitationCovers(document, service, "", "zoe-mbp"));
    document.api = "https://api.elsewhere.com";
    EXPECT_FALSE(invitationCovers(document, service, "codexradar.com", "zoe-mbp"));
    document.api = "https://api.deng.codexradar.com/x";
    EXPECT_TRUE(invitationCovers(document, service, "", "zoe-mbp"));
    document.apiKeyMode = "required";
    EXPECT_FALSE(invitationCovers(document, service, "codexradar.com", "zoe-mbp"));
    document.apiKeyMode = "optional";
    document.username.ascii = true;
    EXPECT_FALSE(invitationCovers(document, service, "codexradar.com", "小明的 Mac"));
}

TEST(ReportBodies, RegistrationMatchesTheExample) {
    const auto text = readText(fixtureDir() / "report-examples" / "registration.json");
    json::Document example;
    ASSERT_TRUE(example.parse(text));
    const auto key = *parseJwk(example.root()["publicKey"]);
    const auto client = example.root()["client"];
    const auto body = registrationBody(*example.root()["deviceId"].string(), *example.root()["username"].string(), key,
                                       ClientInfo(*client["version"].string(), *client["platform"].string(), static_cast<int>(*client["osMajor"].truncated())),
                                       false);
    EXPECT_EQ(body, compactJson(example.root()));
    EXPECT_EQ(renameBody("zoe-studio"), R"({"username":"zoe-studio"})");
}

TEST(ReportDiscoveryStorage, RoundTrips) {
    const auto service = *ServiceUrl::parse("https://example.com");
    const auto text = readText(fixtureDir() / "report-examples" / "discovery.json");
    DiscoveryProblem problem;
    const auto parsed = parseDiscoveryBody(text, service, &problem);
    ASSERT_TRUE(parsed) << problem.field;
    const auto stored = discoveryJson(*parsed);
    const auto again = parseDiscoveryBody(stored, service);
    ASSERT_TRUE(again);
    EXPECT_EQ(discoveryJson(*again), stored);
    EXPECT_FALSE(parseDiscoveryBody("<html></html>", service, &problem));
    EXPECT_EQ(problem.kind, DiscoveryProblem::Kind::NotSupported);
    EXPECT_FALSE(parseDiscoveryBody(R"({"protocol":"agent-speed-report","versions":[1]})", service, &problem));
    EXPECT_EQ(problem.kind, DiscoveryProblem::Kind::Invalid);
}
