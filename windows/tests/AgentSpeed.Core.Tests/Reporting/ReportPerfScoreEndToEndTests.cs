using System.Net.Http;
using System.Text.Json;
using AgentSpeed.Core.Monitoring;
using AgentSpeed.Core.Reporting;

namespace AgentSpeed.Core.Tests.Reporting;

/// <summary>
/// The performance score extension end to end (METRICS 11.10) against <c>spec/tools/report_server.py --perf-score</c> on
/// loopback: a real transcript read by the live monitor with the watch on, the engine's counting of measured responses and
/// tool results, the report side's statistics window, the body builder, signing and both transports. The server pools the
/// statistics of every accepted report into its model leaderboard — a retried report is a duplicate, so every completion is
/// counted once — and shows the live values of the latest report. Never reaches beyond 127.0.0.1.
/// </summary>
[Collection("report server")]
public sealed class ReportPerfScoreEndToEndTests
{
    private static readonly ReportClientInfo Client = new("1.3.0", "Windows", 11);

    [Fact]
    public async Task OverHttpStatisticsCountOnceAndLiveValuesAreAccepted()
    {
        if (!ReferencePython.Available)
        {
            return;
        }

        using var server = new ReferenceServer("--perf-score", "--api-path", "/agent-speed");
        using var transport = new HttpReportTransport();
        var api = new ReportApi(transport, Client.UserAgent);
        var device = await PerfDevice.Register(api, server, "perf-http");
        Assert.True(device.Document.Extensions.PerfScore);
        using var pipeline = new PerfPipeline(device.Document);

        pipeline.Work(turns: 4);
        var first = pipeline.Build(device, seq: 1);
        Assert.Equal((4, 4), Counted(first));
        Assert.Equal("live", JsonDocument.Parse(first).RootElement.GetProperty("perfScore").GetProperty("form").GetString());
        Assert.Equal("accepted", (await device.Post(api, first)).Result);
        Assert.Equal("duplicate", (await device.Post(api, first)).Result);
        await AssertBoard(device.Document, first, responses: 4, calls: 4);

        var second = pipeline.Build(device, seq: 2);
        Assert.Equal((0, 0), Counted(second));
        Assert.Equal("accepted", (await device.Post(api, second)).Result);
        await AssertBoard(device.Document, second, responses: 4, calls: 4);

        pipeline.Work(turns: 2);
        var third = pipeline.Build(device, seq: 3);
        Assert.Equal((2, 2), Counted(third));
        Assert.Equal("accepted", (await device.Post(api, third)).Result);
        await AssertBoard(device.Document, third, responses: 6, calls: 6);

        // A service that advertises the extension rejects broken score fields and uses up no sequence number (REPORTING 8.3).
        var broken = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(third)!;
        broken["run"] = JsonDocument.Parse($$"""{"id":"{{device.RunId}}","seq":4}""").RootElement;
        broken["perfScore"] = JsonDocument.Parse("""{"score":1200,"form":"none","thinkingTokS":0}""").RootElement;
        var rejected = await device.Post(api, JsonSerializer.SerializeToUtf8Bytes(broken));
        Assert.Equal((422, "payload_invalid"), (rejected.Status, rejected.Code));
        Assert.Equal(new ReportReaction.Strike(false), ReportOutcome.Classify(rejected, false).Reaction);
        await AssertBoard(device.Document, third, responses: 6, calls: 6);
        Assert.Contains("+2 responses +2 tool calls", server.Log, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OverWebSocketStatisticsCountOnceAndLiveValuesAreAccepted()
    {
        if (!ReferencePython.Available)
        {
            return;
        }

        using var server = new ReferenceServer("--perf-score", "--websocket", "--ws-min-interval", "50");
        using var transport = new HttpReportTransport();
        var api = new ReportApi(transport, Client.UserAgent);
        var device = await PerfDevice.Register(api, server, "perf-ws");
        Assert.True(device.Document.Extensions.PerfScore);
        Assert.NotNull(device.Document.WebSocket);
        using var pipeline = new PerfPipeline(device.Document);

        var events = new ReportStreamSocketTests.EventLog();
        var socket = device.Open(events);
        Assert.IsType<ReportStreamEvent.Opened>(await events.Next());
        Assert.IsType<ReportServiceMessage.Ready>(Message(await events.Next()));

        pipeline.Work(turns: 3);
        var first = pipeline.Build(device, seq: 1, stream: true);
        Assert.Equal((3, 3), Counted(first));
        Assert.True(socket.Write(device.Message(first)));
        Assert.Equal(ReportAckStatus.Accepted, Assert.IsType<ReportServiceMessage.Ack>(Message(await events.Next())).Status);
        Assert.True(socket.Write(device.Message(first)));
        Assert.Equal(ReportAckStatus.Duplicate, Assert.IsType<ReportServiceMessage.Ack>(Message(await events.Next())).Status);
        await AssertBoard(device.Document, first, responses: 3, calls: 3);

        pipeline.Work(turns: 1);
        var second = pipeline.Build(device, seq: 2, stream: true);
        Assert.Equal((1, 1), Counted(second));
        Assert.True(socket.Write(device.Message(second)));
        Assert.Equal(ReportAckStatus.Accepted, Assert.IsType<ReportServiceMessage.Ack>(Message(await events.Next())).Status);
        await AssertBoard(device.Document, second, responses: 4, calls: 4);
        socket.Close();
    }

    [Fact]
    public async Task AServiceWithOnlyTheRetiredTaskScoreGetsTheThinkingSpeed()
    {
        if (!ReferencePython.Available)
        {
            return;
        }

        using var server = new ReferenceServer("--task-score");
        using var transport = new HttpReportTransport();
        var api = new ReportApi(transport, Client.UserAgent);
        var device = await PerfDevice.Register(api, server, "task-legacy");
        Assert.True(device.Document.Extensions.TaskScore);
        Assert.False(device.Document.Extensions.PerfScore);
        using var pipeline = new PerfPipeline(device.Document);

        pipeline.Work(turns: 3);
        var body = JsonDocument.Parse(pipeline.Build(device, seq: 1)).RootElement;
        Assert.False(body.TryGetProperty("perfScore", out _));
        var task = body.GetProperty("taskScore");
        Assert.Equal((JsonValueKind.Null, "none"), (task.GetProperty("score").ValueKind, task.GetProperty("form").GetString()));
        Assert.All(body.GetProperty("models").EnumerateArray(), m => Assert.Equal(0, m.GetProperty("taskStats").GetArrayLength()));
        Assert.Equal("accepted", (await device.Post(api, JsonSerializer.SerializeToUtf8Bytes(body))).Result);
    }

    /// <summary>Σ <c>n</c> and Σ <c>toolN</c> over every entry's statistics rows.</summary>
    private static (long Responses, long Calls) Counted(byte[] body)
    {
        var rows = JsonDocument.Parse(body).RootElement.GetProperty("models").EnumerateArray()
            .SelectMany(m => m.GetProperty("perfStats").EnumerateArray()).ToList();
        return (rows.Sum(r => r.GetProperty("n").GetInt64()), rows.Sum(r => r.GetProperty("toolN").GetInt64()));
    }

    /// <summary>
    /// The server's pooled leaderboard (REPORTING-EXTENSIONS 9): every counted completion once, and the live values of the
    /// device's latest accepted report, <paramref name="latest"/>.
    /// </summary>
    private static async Task AssertBoard(ReportDiscovery document, byte[] latest, long responses, long calls)
    {
        using var http = new HttpClient();
        var board = JsonDocument.Parse(await http.GetStringAsync(new Uri(document.Api.Text + "/public/models"))).RootElement;
        var models = board.GetProperty("models").EnumerateArray().ToList();
        Assert.Equal(responses, models.Sum(m => m.GetProperty("n").GetInt64()));
        Assert.Equal(calls, models.Sum(m => m.GetProperty("tools").GetInt64()));
        var body = JsonDocument.Parse(latest).RootElement;
        var top = body.GetProperty("perfScore");
        var live = board.GetProperty("live");
        Assert.Equal(top.GetProperty("form").GetString(), live.GetProperty("form").GetString());
        Assert.Equal(top.GetProperty("score").GetRawText(), live.GetProperty("score").GetRawText());
        Assert.Equal(top.GetProperty("thinkingTokS").GetDouble(), live.GetProperty("thinkingTokS").GetDouble(), 6);
        foreach (var entry in body.GetProperty("models").EnumerateArray())
        {
            var row = models.Single(m => m.GetProperty("id").GetString() == entry.GetProperty("id").GetString());
            var value = row.GetProperty("live");
            Assert.Equal(entry.GetProperty("perfScore").GetProperty("form").GetString(), value.GetProperty("form").GetString());
            Assert.Equal(entry.GetProperty("perfScore").GetProperty("score").GetRawText(), value.GetProperty("score").GetRawText());
            Assert.Equal(1, value.GetProperty("devices").GetInt32());
        }
    }

    private static ReportServiceMessage Message(ReportStreamEvent e) => Assert.IsType<ReportStreamEvent.Message>(e).Value;

    /// <summary>
    /// The app's path for one service: a live monitor over a Claude transcript with the statistics watch on, the report
    /// window and the statistics window of the controller, and the body builder with the document's options.
    /// </summary>
    private sealed class PerfPipeline : IDisposable
    {
        private readonly TempDirectory _temp = new();
        private readonly MonitorCore _core;
        private readonly ReportDiscovery _document;
        private readonly ReportPerfWindow _perf = new();
        private readonly string _path;
        private readonly long _todayStart;
        private ReportWindow? _window;
        private long _clock = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - 120_000;
        private int _messages;

        public PerfPipeline(ReportDiscovery document)
        {
            _document = document;
            _todayStart = _clock - 3_600_000;
            var claude = _temp.Combine("claude", "projects");
            _path = Path.Combine(claude, "p", "a.jsonl");
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, string.Empty);
            Directory.CreateDirectory(_temp.Combine("codex", "sessions"));
            _core = new MonitorCore(MonitorConfiguration.Default, _temp.Combine("codex", "sessions"), claude, _temp.Combine("dsh"), _todayStart)
            {
                Clock = () => _clock,
            };
            _core.Seed(_clock, _todayStart, CancellationToken.None);
            Assert.True(_perf.Update(on: true, "service"));
            _core.PerfWatch = _perf.Watch;
        }

        /// <summary>
        /// <paramref name="turns"/> requests, each a measured response that calls a tool, then the tool's result: one response
        /// and one call counted per turn, and the agent keeps working (a live score once three responses are measured).
        /// </summary>
        public void Work(int turns)
        {
            for (var i = 0; i < turns; i++)
            {
                var id = "m" + ++_messages;
                var call = "call" + _messages;
                var request = ClaudeLines.ApiRequest(Tick(500));
                var assistant = ClaudeLines.Assistant(Tick(1500), id, 240, "\"tool_use\"", input: 3000, cacheRead: 2800, thinking: 60).Replace(
                    """[{"type":"text","text":"x"}]""",
                    $$"""[{"type":"tool_use","id":"{{call}}","name":"Bash","input":{"command":"cargo test {{i}}"} }]""", StringComparison.Ordinal);
                Append(request, assistant,
                    ClaudeLines.User(Tick(700), $$"""[{"type":"tool_result","tool_use_id":"{{call}}","is_error":false,"content":"ok"}]"""));
            }
        }

        public byte[] Build(PerfDevice device, int seq, bool stream = false)
        {
            var sample = ReportSample.From(_core.BuildSnapshot(_clock, _todayStart, MonitorState.Running, observeLive: true).Snapshot,
                ReportTrafficSample.Off);
            _window ??= new ReportWindow(sample);
            var content = _window.Take(sample) with { Perf = _perf.Take(sample) };
            _core.PerfWatch = _perf.Watch;
            var envelope = new ReportEnvelope(Client, device.DeviceId, device.Username, device.RunId, seq, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                5000, ReportActivity.Active, 2);
            var options = new ReportOptions(_document.Sections, _document.Limits.MaxModels, _document.Limits.SendLimitBytes)
            {
                Extensions = _document.Extensions,
                Stream = stream ? new ReportStreamBody(30, _document.WebSocket!.MaxMessageBytes) : null,
            };
            Assert.NotEqual(ReportScoreExtension.None, options.ScoreExtension);
            return ReportBody.Data(envelope, content, sample, options);
        }

        public void Dispose()
        {
            _core.Dispose();
            _temp.Dispose();
        }

        private long Tick(long ms) => _clock += ms;

        private void Append(params string[] lines)
        {
            File.AppendAllText(_path, string.Concat(lines.Select(l => l + "\n")));
            Assert.True(_core.ProcessPaths([_path], _clock, _todayStart));
        }
    }

    /// <summary>A registered device of one run, sending over HTTP or writing <c>report</c> messages.</summary>
    private sealed class PerfDevice(ReportDiscovery document, string username)
    {
        private readonly SoftwareReportSigner _signer = new();

        public ReportDiscovery Document { get; } = document;

        public string DeviceId { get; } = ReportCanonical.NewUuid();

        public string Username { get; } = username;

        public string RunId { get; } = ReportCanonical.NewUuid();

        private ReportRequestBuilder Builder => new(Client.UserAgent, DeviceId, null);

        private static long Now => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        public static async Task<PerfDevice> Register(ReportApi api, ReferenceServer server, string username)
        {
            var document = Assert.IsType<ReportDiscoveryOutcome.Document>(await api.DiscoverAsync(server.Service)).Discovery;
            var device = new PerfDevice(document, username);
            var request = device.Builder.Make("POST", document.Api.Endpoint(ReportEndpoint.Devices),
                ReportDocuments.Registration(device.DeviceId, username, device._signer.PublicKey, Client), device._signer, Now);
            Assert.Equal("created", (await api.CallAsync(request)).Reply?.Result);
            return device;
        }

        public async Task<ReportReply> Post(ReportApi api, byte[] body)
        {
            var result = await api.CallAsync(Builder.Make("POST", Document.Api.Endpoint(ReportEndpoint.Reports), body, _signer, Now));
            Assert.True(result.HasReply, result.Error?.Reason);
            return result.Reply!;
        }

        public IReportStreamSocket Open(ReportStreamSocketTests.EventLog events) =>
            new WebSocketReportTransport().Open(ReportWebSocketProtocol.Handshake(Builder, Document.Api, _signer, Now), Document.WebSocket!, events.Add);

        public string Message(byte[] body) =>
            ReportWebSocketProtocol.ReportMessage(Builder.Make("POST", Document.Api.Endpoint(ReportEndpoint.Reports), body, _signer, Now).Headers, body);
    }
}
