#include <fcntl.h>
#include <gtest/gtest.h>
#include <unistd.h>

#include <chrono>
#include <map>
#include <thread>

#include "agentspeed/remote_protocol.hpp"
#include "fixture_support.hpp"
#include "net_stub.hpp"
#include "quota_session.hpp"
#include "quota_vendor_support.hpp"

// REMOTE 11 in the relay's process: the QUOTAS 10 vendor fixtures through the relay's readers and its HTTPS client
// (a local TLS server answering for every vendor host), the local routes, and the rules that keep a disabled source,
// a switched-off network and an agent that does not run from asking anything. Every line must pass the app's decoder.
using namespace agentspeed;
using namespace agentspeed::relay;
using namespace relay_test;

namespace {

constexpr std::int64_t kNow = 1794338773000;
const std::string kHost = "8C0D6B0A-1111-4222-8333-444455556666";
const std::vector<std::string> kVendorHosts = {"api.kimi.com", "cloudcode-pa.googleapis.com", "api.factory.ai", "cli-chat-proxy.grok.com"};

const TestPki& pki() {
    static const TestPki made = makePki(kVendorHosts);
    return made;
}

json::Value vendorCase(const char* name) {
    static const fixtures::JsonFile file(fixtures::fixtureDir() / "quota-cases.json");
    return file.root()["vendors"][name];
}

std::string body(const char* name) { return fixtures::compactJson(vendorCase(name)["body"]); }

std::string requestLine(const std::string& request) { return request.substr(0, request.find("\r\n")); }

std::string headerOf(const std::string& request, const std::string& name) {
    const auto at = request.find("\r\n" + name + ": ");
    if (at == std::string::npos) return {};
    const auto start = at + name.size() + 4;
    return request.substr(start, request.find("\r\n", start) - start);
}

// Every header name of a request, in order.
std::vector<std::string> headerNames(const std::string& request) {
    std::vector<std::string> out;
    std::size_t pos = request.find("\r\n") + 2;
    while (pos < request.size()) {
        const auto end = request.find("\r\n", pos);
        if (end == pos) break;
        out.push_back(request.substr(pos, request.find(':', pos) - pos));
        pos = end + 2;
    }
    return out;
}

// Each vendor's fixture answer by its request line.
std::optional<std::string> vendorAnswer(const std::string& request) {
    const auto line = requestLine(request);
    if (line == "GET /coding/v1/usages HTTP/1.1") return answer(200, body("kimiCurrent"));
    if (line == "POST /v1internal:loadCodeAssist HTTP/1.1") return answer(200, body("geminiProject"));
    if (line == "POST /v1internal:retrieveUserQuota HTTP/1.1") return answer(200, body("gemini"));
    if (line == "GET /api/billing/limits HTTP/1.1") return answer(200, body("droid"));
    if (line == "GET /v1/billing?format=credits HTTP/1.1") return answer(200, body("grokApi"));
    return answer(404, "{}");
}

class FakeLocator final : public quota::AntigravityLocator {
public:
    std::vector<quota::AntigravityServer> list;
    std::vector<quota::AntigravityServer> servers() override { return list; }
};

class Rig {
public:
    quota2_test::TempHome home;
    std::set<SourceKind> running;
    std::shared_ptr<quota2_test::CountingFiles> files = std::make_shared<quota2_test::CountingFiles>();
    std::shared_ptr<FakeLocator> antigravity = std::make_shared<FakeLocator>();
    net::ClientOptions client;
    std::unique_ptr<QuotaSession> session;

    Rig() {
        path_ = home.file("relay-lines");
        fd_ = ::open(path_.c_str(), O_CREAT | O_RDWR | O_TRUNC | O_APPEND | O_CLOEXEC, 0600);
        out_ = std::make_unique<Output>(fd_);
        home.write("ca.pem", pki().caPem);
        client.caBundles = {home.file("ca.pem")};
    }
    ~Rig() {
        session.reset();
        ::close(fd_);
    }

    std::string folder() const { return home.file("relay"); }

    // Every vendor host goes to `port` on 127.0.0.1.
    void routeTo(int port) {
        client.connectTo = [port](const std::string&, int) { return std::optional(std::make_pair(std::string("127.0.0.1"), port)); };
    }

