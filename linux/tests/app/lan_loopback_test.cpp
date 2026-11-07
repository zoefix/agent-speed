#include <gtest/gtest.h>

#include <QCoreApplication>
#include <QElapsedTimer>
#include <QTcpServer>
#include <QTcpSocket>
#include <QUdpSocket>
#include <functional>
#include <memory>

#include "agentspeed/lan_frame.hpp"
#include "agentspeed/lan_websocket.hpp"
#include "lan/lan_net.hpp"
#include "lan/lan_server.hpp"
#include "lan/lan_targets.hpp"

// LAN 4 and 5 over real sockets on 127.0.0.1: the server's HTTP answers, the WebSocket stream, the line protocol and the
// connection cap; UDP and HTTP targets against loopback receivers.
using namespace agentspeed;
using namespace agentspeed::lan;

namespace {

const std::string kKey = "K7Q4M2X9PT3D";

bool pump(const std::function<bool()>& done, int ms = 3000) {
    QElapsedTimer timer;
    timer.start();
    while (!done()) {
        if (timer.elapsed() > ms) return false;
        QCoreApplication::processEvents(QEventLoop::AllEvents, 10);
    }
    return true;
}

void wait(int ms) {
    QElapsedTimer timer;
    timer.start();
    while (timer.elapsed() < ms) QCoreApplication::processEvents(QEventLoop::AllEvents, 10);
}

std::shared_ptr<const Frame> frameWith(double speed, std::int64_t seq = 1, std::size_t sources = 0, std::size_t history = 1) {
    FrameInput input;
    input.instance = "3f2504e0-4f89-41d3-9a0c-0305e82c3301";
    input.name = "loopback";
    input.run = "9b1c4e7a2f6d8e03";
    input.seq = seq;
    input.atMs = 1'794'042'000'250;
    input.live = {speed, 0.1, 1, 0, 1};
    for (std::size_t i = 0; i < sources; ++i) input.sources[static_cast<SourceKind>(i)] = {1234.5, 0.123, 1, 1, 12};
    for (std::size_t i = 0; i < history; ++i) input.history.push_back(1234.5);
    return std::make_shared<const Frame>(buildFrame(input));
}

// A raw client that keeps everything it receives.
struct Client {
    QTcpSocket socket;
    QByteArray received;
    bool closed = false;
    explicit Client(quint16 port) {
        QObject::connect(&socket, &QTcpSocket::readyRead, [this] { received += socket.readAll(); });
        QObject::connect(&socket, &QTcpSocket::disconnected, [this] { closed = true; });
        socket.connectToHost(QHostAddress::LocalHost, port);
    }
    bool connected() { return pump([&] { return socket.state() == QAbstractSocket::ConnectedState; }); }
    void send(const QByteArray& bytes) { socket.write(bytes); }
    bool waitClosed(int ms = 3000) { return pump([&] { return closed; }, ms); }
    bool waitFor(const QByteArray& needle, int ms = 3000) { return pump([&] { return received.contains(needle); }, ms); }
};

QByteArray masked(int opcode, const QByteArray& payload, bool mask = true) {
    QByteArray out;
    out += static_cast<char>(0x80 | opcode);
    out += static_cast<char>((mask ? 0x80 : 0) | payload.size());
    const QByteArray key = QByteArray::fromHex("37fa213d");
    if (!mask) return out + payload;
    out += key;
    for (int i = 0; i < payload.size(); ++i) out += static_cast<char>(payload[i] ^ key[i % 4]);
    return out;
}

QByteArray upgrade(const QByteArray& extra = {}) {
    return "GET /v1/stream?key=K7Q4-M2X9-PT3D HTTP/1.1\r\nHost: 127.0.0.1\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n"
           "Sec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==\r\nSec-WebSocket-Version: 13\r\n" +
           extra + "\r\n";
}

// The server frames in `bytes` after the handshake.
struct ServerFrame {
    int opcode = 0;
    QByteArray payload;
};
std::vector<ServerFrame> serverFrames(QByteArray bytes) {
    std::vector<ServerFrame> out;
    const auto head = bytes.indexOf("\r\n\r\n");
    if (head >= 0) bytes.remove(0, head + 4);
    while (bytes.size() >= 2) {
        const int opcode = bytes[0] & 0x0F;
        qsizetype size = bytes[1] & 0x7F, pos = 2;
        if (size == 126) {
            if (bytes.size() < 4) break;
            size = (static_cast<unsigned char>(bytes[2]) << 8) | static_cast<unsigned char>(bytes[3]);
            pos = 4;
        }
        if (bytes.size() < pos + size) break;
        out.push_back({opcode, bytes.mid(pos, size)});
        bytes.remove(0, pos + size);
    }
    return out;
}

class LanLoopback : public ::testing::Test {
protected:
    void SetUp() override {
        ASSERT_EQ(server.start(0, QHostAddress::LocalHost), LanServer::State::Listening);
        server.setKey(kKey);
        port = server.port();
    }
    QByteArray get(const QByteArray& request) {
        Client client(port);
        EXPECT_TRUE(client.connected());
        client.send(request);
        EXPECT_TRUE(client.waitClosed()) << "an HTTP answer closes the connection";
        return client.received;
    }
    LanServer server;
    quint16 port = 0;
};

}  // namespace

