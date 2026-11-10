using System.Text.Json;
using AgentSpeed.Core.Quota;
using AgentSpeed.Core.Quota.Network;
using AgentSpeed.Core.Remote;
using L = AgentSpeed.Core.Tests.Remote.RelayLines;

namespace AgentSpeed.Core.Tests.Remote;

/// <summary>Quota lines for tests (the macOS <c>RemoteQuotaTests.quotaLine</c>).</summary>
internal static class QuotaLines
{
    public static Dictionary<string, object?> Window(
        string kind = "fiveHour", string? name = null, long? minutes = 300, double used = 25.5, long? resets = L.BaseMs + 3_600_000) => new()
    {
        ["kind"] = kind, ["name"] = name, ["minutes"] = minutes, ["usedPercent"] = used, ["resetsAtMs"] = resets,
    };

    public static string Quota(
        string source = "kimi", long observed = L.BaseMs, string status = "ok", IEnumerable<Dictionary<string, object?>>? windows = null,
        bool running = true, bool signedIn = true) => L.Json(new()
    {
        ["t"] = "quota",
        ["q"] = new Dictionary<string, object?>
        {
            ["source"] = source, ["observedMs"] = observed, ["windows"] = (windows ?? [Window()]).ToList(), ["running"] = running,
            ["signedIn"] = signedIn, ["status"] = status,
        },
    });

    public static string Removed(string source) => L.Json(new() { ["t"] = "quotaRemoved", ["source"] = source });

    public static string Bridge(string state, string path = "~/.claude/settings.json") =>
        L.Json(new() { ["t"] = "quotaBridge", ["state"] = state, ["settingsPath"] = path });

    public static List<string?> Sources(JsonElement config) => [.. config.GetProperty("sources").EnumerateArray().Select(s => s.GetString())];
}

/// <summary>The relay quota protocol of REMOTE 11 on the app side (2.1, 2.4, 5.6, 6, 11.1, 11.3), with scripted relay lines.</summary>
public sealed class RemoteQuotaTests
{
    private static readonly RemoteQuotaConfig Bridged = new([SourceKind.Codex, SourceKind.ClaudeCode], true, false, true);

    private static RelayMessage Message(string line) => L.Message(line);

    private static void SameSources(SourceSet expected, SourceSet actual) => Assert.Equal(expected, actual);

    /// <summary>The invalid field: a window's last path component, else the whole name.</summary>
    private static string? InvalidField(string line) => L.Invalid(line) is { Field: var field }
        ? field.StartsWith("q.windows.", StringComparison.Ordinal) ? field[(field.LastIndexOf('.') + 1)..] : field
        : null;

    private static List<JsonElement> QuotaLinesWritten(FakeProcess run) => [.. run.WrittenLines.Where(l => l.GetProperty("t").GetString() == "quota")];

    // Decoding and validation (REMOTE 11.1, 5.6)

    [Fact]
    public void AQuotaLine()
    {
        var line = QuotaLines.Quota(windows:
        [
            QuotaLines.Window(), QuotaLines.Window("sevenDay", "Gemini", minutes: null, used: 103, resets: null),
            QuotaLines.Window("other", minutes: 1440, used: 0),
        ]);
        var report = Assert.IsType<RelayMessage.Quota>(Message(line)).Value;
        Assert.Equal(L.HostId, report.HostId);
        Assert.Equal(SourceKind.Kimi, report.Source);
        Assert.True(report.Running && report.SignedIn);
        Assert.Equal(QuotaRouteStatus.Ok(L.BaseMs), report.Status);
        Assert.Equal(L.BaseMs, report.Snapshot.ObservedMs);
        Assert.Equal(["5h", "1d", "Gemini 7d"], report.Snapshot.Windows.Select(w => QuotaWindows.Label(w)));
        Assert.Equal(new QuotaWindow(QuotaWindowKind.FiveHour, null, 300, 25.5, L.BaseMs + 3_600_000), report.Snapshot.Windows[0]);
        Assert.Equal(QuotaWindow.WeekMinutes, report.Snapshot.Windows[^1].Minutes);
        Assert.Null(report.Snapshot.Windows[^1].ResetsAtMs);
        Assert.Equal(103, report.Snapshot.Windows[^1].UsedPercent);

        var signedOut = Assert.IsType<RelayMessage.Quota>(Message(QuotaLines.Quota(running: false, signedIn: false, windows: []))).Value;
        Assert.False(signedOut.Running || signedOut.SignedIn);
        Assert.False(signedOut.Snapshot.HasWindows);
        var unnamed = Assert.IsType<RelayMessage.Quota>(Message(QuotaLines.Quota(windows: [QuotaLines.Window(name: "")]))).Value;
        Assert.Null(unnamed.Snapshot.Windows[0].Name);
    }

