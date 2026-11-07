#include <gtest/gtest.h>

#include <map>

#include "agentspeed/lan_protocol.hpp"
#include "agentspeed/lan_websocket.hpp"
#include "lan_vectors.hpp"

// LAN 4.2–4.6: first lines, request heads and their responses (the lockout sequence in order), hellos, access keys and
// the WebSocket framing.
using namespace agentspeed;
using namespace agentspeed::lan;

namespace {

std::string_view firstLineName(FirstLine kind) {
    switch (kind) {
    case FirstLine::Http: return "http";
    case FirstLine::Line: return "line";
    case FirstLine::Close: return "close";
    case FirstLine::Pending: return "pending";
    }
    return "?";
}

std::string actionName(ws::ClientFrame::Action action) {
    switch (action) {
    case ws::ClientFrame::Action::Pong: return "pong";
    case ws::ClientFrame::Action::EchoClose: return "echoClose";
    case ws::ClientFrame::Action::Ignore: return "ignore";
    case ws::ClientFrame::Action::Close: return "close";
    case ws::ClientFrame::Action::Incomplete: return "incomplete";
    }
    return "?";
}

const std::string kKey = "K7Q4M2X9PT3D";

}  // namespace

TEST(LanFirstLine, Vectors) {
    const auto cases = lan_vectors::section("firstLines").elements();
    ASSERT_EQ(cases.size(), 12u);
    for (const auto& c : cases) {
        const auto data = *c.at(0).string();
        EXPECT_EQ(firstLineName(classifyFirstLine(data, true)), *c.at(1).string()) << data.substr(0, 40);
    }
}

TEST(LanFirstLine, WaitsForTheLineUpToTheLimit) {
    EXPECT_EQ(classifyFirstLine("GET /v1/live HT"), FirstLine::Pending);
    EXPECT_EQ(classifyFirstLine(std::string(8191, 'G')), FirstLine::Pending);
    EXPECT_EQ(classifyFirstLine(std::string(8192, 'G')), FirstLine::Close);
    EXPECT_EQ(classifyFirstLine("{\"type\""), FirstLine::Pending);
    EXPECT_EQ(classifyFirstLine("GET \xff HTTP/1.1\n"), FirstLine::Close);
}

TEST(LanRequests, VectorsInOrderWithOneLockout) {
    const auto cases = lan_vectors::section("requests").elements();
    ASSERT_EQ(cases.size(), 28u);
    Lockout lockout;
    for (const auto& c : cases) {
        const auto name = *c["name"].string();
        const auto parsed = parseHead(*c["head"].string(), true);
        ASSERT_EQ(parsed.state, HeadState::Ok) << name;
        const auto response = respond(parsed.head, *c["peer"].string(), *c["nowMs"].truncated(), kKey, c["hasFrame"].isTrue(), lockout);
        EXPECT_EQ(response.status, *c["status"].truncated()) << name;
        EXPECT_EQ(response.error, fixtures::optString(c["error"])) << name;
        std::map<std::string, std::string> expected, got;
        for (const auto& [header, value] : c["headers"].members()) expected[header] = *value.string();
        for (const auto& [header, value] : response.headers) got[header] = value;
        EXPECT_EQ(got, expected) << name;
        std::vector<std::string> order;
        for (const auto& [header, _] : c["headers"].members()) order.push_back(header);
        std::vector<std::string> gotOrder;
        for (const auto& [header, _] : response.headers) gotOrder.push_back(header);
        EXPECT_EQ(gotOrder, order) << name;
    }
}