TEST_F(LanLoopback, LiveAnswers) {
    EXPECT_TRUE(get("GET /v1/live?key=K7Q4M2X9PT3D HTTP/1.1\r\n\r\n").startsWith("HTTP/1.1 503 Service Unavailable\r\n"));
    const auto frame = frameWith(75.9);
    server.publish(frame, true, lan_net::steadyMs());
    const auto ok = get("GET /v1/live HTTP/1.1\r\nAuthorization: Bearer k7q4-m2x9-pt3d\r\n\r\n");
    EXPECT_TRUE(ok.startsWith("HTTP/1.1 200 OK\r\n"));
    EXPECT_TRUE(ok.contains("Connection: close\r\n"));
    EXPECT_TRUE(ok.contains("Cache-Control: no-store\r\n"));
    EXPECT_TRUE(ok.endsWith(QByteArray::fromStdString(frame->wire())));
    EXPECT_TRUE(get("POST /v1/live HTTP/1.1\r\n\r\n").startsWith("HTTP/1.1 405 Method Not Allowed\r\n"));
    EXPECT_TRUE(get("GET /v1/nothing?key=K7Q4M2X9PT3D HTTP/1.1\r\n\r\n").startsWith("HTTP/1.1 404 Not Found\r\n"));
    const auto denied = get("GET /v1/live?key=AAAA-BBBB-CCCC HTTP/1.1\r\n\r\n");
    EXPECT_TRUE(denied.startsWith("HTTP/1.1 401 Unauthorized\r\n"));
    EXPECT_TRUE(denied.contains("WWW-Authenticate: Bearer realm=\"agent-speed\"\r\n"));
    EXPECT_TRUE(denied.endsWith(R"({"schema":1,"type":"error","error":"unauthorized"})"));
    EXPECT_EQ(server.devices(lan_net::steadyMs()), 1) << "the authorized request of 127.0.0.1";
    for (int i = 0; i < 9; ++i) get("GET /v1/live HTTP/1.1\r\n\r\n");
    const auto locked = get("GET /v1/live?key=K7Q4M2X9PT3D HTTP/1.1\r\n\r\n");
    EXPECT_TRUE(locked.startsWith("HTTP/1.1 429 Too Many Requests\r\n")) << locked.left(40).toStdString();
    EXPECT_TRUE(locked.contains("Retry-After: 60\r\n"));
}

TEST_F(LanLoopback, NotARequestIsClosedWithoutReply) {
    Client client(port);
    ASSERT_TRUE(client.connected());
    client.send("hello\n");
    EXPECT_TRUE(client.waitClosed());
    EXPECT_TRUE(client.received.isEmpty());
}

TEST_F(LanLoopback, WebSocketStream) {
    server.publish(frameWith(10), true, lan_net::steadyMs());
    Client client(port);
    ASSERT_TRUE(client.connected());
    client.send(upgrade());
    ASSERT_TRUE(client.waitFor("\r\n\r\n"));
    EXPECT_TRUE(client.received.startsWith("HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n"
                                           "Sec-WebSocket-Accept: s3pPLMBiTxaQ9kYGzzhZRbK+xOo=\r\n\r\n"));
    ASSERT_TRUE(pump([&] { return serverFrames(client.received).size() >= 1; }));
    auto frames = serverFrames(client.received);
    EXPECT_EQ(frames[0].opcode, ws::kText);
    EXPECT_EQ(frames[0].payload.toStdString(), frameWith(10)->wire()) << "the latest frame at once";
    EXPECT_EQ(server.devices(lan_net::steadyMs()), 1);

    // A changed frame within 500 ms is held, then goes out once the interval has passed.
    const auto start = lan_net::steadyMs();
    server.publish(frameWith(20, 2), true, start);
    wait(100);
    EXPECT_EQ(serverFrames(client.received).size(), 1u);
    server.publish(frameWith(30, 3), true, start + 600);
    ASSERT_TRUE(pump([&] { return serverFrames(client.received).size() >= 2; }));
    EXPECT_EQ(serverFrames(client.received)[1].payload.toStdString(), frameWith(30, 3)->wire());

    client.send(masked(ws::kPing, "hi"));
    ASSERT_TRUE(pump([&] { return serverFrames(client.received).size() >= 3; }));
    frames = serverFrames(client.received);
    EXPECT_EQ(frames[2].opcode, ws::kPong);
    EXPECT_EQ(frames[2].payload, "hi");

    client.send(masked(ws::kClose, QByteArray::fromHex("03e8") + "bye"));
    EXPECT_TRUE(client.waitClosed());
    frames = serverFrames(client.received);
    ASSERT_EQ(frames.size(), 4u);
    EXPECT_EQ(frames[3].opcode, ws::kClose);
    EXPECT_EQ(frames[3].payload.toHex(), "03e8") << "the client's code echoed";
}