    [Theory]
    [InlineData("noCredential", QuotaStatusKind.NoCredential)]
    [InlineData("expired", QuotaStatusKind.Expired)]
    [InlineData("failed", QuotaStatusKind.Failed)]
    [InlineData("networkOff", QuotaStatusKind.NetworkOff)]
    [InlineData("noCertificates", QuotaStatusKind.NoCertificates)]
    [InlineData("notRunning", QuotaStatusKind.NotRunning)]
    public void StatusesBecomeRouteStatuses(string status, QuotaStatusKind kind) =>
        Assert.Equal(QuotaRouteStatus.Of(kind), Assert.IsType<RelayMessage.Quota>(Message(QuotaLines.Quota(status: status))).Value.Status);

    [Theory]
    [InlineData("grokBuild", QuotaHosts.Grok)]
    [InlineData("kimi", QuotaHosts.Kimi)]
    [InlineData("gemini", QuotaHosts.Gemini)]
    [InlineData("droid", QuotaHosts.Factory)]
    [InlineData("antigravity", QuotaHosts.Loopback)]
    public void ARefusalNamesTheSourcesVendor(string source, string host)
    {
        var report = Assert.IsType<RelayMessage.Quota>(Message(QuotaLines.Quota(source: source, status: "denied", windows: []))).Value;
        Assert.Equal(QuotaRouteStatus.Denied(host), report.Status);
        Assert.False(report.Snapshot.HasWindows);
    }

    [Fact]
    public void EveryRuleMakesTheLineInvalid()
    {
        Assert.Equal("q.windows", InvalidField(QuotaLines.Quota(windows: Enumerable.Range(0, 17).Select(_ => QuotaLines.Window()))));
        Assert.Equal("kind", InvalidField(QuotaLines.Quota(windows: [QuotaLines.Window("hourly")])));
        Assert.Equal("name", InvalidField(QuotaLines.Quota(windows: [QuotaLines.Window(name: new string('n', 65))])));
        Assert.Equal(RemoteLineErrorKind.BadCharacter, L.Invalid(QuotaLines.Quota(windows: [QuotaLines.Window(name: "Gem‮ini")]))?.Kind);
        Assert.Equal(RemoteLineErrorKind.BadCharacter, L.Invalid(QuotaLines.Quota(windows: [QuotaLines.Window(name: "a\nb")]))?.Kind);
        Assert.Equal(RemoteLineErrorKind.BadCharacter, L.Invalid(QuotaLines.Quota(windows: [QuotaLines.Window(name: "a\u0085b")]))?.Kind);
        Assert.Equal("minutes", InvalidField(QuotaLines.Quota(windows: [QuotaLines.Window(minutes: 0)])));
        Assert.Equal("minutes", InvalidField(QuotaLines.Quota(windows: [QuotaLines.Window(minutes: 527_041)])));
        Assert.Equal(RemoteLineErrorKind.Missing, L.Invalid(QuotaLines.Quota(windows: [QuotaLines.Window("other", minutes: null)]))?.Kind);
        Assert.Equal("usedPercent", InvalidField(QuotaLines.Quota(windows: [QuotaLines.Window(used: 1000.5)])));
        Assert.Equal("usedPercent", InvalidField(QuotaLines.Quota(windows: [QuotaLines.Window(used: -1)])));
        Assert.Equal("resetsAtMs", InvalidField(QuotaLines.Quota(windows: [QuotaLines.Window(resets: 1_000)])));
        Assert.Equal("q.observedMs", InvalidField(QuotaLines.Quota(observed: 5_000_000_000_000)));
        Assert.Equal("q.status", InvalidField(QuotaLines.Quota(status: "great")));
        Assert.Equal("q.running", InvalidField("""{"t":"quota","q":{"source":"kimi","observedMs":1780000000000,"windows":[],"signedIn":true,"status":"ok"}}"""));
        Assert.Equal("q", InvalidField("""{"t":"quota"}"""));

        var plain = QuotaLines.Quota();
        Assert.Equal("usedPercent", InvalidField(L.Replaced(plain, "\"usedPercent\":25.5", "\"usedPercent\":\"12\"")));
        Assert.Equal("usedPercent", InvalidField(L.Replaced(plain, "\"usedPercent\":25.5", "\"usedPercent\":1e400")));
        Assert.Equal("minutes", InvalidField(L.Replaced(plain, "\"minutes\":300", "\"minutes\":300.5")));
        Assert.Equal("q.signedIn", InvalidField(L.Replaced(plain, "\"signedIn\":true", "\"signedIn\":1")));
        Assert.Equal("q.windows", InvalidField(L.Replaced(plain, "\"windows\":[", "\"windows\":{\"x\":[").Replace("]", "]}", StringComparison.Ordinal)));

        // Exactly the limits are fine.
        var edge = QuotaLines.Quota(windows: Enumerable.Range(0, 16).Select(_ => QuotaLines.Window(name: new string('名', 64), minutes: 527_040, used: 1000)));
        Assert.Equal(16, Assert.IsType<RelayMessage.Quota>(Message(edge)).Value.Snapshot.Windows.Count);
        Assert.IsType<RelayMessage.Quota>(Message(QuotaLines.Quota(windows: [QuotaLines.Window("credits", minutes: null, resets: null, used: 0)])));
    }

