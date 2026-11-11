#include <gtest/gtest.h>

#include <map>

#include "net/http_wire.hpp"
#include "net/proxy.hpp"
#include "net/url.hpp"

// REMOTE 11.4 without a network: the URLs, the request bytes, the answer's framing and cap, and the proxy choice.
using namespace agentspeed;
using namespace agentspeed::relay::net;
using quota::http::Failure;

namespace {

ResponseReader read(const std::string& bytes, std::size_t step = 7, bool close = true, std::size_t cap = 64) {
    ResponseReader reader(cap);
    for (std::size_t i = 0; i < bytes.size(); i += step) reader.feed(std::string_view(bytes).substr(i, step));
    if (close) reader.end();
    return reader;
}

Variable variables(std::map<std::string, std::string> values) {
    return [values = std::move(values)](std::string_view name) -> std::optional<std::string> {
        const auto found = values.find(std::string(name));
        if (found == values.end()) return std::nullopt;
        return found->second;
    };
}

}  // namespace

TEST(RelayHttpWire, ParsesOnlyPlainUrls) {
    const auto url = parseUrl("https://API.kimi.com/coding/v1/usages?x=1");
    ASSERT_TRUE(url);
    EXPECT_TRUE(url->tls);
    EXPECT_EQ(url->host, "api.kimi.com");
    EXPECT_EQ(url->port, 443);
    EXPECT_EQ(url->target, "/coding/v1/usages?x=1");
    const auto loopback = parseUrl("http://127.0.0.1:41234/exa.language_server_pb.LanguageServerService/GetUserStatus");
    ASSERT_TRUE(loopback);
    EXPECT_FALSE(loopback->tls);
    EXPECT_EQ(loopback->port, 41234);
    EXPECT_EQ(parseUrl("https://api.factory.ai")->target, "/");
    for (const auto* bad : {"ftp://api.kimi.com/", "https://user@api.kimi.com/", "https://api.kimi.com:0/", "https://api.kimi.com:70000/",
                            "https://[::1]/", "https://api.kimi.com/a b", "https://api.kimi.com/a#frag", "https:///path", "https://a_b.com/"}) {
        EXPECT_FALSE(parseUrl(bad)) << bad;
    }
}

TEST(RelayHttpWire, WritesTheRouteHeadersAndTheFramingOnly) {
    quota::http::Request get;
    get.url = "https://api.kimi.com/coding/v1/usages";
    get.headers = {{"Accept", "application/json"}, {"Authorization", "Bearer t0k"}};
    EXPECT_EQ(*requestBytes(get, *parseUrl(get.url), "AgentSpeed/9.9.9 (Linux)"),
              "GET /coding/v1/usages HTTP/1.1\r\nHost: api.kimi.com\r\nUser-Agent: AgentSpeed/9.9.9 (Linux)\r\n"
              "Accept: application/json\r\nAuthorization: Bearer t0k\r\nConnection: close\r\n\r\n");

    quota::http::Request post;
    post.method = quota::http::Method::Post;
    post.url = "http://127.0.0.1:5000/x";
    post.loopback = true;
    post.headers = {{"Content-Type", "application/json"}, {"User-Agent", "Mozilla/5.0"}};
    post.body = R"({"forceRefresh": true})";
    EXPECT_EQ(*requestBytes(post, *parseUrl(post.url), "AgentSpeed/9.9.9 (Linux)"),
              "POST /x HTTP/1.1\r\nHost: 127.0.0.1:5000\r\nContent-Type: application/json\r\nUser-Agent: Mozilla/5.0\r\n"
              "Content-Length: 22\r\nConnection: close\r\n\r\n{\"forceRefresh\": true}");

    for (const auto& header : std::vector<quota::http::Header>{{"Authorization", "Bearer a\r\nX-Evil: 1"},
                                                               {"Bad Name", "x"},
                                                               {"Host", "evil.example"},
                                                               {"Content-Length", "0"},
                                                               {"X", std::string("a\0b", 3)}}) {
        auto request = get;
        request.headers.push_back(header);
        EXPECT_FALSE(requestBytes(request, *parseUrl(request.url), "UA")) << header.name;
    }
}

