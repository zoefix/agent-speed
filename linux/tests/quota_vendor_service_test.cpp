#include <gtest/gtest.h>

#include "quota_service_support.hpp"

// QUOTAS 9.1-9.9: what the vendor service asks, with which headers, when, and what it shows.
namespace {

using agentspeed::quota::Kind;
using agentspeed::quota::http::Header;
using agentspeed::quota::http::Method;
using agentspeed::quota::http::Request;
using namespace quota2_test;

std::vector<Header> headers(std::initializer_list<std::pair<const char*, std::string>> list) {
    std::vector<Header> out;
    for (const auto& [name, value] : list) out.push_back({name, value});
    return out;
}

void expectRequest(const Request& got, Method method, const std::string& url, const std::vector<Header>& wantHeaders,
                   const std::string& body = {}, bool loopback = false) {
    SCOPED_TRACE(url);
    EXPECT_EQ(got.method, method);
    EXPECT_EQ(got.url, url);
    EXPECT_EQ(got.headers, wantHeaders);
    EXPECT_EQ(got.body, body);
    EXPECT_EQ(got.loopback, loopback);
}

TEST(QuotaVendorService, EveryRouteSendsExactlyItsHeaders) {
    ServiceFixture f;
    const auto& readings = f.service->tick(ServiceFixture::allOn());
    ASSERT_EQ(f.transport.sent.size(), 9u);
    const std::string json = "application/json";
    expectRequest(f.sentTo("grok.com").at(0), Method::Get, "https://cli-chat-proxy.grok.com/v1/billing?format=credits",
                  headers({{"Accept", json}, {"Authorization", "Bearer grok-key"}, {"x-xai-token-auth", "xai-grok-cli"}}));
    expectRequest(f.sentTo("kimi").at(0), Method::Get, "https://api.kimi.com/coding/v1/usages",
                  headers({{"Accept", json}, {"Authorization", "Bearer kimi-token"}}));
    const auto gemini = f.sentTo("googleapis");
    ASSERT_EQ(gemini.size(), 2u);
    expectRequest(gemini[0], Method::Post, "https://cloudcode-pa.googleapis.com/v1internal:loadCodeAssist",
                  headers({{"Authorization", "Bearer gem-token"}, {"Content-Type", json}}),
                  R"({"metadata":{"ideType":"GEMINI_CLI","pluginType":"GEMINI"}})");
    expectRequest(gemini[1], Method::Post, "https://cloudcode-pa.googleapis.com/v1internal:retrieveUserQuota",
                  headers({{"Authorization", "Bearer gem-token"}, {"Content-Type", json}}), R"({"project":"proj-1"})");
    expectRequest(f.sentTo("factory").at(0), Method::Get, "https://api.factory.ai/api/billing/limits",
                  headers({{"Accept", json}, {"Authorization", "Bearer fk-1"}}));
    expectRequest(f.sentTo("z.ai").at(0), Method::Get, "https://api.z.ai/api/monitor/usage/quota/limit",
                  headers({{"Authorization", "Bearer zk-1"}}));
    expectRequest(f.sentTo("qoder").at(0), Method::Get, "https://qoder.com.cn/api/v2/me/usages/big_model_credits",
                  headers({{"Cookie", "sid=q1"}, {"Origin", "https://qoder.com.cn"}, {"Referer", "https://qoder.com.cn/"}, {"Accept", json}}));
    expectRequest(f.sentTo("workbuddy").at(0), Method::Post, "https://www.workbuddy.cn/billing/meter/get-user-resource-summary",
                  headers({{"Content-Type", json}, {"Cookie", "wb=w1"}, {"User-Agent", kFirefoxUa}}), "{}");
    expectRequest(f.sentTo("127.0.0.1").at(0), Method::Post,
                  "http://127.0.0.1:5555/exa.language_server_pb.LanguageServerService/RetrieveUserQuotaSummary",
                  headers({{"Connect-Protocol-Version", "1"}, {"Content-Type", json}, {"X-Codeium-Csrf-Token", "tok"}}),
                  R"({"forceRefresh":true})", true);

    for (const auto source : {SourceKind::GrokBuild, SourceKind::Kimi, SourceKind::Gemini, SourceKind::Droid, SourceKind::ZCode,
                              SourceKind::Qoder, SourceKind::QoderCli, SourceKind::WorkBuddy, SourceKind::Antigravity}) {
        SCOPED_TRACE(std::string(agentspeed::sourceId(source)));
        const auto snapshot = readings.snapshot(source);
        ASSERT_TRUE(snapshot.has_value());
        EXPECT_EQ(snapshot->source, source);
        EXPECT_EQ(snapshot->observedMs, kNow - 1000) << "the Date header";
        EXPECT_FALSE(snapshot->windows.empty());
        EXPECT_TRUE(readings.signedIn(source));
        // Grok Build's own switch is the log's; its live request has the second switch's status line.
        const auto status = source == SourceKind::GrokBuild ? readings.status(VendorRoute::GrokBuildLive) : readings.status(source);
        ASSERT_TRUE(status.has_value());
        EXPECT_EQ(status->kind, VendorStatusKind::Ok);
        EXPECT_EQ(status->okMs, kNow - 1000);
    }
    EXPECT_EQ(readings.status(SourceKind::GrokBuild)->kind, VendorStatusKind::NoCredential) << "no log on this computer";
    EXPECT_EQ(readings.snapshot(SourceKind::Qoder)->windows[0].kind, Kind::Credits);
    EXPECT_EQ(readings.snapshot(SourceKind::Antigravity)->windows[0].name, "Gemini");
    // Never a refresh token, a provider key or the Kimi base URL.
    EXPECT_FALSE(f.touched("settings.json"));
    for (const auto& r : f.transport.sent) {
        for (const auto& h : r.headers) EXPECT_EQ(h.value.find("never-read"), std::string::npos);
    }
}

TEST(QuotaVendorService, ADisabledSourceOrSwitchReadsNothingAndShowsNothing) {
    ServiceFixture f;
    auto in = ServiceFixture::allOn();
    in.enabledSources.clear();
    const auto& readings = f.service->tick(in);
    EXPECT_TRUE(f.transport.sent.empty());
    EXPECT_TRUE(f.secrets.lookups.empty());
    EXPECT_EQ(f.sessions->calls, 0);
    EXPECT_EQ(f.locator->calls, 0);
    EXPECT_TRUE(f.files->touched.empty()) << f.files->touched.front();
    EXPECT_TRUE(readings.routes.empty());

    auto switches = ServiceFixture::allOn();
    switches.switchesOn.clear();
    f.service->tick(switches);
    EXPECT_TRUE(f.transport.sent.empty());
    EXPECT_TRUE(f.secrets.lookups.empty());
    EXPECT_TRUE(f.files->touched.empty());

    // No consent: the browser is never read.
    auto consent = ServiceFixture::allOn();
    consent.qoderConsent = consent.workBuddyConsent = false;
    f.service->tick(consent);
    EXPECT_EQ(f.sessions->calls, 0);
    EXPECT_TRUE(f.sentTo("qoder").empty());
    EXPECT_TRUE(f.sentTo("workbuddy").empty());
}

TEST(QuotaVendorService, NetworkOffAsksNothingAndReadsNoCredential) {
    ServiceFixture f;
    auto in = ServiceFixture::allOn();
    in.networkEnabled = false;
    const auto& readings = f.service->tick(in);
    EXPECT_TRUE(f.transport.sent.empty());
    EXPECT_TRUE(f.secrets.lookups.empty());
    EXPECT_EQ(f.sessions->calls, 0);
    EXPECT_EQ(f.locator->calls, 0);
    for (const auto& path : f.files->touched) {
        // Only the local routes look at their own files.
        EXPECT_TRUE(path.find("unified.jsonl") != std::string::npos || path.find("/.config/") != std::string::npos) << path;
    }
    for (const auto route : {VendorRoute::Kimi, VendorRoute::Gemini, VendorRoute::Droid, VendorRoute::Zcode, VendorRoute::Qoder,
                             VendorRoute::WorkBuddy, VendorRoute::Antigravity, VendorRoute::GrokBuildLive}) {
        ASSERT_TRUE(readings.status(route).has_value()) << agentspeed::quota::vendorRouteId(route);
        EXPECT_EQ(readings.status(route)->kind, VendorStatusKind::NetworkOff);
    }
}

TEST(QuotaVendorService, QoderCountsWhileEitherQoderSourceIsEnabled) {
    ServiceFixture f;
    auto in = ServiceFixture::only(VendorRoute::Qoder);
    in.enabledSources = {SourceKind::QoderCli};
    in.running = {SourceKind::QoderCli};
    const auto& readings = f.service->tick(in);
    ASSERT_EQ(f.sentTo("qoder").size(), 1u);
    ASSERT_TRUE(readings.snapshot(SourceKind::QoderCli).has_value());
    EXPECT_EQ(readings.snapshot(SourceKind::QoderCli)->source, SourceKind::QoderCli);
    EXPECT_TRUE(readings.signedIn(SourceKind::Qoder));
}

TEST(QuotaVendorService, CadenceFailureWaitAndDenial) {
    ServiceFixture f;
    f.service->tick(ServiceFixture::only(VendorRoute::Kimi));
    ASSERT_EQ(f.transport.sent.size(), 1u);
    f.service->tick(ServiceFixture::only(VendorRoute::Kimi, kNow + 4 * kMinute));
    EXPECT_EQ(f.transport.sent.size(), 1u) << "at most every 5 minutes";
    f.vendors.statuses["kimi"] = 500;
    auto readings = f.service->tick(ServiceFixture::only(VendorRoute::Kimi, kNow + 5 * kMinute));
    EXPECT_EQ(f.transport.sent.size(), 2u);
    EXPECT_EQ(readings.status(VendorRoute::Kimi)->kind, VendorStatusKind::Failed);
    EXPECT_TRUE(readings.snapshot(SourceKind::Kimi).has_value()) << "the last snapshot stays";
    f.service->tick(ServiceFixture::only(VendorRoute::Kimi, kNow + 19 * kMinute));
    EXPECT_EQ(f.transport.sent.size(), 2u) << "a failure waits 15 minutes";
    f.vendors.statuses["kimi"] = 401;
    readings = f.service->tick(ServiceFixture::only(VendorRoute::Kimi, kNow + 20 * kMinute));
    EXPECT_EQ(f.transport.sent.size(), 3u);
    EXPECT_EQ(readings.status(VendorRoute::Kimi)->kind, VendorStatusKind::Denied);
    EXPECT_EQ(readings.status(VendorRoute::Kimi)->host, "api.kimi.com");
    EXPECT_EQ(f.logged.back(), (std::pair<std::string, int>{"api.kimi.com", 401}));
    f.service->tick(ServiceFixture::only(VendorRoute::Kimi, kNow + 300 * kMinute));
    EXPECT_EQ(f.transport.sent.size(), 3u) << "401 stops until the credential changes";

    // The same token rewritten: a new modification time asks again.
    f.vendors.statuses.clear();
    f.home.touch(".kimi-code/credentials/kimi-code.json", 10);
    readings = f.service->tick(ServiceFixture::only(VendorRoute::Kimi, kNow + 301 * kMinute));
    EXPECT_EQ(f.transport.sent.size(), 4u);
    EXPECT_EQ(readings.status(VendorRoute::Kimi)->kind, VendorStatusKind::Ok);
}

TEST(QuotaVendorService, AnotherAccountDropsTheOldNumbersAndAsksAtOnce) {
    ServiceFixture f;
    auto readings = f.service->tick(ServiceFixture::only(VendorRoute::Kimi));
    ASSERT_TRUE(readings.snapshot(SourceKind::Kimi).has_value());
    f.home.write(".kimi-code/credentials/kimi-code.json", R"({"access_token": "other-account", "expires_at": 1796000000})");
    f.home.touch(".kimi-code/credentials/kimi-code.json", 5);
    f.vendors.statuses["kimi"] = 500;
    readings = f.service->tick(ServiceFixture::only(VendorRoute::Kimi, kNow + kMinute));
    ASSERT_EQ(f.transport.sent.size(), 2u);
    EXPECT_EQ(header(f.transport.sent[1], "Authorization"), "Bearer other-account");
    EXPECT_FALSE(readings.snapshot(SourceKind::Kimi).has_value());
    EXPECT_EQ(readings.status(VendorRoute::Kimi)->kind, VendorStatusKind::Failed);
}

TEST(QuotaVendorService, AnExpiredTokenAsksNothingAndKeepsTheSnapshot) {
    ServiceFixture f;
    auto readings = f.service->tick(ServiceFixture::only(VendorRoute::Gemini));
    ASSERT_TRUE(readings.snapshot(SourceKind::Gemini).has_value());
    f.home.write(".gemini/oauth_creds.json", R"({"access_token": "gem-token", "expiry_date": 1794338000000})");
    f.home.touch(".gemini/oauth_creds.json", 5);
    readings = f.service->tick(ServiceFixture::only(VendorRoute::Gemini, kNow + 10 * kMinute));
    EXPECT_EQ(f.transport.sent.size(), 2u);
    EXPECT_EQ(readings.status(VendorRoute::Gemini)->kind, VendorStatusKind::Expired);
    EXPECT_FALSE(readings.signedIn(SourceKind::Gemini));
    EXPECT_TRUE(readings.snapshot(SourceKind::Gemini).has_value());

    // Grok: no request within a minute of its expiry.
    ServiceFixture g;
    g.home.write("grok/auth.json", R"({"scope": {"access_token": "grok-access", "expires_at": "2026-11-10T19:27:00Z"}})");
    readings = g.service->tick(ServiceFixture::only(VendorRoute::GrokBuildLive));
    EXPECT_TRUE(g.transport.sent.empty());
    EXPECT_EQ(readings.status(VendorRoute::GrokBuildLive)->kind, VendorStatusKind::Expired);
    g.home.write("grok/auth.json", R"({"scope": {"access_token": "grok-access", "expires_at": "2026-11-10T19:30:00Z"}})");
    g.home.touch("grok/auth.json", 5);
    g.service->tick(ServiceFixture::only(VendorRoute::GrokBuildLive, kNow + 1000));
    ASSERT_EQ(g.transport.sent.size(), 1u);
    EXPECT_EQ(header(g.transport.sent[0], "Authorization"), "Bearer grok-access");
}

TEST(QuotaVendorService, NoCredentialAndNotRunning) {
    ServiceFixture f;
    std::filesystem::remove(f.home.file(".kimi-code/credentials/kimi-code.json"));
    auto readings = f.service->tick(ServiceFixture::only(VendorRoute::Kimi));
    EXPECT_EQ(readings.status(VendorRoute::Kimi)->kind, VendorStatusKind::NoCredential);
    EXPECT_FALSE(readings.signedIn(SourceKind::Kimi));

    auto in = ServiceFixture::only(VendorRoute::Droid);
    in.running.clear();
    readings = f.service->tick(in);
    EXPECT_TRUE(f.transport.sent.empty());
    EXPECT_EQ(readings.status(VendorRoute::Droid)->kind, VendorStatusKind::NotRunning);
    EXPECT_TRUE(readings.signedIn(SourceKind::Droid));
    in.dashboardOpen = true;
    readings = f.service->tick(in);
    EXPECT_EQ(f.sentTo("factory").size(), 1u) << "the open dashboard asks";
}

TEST(QuotaVendorService, KimiCredentialOrderAndDroidEnvironmentKey) {
    ServiceFixture f;
    f.home.write("kimi-home/credentials/kimi-code.json", R"({"access_token": "from-kimi-home"})");
    f.home.write(".kimi/credentials/kimi-code.json", R"({"access_token": "legacy"})");
    f.variables["KIMI_CODE_HOME"] = f.home.file("kimi-home");
    f.variables["KIMI_CODE_BASE_URL"] = "https://evil.example";
    f.variables["FACTORY_API_KEY"] = " fk-env ";
    f.build({agentspeed::quota::kVendorRoutes.begin(), agentspeed::quota::kVendorRoutes.end()});
    auto in = ServiceFixture::only(VendorRoute::Kimi);
    in.enabledSources.insert(SourceKind::Droid);
    in.running.insert(SourceKind::Droid);
    in.switchesOn.insert("quotaDroid");
    f.service->tick(in);
    EXPECT_EQ(header(f.sentTo("kimi").at(0), "Authorization"), "Bearer from-kimi-home");
    EXPECT_EQ(f.sentTo("kimi").at(0).url, "https://api.kimi.com/coding/v1/usages");
    EXPECT_EQ(header(f.sentTo("factory").at(0), "Authorization"), "Bearer fk-env");
    EXPECT_FALSE(f.touched(".factory"));
}

TEST(QuotaVendorService, GeminiLearnsItsProjectOncePerToken) {
    ServiceFixture f;
    f.service->tick(ServiceFixture::only(VendorRoute::Gemini));
    EXPECT_EQ(f.sentTo("loadCodeAssist").size(), 1u);
    f.service->tick(ServiceFixture::only(VendorRoute::Gemini, kNow + 5 * kMinute));
    EXPECT_EQ(f.sentTo("loadCodeAssist").size(), 1u);
    ASSERT_EQ(f.sentTo("retrieveUserQuota").size(), 2u);
    EXPECT_EQ(f.sentTo("retrieveUserQuota").at(1).body, R"({"project":"proj-1"})");
    // Without a project the body is `{}`; a new token learns again. A 403 (ended sign-in) has no snapshot.
    f.vendors.bodies["loadCodeAssist"] = "{}";
    f.home.write(".gemini/oauth_creds.json", R"({"access_token": "gem-2", "expiry_date": 1796000000000})");
    f.home.touch(".gemini/oauth_creds.json", 5);
    f.service->tick(ServiceFixture::only(VendorRoute::Gemini, kNow + 6 * kMinute));
    EXPECT_EQ(f.sentTo("loadCodeAssist").size(), 2u);
    EXPECT_EQ(f.sentTo("retrieveUserQuota").back().body, "{}");
    f.vendors.statuses["loadCodeAssist"] = 403;
    f.home.write(".gemini/oauth_creds.json", R"({"access_token": "gem-3", "expiry_date": 1796000000000})");
    f.home.touch(".gemini/oauth_creds.json", 10);
    const auto& readings = f.service->tick(ServiceFixture::only(VendorRoute::Gemini, kNow + 7 * kMinute));
    EXPECT_EQ(f.sentTo("retrieveUserQuota").size(), 3u);
    EXPECT_EQ(readings.status(VendorRoute::Gemini)->kind, VendorStatusKind::Denied);
    EXPECT_FALSE(readings.snapshot(SourceKind::Gemini).has_value());
}

TEST(QuotaVendorService, ZcodeReadsItsKeyOnlyWhenItsRequestIsDue) {
    ServiceFixture f;
    auto in = ServiceFixture::only(VendorRoute::Zcode);
    in.running.clear();
    auto readings = f.service->tick(in);
    EXPECT_TRUE(f.secrets.lookups.empty()) << "not running: the key is not read";
    EXPECT_EQ(readings.status(VendorRoute::Zcode)->kind, VendorStatusKind::NotRunning);

    in.running = {SourceKind::ZCode};
    f.service->tick(in);
    EXPECT_EQ(f.secrets.lookups.size(), 1u);
    EXPECT_EQ(f.sentTo("api.z.ai").size(), 1u);
    in.nowMs = kNow + kMinute;
    f.service->tick(in);
    EXPECT_EQ(f.secrets.lookups.size(), 1u) << "not due";

    // Another region asks the other host with the same key, at once.
    in.zcodeRegion = agentspeed::quota::requests::ZaiRegion::BigModelCn;
    in.nowMs = kNow + 2 * kMinute;
    readings = f.service->tick(in);
    EXPECT_EQ(f.secrets.lookups.size(), 2u);
    ASSERT_EQ(f.sentTo("open.bigmodel.cn").size(), 1u);
    EXPECT_EQ(f.sentTo("open.bigmodel.cn")[0].url, "https://open.bigmodel.cn/api/monitor/usage/quota/limit");

    // A refused unlock prompt is not shown again until the key changes.
    f.secrets.items[agentspeed::quota::zcodeKeyAttributes()] = {agentspeed::quota::SecretResult::Refused, ""};
    in.nowMs = kNow + 10 * kMinute;
    readings = f.service->tick(in);
    EXPECT_EQ(f.secrets.lookups.size(), 3u);
    EXPECT_FALSE(readings.signedIn(SourceKind::ZCode));
    in.nowMs = kNow + 30 * kMinute;
    f.service->tick(in);
    EXPECT_EQ(f.secrets.lookups.size(), 3u);
    f.secrets.items[agentspeed::quota::zcodeKeyAttributes()] = {agentspeed::quota::SecretResult::Found, "zk-2"};
    f.service->zcodeKeyChanged();
    f.service->tick(in);
    EXPECT_EQ(f.secrets.lookups.size(), 4u);
    EXPECT_EQ(header(f.transport.sent.back(), "Authorization"), "Bearer zk-2");

    // No key saved: no status (the key field says what to do).
    f.secrets.items.clear();
    f.service->zcodeKeyChanged();
    readings = f.service->tick(in);
    EXPECT_FALSE(readings.status(VendorRoute::Zcode).has_value());
}

TEST(QuotaVendorService, AntigravityFallsBackAndForgetsARefusedToken) {
    ServiceFixture f;
    f.vendors.statuses["RetrieveUserQuotaSummary"] = 404;
    auto in = ServiceFixture::only(VendorRoute::Antigravity);
    auto readings = f.service->tick(in);
    ASSERT_EQ(f.transport.sent.size(), 2u);
    EXPECT_EQ(f.transport.sent[1].url, "http://127.0.0.1:5555/exa.language_server_pb.LanguageServerService/GetUserStatus");
    EXPECT_EQ(f.transport.sent[1].body,
              R"({"metadata":{"ideName":"antigravity","extensionName":"antigravity","ideVersion":"unknown","locale":"en"}})");
    ASSERT_TRUE(readings.snapshot(SourceKind::Antigravity).has_value());
    EXPECT_EQ(readings.snapshot(SourceKind::Antigravity)->windows.at(0).name, "Pro");
    EXPECT_TRUE(readings.signedIn(SourceKind::Antigravity));
    EXPECT_TRUE(readings.runs(SourceKind::Antigravity));

    f.vendors.statuses = {{"127.0.0.1", 403}};
    in.nowMs = kNow + 5 * kMinute;
    readings = f.service->tick(in);
    ASSERT_EQ(f.transport.sent.size(), 4u) << "http then https";
    EXPECT_EQ(f.transport.sent[3].url.rfind("https://127.0.0.1:5555/", 0), 0u);
    EXPECT_EQ(readings.status(VendorRoute::Antigravity)->kind, VendorStatusKind::Denied);
    EXPECT_FALSE(readings.signedIn(SourceKind::Antigravity));

    f.vendors.statuses.clear();
    in.nowMs = kNow + 20 * kMinute;
    readings = f.service->tick(in);
    ASSERT_EQ(f.transport.sent.size(), 5u);
    EXPECT_EQ(header(f.transport.sent[4], "X-Codeium-Csrf-Token"), "") << "the token was forgotten";
    EXPECT_EQ(headerNames(f.transport.sent[4]), (std::vector<std::string>{"Connect-Protocol-Version", "Content-Type"}));
    EXPECT_EQ(readings.status(VendorRoute::Antigravity)->kind, VendorStatusKind::Ok);

    // Not running: no server is looked for.
    in.running.clear();
    const int calls = f.locator->calls;
    readings = f.service->tick(in);
    EXPECT_EQ(f.locator->calls, calls);
    EXPECT_EQ(readings.status(VendorRoute::Antigravity)->kind, VendorStatusKind::NotRunning);
}

TEST(QuotaVendorService, BrowserSessionsNoBrowserDenialAndANewCookie) {
    ServiceFixture f;
    f.sessions->sessions.clear();
    auto in = ServiceFixture::only(VendorRoute::WorkBuddy);
    auto readings = f.service->tick(in);
    EXPECT_EQ(f.sessions->calls, 1);
    EXPECT_EQ(readings.status(VendorRoute::WorkBuddy)->kind, VendorStatusKind::NoBrowser);
    in.nowMs = kNow + 10 * kMinute;
    f.service->tick(in);
    EXPECT_EQ(f.sessions->calls, 1) << "15 minutes after no session";

    f.sessions->sessions["workbuddy.cn"] = {"workbuddy.cn", "wb=1", kFirefoxUa};
    f.vendors.statuses["workbuddy"] = 401;
    in.nowMs = kNow + 15 * kMinute;
    readings = f.service->tick(in);
    EXPECT_EQ(f.transport.sent.size(), 1u);
    EXPECT_EQ(readings.status(VendorRoute::WorkBuddy)->kind, VendorStatusKind::Denied);
    EXPECT_EQ(readings.status(VendorRoute::WorkBuddy)->host, "www.workbuddy.cn");
    in.nowMs = kNow + 30 * kMinute;
    readings = f.service->tick(in);
    EXPECT_EQ(f.sessions->calls, 3);
    EXPECT_EQ(f.transport.sent.size(), 1u) << "the refused cookie is not sent again";

    f.vendors.statuses.clear();
    f.sessions->sessions["workbuddy.cn"] = {"workbuddy.cn", "wb=2", kFirefoxUa};
    in.nowMs = kNow + 45 * kMinute;
    readings = f.service->tick(in);
    EXPECT_EQ(f.transport.sent.size(), 2u);
    EXPECT_EQ(readings.status(VendorRoute::WorkBuddy)->kind, VendorStatusKind::Ok);
    EXPECT_TRUE(readings.signedIn(SourceKind::WorkBuddy));

    // Both browser routes off: refused browsers are asked again next time.
    auto off = in;
    off.switchesOn.clear();
    f.service->tick(off);
    EXPECT_EQ(f.sessions->resets, 1);
}

TEST(QuotaVendorService, GrokLogAndLiveShareTheSnapshotAndLiveNeedsBothSwitches) {
    ServiceFixture f;
    f.home.write("grok/logs/unified.jsonl",
                 R"({"ts": "2026-11-10T12:00:00Z", "msg": "billing: fetched credits config", "ctx": {"config": {"creditUsagePercent": 77}}})"
                 "\n");
    auto in = ServiceFixture::only(VendorRoute::GrokBuild);
    auto readings = f.service->tick(in);
    EXPECT_TRUE(f.transport.sent.empty()) << "live is off";
    ASSERT_TRUE(readings.snapshot(SourceKind::GrokBuild).has_value());
    EXPECT_DOUBLE_EQ(readings.snapshot(SourceKind::GrokBuild)->windows[0].usedPercent, 77);
    EXPECT_TRUE(readings.signedIn(SourceKind::GrokBuild)) << "a log snapshot under 24 hours";

    auto liveOnly = in;
    liveOnly.switchesOn = {std::string(agentspeed::quota::kGrokLiveSwitch)};
    f.service->tick(liveOnly);
    EXPECT_TRUE(f.transport.sent.empty()) << "live needs quotaGrokBuild too";

    in.switchesOn.insert(std::string(agentspeed::quota::kGrokLiveSwitch));
    readings = f.service->tick(in);
    ASSERT_EQ(f.transport.sent.size(), 1u);
    EXPECT_DOUBLE_EQ(readings.snapshot(SourceKind::GrokBuild)->windows[0].usedPercent, 10) << "the newer live answer";

    in.nowMs = kNow + 25 * 3'600'000;
    in.switchesOn.erase(std::string(agentspeed::quota::kGrokLiveSwitch));
    readings = f.service->tick(in);
    EXPECT_FALSE(readings.signedIn(SourceKind::GrokBuild)) << "older than 24 hours";
    EXPECT_TRUE(readings.snapshot(SourceKind::GrokBuild).has_value());
}

TEST(QuotaVendorService, TurningARouteOffForgetsItAndItAsksAgainWhenOn) {
    ServiceFixture f;
    auto in = ServiceFixture::only(VendorRoute::Droid);
    f.service->tick(in);
    ASSERT_EQ(f.transport.sent.size(), 1u);
    auto off = in;
    off.enabledSources.clear();
    off.nowMs = kNow + kMinute;
    auto readings = f.service->tick(off);
    EXPECT_FALSE(readings.snapshot(SourceKind::Droid).has_value());
    EXPECT_FALSE(readings.signedIn(SourceKind::Droid));
    in.nowMs = kNow + 2 * kMinute;
    readings = f.service->tick(in);
    EXPECT_EQ(f.transport.sent.size(), 2u) << "asks again at once";
    EXPECT_TRUE(readings.snapshot(SourceKind::Droid).has_value());
}

TEST(QuotaVendorService, DeferredAnswersArriveWithTheNextTick) {
    ServiceFixture f;
    f.transport.deferred = true;
    auto in = ServiceFixture::only(VendorRoute::Kimi);
    auto readings = f.service->tick(in);
    EXPECT_TRUE(f.service->busy());
    EXPECT_FALSE(readings.snapshot(SourceKind::Kimi).has_value());
    in.nowMs = kNow + 6 * kMinute;
    f.service->tick(in);
    EXPECT_EQ(f.transport.sent.size(), 1u) << "one request in flight per route";
    f.transport.completeAll();
    EXPECT_EQ(f.wakes, 1);
    // Applied; six minutes after the first request the next one is due at once.
    readings = f.service->tick(in);
    EXPECT_TRUE(readings.snapshot(SourceKind::Kimi).has_value());
    EXPECT_EQ(f.transport.sent.size(), 2u);

    // A route turned off while its request is in flight drops the late answer.
    ASSERT_EQ(f.transport.pending.size(), 1u);
    auto off = in;
    off.enabledSources.clear();
    f.service->tick(off);
    f.transport.completeAll();
    readings = f.service->tick(off);
    EXPECT_TRUE(readings.routes.empty());
    EXPECT_FALSE(f.service->busy());
}

TEST(QuotaVendorService, FailuresRedirectsCertificatesAndNonJson) {
    ServiceFixture f;
    f.transport.handler = [](const Request&) { return failure(agentspeed::quota::http::Failure::Redirect); };
    auto readings = f.service->tick(ServiceFixture::only(VendorRoute::Droid));
    EXPECT_EQ(readings.status(VendorRoute::Droid)->kind, VendorStatusKind::Failed);
    EXPECT_EQ(f.logged.back(), (std::pair<std::string, int>{"api.factory.ai", 0}));

    ServiceFixture g;
    g.transport.handler = [](const Request&) { return failure(agentspeed::quota::http::Failure::NoCertificates); };
    readings = g.service->tick(ServiceFixture::only(VendorRoute::Droid));
    EXPECT_EQ(readings.status(VendorRoute::Droid)->kind, VendorStatusKind::NoCertificates);

    ServiceFixture h;
    h.transport.handler = [](const Request&) { return answer(200, "<html>sign in</html>"); };
    readings = h.service->tick(ServiceFixture::only(VendorRoute::Droid));
    EXPECT_EQ(readings.status(VendorRoute::Droid)->kind, VendorStatusKind::Failed);
    EXPECT_FALSE(readings.snapshot(SourceKind::Droid).has_value());

    ServiceFixture z;
    z.vendors.bodies["quota/limit"] = R"({"success": false, "code": 401})";
    readings = z.service->tick(ServiceFixture::only(VendorRoute::Zcode));
    EXPECT_EQ(readings.status(VendorRoute::Zcode)->kind, VendorStatusKind::Failed);
}

TEST(QuotaVendorService, AHostRunsOnlyTheCliRoutes) {
    ServiceFixture f(agentspeed::quota::hostRoutes());
    f.service->tick(ServiceFixture::allOn());
    EXPECT_TRUE(f.sentTo("z.ai").empty());
    EXPECT_TRUE(f.sentTo("qoder").empty());
    EXPECT_TRUE(f.sentTo("workbuddy").empty());
    EXPECT_TRUE(f.secrets.lookups.empty());
    EXPECT_EQ(f.sessions->calls, 0);
    EXPECT_FALSE(f.touched("Devin"));
    EXPECT_EQ(f.transport.sent.size(), 6u) << "grok live, kimi, gemini (2), droid, antigravity";
}

TEST(QuotaVendorService, HostInputsFromTheRelayConfig) {
    ServiceFixture f(agentspeed::quota::hostRoutes());
    auto in = agentspeed::quota::hostInputs({SourceKind::GrokBuild, SourceKind::Kimi, SourceKind::Codex}, true, false,
                                            {SourceKind::GrokBuild, SourceKind::Kimi}, kNow);
    EXPECT_EQ(agentspeed::quota::activeRoutes(in), (std::set<VendorRoute>{VendorRoute::GrokBuild, VendorRoute::Kimi}));
    f.service->tick(in);
    EXPECT_EQ(f.transport.urls(), (std::vector<std::string>{"https://api.kimi.com/coding/v1/usages"}));
    in = agentspeed::quota::hostInputs({SourceKind::GrokBuild}, true, true, {SourceKind::GrokBuild}, kNow);
    EXPECT_EQ(agentspeed::quota::activeRoutes(in), (std::set<VendorRoute>{VendorRoute::GrokBuild, VendorRoute::GrokBuildLive}));
}

TEST(QuotaVendorService, StatusIdsForTheRelay) {
    using agentspeed::quota::vendorStatusId;
    EXPECT_EQ(vendorStatusId(VendorStatusKind::Ok), "ok");
    EXPECT_EQ(vendorStatusId(VendorStatusKind::NoCredential), "noCredential");
    EXPECT_EQ(vendorStatusId(VendorStatusKind::NoBrowser), "noBrowser");
    EXPECT_EQ(vendorStatusId(VendorStatusKind::Expired), "expired");
    EXPECT_EQ(vendorStatusId(VendorStatusKind::Denied), "denied");
    EXPECT_EQ(vendorStatusId(VendorStatusKind::Failed), "failed");
    EXPECT_EQ(vendorStatusId(VendorStatusKind::NetworkOff), "networkOff");
    EXPECT_EQ(vendorStatusId(VendorStatusKind::NotRunning), "notRunning");
    EXPECT_EQ(vendorStatusId(VendorStatusKind::NoCertificates), "noCertificates");
}

}  // namespace