    [Fact]
    public void SourcesAHostCannotReadAreIgnored()
    {
        foreach (var source in new[] { "claudeDesktop", "zcode", "devin", "qoder", "someday" })
        {
            Assert.Equal(RelayDecodeKind.Ignored, L.Decode(QuotaLines.Quota(source: source)).Kind);
            Assert.Equal(RelayDecodeKind.Ignored, L.Decode(QuotaLines.Removed(source)).Kind);
        }

        // The line is still validated first.
        Assert.Equal("q.status", InvalidField(QuotaLines.Quota(source: "zcode", status: "great")));
    }

    [Fact]
    public void RemovedAndBridgeLines()
    {
        Assert.Equal(new RelayMessage.QuotaRemoved(L.HostId, SourceKind.Gemini), Message(QuotaLines.Removed("gemini")));
        Assert.Equal("source", InvalidField("""{"t":"quotaRemoved"}"""));
        Assert.Equal(new RelayMessage.QuotaBridge(new RemoteQuotaBridge(L.HostId, ClaudeCliBridgeStatus.Waiting, "~/.claude/settings.json")),
            Message(QuotaLines.Bridge("waiting")));
        string[] states = ["off", "waiting", "ok", "replaced", "unreadable", "failed"];
        Assert.Equal(Enum.GetValues<ClaudeCliBridgeStatus>(),
            states.Select(s => Assert.IsType<RelayMessage.QuotaBridge>(Message(QuotaLines.Bridge(s))).Value.State));
        Assert.Equal("state", InvalidField(QuotaLines.Bridge("broken", "~")));
        Assert.Equal("settingsPath", InvalidField(QuotaLines.Bridge("ok", "~/‮")));
        Assert.Equal("settingsPath", InvalidField(QuotaLines.Bridge("ok", "~/" + new string('a', RemoteLimits.CwdBytes))));
        Assert.Equal("settingsPath", InvalidField("""{"t":"quotaBridge","state":"ok"}"""));
    }

    // Encoding (REMOTE 11.1)

