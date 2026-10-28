using AgentSpeed.Core.Reporting;
using AgentSpeed.Core.Traffic;

namespace AgentSpeed.Core.Tests.Reporting;

/// <summary>
/// The client against spec/tools/report_server.py on loopback (REPORTING 12.1): the real HttpClient transport, signing,
/// bodies and reply handling. Never reaches beyond 127.0.0.1.
/// </summary>
[Collection("report server")]
public sealed class ReportEndToEndTests
{
    private static readonly ReportClientInfo Client = new("1.3.0", "Windows", 11);

    private sealed class Device(ReportApi api, ReportDiscovery document)
    {
        public ReportDiscovery Document { get; } = document;

        public IReportSigner Signer { get; set; } = new SoftwareReportSigner();

        public string DeviceId { get; } = ReportCanonical.NewUuid();

        public string? ApiKey { get; set; }

        public string RunId { get; } = ReportCanonical.NewUuid();

        public int Seq { get; private set; }

        public async Task<ReportReply> Send(string method, string endpoint, byte[] body, IReportSigner? signer = null, IReportSigner? newKey = null,
            long skew = 0)
        {
            var builder = new ReportRequestBuilder(Client.UserAgent, DeviceId, ApiKey);
            var request = builder.Make(method, Document.Api.Endpoint(endpoint), body, signer ?? Signer,
                DateTimeOffset.UtcNow.ToUnixTimeSeconds() + skew, newKey: newKey);
            var result = await api.CallAsync(request);
            Assert.True(result.HasReply, result.Error?.Reason);
            return result.Reply!;
        }

        public Task<ReportReply> Register(string username, bool transfer = false) =>
            Send("POST", ReportEndpoint.Devices, ReportDocuments.Registration(DeviceId, username, Signer.PublicKey, Client, transfer));

        public async Task<(ReportReply Reply, byte[] Body)> Report(string username, ReportActivity activity = ReportActivity.Active, IReportSigner? signer = null)
        {
            Seq++;
            var body = Body(username, Seq, activity, Document.Sections);
            return (await Send("POST", ReportEndpoint.Reports, body, signer), body);
        }

