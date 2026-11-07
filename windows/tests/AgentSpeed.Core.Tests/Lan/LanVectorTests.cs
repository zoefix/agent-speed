using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using AgentSpeed.Core.Lan;
using AgentSpeed.Core.Reporting;

namespace AgentSpeed.Core.Tests.Lan;

/// <summary>Every section of spec/fixtures/lan-vectors.json (written by spec/tools/lan_reference.py), checked against Core.</summary>
public sealed class LanVectorTests
{
    private static readonly Lazy<JsonDocument> Vectors = new(() => JsonDocument.Parse(File.ReadAllText(FixtureLocator.PathOf("lan-vectors.json"))));

    private static JsonElement Root => Vectors.Value.RootElement;

    private static string Key => Root.GetProperty("key").GetString()!;

    [Fact]
    public void KeyDisplaysInGroupsOfFour()
    {
        Assert.True(LanKey.IsValid(Key));
        Assert.False(LanKey.IsValid("K7Q4M2X9PL3D"));
        Assert.Equal(Root.GetProperty("keyDisplay").GetString(), LanKey.Display(Key));
        for (var i = 0; i < 50; i++)
        {
            Assert.True(LanKey.IsValid(LanKey.New()));
        }
    }

    [Fact]
    public void FramesAreByteExact()
    {
        foreach (var item in Root.GetProperty("frames").EnumerateArray())
        {
            var input = item.GetProperty("input");
            var frame = LanFrame.Build(input.GetProperty("instance").GetString()!, input.GetProperty("name").GetString()!,
                input.GetProperty("run").GetString()!, input.GetProperty("seq").GetInt64(), input.GetProperty("atMs").GetInt64(),
                new LanTickValues(Values(input.GetProperty("live")), input.GetProperty("sources").EnumerateObject()
                    .ToDictionary(p => SourceOf(p.Name), p => Values(p.Value))),
                input.GetProperty("history").EnumerateArray().Select(v => v.GetDouble()).ToArray());
            Assert.True(item.GetProperty("frame").GetString() == Encoding.UTF8.GetString(frame.Bytes), item.GetProperty("name").GetString());
        }
    }

    [Fact]
    public void HistorySamplesOncePerSecond()
    {
        foreach (var item in Root.GetProperty("history").EnumerateArray())
        {
            var history = new LanHistory();
            IReadOnlyList<double> after = [];
            foreach (var tick in item.GetProperty("ticks").EnumerateArray())
            {
                after = history.Tick(tick[0].GetInt64(), tick[1].GetDouble());
            }

            Assert.Equal(item.GetProperty("after").EnumerateArray().Select(v => v.GetDouble()), after);
        }
    }

    [Fact]
    public void DatagramsFollowTheSizeRule()
    {
        foreach (var item in Root.GetProperty("datagrams").EnumerateArray())
        {
            var frameText = item.GetProperty("frame").GetString()!;
            var frame = Rebuild(frameText);
            Assert.Equal(frameText, Encoding.UTF8.GetString(frame.Bytes));
            var key = item.GetProperty("key").ValueKind == JsonValueKind.Null ? null : item.GetProperty("key").GetString();
            var (bytes, omitted) = LanDatagram.Build(frame, key);
            var name = item.GetProperty("name").GetString();
            Assert.True(item.GetProperty("datagram").GetString() == Encoding.UTF8.GetString(bytes), name);
            Assert.Equal(item.GetProperty("bytes").GetInt32(), bytes.Length);
            Assert.True(bytes.Length <= LanDatagram.MaxBytes);
            Assert.Equal(item.GetProperty("omitted").EnumerateArray().Select(v => v.GetString()), omitted);
        }
    }

    [Fact]
    public void FirstLinesPickTheProtocol()
    {
        foreach (var item in Root.GetProperty("firstLines").EnumerateArray())
        {
            var kind = LanRequestHead.Classify(Encoding.UTF8.GetBytes(item[0].GetString()!), final: true);
            Assert.True(item[1].GetString() == kind.ToString().ToLowerInvariant(), item[0].GetString());
        }
    }