TEST_F(LanLoopback, UnmaskedClientFrameCloses1002) {
    Client client(port);
    ASSERT_TRUE(client.connected());
    client.send(upgrade());
    ASSERT_TRUE(client.waitFor("\r\n\r\n"));
    client.send(masked(ws::kText, "x", false));
    EXPECT_TRUE(client.waitClosed());
    const auto frames = serverFrames(client.received);
    ASSERT_FALSE(frames.empty());
    EXPECT_EQ(frames.back().opcode, ws::kClose);
    EXPECT_EQ(frames.back().payload.toHex(), "03ea");
}

TEST_F(LanLoopback, NewKeyCloses1008AndStopCloses1001) {
    server.publish(frameWith(10), true, lan_net::steadyMs());
    Client a(port), b(port);
    ASSERT_TRUE(a.connected());
    ASSERT_TRUE(b.connected());
    a.send(upgrade());
    b.send("{\"type\":\"hello\",\"key\":\"K7Q4-M2X9-PT3D\"}\n");
    ASSERT_TRUE(pump([&] { return serverFrames(a.received).size() >= 1 && b.received.contains('\n'); }));
    server.setKey("ABCDEFGHJKMN");
    EXPECT_TRUE(a.waitClosed());
    EXPECT_TRUE(b.waitClosed());
    EXPECT_EQ(serverFrames(a.received).back().payload.toHex(), "03f0");

    Client c(port);
    ASSERT_TRUE(c.connected());
    c.send(upgrade().replace("K7Q4-M2X9-PT3D", "ABCD-EFGH-JKMN"));
    ASSERT_TRUE(pump([&] { return serverFrames(c.received).size() >= 1; }));
    server.stop();
    EXPECT_TRUE(c.waitClosed());
    EXPECT_EQ(serverFrames(c.received).back().payload.toHex(), "03e9");
}

TEST_F(LanLoopback, LineProtocol) {
    const auto first = frameWith(10);
    server.publish(first, true, lan_net::steadyMs());
    Client client(port);
    ASSERT_TRUE(client.connected());
    client.send("{\"type\":\"hello\",\"key\":\"k7q4 m2x9 pt3d\",\"device\":\"desk\"}\r\n");
    ASSERT_TRUE(client.waitFor("\n"));
    EXPECT_EQ(client.received, QByteArray::fromStdString(first->wire()) + "\n");
    client.send("anything after the hello is discarded\n");
    const auto second = frameWith(20, 2);
    server.publish(second, true, lan_net::steadyMs() + 600);
    ASSERT_TRUE(pump([&] { return client.received.count('\n') >= 2; }));
    EXPECT_TRUE(client.received.endsWith(QByteArray::fromStdString(second->wire()) + "\n"));
    // Unchanged frames wait for the 5 s heartbeat.
    server.publish(frameWith(20, 3), false, lan_net::steadyMs() + 1200);
    wait(100);
    EXPECT_EQ(client.received.count('\n'), 2);
    server.publish(frameWith(20, 4), false, lan_net::steadyMs() + 6000);
    ASSERT_TRUE(pump([&] { return client.received.count('\n') >= 3; }));

    Client bad(port);
    ASSERT_TRUE(bad.connected());
    bad.send("{\"type\":\"subscribe\"}\n");
    EXPECT_TRUE(bad.waitClosed());
    EXPECT_EQ(bad.received, R"({"schema":1,"type":"error","error":"badRequest"})"
                            "\n");
    Client wrong(port);
    ASSERT_TRUE(wrong.connected());
    wrong.send("{\"type\":\"hello\",\"key\":\"AAAA-BBBB-CCCC\"}\n");
    EXPECT_TRUE(wrong.waitClosed());
    EXPECT_EQ(wrong.received, R"({"schema":1,"type":"error","error":"unauthorized"})"
                              "\n");
}