TEST(RelayHttpWire, ReadsAnAnswerByItsLength) {
    auto reader = read("HTTP/1.1 100 Continue\r\n\r\nHTTP/1.1 200 OK\r\nDate: Tue, 10 Nov 2026 19:26:13 GMT\r\ncontent-length: 7\r\n\r\n"
                       "{\"a\":1}trailing junk", 5, false);
    ASSERT_TRUE(reader.done());
    EXPECT_EQ(reader.status(), 200);
    EXPECT_EQ(reader.body(), "{\"a\":1}");
    EXPECT_EQ(reader.date(), "Tue, 10 Nov 2026 19:26:13 GMT");
    EXPECT_FALSE(reader.contentEncoding());

    reader = read("HTTP/1.1 200 OK\r\nContent-Length: 9\r\n\r\nshort");
    EXPECT_TRUE(reader.failed());
    EXPECT_EQ(reader.failure(), Failure::Network);
    reader = read("HTTP/1.1 200 OK\r\nContent-Length: 65\r\n\r\n", 7, false);
    EXPECT_EQ(reader.failure(), Failure::TooLarge);
    reader = read("HTTP/1.1 200 OK\r\nContent-Length: 3\r\nContent-Length: 4\r\n\r\nabcd");
    EXPECT_EQ(reader.failure(), Failure::Network);
    reader = read("HTTP/1.1 204 No Content\r\n\r\n", 7, false);
    EXPECT_TRUE(reader.done());
    EXPECT_EQ(reader.body(), "");
}

TEST(RelayHttpWire, ReadsChunksAndAnswersThatEndWithTheConnection) {
    auto reader = read("HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\nContent-Length: 999\r\n\r\n"
                       "4;ext=1\r\n{\"a\"\r\n3\r\n:1}\r\n0\r\nX-Trailer: 1\r\n\r\n",
                       1, false);
    ASSERT_TRUE(reader.done());
    EXPECT_EQ(reader.body(), "{\"a\":1}");
    reader = read("HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\n\r\n40\r\n" + std::string(64, 'x') + "\r\n1\r\nx\r\n0\r\n\r\n");
    EXPECT_EQ(reader.failure(), Failure::TooLarge);
    reader = read("HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\n\r\n4\r\nab");
    EXPECT_EQ(reader.failure(), Failure::Network);
    reader = read("HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\n\r\nzz\r\n");
    EXPECT_EQ(reader.failure(), Failure::Network);
    reader = read("HTTP/1.0 200 OK\r\n\r\n{\"until\":\"close\"}");
    ASSERT_TRUE(reader.done());
    EXPECT_EQ(reader.body(), "{\"until\":\"close\"}");
    reader = read("HTTP/1.0 200 OK\r\n\r\n" + std::string(65, 'x'));
    EXPECT_EQ(reader.failure(), Failure::TooLarge);
}

