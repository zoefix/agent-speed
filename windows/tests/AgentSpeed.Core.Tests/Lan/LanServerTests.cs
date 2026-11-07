using System.Text;
using System.Text.Json;
using AgentSpeed.Core.Lan;

namespace AgentSpeed.Core.Tests.Lan;

/// <summary>LAN 4 over real loopback sockets: HTTP answers, the WebSocket stream, the line protocol and the connection cap.</summary>
public sealed class LanServerTests
{
    private const string Key = LanServerRig.Key;

    private static string Get(string path, string? key = Key) =>
        $"GET {path} HTTP/1.1\r\nHost: 127.0.0.1\r\n" + (key is null ? string.Empty : $"Authorization: Bearer {key}\r\n") + "\r\n";

    private static (string Status, string Body) Split(string response)
    {
        var end = response.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        return (response[..response.IndexOf("\r\n", StringComparison.Ordinal)], response[(end + 4)..]);
    }

    [Fact]
    public async Task LiveAnswersWithTheLatestFrameAndItsErrors()
    {
        using var rig = new LanServerRig();
        var (starting, startingBody) = Split(await LanTestClient.RequestAsync(rig.Port, Get("/v1/live")));
        Assert.Equal("HTTP/1.1 503 Service Unavailable", starting);
        Assert.Equal("{\"schema\":1,\"type\":\"error\",\"error\":\"starting\"}", startingBody);

        var frame = rig.Publish(75.94);
        var response = await LanTestClient.RequestAsync(rig.Port, Get("/v1/live?key=k7q4-m2x9-pt3d", key: null));
        var (status, body) = Split(response);
        Assert.Equal("HTTP/1.1 200 OK", status);
        Assert.Contains("Content-Type: application/json; charset=utf-8\r\n", response, StringComparison.Ordinal);
        Assert.Contains("Cache-Control: no-store\r\nConnection: close\r\n", response, StringComparison.Ordinal);
        Assert.Contains($"Content-Length: {frame.Bytes.Length}\r\n", response, StringComparison.Ordinal);
        Assert.Equal(Encoding.UTF8.GetString(frame.Bytes), body);
        Assert.Equal(75.9, JsonDocument.Parse(body).RootElement.GetProperty("outputTokS").GetDouble());

        Assert.StartsWith("HTTP/1.1 401 Unauthorized", await LanTestClient.RequestAsync(rig.Port, Get("/v1/live", "WRONG-KEY")), StringComparison.Ordinal);
        Assert.StartsWith("HTTP/1.1 404 Not Found", await LanTestClient.RequestAsync(rig.Port, Get("/v1/other")), StringComparison.Ordinal);
        var post = await LanTestClient.RequestAsync(rig.Port, "POST /v1/live HTTP/1.1\r\nContent-Length: 2\r\n\r\n{}");
        Assert.StartsWith("HTTP/1.1 405 Method Not Allowed", post, StringComparison.Ordinal);
        Assert.Contains("Allow: GET\r\n", post, StringComparison.Ordinal);
        Assert.StartsWith("HTTP/1.1 426 Upgrade Required", await LanTestClient.RequestAsync(rig.Port, Get("/v1/stream")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheTenthWrongKeyLocksThePeerOut()
    {
        using var rig = new LanServerRig();
        rig.Publish(10);
        for (var i = 0; i < 10; i++)
        {
            Assert.StartsWith("HTTP/1.1 401", await LanTestClient.RequestAsync(rig.Port, Get("/v1/live", "AAAA-BBBB-CCCC")), StringComparison.Ordinal);
        }

        var locked = await LanTestClient.RequestAsync(rig.Port, Get("/v1/live"));
        Assert.StartsWith("HTTP/1.1 429 Too Many Requests", locked, StringComparison.Ordinal);
        Assert.Contains("Retry-After: 60\r\n", locked, StringComparison.Ordinal);
        rig.Advance(60_000);
        Assert.StartsWith("HTTP/1.1 200", await LanTestClient.RequestAsync(rig.Port, Get("/v1/live")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task GarbageAndSilenceAreClosedWithoutAReply()
    {
        using var rig = new LanServerRig();
        Assert.Equal(string.Empty, await LanTestClient.RequestAsync(rig.Port, "hello\n"));
        Assert.Equal(string.Empty, await LanTestClient.RequestAsync(rig.Port, "get /v1/live HTTP/1.1\r\n\r\n"));
    }

    [Fact]
    public async Task WebSocketStreamsFramesAndAnswersPings()
    {
        using var rig = new LanServerRig();
        var first = rig.Publish(40);
        using var client = await LanTestClient.UpgradeAsync(rig.Port, Key);
        var head = await client.ReadHeadAsync();
        Assert.StartsWith("HTTP/1.1 101 Switching Protocols\r\n", head, StringComparison.Ordinal);
        Assert.Contains("Upgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: s3pPLMBiTxaQ9kYGzzhZRbK+xOo=\r\n", head, StringComparison.Ordinal);

        var (opcode, payload) = await client.ReadFrameAsync();
        Assert.Equal(LanWebSocket.Text, opcode);
        Assert.Equal(first.Bytes, payload);

        await client.SendAsync(LanTestClient.ClientFrame(LanWebSocket.Ping, "hi"u8.ToArray()));
        var pong = await client.ReadFrameAsync();
        Assert.Equal(LanWebSocket.PongOpcode, pong.Opcode);
        Assert.Equal("hi"u8.ToArray(), pong.Payload);

        rig.Advance(500);
        var second = rig.Publish(80);
        var next = await client.ReadFrameAsync();
        Assert.Equal(second.Bytes, next.Payload);
        Assert.Equal(1, rig.Server.Status.Devices);

        await client.SendAsync(LanTestClient.ClientFrame(LanWebSocket.CloseOpcode, [0x03, 0xE8, (byte)'b', (byte)'y', (byte)'e']));
        var close = await client.ReadFrameAsync();
        Assert.Equal(LanWebSocket.CloseOpcode, close.Opcode);
        Assert.Equal(1000, LanTestClient.CloseCode(close.Payload));
        Assert.False(await client.IsOpenAsync(TimeSpan.FromSeconds(3)));
    }

    [Fact]
    public async Task AnUnmaskedFrameClosesWith1002()
    {
        using var rig = new LanServerRig();
        rig.Publish(1);
        using var client = await LanTestClient.UpgradeAsync(rig.Port, Key);
        await client.ReadHeadAsync();
        await client.ReadFrameAsync();
        await client.SendAsync(LanTestClient.ClientFrame(LanWebSocket.Text, "x"u8.ToArray(), masked: false));
        var close = await client.ReadFrameAsync();
        Assert.Equal(LanWebSocket.CloseOpcode, close.Opcode);
        Assert.Equal(1002, LanTestClient.CloseCode(close.Payload));
    }

    [Fact]
    public async Task WebSocketHeartbeatAndKeyRegeneration()
    {
        using var rig = new LanServerRig();
        rig.Publish(5);
        using var client = await LanTestClient.UpgradeAsync(rig.Port, Key);
        await client.ReadHeadAsync();
        await client.ReadFrameAsync();

        rig.Advance(500);
        rig.Publish(5);
        rig.Advance(4500);
        var heartbeat = rig.Publish(5);
        Assert.Equal(heartbeat.Bytes, (await client.ReadFrameAsync()).Payload);

        rig.Server.SetKey("ZZZZ2222YYYY");
        var close = await client.ReadFrameAsync();
        Assert.Equal(LanWebSocket.CloseOpcode, close.Opcode);
        Assert.Equal(1008, LanTestClient.CloseCode(close.Payload));
    }

    [Fact]
    public async Task StoppingSendsGoingAway()
    {
        using var rig = new LanServerRig();
        rig.Publish(5);
        using var client = await LanTestClient.UpgradeAsync(rig.Port, Key);
        await client.ReadHeadAsync();
        await client.ReadFrameAsync();
        rig.Server.Stop();
        var close = await client.ReadFrameAsync();
        Assert.Equal(1001, LanTestClient.CloseCode(close.Payload));
        Assert.Equal(LanServerStatusKind.Off, rig.Server.Status.Kind);
    }

    [Fact]
    public async Task QuittingWritesGoingAwayBeforeItReturns()
    {
        using var rig = new LanServerRig();
        rig.Publish(5);
        using var client = await LanTestClient.UpgradeAsync(rig.Port, Key);
        await client.ReadHeadAsync();
        await client.ReadFrameAsync();
        rig.Server.Dispose();
        var close = await client.ReadFrameAsync();
        Assert.Equal(LanWebSocket.CloseOpcode, close.Opcode);
        Assert.Equal(1001, LanTestClient.CloseCode(close.Payload));
    }

    [Fact]
    public async Task LineProtocolSendsFramesAfterAValidHello()
    {
        using var rig = new LanServerRig();
        var first = rig.Publish(12.34);
        using var client = await LanTestClient.ConnectAsync(rig.Port);
        await client.SendAsync("{\"type\":\"hello\",\"key\":\"k7q4 m2x9 pt3d\",\"device\":\"desk\"}\n");
        Assert.Equal(Encoding.UTF8.GetString(first.Bytes), await client.ReadLineAsync());
        await client.SendAsync("anything after the hello is ignored\n");
        rig.Advance(500);
        var second = rig.Publish(50);
        Assert.Equal(Encoding.UTF8.GetString(second.Bytes), await client.ReadLineAsync());
    }

    [Theory]
    [InlineData("{\"type\":\"subscribe\",\"key\":\"K7Q4M2X9PT3D\"}\n", "badRequest")]
    [InlineData("{\"type\":\"hello\",\"key\":\"K7Q4-M2X9-XXXX\"}\n", "unauthorized")]
    public async Task ABadHelloGetsOneErrorLine(string hello, string error)
    {
        using var rig = new LanServerRig();
        rig.Publish(1);
        var reply = await LanTestClient.RequestAsync(rig.Port, hello);
        Assert.Equal($"{{\"schema\":1,\"type\":\"error\",\"error\":\"{error}\"}}\n", reply);
    }

    [Fact]
    public async Task TheSeventeenthConnectionIsClosedAtOnce()
    {
        using var rig = new LanServerRig();
        var open = new List<LanTestClient>();
        try
        {
            for (var i = 0; i < LanServer.MaxConnections; i++)
            {
                open.Add(await LanTestClient.ConnectAsync(rig.Port));
            }

            await WaitUntil(() => rig.Server.ConnectionCount == LanServer.MaxConnections);
            using var extra = await LanTestClient.ConnectAsync(rig.Port);
            Assert.False(await extra.IsOpenAsync(TimeSpan.FromSeconds(3)));
            Assert.True(await open[0].IsOpenAsync(TimeSpan.FromMilliseconds(200)));

            open[0].Dispose();
            open.RemoveAt(0);
            await WaitUntil(() => rig.Server.ConnectionCount == LanServer.MaxConnections - 1);
            rig.Publish(3);
            Assert.StartsWith("HTTP/1.1 200", await LanTestClient.RequestAsync(rig.Port, Get("/v1/live")), StringComparison.Ordinal);
        }
        finally
        {
            open.ForEach(c => c.Dispose());
        }
    }

    [Fact]
    public async Task ATakenPortIsReportedAndTheOldPortCloses()
    {
        using var first = new LanServerRig();
        using var second = new LanServerRig(start: false);
        second.Server.Start(first.Port, Key);
        Assert.Equal(LanServerStatusKind.PortInUse, second.Server.Status.Kind);
        var port = first.Port;
        first.Server.Stop();
        await Task.Delay(100);
        await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            using var client = await LanTestClient.ConnectAsync(port);
            await client.SendAsync(Get("/v1/live"));
            Assert.NotEmpty(await client.ReadToEndAsync());
        });
    }

    internal static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++)
        {
            await Task.Delay(50);
        }

        Assert.True(condition());
    }
}