TEST(LanRequests, ResponseBytes) {
    Lockout lockout;
    const auto head = parseHead("GET /v1/live HTTP/1.1\r\nAuthorization: Bearer K7Q4-M2X9-PT3D\r\n\r\n", true).head;
    const auto ok = respond(head, "192.168.1.5", 0, kKey, true, lockout);
    EXPECT_TRUE(ok.authorized);
    EXPECT_EQ(responseBytes(ok, "{}"),
              "HTTP/1.1 200 OK\r\nContent-Type: application/json; charset=utf-8\r\nContent-Length: 2\r\nCache-Control: no-store\r\n"
              "Connection: close\r\n\r\n{}");
    const auto missing = respond(parseHead("GET /v1/live HTTP/1.1\r\n\r\n", true).head, "192.168.1.5", 0, kKey, true, lockout);
    EXPECT_FALSE(missing.authorized);
    const auto body = errorBody("unauthorized");
    EXPECT_EQ(body, R"({"schema":1,"type":"error","error":"unauthorized"})");
    EXPECT_EQ(responseBytes(missing, "{}"),
              "HTTP/1.1 401 Unauthorized\r\nContent-Type: application/json; charset=utf-8\r\nContent-Length: " + std::to_string(body.size()) +
                  "\r\nCache-Control: no-store\r\nConnection: close\r\nWWW-Authenticate: Bearer realm=\"agent-speed\"\r\n\r\n" + body);
    const auto upgrade = parseHead("GET /v1/stream HTTP/1.1\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Version: 13\r\n"
                                   "Sec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==\r\nAuthorization: Bearer K7Q4M2X9PT3D\r\n\r\n",
                                   true)
                             .head;
    EXPECT_EQ(responseBytes(respond(upgrade, "192.168.1.5", 0, kKey, false, lockout), ""),
              "HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n"
              "Sec-WebSocket-Accept: s3pPLMBiTxaQ9kYGzzhZRbK+xOo=\r\n\r\n");
}

TEST(LanRequests, HeadLimits) {
    EXPECT_EQ(parseHead("GET /v1/live HTTP/1.1\r\nHost: x\r\n").state, HeadState::Pending);
    EXPECT_EQ(parseHead("GET /v1/live HTTP/1.1\r\nHost: x\r\n", true).state, HeadState::Invalid);
    EXPECT_EQ(parseHead("GET /v1/live HTTP/1.1\r\nX: " + std::string(8200, 'a')).state, HeadState::Invalid);
    EXPECT_EQ(parseHead("GET /v1/live HTTP/1.1\r\nX: " + std::string(8170, 'a') + "\r\n\r\n").state, HeadState::Invalid);
    EXPECT_EQ(parseHead("GET /v1/live HTTP/1.1\r\nX: " + std::string(8162, 'a') + "\r\n\r\n").state, HeadState::Ok);
    const auto repeated = parseHead("GET /v1/live HTTP/1.1\r\nX-A:  one \r\nx-a: two\r\n\r\n").head;
    ASSERT_NE(repeated.header("x-a"), nullptr);
    EXPECT_EQ(*repeated.header("x-a"), "one");
}