    [Fact]
    public void StartAndConfigLines()
    {
        var config = new RemoteQuotaConfig([SourceKind.Kimi, SourceKind.Codex, SourceKind.ZCode, SourceKind.ClaudeDesktop], true, false, true);
        SameSources([SourceKind.Codex, SourceKind.Kimi], config.Sources);
        SameSources([SourceKind.Codex], (config with { Sources = [SourceKind.Codex, SourceKind.Qoder] }).Sources);
        Assert.Equal("""{"t":"quota","config":{"sources":["codex","kimi"],"network":true,"grokLive":false,"claudeBridge":true}}""" + "\n",
            System.Text.Encoding.UTF8.GetString(RelayLineEncoder.Quota(config)!));
        Assert.Equal("""{"t":"quota","config":{"sources":[],"network":false,"grokLive":false,"claudeBridge":false}}""" + "\n",
            System.Text.Encoding.UTF8.GetString(RelayLineEncoder.Quota(RemoteQuotaConfig.Off)!));
        var every = new RemoteQuotaConfig(SourceSet.Everything, true, true, false);
        Assert.Equal(["codex", "claudeCode", "gemini", "antigravity", "kimi", "grokBuild", "droid"],
            QuotaLines.Sources(Parse(RelayLineEncoder.Quota(every)).GetProperty("config")));

        var zone = RemoteZone.Current(TimeZoneInfo.Utc);
        var prices = RemotePriceTableJson.From("""{"schema":3}"""u8)!;
        var start = Parse(RelayLineEncoder.Start(1, [SourceKind.Codex], zone, prices, config));
        Assert.Equal(["t", "protocol", "todayStartMs", "sources", "zone", "prices", "quota"], start.EnumerateObject().Select(m => m.Name));
        Assert.True(start.GetProperty("quota").GetProperty("claudeBridge").GetBoolean());
        Assert.Equal(["codex", "kimi"], QuotaLines.Sources(start.GetProperty("quota")));
        Assert.False(Parse(RelayLineEncoder.Start(1, [SourceKind.Codex], zone, prices)).TryGetProperty("quota", out _));
    }

    [Fact]
    public void EachHostAddsItsOwnBridge()
    {
        var switches = new RemoteQuotaSwitches(
            [SourceKind.Codex, SourceKind.ClaudeCode, SourceKind.GrokBuild, SourceKind.Kimi, SourceKind.ZCode, SourceKind.Devin], GrokLive: true);
        var plain = new RemoteHost("devbox", L.HostId);
        var bridged = plain with { QuotaClaudeCli = true };

        var off = RemoteQuotaConfig.ForHost(plain, switches, networkEnabled: true);
        SameSources([SourceKind.Codex, SourceKind.Kimi, SourceKind.GrokBuild], off.Sources);
        Assert.True(off.GrokLive && off.Network && !off.ClaudeBridge);
        var on = RemoteQuotaConfig.ForHost(bridged, switches, networkEnabled: false);
        SameSources([SourceKind.Codex, SourceKind.ClaudeCode, SourceKind.Kimi, SourceKind.GrokBuild], on.Sources);
        Assert.True(on.ClaudeBridge && !on.Network);
        Assert.False(RemoteQuotaConfig.ForHost(plain, new RemoteQuotaSwitches([SourceKind.Codex], GrokLive: true), true).GrokLive);

        // A disabled source reads nothing on the host; the bridge stays the host's switch (QUOTAS 9.1).
        SourceSet codexAndClaude = [SourceKind.Codex, SourceKind.ClaudeCode];
        var disabled = RemoteQuotaConfig.ForHost(bridged, switches, true, codexAndClaude);
        SameSources(codexAndClaude, disabled.Sources);
        Assert.False(disabled.GrokLive);
        var noClaudeSource = RemoteQuotaConfig.ForHost(bridged, switches, true, SourceSet.Of(SourceKind.Codex));
        SameSources([SourceKind.Codex], noClaudeSource.Sources);
        Assert.True(noClaudeSource.ClaudeBridge);
        var claudeOff = RemoteQuotaConfig.ForHost(bridged, switches with { ClaudeCode = false }, true);
        Assert.False(claudeOff.Sources.Contains(SourceKind.ClaudeCode));
        Assert.True(claudeOff.ClaudeBridge);
    }

