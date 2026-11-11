#include <gtest/gtest.h>
#include <unistd.h>

#include <chrono>
#include <filesystem>
#include <fstream>
#include <map>
#include <thread>

#include "fake_home.hpp"
#include "net/https_client.hpp"
#include "net_stub.hpp"

// REMOTE 11.4 against local servers: TLS with a test CA and the server's name checked, redirects refused, the 256 KiB
// cap, the timeout, the `CONNECT` proxy and `no_proxy`, the loopback route's self-signed certificate, and no request
// without a CA bundle.
using namespace agentspeed;
using namespace agentspeed::relay;
using namespace relay_test;
using quota::http::Failure;
namespace fs = std::filesystem;

namespace {

const std::vector<std::string> kHosts = {"api.kimi.com", "cloudcode-pa.googleapis.com", "api.factory.ai", "cli-chat-proxy.grok.com"};

const TestPki& pki() {
    static const TestPki made = makePki(kHosts);
    return made;
}

class HttpsClient : public ::testing::Test {
protected:
    void SetUp() override {
        folder_ = makeTempDir("agentspeed-https");
        std::ofstream(folder_ / "ca.pem") << pki().caPem;
    }
    void TearDown() override { fs::remove_all(folder_); }

    // Every vendor host connects to `port` on 127.0.0.1.
    net::ClientOptions options(int port, std::map<std::string, std::string> environment = {}) const {
        net::ClientOptions o;
        o.userAgent = "AgentSpeed/9.9.9 (Linux)";
        o.caBundles = {(folder_ / "missing.pem").string(), (folder_ / "ca.pem").string()};
        o.variable = [environment](std::string_view name) -> std::optional<std::string> {
            const auto found = environment.find(std::string(name));
            if (found == environment.end()) return std::nullopt;
            return found->second;
        };
        o.connectTo = [port](const std::string&, int) { return std::optional(std::make_pair(std::string("127.0.0.1"), port)); };
        return o;
    }

    static quota::http::Request kimi() {
        quota::http::Request r;
        r.url = "https://api.kimi.com/coding/v1/usages";
        r.headers = {{"Accept", "application/json"}, {"Authorization", "Bearer kimi-secret"}};
        return r;
    }

    fs::path folder_;
};

}  // namespace

TEST_F(HttpsClient, AsksOverTlsWithTheHostsCertificates) {
    StubServer server([](const std::string&) { return answer(200, R"({"usages":{}})", {"Date: Tue, 10 Nov 2026 19:26:13 GMT"}); },
                      &pki().leaf);
    const auto response = net::fetch(kimi(), options(server.port()));
    EXPECT_EQ(response.failure, Failure::None);
    EXPECT_EQ(response.status, 200);
    EXPECT_EQ(response.body, R"({"usages":{}})");
    EXPECT_EQ(response.dateMs, 1794338773000);
    EXPECT_GT(response.receivedMs, 0);
    ASSERT_EQ(server.requests().size(), 1u);
    EXPECT_EQ(server.requests()[0],
              "GET /coding/v1/usages HTTP/1.1\r\nHost: api.kimi.com\r\nUser-Agent: AgentSpeed/9.9.9 (Linux)\r\nAccept: application/json\r\n"
              "Authorization: Bearer kimi-secret\r\nConnection: close\r\n\r\n");
}

TEST_F(HttpsClient, PostsABody) {
    StubServer server([](const std::string&) { return answer(200, "{}"); }, &pki().leaf);
    quota::http::Request r;
    r.method = quota::http::Method::Post;
    r.url = "https://cloudcode-pa.googleapis.com/v1internal:retrieveUserQuota";
    r.headers = {{"Content-Type", "application/json"}};
    r.body = R"({"project": "p"})";
    EXPECT_EQ(net::fetch(r, options(server.port())).failure, Failure::None);
    ASSERT_EQ(server.requests().size(), 1u);
    EXPECT_NE(server.requests()[0].find("Content-Length: 16\r\nConnection: close\r\n\r\n{\"project\": \"p\"}"), std::string::npos);
}