TEST(LanLockout, RemembersAtMost256Addresses) {
    Lockout lockout;
    for (int i = 0; i < 300; ++i) lockout.fail("10.0.0." + std::to_string(i), i);
    EXPECT_EQ(lockout.size(), 256u);
    for (int i = 0; i < 9; ++i) lockout.fail("10.0.0.299", 1000 + i);
    EXPECT_TRUE(lockout.locked("10.0.0.299", 1010));
    EXPECT_EQ(*lockout.locked("10.0.0.299", 1010), 60);
    EXPECT_EQ(*lockout.locked("10.0.0.299", 60'009), 1);
    EXPECT_FALSE(lockout.locked("10.0.0.299", 61'008));
    lockout.succeed("10.0.0.299");
    EXPECT_EQ(lockout.size(), 255u);
}

TEST(LanHello, Vectors) {
    const auto cases = lan_vectors::section("hellos").elements();
    ASSERT_EQ(cases.size(), 8u);
    Lockout lockout;
    for (const auto& c : cases) {
        EXPECT_EQ(hello(*c["line"].string(), "192.168.1.77", *c["nowMs"].truncated(), kKey, lockout), fixtures::optString(c["error"]))
            << *c["name"].string();
    }
}

TEST(LanKeys, Vectors) {
    const auto cases = lan_vectors::section("keys").elements();
    ASSERT_EQ(cases.size(), 9u);
    EXPECT_EQ(*lan_vectors::section("key").string(), kKey);
    for (const auto& c : cases) EXPECT_EQ(keyMatches(c["given"].string(), kKey), c["matches"].isTrue()) << *c["given"].string();
    EXPECT_FALSE(keyMatches(std::nullopt, kKey));
}

TEST(LanKeys, RequestKey) {
    const auto key = [](const std::string& head) { return requestKey(parseHead(head, true).head); };
    EXPECT_EQ(key("GET /v1/live?key=a%2Db HTTP/1.1\r\n\r\n"), std::optional<std::string>("a-b"));
    EXPECT_EQ(key("GET /v1/live?keys=1&key=x HTTP/1.1\r\n\r\n"), std::optional<std::string>("x"));
    EXPECT_EQ(key("GET /v1/live?key HTTP/1.1\r\n\r\n"), std::nullopt);
    EXPECT_EQ(key("GET /v1/live?key=%zz+1 HTTP/1.1\r\n\r\n"), std::optional<std::string>("%zz+1"));
    EXPECT_EQ(key("GET /v1/live?key=q HTTP/1.1\r\nAuthorization: Bearer   t  \r\n\r\n"), std::optional<std::string>("t"));
    EXPECT_EQ(key("GET /v1/live?key=q HTTP/1.1\r\nAuthorization: Bearer\r\n\r\n"), std::optional<std::string>("q"));
}

TEST(LanWebSocket, AcceptValues) {
    const auto cases = lan_vectors::section("websocket")["accept"].elements();
    ASSERT_EQ(cases.size(), 2u);
    for (const auto& c : cases) {
        EXPECT_EQ(ws::accept(*c["key"].string()), *c["accept"].string());
        EXPECT_TRUE(ws::validKey(*c["key"].string()));
    }
    EXPECT_FALSE(ws::validKey("dGhlIHNhbXBsZSBub25j"));
    EXPECT_FALSE(ws::validKey("dGhlIHNhbXBsZSBub25jZQ"));
    EXPECT_FALSE(ws::validKey("dGhlIHNhbXBsZSBub25jZ!=="));
}

TEST(LanWebSocket, ClientFrames) {
    const auto cases = lan_vectors::section("websocket")["clientFrames"].elements();
    ASSERT_EQ(cases.size(), 13u);
    for (const auto& c : cases) {
        const auto name = *c["name"].string();
        const auto data = lan_vectors::unhex(*c["frame"].string());
        const auto got = ws::clientFrame(data);
        const auto expected = c["result"];
        EXPECT_EQ(actionName(got.action), *expected["action"].string()) << name;
        if (const auto code = expected["code"]; code.valid()) {
            EXPECT_EQ(got.code, fixtures::optInt(code)) << name;
        }
        if (const auto payload = expected["payload"]; payload.valid()) {
            EXPECT_EQ(lan_vectors::hex(got.payload), *payload.string()) << name;
        }
        if (got.action != ws::ClientFrame::Action::Close) {
            EXPECT_EQ(got.consumed, data.size()) << name;
        }
        // Every prefix of a frame that is not decided by its header waits for more.
        if (got.action != ws::ClientFrame::Action::Close) {
            for (std::size_t n = 0; n < data.size(); ++n)
                EXPECT_EQ(ws::clientFrame(std::string_view(data).substr(0, n)).action, ws::ClientFrame::Action::Incomplete) << name << " " << n;
        }
    }
}

TEST(LanWebSocket, ServerFramesAndCloses) {
    const auto frames = lan_vectors::section("websocket")["serverFrames"].elements();
    ASSERT_EQ(frames.size(), 3u);
    for (const auto& c : frames) {
        const auto bytes = lan_vectors::hex(ws::frame(static_cast<int>(*c["opcode"].truncated()), *c["payload"].string()));
        const auto expected = *c["frame"].string();
        if (c["prefixOnly"].isTrue()) {
            EXPECT_EQ(bytes.substr(0, expected.size()), expected);
        } else {
            EXPECT_EQ(bytes, expected);
        }
    }
    const auto closes = lan_vectors::section("websocket")["closes"].elements();
    ASSERT_EQ(closes.size(), 3u);
    for (const auto& c : closes) {
        const auto code = fixtures::optInt(c["code"]);
        EXPECT_EQ(lan_vectors::hex(ws::close(code ? std::optional<int>(static_cast<int>(*code)) : std::nullopt)), *c["frame"].string());
    }
}