    void start(std::set<SourceKind> enabled) {
        QuotaOptions options;
        options.folder = folder();
        options.version = "9.9.9";
        options.environment = [home = home.path.string()](const std::string& name) -> std::optional<std::string> {
            if (name == "HOME") return home;
            return std::nullopt;
        };
        options.vendors = quota::VendorEnvironment::fixed(home.path.string(), {}, files);
        options.self = AGENTSPEED_RELAY_BINARY;
        options.client = client;
        options.running = [this] { return running; };
        options.antigravity = antigravity;
        session = std::make_unique<QuotaSession>(*out_, std::move(enabled), std::move(options), [] { return kNow; });
    }

    void configure(std::set<SourceKind> sources, bool network, bool grokLive = false) {
        session->setConfig(QuotaConfig{std::move(sources), network, grokLive, false});
    }

    // Ticks until `done` holds (true) or 10 s pass (false).
    bool runUntil(const std::function<bool()>& done) {
        const auto deadline = std::chrono::steady_clock::now() + std::chrono::seconds(10);
        while (std::chrono::steady_clock::now() < deadline) {
            session->tick();
            if (done()) return true;
            std::this_thread::sleep_for(std::chrono::milliseconds(20));
        }
        return false;
    }
    void settle(int ticks = 10) {
        for (int k = 0; k < ticks; ++k) {
            session->tick();
            std::this_thread::sleep_for(std::chrono::milliseconds(20));
        }
    }

    // The lines as written, for a failure's message.
    std::string dump() const { return fixtures::readText(path_); }

    // Every line written so far, as the app decodes it.
    std::vector<remote::RelayMessage> messages() const {
        const remote::LineDecoder decoder(kHost);
        std::vector<remote::RelayMessage> out;
        const auto text = fixtures::readText(path_);
        std::size_t pos = 0;
        while (pos < text.size()) {
            const auto end = text.find('\n', pos);
            const auto line = text.substr(pos, end - pos);
            pos = end + 1;
            auto outcome = decoder.decode(line);
            if (const auto* error = std::get_if<remote::LineError>(&outcome)) ADD_FAILURE() << "the app refuses " << error->field << ": " << line;
            if (auto* message = std::get_if<remote::RelayMessage>(&outcome)) out.push_back(std::move(*message));
        }
        return out;
    }
    std::vector<remote::HostQuota> reports(SourceKind source) const {
        std::vector<remote::HostQuota> out;
        for (const auto& m : messages()) {
            if (const auto* q = std::get_if<remote::HostQuota>(&m); q && q->snapshot.source == source) out.push_back(*q);
        }
        return out;
    }
    std::optional<remote::HostQuota> last(SourceKind source) const {
        auto all = reports(source);
        if (all.empty()) return std::nullopt;
        return all.back();
    }
    bool hasWindows(SourceKind source) const {
        const auto q = last(source);
        return q && !q->snapshot.windows.empty();
    }
    std::vector<SourceKind> removed() const {
        std::vector<SourceKind> out;
        for (const auto& m : messages()) {
            if (const auto* r = std::get_if<remote::HostQuotaRemoved>(&m)) out.push_back(r->source);
        }
        return out;
    }