TEST_F(LanLoopback, SixteenConnectionsAtMost) {
    std::vector<std::unique_ptr<Client>> clients;
    for (int i = 0; i < 16; ++i) {
        clients.push_back(std::make_unique<Client>(port));
        ASSERT_TRUE(clients.back()->connected());
    }
    ASSERT_TRUE(pump([&] { return server.connections() == 16; }));
    Client extra(port);
    EXPECT_TRUE(pump([&] { return extra.closed || extra.socket.state() == QAbstractSocket::UnconnectedState; }, 2000))
        << "the 17th is closed at once";
    EXPECT_TRUE(extra.received.isEmpty());
    EXPECT_EQ(server.connections(), 16);
    for (auto& c : clients) EXPECT_FALSE(c->closed);
}

TEST(LanLoopbackPort, ATakenPortIsPortInUse) {
    QTcpServer blocker;
    ASSERT_TRUE(blocker.listen(QHostAddress::LocalHost, 0));
    LanServer server;
    EXPECT_EQ(server.start(blocker.serverPort(), QHostAddress::LocalHost), LanServer::State::PortInUse);
    blocker.close();
    EXPECT_EQ(server.start(blocker.serverPort(), QHostAddress::LocalHost), LanServer::State::Listening) << "the retry succeeds";
}

TEST(LanLoopbackTargets, UdpWithSignatureAndSizeRule) {
    QUdpSocket receiver;
    ASSERT_TRUE(receiver.bind(QHostAddress::LocalHost, 0));
    LanTargets targets;
    Target target;
    target.id = "8c0d6b0a-5a8e-4b0e-9d4e-2f1f3c5a7b90";
    target.kind = TargetKind::Udp;
    target.address = "127.0.0.1:" + std::to_string(receiver.localPort());
    target.key = "s3cret-phrase";
    targets.configure({target}, true);
    EXPECT_EQ(targets.status(target.id).status, TargetStatus::Waiting);
    const auto now = lan_net::steadyMs();
    const auto big = frameWith(1300, 7, 6, 60);
    targets.publish(big, true, now);
    ASSERT_TRUE(pump([&] { return receiver.hasPendingDatagrams(); }));
    QByteArray data(static_cast<qsizetype>(receiver.pendingDatagramSize()), '\0');
    receiver.readDatagram(data.data(), data.size());
    EXPECT_LE(data.size(), 1400);
    const auto split = data.indexOf('\n');
    ASSERT_GT(split, 0);
    const auto body = data.left(split).toStdString();
    EXPECT_EQ(data.mid(split + 1).toStdString(), signature("s3cret-phrase", body));
    EXPECT_EQ(body, big->json(true, false)) << "history left out, sources kept";
    const auto status = targets.status(target.id);
    EXPECT_EQ(status.status, TargetStatus::Ok);
    EXPECT_GT(status.lastSuccessWallMs, 0);
    // An unchanged frame waits for the 1 s heartbeat.
    targets.publish(frameWith(1300, 8, 6, 60), false, now + 500);
    wait(50);
    EXPECT_FALSE(receiver.hasPendingDatagrams());
    targets.publish(frameWith(1300, 9, 6, 60), false, now + 1000);
    EXPECT_TRUE(pump([&] { return receiver.hasPendingDatagrams(); }));
}

TEST(LanLoopbackTargets, NotLocalSendsNothing) {
    LanTargets targets;
    Target target;
    target.id = "1b9e8a3c-2d4f-4e6a-8b0c-7d5e3f1a9c24";
    target.kind = TargetKind::Udp;
    target.address = "8.8.8.8:7247";
    targets.configure({target}, true);
    targets.publish(frameWith(1), true, lan_net::steadyMs());
    EXPECT_EQ(targets.status(target.id).status, TargetStatus::NotLocal);
    targets.configure({target}, false);
    EXPECT_EQ(targets.status(target.id).status, TargetStatus::NetworkOff);
}

namespace {

// An HTTP receiver that answers every request with `status`.
struct HttpReceiver {
    QTcpServer server;
    QByteArray request;
    int requests = 0;
    explicit HttpReceiver(int status) {
        server.listen(QHostAddress::LocalHost, 0);
        QObject::connect(&server, &QTcpServer::newConnection, [this, status] {
            auto* socket = server.nextPendingConnection();
            QObject::connect(socket, &QTcpSocket::readyRead, socket, [this, socket, status] {
                request += socket->readAll();
                const auto head = request.indexOf("\r\n\r\n");
                if (head < 0) return;
                const auto length = request.mid(request.indexOf("Content-Length: ") + 16).split('\r').front().toInt();
                if (request.size() < head + 4 + length) return;
                ++requests;
                socket->write("HTTP/1.1 " + QByteArray::number(status) + " X\r\nContent-Length: 0\r\n\r\n");
            });
        });
    }
};

Target httpTarget(const std::string& address) {
    Target target;
    target.id = "1b9e8a3c-2d4f-4e6a-8b0c-7d5e3f1a9c24";
    target.kind = TargetKind::Http;
    target.address = address;
    target.key = "s3cret-phrase";
    return target;
}

}  // namespace