        public byte[] Body(string username, int seq, ReportActivity activity, IReadOnlySet<ReportSection> sections)
        {
            var counters = new ReportCounters();
            counters.Add(new ReportUsage(2, 640, 90_000, 80_000, 0, 0, 600, 1500, 4200), new ReportModelKey("gpt-5.5", SourceKind.Codex));
            var facts = new ReportLiveFacts(
                new Dictionary<SourceKind, ReportAgentCounts> { [SourceKind.Codex] = new(activity == ReportActivity.Active ? 1 : 0, 0, 1) },
                activity == ReportActivity.Active ? new Dictionary<ReportModelKey, int> { [new("gpt-5.5", SourceKind.Codex)] = 1 } : new Dictionary<ReportModelKey, int>(),
                counters, 1);
            var sample = new ReportSample(new Rate(128.4, 18_000), 0.4, new LiveCost(900, 128.4),
                new Dictionary<SourceKind, ReportSourceSample> { [SourceKind.Codex] = new(new Rate(128.4, 18_000), Rate.Zero, new LiveCost(900, 128.4)) },
                facts, ReportTrafficSample.Unavailable);
            var envelope = new ReportEnvelope(Client, DeviceId, username, RunId, seq, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), 5000, activity, 6);
            var baseline = ReportSample.Empty with { Facts = ReportLiveFacts.Empty with { Epoch = 1 } };
            return ReportBody.Data(envelope, new ReportWindow(baseline).Content(sample), sample,
                new ReportOptions(sections, Document.Limits.MaxModels, Document.Limits.SendLimitBytes));
        }
    }

    private static async Task<(Device Device, ReportApi Api)> Connect(ReferenceServer server, HttpReportTransport transport)
    {
        var api = new ReportApi(transport, Client.UserAgent);
        var outcome = await api.DiscoverAsync(server.Service);
        var document = Assert.IsType<ReportDiscoveryOutcome.Document>(outcome);
        return (new Device(api, document.Discovery), api);
    }

    [Fact]
    public async Task Lifecycle()
    {
        if (!ReferencePython.Available)
        {
            return;
        }

        using var server = new ReferenceServer();
        using var transport = new HttpReportTransport();
        var (device, api) = await Connect(server, transport);
        Assert.True(device.Document.SupportsThisClient);
        Assert.True(device.Document.Api.IsLoopbackHttp);
        Assert.Equal(4, device.Document.Sections.Count);
        Assert.IsType<ReportDiscoveryOutcome.Document>(await api.DiscoverAsync(server.Service, "\"stale-etag\""));

        Assert.Equal("created", (await device.Register("小明的 Mac")).Result);
        Assert.Equal("exists", (await device.Register("小明的 Mac")).Result);
        for (var i = 0; i < 2; i++)
        {
            Assert.Equal("accepted", (await device.Report("小明的 Mac")).Reply.Result);
        }

        var (third, body) = await device.Report("小明的 Mac");
        Assert.Equal("accepted", third.Result);
        var duplicate = await device.Send("POST", ReportEndpoint.Reports, body);
        Assert.Equal("duplicate", duplicate.Result);

        var (limited, _) = await device.Report("小明的 Mac");
        Assert.Equal(429, limited.Status);
        var limitedOutcome = Assert.IsType<ReportAttemptOutcome.RateLimited>(ReportOutcome.Classify(limited, false).Outcome);
        Assert.InRange(limitedOutcome.Seconds, 5, 3600);

        var renamed = await device.Send("PATCH", ReportEndpoint.Device(device.DeviceId), ReportDocuments.Rename("zoe-studio"));
        Assert.Equal("updated", renamed.Result);
        Assert.Equal("zoe-studio", renamed.Username);
        await Task.Delay(4200);
        var (mismatch, _) = await device.Report("小明的 Mac");
        Assert.Equal("username_mismatch", mismatch.Code);
        Assert.Equal("zoe-studio", mismatch.ErrorUsername);
        Assert.Equal(new ReportReaction.Rename("zoe-studio"), ReportOutcome.Classify(mismatch, false).Reaction);

        var rotated = new SoftwareReportSigner();
        var rotation = await device.Send("PUT", ReportEndpoint.Key(device.DeviceId), ReportDocuments.Rotation(rotated.PublicKey), newKey: rotated);
        Assert.Equal("rotated", rotation.Result);
        await Task.Delay(4200);
        var (stale, _) = await device.Report("zoe-studio");
        Assert.Equal("signature_invalid", stale.Code);
        Assert.Equal((new ReportAttemptOutcome.Again(), new ReportReaction.UsePendingKey()), ReportOutcome.Classify(stale, true));
        device.Signer = rotated;
        Assert.Equal("accepted", (await device.Report("zoe-studio")).Reply.Result);

        var skewed = await device.Send("POST", ReportEndpoint.Reports,
            device.Body("zoe-studio", 99, ReportActivity.Active, new HashSet<ReportSection>()), skew: -400);
        Assert.Equal("timestamp_out_of_window", skewed.Code);
        var correction = Assert.IsType<ReportReaction.CorrectClock>(ReportOutcome.Classify(skewed, false).Reaction);
        Assert.InRange(Math.Abs(correction.ServerTime!.Value - DateTimeOffset.UtcNow.ToUnixTimeSeconds()), 0, 5);

        Assert.Equal("deleted", (await device.Send("DELETE", ReportEndpoint.Device(device.DeviceId), [])).Result);
        var (gone, _) = await device.Report("zoe-studio");
        Assert.Equal("unknown_device", gone.Code);
        Assert.Equal(new ReportReaction.ReRegister(), ReportOutcome.Classify(gone, false).Reaction);
        Assert.Contains("forget", server.Log, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ApiKeysAndLeaving()
    {
        if (!ReferencePython.Available)
        {
            return;
        }

        using var server = new ReferenceServer("--api-key-mode", "required", "--api-key", "secret-12345", "--charset", "ascii", "--no-spaces",
            "--max-length", "16", "--api-path", "/agent-speed");
        using var transport = new HttpReportTransport();
        var (device, _) = await Connect(server, transport);
        Assert.Equal(ReportApiKeyMode.Required, device.Document.ApiKeyMode);
        Assert.Equal(new ReportUsernameRules(2, 16, ReportUsernameCharset.Ascii, false), device.Document.UsernameRules);
        Assert.EndsWith("/agent-speed", device.Document.Api.Text, StringComparison.Ordinal);
        var missing = await device.Register("Zoes-MacBook");
        Assert.Equal("api_key_required", missing.Code);
        Assert.Equal(new ReportReaction.NeedsUser(ReportProblem.ApiKey), ReportOutcome.Classify(missing, false).Reaction);
        device.ApiKey = "secret-12345";
        var invalid = await device.Register("Zoe MBP");
        Assert.Equal("username_invalid", invalid.Code);
        Assert.Equal("spaces", invalid.Reason);
        Assert.Equal(ReportUsernameProblem.Spaces, ReportUsername.Validate("Zoe MBP", device.Document.UsernameRules));
        Assert.Equal("Zoes-MacBook-Pro", ReportUsername.Fit("Zoe’s MacBook Pro", device.Document.UsernameRules));
        Assert.Equal("created", (await device.Register("Zoes-MacBook")).Result);
        Assert.Equal("accepted", (await device.Report("Zoes-MacBook", ReportActivity.Leaving)).Reply.Result);
        device.ApiKey = "wrong-key-000";
        var (wrong, _) = await device.Report("Zoes-MacBook");
        Assert.Equal("api_key_invalid", wrong.Code);
        Assert.Contains("leaving", server.Log, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TransportFailuresAreClassified()
    {
        if (!ReferencePython.Available)
        {
            return;
        }

        int port;
        using (var server = new ReferenceServer())
        {
            port = server.Port;
        }

        using var transport = new HttpReportTransport();
        var api = new ReportApi(transport, Client.UserAgent);
        var closed = ReportServiceUrl.Parse("http://127.0.0.1:" + port.ToString(System.Globalization.CultureInfo.InvariantCulture))!;
        Assert.Equal(new ReportDiscoveryOutcome.Failed(ReportFailure.Unreachable), await api.DiscoverAsync(closed));

        using var running = new ReferenceServer();
        var missing = ReportServiceUrl.Parse(running.Service.Text + "/nothing-here")!;
        Assert.IsType<ReportDiscoveryOutcome.NotSupported>(await api.DiscoverAsync(missing));
    }
}

[CollectionDefinition("report server", DisableParallelization = true)]
public sealed class ReportServerCollection
{
}