    void writeCredentials() const {
        home.write(".kimi-code/credentials/kimi-code.json", R"({"access_token":"kimi-token","expires_at":4102444000,"refresh_token":"never"})");
        home.write(".gemini/oauth_creds.json", R"({"access_token":"gemini-token","expiry_date":4102444000000})");
        home.write(".factory/.env", "FACTORY_API_KEY=fk-test\n");
        home.write(".grok/auth.json", R"({"https://auth.x.ai":{"key":"grok-token","expires_at":"2099-12-31T00:00:00Z"}})");
    }

private:
    std::string path_;
    int fd_ = -1;
    std::unique_ptr<Output> out_;
};

void expectHealthy(const remote::HostQuota& q) {
    EXPECT_TRUE(q.running);
    EXPECT_TRUE(q.signedIn);
    EXPECT_EQ(q.status, remote::HostQuota::Status::Ok);
}

}  // namespace

TEST(RelayQuotaSession, VendorFixturesThroughTheRelaysReadersAndClient) {
    StubServer vendors(vendorAnswer, &pki().leaf);
    StubServer language([](const std::string& request) -> std::optional<std::string> {
        if (requestLine(request).find("RetrieveUserQuotaSummary") != std::string::npos) return answer(200, body("antigravity"));
        return answer(404, "{}");
    }, &pki().selfSigned);
    Rig rig;
    rig.writeCredentials();
    rig.routeTo(vendors.port());
    rig.antigravity->list = {quota::antigravityServer(4242, {{"https", language.port(), std::string("csrf-1")}})};
    const std::set<SourceKind> sources = {SourceKind::GrokBuild, SourceKind::Kimi, SourceKind::Gemini, SourceKind::Droid, SourceKind::Antigravity};
    rig.running = sources;
    rig.start(sources);
    rig.configure(sources, true, true);
    ASSERT_TRUE(rig.runUntil([&] {
        for (const auto source : sources) {
            if (!rig.hasWindows(source)) return false;
        }
        return true;
    })) << rig.dump();

    quota2_test::expectWindows(rig.last(SourceKind::Kimi)->snapshot.windows, vendorCase("kimiCurrent")["expected"]);
    quota2_test::expectWindows(rig.last(SourceKind::Gemini)->snapshot.windows, vendorCase("gemini")["expected"]);
    quota2_test::expectWindows(rig.last(SourceKind::Droid)->snapshot.windows, vendorCase("droid")["expected"]);
    // The live answer is newer than the log's line (QUOTAS 9.2: the newer wins).
    quota2_test::expectWindows(rig.last(SourceKind::GrokBuild)->snapshot.windows, vendorCase("grokApi")["expected"]);
    quota2_test::expectWindows(rig.last(SourceKind::Antigravity)->snapshot.windows, vendorCase("antigravity")["expected"]);
    for (const auto source : sources) {
        SCOPED_TRACE(std::string(sourceId(source)));
        expectHealthy(*rig.last(source));
        EXPECT_EQ(rig.last(source)->snapshot.observedMs, kNow);
    }

    // Exactly the headers QUOTAS 9 lists, the host's own credentials, and nothing else.
    std::map<std::string, std::string> byLine;
    for (const auto& request : vendors.requests()) byLine[requestLine(request)] = request;
    ASSERT_EQ(byLine.size(), 5u);
    const auto& kimi = byLine["GET /coding/v1/usages HTTP/1.1"];
    EXPECT_EQ(headerNames(kimi), (std::vector<std::string>{"Host", "User-Agent", "Accept", "Authorization", "Connection"}));
    EXPECT_EQ(headerOf(kimi, "Host"), "api.kimi.com");
    EXPECT_EQ(headerOf(kimi, "User-Agent"), "AgentSpeed/9.9.9 (Linux)");
    EXPECT_EQ(headerOf(kimi, "Authorization"), "Bearer kimi-token");
    EXPECT_EQ(headerOf(byLine["GET /api/billing/limits HTTP/1.1"], "Authorization"), "Bearer fk-test");
    EXPECT_EQ(headerOf(byLine["GET /v1/billing?format=credits HTTP/1.1"], "Authorization"), "Bearer grok-token");
    EXPECT_EQ(headerOf(byLine["GET /v1/billing?format=credits HTTP/1.1"], "x-xai-token-auth"), "xai-grok-cli");
    const auto& quotaRequest = byLine["POST /v1internal:retrieveUserQuota HTTP/1.1"];
    EXPECT_EQ(headerOf(quotaRequest, "Authorization"), "Bearer gemini-token");
    EXPECT_EQ(quotaRequest.substr(quotaRequest.find("\r\n\r\n") + 4), R"({"project":"proj-123"})");
    ASSERT_EQ(language.requests().size(), 1u);
    EXPECT_EQ(headerOf(language.requests()[0], "X-Codeium-Csrf-Token"), "csrf-1");
    EXPECT_EQ(headerOf(language.requests()[0], "Host"), "127.0.0.1:" + std::to_string(language.port()));
    for (const auto& request : vendors.requests()) EXPECT_EQ(request.find("never"), std::string::npos) << "a refresh token is never sent";
}

TEST(RelayQuotaSession, LegacyAnswersAndTheLanguageServersStatusCall) {
    StubServer vendors([](const std::string&) { return answer(200, body("kimiLegacy")); }, &pki().leaf);
    StubServer language([](const std::string& request) -> std::optional<std::string> {
        if (requestLine(request).find("GetUserStatus") != std::string::npos) return answer(200, body("antigravityStatus"));
        return answer(404, "{}");
    }, nullptr);
    Rig rig;
    rig.writeCredentials();
    rig.routeTo(vendors.port());
    rig.antigravity->list = {quota::antigravityServer(7, {{"http", language.port(), std::nullopt}})};
    rig.running = {SourceKind::Kimi, SourceKind::Antigravity};
    rig.start(rig.running);
    rig.configure(rig.running, true);
    ASSERT_TRUE(rig.runUntil([&] { return rig.hasWindows(SourceKind::Kimi) && rig.hasWindows(SourceKind::Antigravity); })) << rig.dump();
    quota2_test::expectWindows(rig.last(SourceKind::Kimi)->snapshot.windows, vendorCase("kimiLegacy")["expected"]);
    quota2_test::expectWindows(rig.last(SourceKind::Antigravity)->snapshot.windows, vendorCase("antigravityStatus")["expected"]);
    ASSERT_EQ(language.requests().size(), 2u);
    EXPECT_EQ(headerOf(language.requests()[0], "X-Codeium-Csrf-Token"), "");
}

TEST(RelayQuotaSession, GrokBuildsLogNeedsNoNetwork) {
    StubServer vendors(vendorAnswer, &pki().leaf);
    Rig rig;
    rig.writeCredentials();
    std::string log;
    for (const auto& line : vendorCase("grokLog")["lines"].elements()) log += *line.string() + "\n";
    rig.home.write(".grok/logs/unified.jsonl", log);
    rig.routeTo(vendors.port());
    rig.start({SourceKind::GrokBuild});
    rig.configure({SourceKind::GrokBuild}, false, true);
    ASSERT_TRUE(rig.runUntil([&] { return rig.hasWindows(SourceKind::GrokBuild); })) << rig.dump();
    const auto q = *rig.last(SourceKind::GrokBuild);
    EXPECT_EQ(q.snapshot.observedMs, *vendorCase("grokLog")["expected"]["observedMs"].truncated());
    quota2_test::expectWindows(q.snapshot.windows, vendorCase("grokLog")["expected"]["windows"]);
    EXPECT_TRUE(q.signedIn);
    EXPECT_FALSE(q.running);
    rig.settle();
    EXPECT_EQ(vendors.connections(), 0);
}

TEST(RelayQuotaSession, ADisabledSourceReadsNothing) {
    StubServer vendors(vendorAnswer, &pki().leaf);
    Rig rig;
    rig.writeCredentials();
    rig.home.write("relay/quota/claude-cli.json",
                   R"({"version":1,"receivedMs":1794338000000,"windows":{"five_hour":{"used_percentage":6,"resets_at":4102000000}}})");
    rig.routeTo(vendors.port());
    rig.running = {SourceKind::Kimi, SourceKind::Codex, SourceKind::ClaudeCode};
    // The quota switches are off: the sources are enabled, the config names none.
    rig.start(rig.running);
    rig.configure({}, true);
    const fixtures::JsonFile codexLine(fixtures::fixtureDir() / "quota-cases.json");
    const auto rateLimits = codexLine.root()["codex"].at(0)["lines"].at(0)["line"]["payload"]["rate_limits"];
    rig.session->codexObserver()(rateLimits, kNow);
    rig.settle();
    EXPECT_TRUE(rig.reports(SourceKind::Kimi).empty());
    EXPECT_TRUE(rig.reports(SourceKind::Codex).empty());
    EXPECT_TRUE(rig.reports(SourceKind::ClaudeCode).empty());
    EXPECT_TRUE(rig.files->touched.empty());
    EXPECT_EQ(vendors.connections(), 0);

    // Switched on in the config, but disabled in Data sources (`start.sources`): still nothing.
    Rig disabled;
    disabled.writeCredentials();
    disabled.routeTo(vendors.port());
    disabled.running = {SourceKind::Kimi, SourceKind::Codex};
    disabled.start({});
    disabled.configure({SourceKind::Kimi, SourceKind::Codex}, true);
    disabled.session->codexObserver()(rateLimits, kNow);
    disabled.settle();
    EXPECT_TRUE(disabled.reports(SourceKind::Kimi).empty());
    EXPECT_TRUE(disabled.reports(SourceKind::Codex).empty());
    EXPECT_TRUE(disabled.files->touched.empty());
    EXPECT_EQ(vendors.connections(), 0);
}

TEST(RelayQuotaSession, NothingAsksWhileTheNetworkIsOffOrTheAgentIsNotRunning) {
    StubServer vendors(vendorAnswer, &pki().leaf);
    Rig rig;
    rig.writeCredentials();
    rig.routeTo(vendors.port());
    rig.running = {SourceKind::Kimi};
    rig.start({SourceKind::Kimi});
    rig.configure({SourceKind::Kimi}, false);
    ASSERT_TRUE(rig.runUntil([&] { return rig.last(SourceKind::Kimi).has_value(); })) << rig.dump();
    rig.settle();
    auto q = *rig.last(SourceKind::Kimi);
    EXPECT_EQ(q.status, remote::HostQuota::Status::NetworkOff);
    EXPECT_TRUE(q.snapshot.windows.empty());
    EXPECT_TRUE(rig.files->touched.empty()) << "no credential is looked at without a request";
    EXPECT_EQ(vendors.connections(), 0);

    // The network on, the agent not running on the host: signed in, but nothing asked.
    rig.running.clear();
    rig.configure({SourceKind::Kimi}, true);
    ASSERT_TRUE(rig.runUntil([&] { return rig.last(SourceKind::Kimi)->status == remote::HostQuota::Status::NotRunning; })) << rig.dump();
    rig.settle(30);
    q = *rig.last(SourceKind::Kimi);
    EXPECT_TRUE(q.signedIn);
    EXPECT_FALSE(q.running);
    EXPECT_EQ(vendors.connections(), 0);
}

TEST(RelayQuotaSession, AHostWithoutCaCertificatesSaysSo) {
    StubServer vendors(vendorAnswer, &pki().leaf);
    Rig rig;
    rig.writeCredentials();
    rig.routeTo(vendors.port());
    rig.client.caBundles = {rig.home.file("no-such-bundle.pem")};
    rig.running = {SourceKind::Droid};
    rig.start({SourceKind::Droid});
    rig.configure({SourceKind::Droid}, true);
    ASSERT_TRUE(rig.runUntil([&] {
        const auto q = rig.last(SourceKind::Droid);
        return q && q->status == remote::HostQuota::Status::NoCertificates;
    })) << rig.dump();
    EXPECT_EQ(vendors.connections(), 0);
}

TEST(RelayQuotaSession, CodexAndTheClaudeCodeBridgeFile) {
    Rig rig;
    rig.home.write(".codex/auth.json", R"({"auth_mode":"chatgpt"})");
    rig.home.write(".claude.json", R"({"oauthAccount":{"emailAddress":"never read"}})");
    rig.home.write("relay/quota/claude-cli.json", R"({"version":1,"receivedMs":1794338685000,"windows":{"five_hour":)"
                                                  R"({"used_percentage":6,"resets_at":1794354000},"seven_day":{"used_percentage":86,"resets_at":1794448800}}})");
    rig.running = {SourceKind::Codex};
    rig.start({SourceKind::Codex, SourceKind::ClaudeCode});
    rig.configure({SourceKind::Codex, SourceKind::ClaudeCode}, false);
    const fixtures::JsonFile cases(fixtures::fixtureDir() / "quota-cases.json");
    const auto codexCase = cases.root()["codex"].at(0);
    const auto line = codexCase["lines"].at(0)["line"];
    rig.session->codexObserver()(line["payload"]["rate_limits"], *codexCase["expected"]["observedMs"].truncated());
    ASSERT_TRUE(rig.runUntil([&] { return rig.hasWindows(SourceKind::Codex) && rig.hasWindows(SourceKind::ClaudeCode); })) << rig.dump();

    const auto codex = *rig.last(SourceKind::Codex);
    EXPECT_EQ(codex.snapshot.observedMs, *codexCase["expected"]["observedMs"].truncated());
    quota2_test::expectWindows(codex.snapshot.windows, codexCase["expected"]["windows"]);
    expectHealthy(codex);
    const auto claude = *rig.last(SourceKind::ClaudeCode);
    EXPECT_EQ(claude.snapshot.observedMs, 1794338685000);
    ASSERT_EQ(claude.snapshot.windows.size(), 2u);
    EXPECT_EQ(claude.snapshot.windows[0].resetsAtMs, 1794354000000);
    EXPECT_DOUBLE_EQ(claude.snapshot.windows[1].usedPercent, 86);
    EXPECT_TRUE(claude.signedIn);
    EXPECT_FALSE(claude.running);

    // A source switched off goes, and its snapshot with it.
    rig.configure({SourceKind::ClaudeCode}, false);
    rig.settle(3);
    EXPECT_EQ(rig.removed(), std::vector<SourceKind>{SourceKind::Codex});
    rig.session->codexObserver()(line["payload"]["rate_limits"], kNow);
    rig.settle(3);
    EXPECT_EQ(rig.reports(SourceKind::Codex).size(), 1u);
    // An unchanged report is not sent again.
    EXPECT_EQ(rig.reports(SourceKind::ClaudeCode).size(), 1u);
}