TEST_F(HttpsClient, RefusesACertificateThatIsNotTheHosts) {
    StubServer selfSigned([](const std::string&) { return answer(200, "{}"); }, &pki().selfSigned);
    EXPECT_EQ(net::fetch(kimi(), options(selfSigned.port())).failure, Failure::Network);
    EXPECT_TRUE(selfSigned.requests().empty());
    // Signed by the CA, but for other names.
    const auto other = makePki({"api.other.example"});
    StubServer misnamed([](const std::string&) { return answer(200, "{}"); }, &other.leaf);
    auto o = options(misnamed.port());
    std::ofstream(folder_ / "other.pem") << other.caPem;
    o.caBundles = {(folder_ / "other.pem").string()};
    EXPECT_EQ(net::fetch(kimi(), o).failure, Failure::Network);
    EXPECT_TRUE(misnamed.requests().empty());
}

TEST_F(HttpsClient, SendsNothingWithoutCaCertificates) {
    StubServer server([](const std::string&) { return answer(200, "{}"); }, &pki().leaf);
    auto o = options(server.port());
    o.caBundles = {(folder_ / "none.pem").string()};
    EXPECT_EQ(net::fetch(kimi(), o).failure, Failure::NoCertificates);
    std::ofstream(folder_ / "empty.pem") << "not a certificate\n";
    o.caBundles = {(folder_ / "empty.pem").string(), (folder_ / "ca.pem").string()};
    EXPECT_EQ(net::fetch(kimi(), o).failure, Failure::NoCertificates);
    EXPECT_EQ(server.connections(), 0);
}

TEST_F(HttpsClient, NeverFollowsARedirect) {
    StubServer server([](const std::string&) { return answer(302, "", {"Location: https://api.kimi.com/elsewhere"}); }, &pki().leaf);
    const auto response = net::fetch(kimi(), options(server.port()));
    EXPECT_EQ(response.failure, Failure::Redirect);
    EXPECT_EQ(server.requests().size(), 1u);
}

TEST_F(HttpsClient, StopsAtTheCap) {
    const std::string big(quota::http::kBodyCap + 1, 'x');
    StubServer server([&](const std::string&) { return answer(200, big); }, &pki().leaf);
    EXPECT_EQ(net::fetch(kimi(), options(server.port())).failure, Failure::TooLarge);
    StubServer chunked(
        [&](const std::string&) {
            std::string out = "HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\n\r\n";
            for (int k = 0; k < 5; ++k) out += "10000\r\n" + std::string(0x10000, 'y') + "\r\n";
            return std::optional(out + "0\r\n\r\n");
        },
        &pki().leaf);
    EXPECT_EQ(net::fetch(kimi(), options(chunked.port())).failure, Failure::TooLarge);
    const std::string fits(quota::http::kBodyCap, 'z');
    StubServer exact([&](const std::string&) { return answer(200, fits); }, &pki().leaf);
    const auto response = net::fetch(kimi(), options(exact.port()));
    EXPECT_EQ(response.failure, Failure::None);
    EXPECT_EQ(response.body.size(), quota::http::kBodyCap);
}

TEST_F(HttpsClient, TimesOut) {
    StubServer server([](const std::string&) { return std::optional<std::string>(); }, &pki().leaf);
    auto o = options(server.port());
    o.timeoutMs = 400;
    const auto started = std::chrono::steady_clock::now();
    EXPECT_EQ(net::fetch(kimi(), o).failure, Failure::Timeout);
    const auto took = std::chrono::steady_clock::now() - started;
    EXPECT_GE(took, std::chrono::milliseconds(350));
    EXPECT_LT(took, std::chrono::seconds(3));
}

TEST_F(HttpsClient, ACancelEndsTheRequestAtOnce) {
    StubServer server([](const std::string&) { return std::optional<std::string>(); }, &pki().leaf);
    int cancel[2];
    ASSERT_EQ(::pipe(cancel), 0);
    std::thread later([&] {
        std::this_thread::sleep_for(std::chrono::milliseconds(200));
        [[maybe_unused]] const auto n = ::write(cancel[1], "x", 1);
    });
    const auto started = std::chrono::steady_clock::now();
    EXPECT_EQ(net::fetch(kimi(), options(server.port()), cancel[0]).failure, Failure::Network);
    EXPECT_LT(std::chrono::steady_clock::now() - started, std::chrono::seconds(3));
    later.join();
    ::close(cancel[0]);
    ::close(cancel[1]);
}