TEST(LanLoopbackTargets, HttpOkAndRequest) {
    HttpReceiver receiver(204);
    LanTargets targets;
    const auto target = httpTarget("http://127.0.0.1:" + std::to_string(receiver.server.serverPort()) + "/hook?x=1");
    targets.configure({target}, true);
    const auto frame = frameWith(42);
    targets.publish(frame, true, lan_net::steadyMs());
    ASSERT_TRUE(pump([&] { return targets.status(target.id).status == TargetStatus::Ok; }));
    const auto& r = receiver.request;
    EXPECT_TRUE(r.startsWith("POST /hook?x=1 HTTP/1.1\r\nHost: 127.0.0.1:" + QByteArray::number(receiver.server.serverPort()) + "\r\n"));
    EXPECT_TRUE(r.contains("\r\nUser-Agent: AgentSpeed/"));
    EXPECT_TRUE(r.contains("\r\nContent-Type: application/json\r\n"));
    EXPECT_TRUE(r.contains("\r\nX-AgentSpeed-Signature: " + QByteArray::fromStdString(signature("s3cret-phrase", frame->wire())) + "\r\n"));
    EXPECT_TRUE(r.contains("\r\nConnection: close\r\n\r\n"));
    EXPECT_TRUE(r.endsWith(QByteArray::fromStdString(frame->wire())));
}

TEST(LanLoopbackTargets, HttpStatusIsAFailure) {
    HttpReceiver receiver(404);
    LanTargets targets;
    const auto target = httpTarget("http://127.0.0.1:" + std::to_string(receiver.server.serverPort()) + "/");
    targets.configure({target}, true);
    const auto now = lan_net::steadyMs();
    targets.publish(frameWith(1), true, now);
    ASSERT_TRUE(pump([&] { return targets.status(target.id).status == TargetStatus::HttpStatus; }));
    const auto status = targets.status(target.id);
    EXPECT_EQ(status.httpCode, 404);
    EXPECT_NEAR(static_cast<double>(status.retryAtMs - now), 2000, 400);
}

TEST(LanLoopbackTargets, ClosedPortIsUnreachableWithBackoff) {
    quint16 closed = 0;
    {
        QTcpServer probe;
        probe.listen(QHostAddress::LocalHost, 0);
        closed = probe.serverPort();
    }
    LanTargets targets;
    const auto target = httpTarget("http://127.0.0.1:" + std::to_string(closed) + "/");
    targets.configure({target}, true);
    const auto now = lan_net::steadyMs();
    targets.publish(frameWith(1), true, now);
    ASSERT_TRUE(pump([&] { return targets.status(target.id).status == TargetStatus::Unreachable; }));
    const auto first = targets.status(target.id);
    EXPECT_NEAR(static_cast<double>(first.retryAtMs - now), 2000, 400);
    // Nothing goes out before the backoff ends; the next attempt doubles it.
    targets.publish(frameWith(2), true, now + 1000);
    wait(100);
    EXPECT_EQ(targets.status(target.id).retryAtMs, first.retryAtMs);
    targets.publish(frameWith(3), true, first.retryAtMs);
    ASSERT_TRUE(pump([&] { return targets.status(target.id).retryAtMs != first.retryAtMs; }));
    EXPECT_NEAR(static_cast<double>(targets.status(target.id).retryAtMs - first.retryAtMs), 4000, 400);
}

TEST(LanLoopbackTargets, SendTest) {
    QUdpSocket receiver;
    ASSERT_TRUE(receiver.bind(QHostAddress::LocalHost, 0));
    LanTargets targets;
    std::optional<TargetStatus> outcome;
    targets.test(TargetKind::Udp, "127.0.0.1:" + std::to_string(receiver.localPort()), std::nullopt, frameWith(5),
                 [&](const LanTargets::Status& s) { outcome = s.status; });
    ASSERT_TRUE(pump([&] { return outcome.has_value(); }));
    EXPECT_EQ(*outcome, TargetStatus::Ok);
    EXPECT_TRUE(pump([&] { return receiver.hasPendingDatagrams(); }));
}