    [Fact]
    public void RequestsAreAnsweredInOrderWithTheLockout()
    {
        var lockout = new LanLockout();
        foreach (var item in Root.GetProperty("requests").EnumerateArray())
        {
            var name = item.GetProperty("name").GetString();
            var head = LanRequestHead.Parse(Encoding.UTF8.GetBytes(item.GetProperty("head").GetString()!));
            Assert.NotNull(head);
            var response = LanResponder.Respond(head, item.GetProperty("peer").GetString()!, item.GetProperty("nowMs").GetInt64(), Key,
                item.GetProperty("hasFrame").GetBoolean(), lockout);
            Assert.True(item.GetProperty("status").GetInt32() == response.Status, name);
            Assert.True(Text(item.GetProperty("error")) == response.Error, name);
            Assert.Equal(item.GetProperty("headers").EnumerateObject().Select(p => (p.Name, p.Value.GetString()!)), response.Headers);
        }
    }

    [Fact]
    public void ResponsesCarryTheFixedHeaders()
    {
        var head = LanRequestHead.Parse("GET /v1/other HTTP/1.1\r\n\r\n"u8)!;
        var text = Encoding.UTF8.GetString(LanResponder.Bytes(LanResponder.Respond(head, "10.0.0.2", 0, Key, true, new LanLockout()), null));
        Assert.Equal("HTTP/1.1 404 Not Found\r\nContent-Type: application/json; charset=utf-8\r\nContent-Length: 46\r\nCache-Control: no-store\r\n"
            + "Connection: close\r\n\r\n{\"schema\":1,\"type\":\"error\",\"error\":\"notFound\"}", text);
    }

    [Fact]
    public void HellosAreCheckedWithTheLockout()
    {
        var lockout = new LanLockout();
        foreach (var item in Root.GetProperty("hellos").EnumerateArray())
        {
            var error = LanHello.Check(Encoding.UTF8.GetBytes(item.GetProperty("line").GetString()!), "192.168.1.77", item.GetProperty("nowMs").GetInt64(), Key,
                lockout);
            Assert.True(Text(item.GetProperty("error")) == error, item.GetProperty("name").GetString());
        }
    }

    [Fact]
    public void WebSocketFramesMatch()
    {
        var websocket = Root.GetProperty("websocket");
        foreach (var item in websocket.GetProperty("accept").EnumerateArray())
        {
            Assert.Equal(item.GetProperty("accept").GetString(), LanResponder.Accept(item.GetProperty("key").GetString()!));
        }

        foreach (var item in websocket.GetProperty("clientFrames").EnumerateArray())
        {
            var data = Convert.FromHexString(item.GetProperty("frame").GetString()!);
            var result = LanWebSocket.Client(data);
            var expected = item.GetProperty("result");
            var name = item.GetProperty("name").GetString();
            var action = expected.GetProperty("action").GetString();
            Assert.True(action == char.ToLowerInvariant(result.Action.ToString()[0]) + result.Action.ToString()[1..], name);
            if (expected.TryGetProperty("payload", out var payload))
            {
                Assert.Equal(payload.GetString(), Convert.ToHexStringLower(result.Payload ?? []));
            }

            if (expected.TryGetProperty("code", out var code))
            {
                Assert.True((code.ValueKind == JsonValueKind.Null ? null : code.GetInt32()) == result.Code, name);
            }

            if (result.Action != LanClientAction.Close)
            {
                Assert.Equal(data.Length, result.Consumed);
            }
        }

        foreach (var item in websocket.GetProperty("serverFrames").EnumerateArray())
        {
            var hex = Convert.ToHexStringLower(LanWebSocket.Frame(item.GetProperty("opcode").GetInt32(),
                Encoding.UTF8.GetBytes(item.GetProperty("payload").GetString()!)));
            var expected = item.GetProperty("frame").GetString()!;
            Assert.Equal(expected, item.TryGetProperty("prefixOnly", out _) ? hex[..expected.Length] : hex);
        }

        foreach (var item in websocket.GetProperty("closes").EnumerateArray())
        {
            var code = item.GetProperty("code");
            Assert.Equal(item.GetProperty("frame").GetString(), Convert.ToHexStringLower(LanWebSocket.Close(code.ValueKind == JsonValueKind.Null ? null : code.GetInt32())));
        }
    }