    [Fact]
    public void TheHostFieldRoundTripsAndIsLeftOutWhileOff()
    {
        var document = RemoteHostsJson.Parse($$"""{"version":1,"hosts":[{"id":"{{L.HostId}}","destination":"devbox","quotaClaudeCli":true,"future":1}]}""");
        var host = document.Hosts[0];
        Assert.True(host.QuotaClaudeCli);
        var written = Parse(RemoteHostsJson.ToJson(document)).GetProperty("hosts")[0];
        Assert.True(written.GetProperty("quotaClaudeCli").GetBoolean());
        Assert.Equal(1, written.GetProperty("future").GetInt32());
        Assert.Equal(document, RemoteHostsJson.Parse(RemoteHostsJson.ToJson(document)));

        var off = document.Updating(host with { QuotaClaudeCli = false })!;
        var offHost = Parse(RemoteHostsJson.ToJson(off)).GetProperty("hosts")[0];
        Assert.False(offHost.TryGetProperty("quotaClaudeCli", out _));
        Assert.Equal(1, offHost.GetProperty("future").GetInt32());
        Assert.NotEqual(host, off.Hosts[0]);
        Assert.Equal(host.ConnectionIdentity, off.Hosts[0].ConnectionIdentity);

        foreach (var value in new[] { "false", "\"yes\"", "1", "null" })
        {
            var parsed = RemoteHostsJson.Parse($$"""{"hosts":[{"id":"{{L.HostId}}","destination":"d","quotaClaudeCli":{{value}}}]}""");
            Assert.False(parsed.Hosts[0].QuotaClaudeCli);
            Assert.False(Parse(RemoteHostsJson.ToJson(parsed)).GetProperty("hosts")[0].TryGetProperty("quotaClaudeCli", out _));
        }

        Assert.False(RemoteHostsJson.Parse($$"""{"hosts":[{"id":"{{L.HostId}}","destination":"d"}]}""").Hosts[0].QuotaClaudeCli);
    }

    // A connection (REMOTE 11.1, 11.3, 6)

    [Fact]
    public void TheConfigGoesWithStartThenOnlyWhenItChanges()
    {
        var config = new RemoteQuotaConfig([SourceKind.Codex], true, false, false);
        using var harness = new ConnectionHarness(quota: config);
        var run = harness.Connect();
        Assert.Equal(["codex"], QuotaLines.Sources(run.WrittenLines[0].GetProperty("quota")));
        harness.Connection.UpdateQuota(config);
        harness.Settle();
        Assert.DoesNotContain("quota", run.WrittenTypes);
        harness.Connection.UpdateQuota(config with { Sources = [SourceKind.Codex, SourceKind.Kimi] });
        harness.Settle();
        var line = run.WrittenLines[^1];
        Assert.Equal("quota", line.GetProperty("t").GetString());
        Assert.Equal(["codex", "kimi"], QuotaLines.Sources(line.GetProperty("config")));
        Assert.Single(harness.Transport.Runs);

        using var plain = new ConnectionHarness();
        Assert.False(plain.Connect().WrittenLines[0].TryGetProperty("quota", out _));
    }

    [Fact]
    public void AConfigChangedBeforeTheRelayRunsGoesWithStart()
    {
        using var harness = new ConnectionHarness(quota: RemoteQuotaConfig.Off, probe: (_, _) => { });
        harness.Start();
        harness.Connection.UpdateQuota(Bridged);
        harness.Settle();
        var probe = harness.Transport.Launched[0];
        probe.SendStdout(ConnectionHarness.ProbeOutput(harness.Relays, installed: true));
        probe.Finish(0);
        harness.Settle();
        var run = harness.Run!;
        Assert.Equal(["start"], run.WrittenTypes);
        Assert.Equal(["codex", "claudeCode"], QuotaLines.Sources(run.WrittenLines[0].GetProperty("quota")));
        Assert.True(run.WrittenLines[0].GetProperty("quota").GetProperty("claudeBridge").GetBoolean());
    }