TEST_F(HttpsClient, GoesThroughTheProxyWithConnect) {
    StubServer server([](const std::string&) { return answer(200, R"({"ok":true})"); }, &pki().leaf);
    ProxyStub proxy(server.port());
    auto o = options(0, {{"https_proxy", "http://127.0.0.1:" + std::to_string(proxy.port())}});
    o.connectTo = nullptr;  // a proxied request resolves nothing itself
    const auto response = net::fetch(kimi(), o);
    EXPECT_EQ(response.failure, Failure::None);
    EXPECT_EQ(response.body, R"({"ok":true})");
    EXPECT_EQ(proxy.connects(), std::vector<std::string>{"CONNECT api.kimi.com:443 HTTP/1.1"});
    ASSERT_EQ(server.requests().size(), 1u);
    EXPECT_NE(server.requests()[0].find("Authorization: Bearer kimi-secret"), std::string::npos);

    ProxyStub refusing(server.port(), true);
    o.variable = [port = refusing.port()](std::string_view name) -> std::optional<std::string> {
        if (name == "HTTPS_PROXY") return "http://127.0.0.1:" + std::to_string(port);
        return std::nullopt;
    };
    EXPECT_EQ(net::fetch(kimi(), o).failure, Failure::Network);
    EXPECT_EQ(refusing.connects().size(), 1u);
    EXPECT_EQ(server.requests().size(), 1u);
}

TEST_F(HttpsClient, NoProxyGoesDirect) {
    StubServer server([](const std::string&) { return answer(200, "{}"); }, &pki().leaf);
    ProxyStub proxy(server.port());
    const auto o = options(server.port(), {{"https_proxy", "http://127.0.0.1:" + std::to_string(proxy.port())}, {"no_proxy", ".kimi.com"}});
    EXPECT_EQ(net::fetch(kimi(), o).failure, Failure::None);
    EXPECT_TRUE(proxy.connects().empty());
    EXPECT_EQ(server.requests().size(), 1u);
}

TEST_F(HttpsClient, TheLoopbackRouteTakesASelfSignedCertificateAndNoProxy) {
    StubServer tls([](const std::string&) { return answer(200, R"({"response":{}})"); }, &pki().selfSigned);
    StubServer plain([](const std::string&) { return answer(200, R"({"userStatus":{}})"); }, nullptr);
    ProxyStub proxy(tls.port());
    auto o = options(0, {{"https_proxy", "http://127.0.0.1:" + std::to_string(proxy.port())}});
    o.connectTo = nullptr;
    o.caBundles = {(folder_ / "none.pem").string()};
    for (const auto& [scheme, port] : {std::pair{std::string("https"), tls.port()}, std::pair{std::string("http"), plain.port()}}) {
        quota::http::Request r;
        r.method = quota::http::Method::Post;
        r.loopback = true;
        r.url = scheme + "://127.0.0.1:" + std::to_string(port) + "/exa.language_server_pb.LanguageServerService/RetrieveUserQuotaSummary";
        r.headers = {{"Connect-Protocol-Version", "1"}, {"Content-Type", "application/json"}};
        r.body = R"({"forceRefresh": true})";
        const auto response = net::fetch(r, o);
        EXPECT_EQ(response.failure, Failure::None) << scheme;
        EXPECT_EQ(response.status, 200) << scheme;
    }
    EXPECT_TRUE(proxy.connects().empty());
    // The loopback route is 127.0.0.1 alone, and only it takes a self-signed certificate.
    quota::http::Request elsewhere;
    elsewhere.loopback = true;
    elsewhere.url = "https://api.kimi.com:" + std::to_string(tls.port()) + "/";
    EXPECT_EQ(net::fetch(elsewhere, o).failure, Failure::Refused);
}

TEST_F(HttpsClient, RefusesWhatNetworkNineDoesNotList) {
    StubServer server([](const std::string&) { return answer(200, "{}"); }, &pki().leaf);
    for (const auto* url : {"https://api.anthropic.com/v1/usage", "http://api.kimi.com/coding/v1/usages", "https://api.kimi.com:8443/x",
                            "https://claude.ai/api/organizations"}) {
        quota::http::Request r;
        r.url = url;
        EXPECT_EQ(net::fetch(r, options(server.port())).failure, Failure::Refused) << url;
    }
    EXPECT_EQ(server.connections(), 0);
}

TEST_F(HttpsClient, ReportsAnUnreachableServer) {
    int port = 0;
    {
        StubServer gone([](const std::string&) { return answer(200, "{}"); }, &pki().leaf);
        port = gone.port();
    }
    EXPECT_EQ(net::fetch(kimi(), options(port)).failure, Failure::Network);
    EXPECT_EQ(net::fetch(kimi(), options(0, {{"https_proxy", "http://127.0.0.1:" + std::to_string(port)}})).failure, Failure::Network);
}