    [Fact]
    public void LocalAddresses()
    {
        foreach (var item in Root.GetProperty("local").EnumerateArray())
        {
            Assert.True(item[2].GetBoolean() == LanAddress.IsLocal(item[0].GetString()!, item[1].GetBoolean()), $"{item[0]} udp={item[1]}");
        }
    }

    [Fact]
    public void TargetAddressesParse()
    {
        foreach (var item in Root.GetProperty("targets").EnumerateArray())
        {
            var kind = item.GetProperty("kind").GetString() == "udp" ? LanTargetKind.Udp : LanTargetKind.Http;
            var address = item.GetProperty("address").GetString()!;
            var parsed = LanTargetAddress.Parse(kind, address, out var error);
            var expected = item.GetProperty("result");
            if (expected.TryGetProperty("error", out var reason))
            {
                Assert.True(parsed is null, address);
                Assert.True(reason.GetString() == error.ToString().ToLowerInvariant(), address);
                continue;
            }

            Assert.True(parsed is not null, address);
            Assert.Equal(expected.GetProperty("host").GetString(), parsed.Host);
            Assert.Equal(expected.GetProperty("literal").GetBoolean(), parsed.Literal);
            Assert.Equal(expected.GetProperty("port").GetInt32(), parsed.Port);
            Assert.Equal(Text(expected, "hostHeader"), parsed.HostHeader);
            Assert.Equal(Text(expected, "target"), parsed.Target);
        }
    }

    [Fact]
    public void ResolutionsAreChecked()
    {
        foreach (var item in Root.GetProperty("resolutions").EnumerateArray())
        {
            var addresses = item.GetProperty("addresses").EnumerateArray().Select(a => IPAddress.Parse(a.GetString()!)).ToArray();
            var status = LanAddress.ResolveCheck(addresses, item.GetProperty("udp").GetBoolean());
            Assert.Equal(item.GetProperty("status").GetString(), StatusName(status));
        }
    }

    [Fact]
    public void SignaturesMatch()
    {
        foreach (var item in Root.GetProperty("signatures").EnumerateArray())
        {
            Assert.Equal(item.GetProperty("signature").GetString(),
                LanSignature.Of(item.GetProperty("key").GetString()!, Encoding.UTF8.GetBytes(item.GetProperty("body").GetString()!)));
        }
    }

    [Fact]
    public void SettingsDocumentsNormalize()
    {
        foreach (var item in Root.GetProperty("settings").EnumerateArray())
        {
            var name = item.GetProperty("name").GetString();
            var settings = LanSettingsJson.Read(item.GetProperty("document"));
            var expected = item.GetProperty("settings");
            Assert.True(Text(expected, "instance") == settings.Instance, name);
            var server = expected.GetProperty("server");
            Assert.True(server.GetProperty("enabled").GetBoolean() == settings.Server.Enabled, name);
            Assert.True(server.GetProperty("port").GetInt32() == settings.Server.Port, name);
            Assert.True(Text(server, "key") == settings.Server.Key, name);
            var targets = expected.GetProperty("targets").EnumerateArray().ToArray();
            Assert.True(targets.Length == settings.Targets.Length, name);
            for (var i = 0; i < targets.Length; i++)
            {
                var target = settings.Targets[i];
                Assert.Equal(targets[i].GetProperty("id").GetString(), target.Id);
                Assert.Equal(targets[i].GetProperty("name").GetString(), target.Name);
                Assert.Equal(targets[i].GetProperty("kind").GetString(), target.Kind == LanTargetKind.Udp ? "udp" : "http");
                Assert.Equal(targets[i].GetProperty("address").GetString(), target.Address);
                Assert.Equal(Text(targets[i], "key"), target.Key);
                Assert.Equal(targets[i].GetProperty("enabled").GetBoolean(), target.Enabled);
            }

            var reread = LanSettingsJson.Parse(LanSettingsJson.ToJson(settings));
            Assert.Equal(settings, reread);
        }
    }