    [Fact]
    public void QuotaTimesBecomeLocalLikeEveryOtherTime()
    {
        using var harness = new ConnectionHarness { RemoteOffsetMs = 2_000 };
        var run = harness.Connect();
        var observed = harness.NowMs + 2_000;
        run.Send(QuotaLines.Quota(observed: observed, windows: [QuotaLines.Window(resets: observed + 60_000), QuotaLines.Window("credits", minutes: null, resets: null)]));
        run.Send(QuotaLines.Bridge("ok"));
        run.Send(QuotaLines.Removed("kimi"));
        harness.Settle();
        var messages = harness.Events.Messages.Select(m => m.Message).ToList();
        var report = Assert.Single(messages.OfType<RelayMessage.Quota>()).Value;
        Assert.Equal(observed - 2_000, report.Snapshot.ObservedMs);
        Assert.Equal(observed + 58_000, report.Snapshot.Windows[0].ResetsAtMs);
        Assert.Null(report.Snapshot.Windows[1].ResetsAtMs);
        Assert.Equal(QuotaRouteStatus.Ok(observed - 2_000), report.Status);
        Assert.Equal(new RemoteQuotaBridge(L.HostId, ClaudeCliBridgeStatus.Ok, "~/.claude/settings.json"),
            Assert.Single(messages.OfType<RelayMessage.QuotaBridge>()).Value);
        Assert.Equal(SourceKind.Kimi, Assert.Single(messages.OfType<RelayMessage.QuotaRemoved>()).Source);

        harness.Events.Clear();
        run.Send(QuotaLines.Quota(status: "failed", observed: observed));
        harness.Settle();
        Assert.Equal(QuotaRouteStatus.Of(QuotaStatusKind.Failed), Assert.IsType<RelayMessage.Quota>(Assert.Single(harness.Events.Messages).Message).Value.Status);
    }

    [Fact]
    public void InvalidQuotaLinesCountLikeAnyOther()
    {
        using var harness = new ConnectionHarness { RemoteOffsetMs = 10_000 };
        var run = harness.Connect();
        harness.Events.Clear();
        var early = QuotaLines.Quota(observed: RemoteFields.EarliestMs + 5_000);
        var resetEarly = QuotaLines.Quota(observed: L.BaseMs, windows: [QuotaLines.Window(resets: RemoteFields.EarliestMs + 5_000)]);
        for (var i = 0; i < 100; i++)
        {
            run.Send((i % 3) switch { 0 => QuotaLines.Quota(windows: [QuotaLines.Window("nope")]), 1 => early, _ => resetEarly });
        }

        for (var i = 0; i < 200; i++)
        {
            run.Send(QuotaLines.Quota(source: "zcode"));
        }

        harness.Settle();
        Assert.False(run.WasTerminated);
        Assert.Empty(harness.Events.Messages);
        run.Send(QuotaLines.Bridge("nope"));
        harness.Settle();
        Assert.True(run.WasTerminated);
        Assert.Equal(RemoteStatus.ProtocolError, harness.Events.Statuses()[^1]);
    }

    [Fact]
    public void RetiringRestoresTheBridgeBeforeStopping()
    {
        using var harness = new ConnectionHarness(quota: Bridged);
        var run = harness.Connect();
        harness.Events.Clear();
        harness.Connection.Retire();
        harness.Settle();
        var line = run.WrittenLines[^1];
        Assert.Equal("quota", line.GetProperty("t").GetString());
        Assert.False(line.GetProperty("config").GetProperty("claudeBridge").GetBoolean());
        Assert.Equal(["codex"], QuotaLines.Sources(line.GetProperty("config")));
        Assert.False(run.IsInputClosed);

        // A retiring connection reports nothing: its host left the settings.
        run.Send(QuotaLines.Bridge("waiting"));
        run.Send(L.Agent());
        harness.Settle();
        Assert.False(run.IsInputClosed);
        run.Send(QuotaLines.Bridge("off") + "\n" + L.Agent());
        harness.Settle();
        Assert.True(run.IsInputClosed && run.WasTerminated);
        Assert.Empty(harness.Events.Events);
        harness.Advance(600);
        Assert.Single(harness.Transport.Runs);
        Assert.Empty(harness.Events.Events);
    }

    [Fact]
    public void ARetiringRelayThatNeverAnswersStopsAfterTenSeconds()
    {
        using var harness = new ConnectionHarness(quota: Bridged);
        var run = harness.Connect();
        harness.Connection.Retire();
        harness.Advance(9);
        Assert.False(run.IsInputClosed);
        harness.Advance(2);
        Assert.True(run.IsInputClosed && run.WasTerminated);
        harness.Advance(600);
        Assert.Single(harness.Transport.Runs);
    }