TEST(RelayHttpWire, RefusesRedirectsAndMalformedHeads) {
    auto reader = read("HTTP/1.1 302 Found\r\nLocation: https://elsewhere.example/\r\nContent-Length: 0\r\n\r\n");
    EXPECT_EQ(reader.failure(), Failure::Redirect);
    EXPECT_EQ(reader.status(), 302);
    for (const auto* bad : {"HTTP/2 200 OK\r\n\r\n", "HTTP/1.1 2000 OK\r\n\r\n", "SSH-2.0-OpenSSH\r\n\r\n", "HTTP/1.1 200 OK\r\n folded\r\n\r\n",
                            "HTTP/1.1 200 OK\r\nno colon\r\n\r\n", "HTTP/1.1 200 OK\r\nContent-Length: -1\r\n\r\n"}) {
        EXPECT_EQ(read(bad).failure(), Failure::Network) << bad;
    }
    EXPECT_EQ(read("HTTP/1.1 200 OK\r\nX: " + std::string(70'000, 'a')).failure(), Failure::Network);
    EXPECT_EQ(read("HTTP/1.1 200 OK\r\n").failure(), Failure::Network);
}

TEST(RelayHttpWire, ReadsTheProxyAnswer) {
    std::string buffer = "HTTP/1.1 200 Connection established\r\nProxy-Agent: x\r\n\r\n\x16\x03";
    bool malformed = false;
    EXPECT_EQ(connectAnswer(buffer, malformed), 200);
    EXPECT_EQ(buffer, "\x16\x03");
    buffer = "HTTP/1.1 407 Proxy";
    EXPECT_FALSE(connectAnswer(buffer, malformed));
    EXPECT_FALSE(malformed);
    buffer += " Authentication Required\r\n\r\n";
    EXPECT_EQ(connectAnswer(buffer, malformed), 407);
    buffer = "garbage\r\n\r\n";
    EXPECT_FALSE(connectAnswer(buffer, malformed));
    EXPECT_TRUE(malformed);
    EXPECT_EQ(connectRequest("api.kimi.com", 443, "AgentSpeed/9.9.9 (Linux)"),
              "CONNECT api.kimi.com:443 HTTP/1.1\r\nHost: api.kimi.com:443\r\nUser-Agent: AgentSpeed/9.9.9 (Linux)\r\n\r\n");
}

TEST(RelayHttpWire, ChoosesTheProxyOfTheEnvironment) {
    using Kind = ProxyRoute::Kind;
    EXPECT_EQ(proxyFor("api.kimi.com", 443, variables({})).kind, Kind::Direct);
    auto route = proxyFor("api.kimi.com", 443, variables({{"https_proxy", "http://Proxy.Corp:3128/"}, {"HTTPS_PROXY", "http://other:1"}}));
    EXPECT_EQ(route.kind, Kind::Proxy);
    EXPECT_EQ(route.host, "proxy.corp");
    EXPECT_EQ(route.port, 3128);
    route = proxyFor("api.kimi.com", 443, variables({{"HTTPS_PROXY", "10.0.0.1"}}));
    EXPECT_EQ(route.host, "10.0.0.1");
    EXPECT_EQ(route.port, 1080);
    route = proxyFrom("http://[fd00::1]:8080");
    EXPECT_EQ(route.kind, Kind::Proxy);
    EXPECT_EQ(route.host, "fd00::1");
    for (const auto* unusable : {"socks5://proxy:1080", "https://proxy:443", "http://user:pass@proxy:3128", "http://proxy:99999",
                                 "http://proxy:3128/path", "http://", "http://[fd00::1"}) {
        EXPECT_EQ(proxyFrom(unusable).kind, Kind::Unusable) << unusable;
    }
    // An unusable proxy is not silently left out.
    EXPECT_EQ(proxyFor("api.kimi.com", 443, variables({{"https_proxy", "socks5://p:1"}})).kind, Kind::Unusable);
    // no_proxy, else NO_PROXY.
    EXPECT_EQ(proxyFor("api.kimi.com", 443, variables({{"https_proxy", "http://p:1"}, {"no_proxy", "localhost, .kimi.com"}})).kind,
              Kind::Direct);
    EXPECT_EQ(proxyFor("api.kimi.com", 443, variables({{"https_proxy", "http://p:1"}, {"NO_PROXY", "*"}})).kind, Kind::Direct);
}

TEST(RelayHttpWire, MatchesNoProxyAsCurlDoes) {
    EXPECT_TRUE(bypassesProxy("kimi.com", "api.kimi.com", 443));
    EXPECT_TRUE(bypassesProxy("API.KIMI.COM", "api.kimi.com", 443));
    EXPECT_TRUE(bypassesProxy("*.kimi.com", "api.kimi.com", 443));
    EXPECT_TRUE(bypassesProxy(" other , api.kimi.com:443 ", "api.kimi.com", 443));
    EXPECT_FALSE(bypassesProxy("api.kimi.com:8443", "api.kimi.com", 443));
    EXPECT_FALSE(bypassesProxy("imi.com", "api.kimi.com", 443));
    EXPECT_FALSE(bypassesProxy("api.kimi.com.evil", "api.kimi.com", 443));
    EXPECT_FALSE(bypassesProxy("", "api.kimi.com", 443));
    EXPECT_FALSE(bypassesProxy("10.0.0.0/8", "api.kimi.com", 443));
}