    [Fact]
    public void PushRulesSendWhenDue()
    {
        foreach (var item in Root.GetProperty("push").EnumerateArray())
        {
            var kind = Enum.Parse<LanReceiverKind>(item.GetProperty("kind").GetString()!, ignoreCase: true);
            var ticks = item.GetProperty("ticks").EnumerateArray().Select(t => (t[0].GetInt64(), t[1].GetBoolean()));
            Assert.Equal(item.GetProperty("sent").EnumerateArray().Select(v => v.GetInt64()), LanPushSchedule.Simulate(kind, ticks));
        }
    }

    [Fact]
    public void KeysCompareNormalized()
    {
        foreach (var item in Root.GetProperty("keys").EnumerateArray())
        {
            Assert.True(item.GetProperty("matches").GetBoolean() == LanKey.Matches(item.GetProperty("given").GetString(), Key), item.GetProperty("given").GetString());
        }
    }

    [Fact]
    public void HttpBackoffDoublesToThirtySeconds()
    {
        var expected = Root.GetProperty("httpBackoffMs").EnumerateArray().Select(v => v.GetInt64()).ToArray();
        Assert.Equal(expected, Enumerable.Range(0, expected.Length).Select(LanBackoff.Ms));
    }

    internal static string StatusName(LanTargetStatusKind kind) => char.ToLowerInvariant(kind.ToString()[0]) + kind.ToString()[1..];

    /// <summary>A frame built again from its own JSON (the values are rounded already, so the bytes are the same).</summary>
    internal static LanFrame Rebuild(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var sources = root.TryGetProperty("sources", out var list)
            ? list.EnumerateArray().ToDictionary(s => SourceOf(s.GetProperty("source").GetString()!), Scope)
            : new Dictionary<SourceKind, LanLiveValues>();
        var history = root.TryGetProperty("history", out var h) ? h.GetProperty("outputTokS").EnumerateArray().Select(v => v.GetDouble()).ToArray() : [];
        var at = DateTimeOffset.Parse(root.GetProperty("at").GetString()!, CultureInfo.InvariantCulture).ToUnixTimeMilliseconds();
        return LanFrame.Build(root.GetProperty("instance").GetString()!, root.GetProperty("name").GetString()!, root.GetProperty("run").GetString()!,
            root.GetProperty("seq").GetInt64(), at, new LanTickValues(Scope(root), sources), history);
    }

    private static LanLiveValues Scope(JsonElement scope)
    {
        var sessions = scope.GetProperty("sessions");
        return new LanLiveValues(scope.GetProperty("outputTokS").GetDouble(), scope.GetProperty("estimatedShare").GetDouble(),
            sessions.GetProperty("generating").GetInt64(), sessions.GetProperty("tools").GetInt64(), sessions.GetProperty("active").GetInt64());
    }

    private static LanLiveValues Values(JsonElement live) => new(live.GetProperty("outputTokS").GetDouble(), live.GetProperty("estimatedShare").GetDouble(),
        live.GetProperty("generating").GetInt64(), live.GetProperty("tools").GetInt64(), live.GetProperty("active").GetInt64());

    private static SourceKind SourceOf(string id) => Enum.GetValues<SourceKind>().Single(k => ReportBody.SourceId(k) == id);

    private static string? Text(JsonElement value) => value.ValueKind == JsonValueKind.Null ? null : value.GetString();

    private static string? Text(JsonElement parent, string name) => parent.TryGetProperty(name, out var value) ? Text(value) : null;
}