    [Fact]
    public void ARetiringRelayThatEndsIsNotStartedAgain()
    {
        using var harness = new ConnectionHarness(quota: Bridged);
        var run = harness.Connect();
        harness.Connection.Retire();
        harness.Settle();
        harness.Events.Clear();
        run.Finish(255);
        harness.Advance(600);
        Assert.Single(harness.Transport.Runs);
        Assert.Empty(harness.Events.Events);
    }

    [Fact]
    public void AConnectionThatIsNotRunningRetiresAtOnce()
    {
        using var probing = new ConnectionHarness(quota: Bridged, probe: (_, _) => { });
        probing.Start();
        probing.Connection.Retire();
        probing.Settle();
        Assert.True(probing.Transport.Launched[0].WasTerminated);
        probing.Advance(600);
        Assert.Single(probing.Transport.Launched);

        using var silent = new ConnectionHarness(quota: Bridged);
        silent.Start();
        var run = silent.Run!;
        silent.Connection.Retire();
        silent.Settle();
        Assert.True(run.IsInputClosed && run.WasTerminated);
        Assert.Equal(["start"], run.WrittenTypes);

        using var waiting = new ConnectionHarness(quota: Bridged, probe: (process, _) => process.Finish(255));
        waiting.Start();
        Assert.NotEmpty(waiting.Scheduling.Deadlines);
        waiting.Connection.Retire();
        waiting.Settle();
        waiting.Advance(3600);
        Assert.Single(waiting.Transport.Launched);
    }

    [Fact]
    public void ReapplyingSendsTheBridgeOffThenOn()
    {
        using var harness = new ConnectionHarness(quota: Bridged);
        var run = harness.Connect();
        harness.Connection.ReapplyQuota();
        harness.Settle();
        var lines = QuotaLinesWritten(run);
        Assert.Equal(2, lines.Count);
        Assert.False(lines[0].GetProperty("config").GetProperty("claudeBridge").GetBoolean());
        Assert.Equal(["codex"], QuotaLines.Sources(lines[0].GetProperty("config")));
        Assert.True(lines[1].GetProperty("config").GetProperty("claudeBridge").GetBoolean());
        Assert.Equal(["codex", "claudeCode"], QuotaLines.Sources(lines[1].GetProperty("config")));
        Assert.Single(harness.Transport.Runs);

        harness.Connection.UpdateQuota(Bridged with { ClaudeBridge = false });
        harness.Connection.ReapplyQuota();
        harness.Settle();
        Assert.Equal(3, QuotaLinesWritten(run).Count);
    }

    // The hub (REMOTE 2.4, 11.1)

    [Fact]
    public void TheHubSendsOnlyEnabledSources()
    {
        using var harness = new RemoteHubTests.Harness();
        harness.Update([new RemoteHost("alpha", RemoteHubTests.A)], sources: SourceSet.Of(SourceKind.Codex),
            quota: new RemoteQuotaSwitches([SourceKind.Codex, SourceKind.Kimi, SourceKind.Gemini]));
        var run = Assert.Single(harness.Runs("alpha"));
        Assert.Equal(["codex"], QuotaLines.Sources(run.WrittenLines[0].GetProperty("quota")));

        using var unset = new RemoteHubTests.Harness();
        SourceSet enabled = [SourceKind.Codex, SourceKind.ClaudeCode];
        unset.Update([new RemoteHost("alpha", RemoteHubTests.A) { QuotaClaudeCli = true }], sources: enabled);
        var quota = Assert.Single(unset.Runs("alpha")).WrittenLines[0].GetProperty("quota");
        Assert.Equal(["claudeCode"], QuotaLines.Sources(quota));
        Assert.True(quota.GetProperty("claudeBridge").GetBoolean());
    }

    [Fact]
    public void AHubSendsEachHostItsConfigAndRetiresARemovedBridge()
    {
        using var harness = new RemoteHubTests.Harness();
        var host = new RemoteHost("alpha", RemoteHubTests.A) { QuotaClaudeCli = true };
        var plain = new RemoteHost("beta", RemoteHubTests.B);
        SourceSet enabled = [SourceKind.Codex, SourceKind.ClaudeCode, SourceKind.Kimi];
        harness.Update([host, plain], sources: enabled, quota: new RemoteQuotaSwitches([SourceKind.Codex, SourceKind.Kimi]));
        var run = Assert.Single(harness.Runs("alpha"));
        var start = run.WrittenLines[0].GetProperty("quota");
        Assert.Equal(["codex", "claudeCode", "kimi"], QuotaLines.Sources(start));
        Assert.True(start.GetProperty("claudeBridge").GetBoolean());
        Assert.Equal(["codex", "kimi"], QuotaLines.Sources(Assert.Single(harness.Runs("beta")).WrittenLines[0].GetProperty("quota")));
        run.Send(L.Hello());
        run.Send(L.Ready);
        harness.Settle();

        harness.Update([host, plain], sources: enabled, quota: new RemoteQuotaSwitches([SourceKind.Codex]));
        Assert.Single(harness.Runs("alpha"));
        Assert.Equal(["codex", "claudeCode"], QuotaLines.Sources(run.WrittenLines[^1].GetProperty("config")));

        // The host's own switch never reconnects either.
        harness.Update([host with { QuotaClaudeCli = false }, plain], sources: enabled, quota: new RemoteQuotaSwitches([SourceKind.Codex]));
        Assert.False(run.WrittenLines[^1].GetProperty("config").GetProperty("claudeBridge").GetBoolean());
        harness.Update([host, plain], sources: enabled, quota: new RemoteQuotaSwitches([SourceKind.Codex]));
        Assert.True(run.WrittenLines[^1].GetProperty("config").GetProperty("claudeBridge").GetBoolean());
        Assert.Single(harness.Runs("alpha"));
        harness.Hub.ReapplyQuota(RemoteHubTests.A);
        harness.Settle();
        Assert.Equal([true, false, true], QuotaLinesWritten(run).TakeLast(3).Select(l => l.GetProperty("config").GetProperty("claudeBridge").GetBoolean()));

        var betaRun = harness.Runs("beta")[0];
        harness.Update([], sources: enabled);
        Assert.Equal([RemoteHubTests.A], harness.Hub.RetiringHostIds);
        Assert.Empty(harness.Hub.ConnectedHostIds);
        Assert.True(betaRun.IsInputClosed);
        Assert.False(run.IsInputClosed);
        Assert.False(run.WrittenLines[^1].GetProperty("config").GetProperty("claudeBridge").GetBoolean());
        run.Send(QuotaLines.Bridge("off"));
        harness.Settle();
        Assert.True(run.IsInputClosed && run.WasTerminated);
        harness.Scheduling.Advance(TimeSpan.FromSeconds(16));
        harness.Settle();
        Assert.Empty(harness.Hub.RetiringHostIds);
    }

    [Fact]
    public void ARemovedBridgeStopsAtTheLatestAfterItsLimit()
    {
        using var harness = new RemoteHubTests.Harness();
        harness.Update([new RemoteHost("alpha", RemoteHubTests.A) { QuotaClaudeCli = true }, new RemoteHost("unreachable", RemoteHubTests.B) { QuotaClaudeCli = true }]);
        var run = Assert.Single(harness.Runs("alpha"));
        run.Send(L.Hello());
        harness.Settle();
        var unreachable = harness.Transport.Launched.Count(p => p.Request.Arguments.Contains("unreachable"));
        harness.Update([]);
        Assert.Equal(new HashSet<string> { RemoteHubTests.A, RemoteHubTests.B }, harness.Hub.RetiringHostIds.ToHashSet());
        harness.Scheduling.Advance(TimeSpan.FromSeconds(9));
        harness.Settle();
        Assert.False(run.IsInputClosed);
        harness.Scheduling.Advance(TimeSpan.FromSeconds(2));
        harness.Settle();
        Assert.True(run.IsInputClosed);
        harness.Scheduling.Advance(TimeSpan.FromSeconds(600));
        harness.Settle();
        Assert.Empty(harness.Hub.RetiringHostIds);
        Assert.Equal(unreachable, harness.Transport.Launched.Count(p => p.Request.Arguments.Contains("unreachable")));
        Assert.Single(harness.Runs("alpha"));
    }

    private static JsonElement Parse(byte[]? line)
    {
        Assert.NotNull(line);
        return Parse(System.Text.Encoding.UTF8.GetString(line).TrimEnd('\n'));
    }

    private static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
