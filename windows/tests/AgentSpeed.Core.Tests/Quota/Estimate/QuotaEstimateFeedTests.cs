using System.Text;
using AgentSpeed.Core.History;
using AgentSpeed.Core.IO.Sqlite;
using AgentSpeed.Core.Monitoring;
using AgentSpeed.Core.Parsing;
using AgentSpeed.Core.Pricing;
using AgentSpeed.Core.Quota;
using AgentSpeed.Core.Quota.Estimate;
using AgentSpeed.Core.Quota.Network;
using AgentSpeed.Core.Remote;

namespace AgentSpeed.Core.Tests.Quota.Estimate;

/// <summary>
/// The quota estimate's feeds on Windows (QUOTAS 10.1–10.4): the history's per-file share and its store, the live engine's
/// responses and Codex's lines, the sign-in keys, Desktop's organization, the relay's account, plan, tier and salt, and the
/// estimator's builds, displays, entries and private files.
/// </summary>
public sealed class QuotaEstimateFeedTests : IDisposable
{
    private const long Minute = 60_000;
    private const long Hour = 60 * Minute;
    private const long Dollar = 1_000_000;
    private const long Now = CodexLines.T0 + 4 * Hour;
    private static readonly byte[] Salt = [.. Enumerable.Range(1, 32).Select(i => (byte)i)];

    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    // MARK: History share

    [Fact]
    public void AShareRoundTripsThroughItsStoredForm()
    {
        var window = QuotaWindow.Of(QuotaWindowKind.FiveHour, 12.5, Now + Hour);
        var share = new EstimateFileShare(
        [
            new EstimateResponse(SourceKind.Codex, "f", Now - 30_000, Now, "gpt-5.5", 1_234, 900, "openai"),
            new EstimateResponse(SourceKind.ClaudeCode, null, null, Now, "claude-opus-5-5", null, 77, CloudRoute: true, Requests: 3),
        ], [new CodexTokenLine("f", Now + 1_500, [window, QuotaWindow.Of(QuotaWindowKind.Other, 4, null, minutes: 90)], "pro")]);

        var back = EstimateFileShare.Decode(share.Encode(), "f");
        Assert.NotNull(back);
        Assert.Equal(share.Responses, back.Responses);
        var line = Assert.Single(back.Lines);
        Assert.Equal(("f", Now + 1_500, "pro"), (line.File, line.Ts, line.PlanType));
        Assert.Equal(share.Lines[0].Windows, line.Windows);
        Assert.Same(EstimateFileShare.Empty, EstimateFileShare.Decode([], "f"));
        Assert.Null(EstimateFileShare.Decode("{\"r\":[[1]]}"u8, "f"));
    }

    [Fact]
    public void PerMinuteSumsEverySourceButCodexWhichStaysWhole()
    {
        var minute = SpendLedger.MinuteOf(Now);
        IReadOnlyList<EstimateResponse> rows = EstimateSpend.PerMinute(
        [
            new(SourceKind.ClaudeCode, "a", minute + 10_000, minute + 20_000, "claude-opus-5-5", 100, 10),
            new(SourceKind.ClaudeCode, "b", minute + 5_000, minute + 50_000, "claude-opus-5-5", 50, 5),
            new(SourceKind.ClaudeCode, "b", minute + 5_000, minute + 51_000, "claude-opus-5-5", null, 7),
            new(SourceKind.Codex, "c", minute, minute + 1_000, "gpt-5.5", 1, 1),
            new(SourceKind.Codex, "c", minute, minute + 2_000, "gpt-5.5", 2, 2),
        ]);
        Assert.Equal(4, rows.Count);
        Assert.Equal(2, rows.Count(r => r.Source == SourceKind.Codex && r.File == "c"));
        var priced = rows.Single(r => r.Source == SourceKind.ClaudeCode && r.CostMicroUsd is not null);
        Assert.Equal((150L, 15L, 2L, minute + 5_000, minute + 50_000, (string?)null), (priced.CostMicroUsd!.Value, priced.Tokens, priced.Requests, priced.StartMs!.Value, priced.EndMs, priced.File));
        Assert.Equal((7L, 1L), rows.Where(r => r.Source == SourceKind.ClaudeCode && r.CostMicroUsd is null).Select(r => (r.Tokens, r.Requests)).Single());
    }

    [Fact]
    public void TheSpendTakesThePriceTablesIdAndTheCloudRoute()
    {
        var spend = new EstimateSpend(() => PriceBook.Bundled);
        var codex = spend.Of(new ParsedResponse("k1", SourceKind.Codex, "c", "gpt-5.5", Now - 1000, Now, new TokenCounts(Input: 10, Output: 5),
            Provider: "azure"), 42);
        Assert.Equal(("c", "azure", 15L, 42L), (codex!.File, codex.Provider, codex.Tokens, codex.CostMicroUsd!.Value));
        var bedrock = spend.Of(new ParsedResponse("k2", SourceKind.ClaudeCode, "c", "us.anthropic.claude-opus-4-1-20250805-v1:0", null, Now, default), null);
        Assert.True(bedrock!.CloudRoute);
        Assert.False(spend.Of(new ParsedResponse("k3", SourceKind.ClaudeCode, "c", "claude-opus-4-1", null, Now, default), null)!.CloudRoute);
        Assert.Null(spend.Of(new ParsedResponse("k4", SourceKind.OpenCode, "c", "gpt-5.5", null, Now, default), 1));
        var remote = spend.Of(new ParsedResponse("k5", SourceKind.Kimi, "c", "kimi-k2", null, Now, default) { Host = "H" }, 1);
        Assert.Equal(("H", (string?)null), (remote!.Host, remote.File));
    }

    [Fact]
    public void TheHistoryKeepsACodexFilesShareAndReadsItBackWithoutAnalysingAgain()
    {
        var file = WriteCodexRollout("2026/10/20/rollout-a.jsonl", 3);
        var (report, share) = Analyse();
        Assert.Equal(1, report.FilesAnalyzed);
        var codex = share.Files[file];
        Assert.Equal(3, codex.Responses.Count);
        Assert.All(codex.Responses, r => Assert.Equal((SourceKind.Codex, file), (r.Source, r.File)));
        Assert.Equal(3, codex.Lines.Count);
        Assert.All(codex.Lines, l => Assert.Equal("pro", l.PlanType));
        Assert.Equal(EstimateHistory.Cut(Now), share.CutMs);

        var (again, stored) = Analyse();
        Assert.Equal(0, again.FilesAnalyzed);
        Assert.Equal(codex.Responses, stored.Files[file].Responses);
        Assert.Equal(codex.Lines.Select(l => l.Ts), stored.Files[file].Lines.Select(l => l.Ts));
    }

    [Fact]
    public void ARecentFileWithoutAShareIsAnalysedAgainOnceAndAnOldOneIsNot()
    {
        var file = WriteCodexRollout("2026/10/20/rollout-b.jsonl", 2);
        Analyse();
        using (var connection = SqliteConnection.Open(StorePath, SqliteOpenMode.ReadWriteCreate))
        {
            connection.Execute("DELETE FROM estimate_files");
        }

        var (recent, share) = Analyse();
        Assert.Equal(1, recent.FilesAnalyzed);
        Assert.Equal(2, share.Files[file].Responses.Count);

        using (var connection = SqliteConnection.Open(StorePath, SqliteOpenMode.ReadWriteCreate))
        {
            connection.Execute("DELETE FROM estimate_files");
        }

        var (late, _) = Analyse(Now + 9 * EstimateLimits.Day);
        Assert.Equal(0, late.FilesAnalyzed);
    }

    // MARK: Sign-in keys and Desktop's organization

    [Fact]
    public void TheSignInReadsOnlyTheIdsAndTurnsThemIntoKeys()
    {
        var auth = _temp.WriteFile("codex/auth.json",
            """{"auth_mode":"chatgpt","OPENAI_API_KEY":null,"tokens":{"id_token":"eyJ.secret","access_token":"at-secret","refresh_token":"rt-secret","account_id":"acct-123"},"last_refresh":"x"}""");
        var claude = _temp.WriteFile("claude/.claude.json",
            """{"numStartups":3,"oauthAccount":{"accountUuid":"user-1","emailAddress":"me@example.com","organizationUuid":"org-1","organizationType":"claude_max","organizationRateLimitTier":"default_claude_max_20x"},"projects":{}}""");
        var signIn = new EstimateSignIn(Salt, auth, claude);
        Assert.True(signIn.RefreshCodex(Now));
        Assert.Equal(AccountKeys.Key(Salt, "codex", "acct-123"), signIn.CodexKey);
        Assert.True(signIn.RefreshClaude(Now));
        Assert.Equal(new ClaudeProfile(Now, AccountKeys.Key(Salt, "claude", "org-1:user-1"), AccountKeys.Key(Salt, "claudeDesktop", "org-1"), "claude_max",
            "default_claude_max_20x"), signIn.ClaudeProfile);

        // Unchanged files are not read again; a change within the minute waits for the next one.
        Assert.False(signIn.RefreshCodex(Now + 5_000));
        File.WriteAllText(auth, """{"tokens":{"account_id":"acct-456"}}""");
        File.SetLastWriteTimeUtc(auth, DateTime.UtcNow.AddMinutes(1));
        Assert.False(signIn.RefreshCodex(Now + 10_000));
        Assert.True(signIn.RefreshCodex(Now + QuotaSignIn.RereadMs + 10_000));
        Assert.Equal(AccountKeys.Key(Salt, "codex", "acct-456"), signIn.CodexKey);

        Assert.Null(EstimateSignIn.CodexAccountId("""{"account_id":"top-level","tokens":{"access_token":"x"}}"""u8));
        Assert.Null(EstimateSignIn.ClaudeAccount("""{"oauthAccount":{"organizationUuid":"org-1"}}"""u8));
        Assert.Equal(("o", "a", (string?)"Not A Plan", (string?)null), EstimateSignIn.ClaudeAccount("""{"oauthAccount":{"organizationUuid":"o","accountUuid":"a","organizationType":"Not A Plan"}}"""u8));
    }

    [Fact]
    public void AnUnreadablePlanBecomesNullInTheProfile()
    {
        var claude = _temp.WriteFile("claude/.claude.json", """{"oauthAccount":{"organizationUuid":"o","accountUuid":"a","organizationType":"Max Plan!"}}""");
        var signIn = new EstimateSignIn(Salt, _temp.Combine("none", "auth.json"), claude);
        Assert.True(signIn.RefreshClaude(Now));
        Assert.Null(signIn.ClaudeProfile!.Plan);
        Assert.True(signIn.RefreshCodex(Now));
        Assert.Null(signIn.CodexKey);
        Assert.False(signIn.RefreshCodex(Now + 1));
    }

    [Fact]
    public void DesktopsOrganizationComesFromItsUsageUrl()
    {
        Assert.Equal("0f0e0d0c-0b0a-4908-8706-050403020100",
            DesktopCacheEntry.OrganizationOf("https://claude.ai/api/organizations/0F0E0D0C-0B0A-4908-8706-050403020100/usage"));
        Assert.Null(DesktopCacheEntry.OrganizationOf("https://claude.ai/api/organizations/0f0e0d0c-0b0a-4908-8706-050403020100/members"));
    }

    // MARK: Relay

    [Fact]
    public void TheRelaysAccountPlanAndTierAreReadAndOtherFormsCountAsUnknown()
    {
        var line = """{"t":"quota","q":{"source":"codex","observedMs":1794338773000,"windows":[],"running":true,"signedIn":true,"status":"ok","account":"0123456789abcdef","plan":"promax","tier":null}}""";
        var report = Assert.IsType<RelayMessage.Quota>(Tests.Remote.RemoteHistoryLines.Message(line, "H")).Value;
        Assert.Equal(("0123456789abcdef", "promax", (string?)null), (report.Account, report.Plan, report.Tier));

        var odd = line.Replace("0123456789abcdef", "0123456789ABCDEF", StringComparison.Ordinal).Replace("\"promax\"", "\"Pro Max\"", StringComparison.Ordinal);
        var unknown = Assert.IsType<RelayMessage.Quota>(Tests.Remote.RemoteHistoryLines.Message(odd, "H")).Value;
        Assert.Equal(((string?)null, (string?)null), (unknown.Account, unknown.Plan));

        var older = """{"t":"quota","q":{"source":"codex","observedMs":1794338773000,"windows":[],"running":true,"signedIn":true,"status":"ok"}}""";
        Assert.Null(Assert.IsType<RelayMessage.Quota>(Tests.Remote.RemoteHistoryLines.Message(older, "H")).Value.Account);
    }

    [Fact]
    public void TheQuotaConfigCarriesTheSaltWhichNoTextShows()
    {
        var salt = RemoteSalt.Of(Salt)!;
        var host = new RemoteHost("devbox", "A0000000-0000-4000-8000-000000000001");
        var config = RemoteQuotaConfig.ForHost(host, new RemoteQuotaSwitches(SourceSet.Of(SourceKind.Codex)) { Salt = salt }, networkEnabled: true);
        var text = Encoding.UTF8.GetString(RelayLineEncoder.Quota(config)!);
        Assert.Contains("\"salt\":\"" + Convert.ToHexStringLower(Salt) + "\"", text, StringComparison.Ordinal);
        Assert.DoesNotContain(salt.Hex, config.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(salt.Hex, new RemoteQuotaSwitches { Salt = salt }.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("salt", Encoding.UTF8.GetString(RelayLineEncoder.Quota(config with { Salt = null })!), StringComparison.Ordinal);
        Assert.Null(RemoteSalt.Of(new byte[16]));
    }

    // MARK: Estimator

    [Fact]
    public void TheEstimatorShowsALiveCodexWindowFromTheLiveEngineAndItsLines()
    {
        var estimator = new QuotaEstimator(_temp.Combine("data"), null, clock: () => Now);
        estimator.SetEnabled([SourceKind.Codex]);
        var (file, r, start) = ("C:/codex/rollout-live.jsonl", Now + Hour, Now - 3 * Hour);
        for (var i = 1; i <= 4; i++)
        {
            var end = start + i * 20 * Minute;
            estimator.Spent(new ParsedResponse("k" + i, SourceKind.Codex, file, "gpt-5.5", end - 20_000, end, new TokenCounts(Input: 100, Output: 10),
                CostMicroUsd: Dollar));
            estimator.CodexLine(new CodexTokenLine(file, end + 1_500, [QuotaWindow.Of(QuotaWindowKind.FiveHour, 10 * i, r + Hour)], "pro"));
        }

        Assert.True(estimator.BuildIfDue());
        Assert.False(estimator.BuildIfDue());
        var windowId = EstimateNames.WindowId(QuotaWindowKind.FiveHour, null, 300);
        var shown = estimator.Display(SourceKind.Codex, null, windowId, Now);
        Assert.NotNull(shown);
        Assert.Equal(4 * Dollar, shown.UsedMicroUsd);
        Assert.Equal("pro", shown.Plan);
        Assert.True(shown.CapMicroUsd is >= 7 * Dollar and <= 11 * Dollar, shown.CapMicroUsd?.ToString());
        Assert.Null(estimator.Display(SourceKind.ClaudeCode, null, windowId, Now));

        var entry = Assert.Single(estimator.Entries(Now, _ => true));
        Assert.Equal((SourceKind.Codex, "pro", 40.0), (entry.Source, entry.Plan, entry.UsedPercent!.Value));
        Assert.Empty(estimator.Entries(Now, s => s != SourceKind.Codex));
    }

    [Fact]
    public void TheHistorysSpendStopsAtItsCutWhereTheLiveEngineTakesOver()
    {
        var estimator = new QuotaEstimator(null, null, clock: () => Now);
        estimator.SetEnabled([SourceKind.Codex]);
        var (file, r) = ("C:/codex/rollout-cut.jsonl", Now + 2 * Hour);
        var window = QuotaWindow.Of(QuotaWindowKind.FiveHour, 30, r);
        var early = new EstimateResponse(SourceKind.Codex, file, Now - 2 * Hour - 20_000, Now - 2 * Hour, "gpt-5.5", 3 * Dollar, 10);
        var late = new EstimateResponse(SourceKind.Codex, file, Now - 20 * Minute, Now - 20 * Minute + 10_000, "gpt-5.5", 5 * Dollar, 10);
        var share = new EstimateFileShare([early, late],
            [new CodexTokenLine(file, early.EndMs + 1_000, [window with { UsedPercent = 10 }], "plus"),
             new CodexTokenLine(file, late.EndMs + 1_000, [window], "plus")]);
        estimator.HistoryUpdated(new EstimateHistory(new Dictionary<string, EstimateFileShare> { [file] = share }, Now - Hour));

        // The later response is the live engine's to give; before the cut the live engine's copy of the earlier one is ignored.
        estimator.Spent(new ParsedResponse("old", SourceKind.Codex, file, "gpt-5.5", early.StartMs, early.EndMs, default, CostMicroUsd: 3 * Dollar));
        Assert.True(estimator.BuildIfDue());
        var windowId = EstimateNames.WindowId(window);
        Assert.Equal(3 * Dollar, estimator.Display(SourceKind.Codex, null, windowId, Now)!.UsedMicroUsd);
    }

    [Fact]
    public void TheStoreAndTheSaltAreWrittenPrivatelyAndReadBackWithoutIds()
    {
        var data = _temp.Combine("data");
        var claude = _temp.WriteFile("claude/.claude.json",
            """{"oauthAccount":{"organizationUuid":"org-secret","accountUuid":"user-secret","organizationType":"claude_max"}}""");
        var clock = Now;
        var first = new QuotaEstimator(data, null, (_temp.Combine("none", "auth.json"), claude), () => clock);
        first.SetEnabled([SourceKind.ClaudeCode]);
        first.RefreshSignIns(Now, codex: false, claude: true);
        var snapshot = new QuotaSnapshot(SourceKind.ClaudeCode, Now - Minute, [QuotaWindow.Of(QuotaWindowKind.SevenDay, 42, Now + 50 * Hour)]);
        first.Snapshot(snapshot);
        first.Snapshot(snapshot);
        first.Flush();

        var store = File.ReadAllText(Path.Combine(data, "quota", "estimate.json"));
        Assert.DoesNotContain("secret", store, StringComparison.Ordinal);
        Assert.Contains(AccountKeys.Key(File.ReadAllBytes(Path.Combine(data, "quota", "estimate-salt")), "claude", "org-secret:user-secret"), store,
            StringComparison.Ordinal);
        var stored = EstimateStore.Read(Path.Combine(data, "quota", "estimate.json"))!;
        Assert.Single(stored.Observations);
        Assert.Single(stored.Keys);

        clock += Hour;
        var second = new QuotaEstimator(data, null, clock: () => clock);
        Assert.Equal(first.Salt, second.Salt);
        Assert.True(second.BuildIfDue());
        Assert.Single(second.Latest!.Model.Observations);
    }

    [Fact]
    public void EqualValuesKeepTheFirstAndLastOfTheirRun()
    {
        var estimator = new QuotaEstimator(null, null, clock: () => Now);
        estimator.SetEnabled([SourceKind.Kimi]);
        for (var i = 0; i < 10; i++)
        {
            estimator.Snapshot(new QuotaSnapshot(SourceKind.Kimi, Now - (10 - i) * Minute, [QuotaWindow.Of(QuotaWindowKind.FiveHour, i < 6 ? 5 : 7, Now + Hour)]));
        }

        Assert.True(estimator.BuildIfDue());
        Assert.Equal([Now - 10 * Minute, Now - 5 * Minute, Now - 4 * Minute, Now - Minute],
            estimator.Latest!.Model.Observations.Select(o => o.T).Order());
    }

    [Fact]
    public void AHostsLineFeedsTheHostsAccount()
    {
        var estimator = new QuotaEstimator(null, null, clock: () => Now);
        var window = QuotaWindow.Of(QuotaWindowKind.SevenDay, 20, Now + 50 * Hour);
        estimator.HostQuota(new RemoteQuotaReport("H", new QuotaSnapshot(SourceKind.Kimi, Now - Minute, [window]), true, true, QuotaRouteStatus.Ok(Now)));
        estimator.Spent(new ParsedResponse("hk", SourceKind.Kimi, "a", "kimi-k2", Now - 2 * Hour, Now - 2 * Hour + 5_000, default, CostMicroUsd: 2 * Dollar)
        {
            Host = "H",
        });
        Assert.True(estimator.BuildIfDue());
        Assert.Equal(2 * Dollar, estimator.Display(SourceKind.Kimi, "H", EstimateNames.WindowId(window), Now)!.UsedMicroUsd);
        Assert.Null(estimator.Display(SourceKind.Kimi, null, EstimateNames.WindowId(window), Now));
    }

    // MARK: Helpers

    private string StorePath => _temp.Combine("store", "history.sqlite");

    private SourceLocations Locations => new(_temp.Combine("codex", "sessions"), _temp.Combine("claude", "projects"), _temp.Combine("dsh", "sessions"),
        _temp.Combine("opencode"), []);

    private (HistoryReport Report, EstimateHistory Share) Analyse(long now = Now)
    {
        using var store = HistoryStore.Open(StorePath);
        var analyzer = new HistoryAnalyzer(store, new HistoryOptions { NowMs = () => now });
        var report = analyzer.Run(Locations, new HashSet<SourceKind> { SourceKind.Codex }, TimeZoneInfo.Utc, CancellationToken.None);
        return (report, analyzer.EstimateHistory!);
    }

    /// <summary>A rollout of <paramref name="turns"/> turns 10 minutes apart, each token count with a 5-hour window of the <c>pro</c> plan.</summary>
    private string WriteCodexRollout(string relative, int turns)
    {
        var t = CodexLines.T0;
        var reset = (t + 5 * Hour) / 1000;
        var lines = new StringBuilder().Append(CodexLines.SessionMeta(t)).Append('\n').Append(CodexLines.TurnContext(t, "gpt-5.5")).Append('\n');
        for (var i = 1; i <= turns; i++)
        {
            var at = t + i * 10 * Minute;
            var usage = CodexLines.Usage(100 * i, 0, 10 * i, 0);
            var last = CodexLines.Usage(100, 0, 10, 0);
            lines.Append(CodexLines.TaskStarted(at - 30_000)).Append('\n');
            lines.Append(CodexLines.Envelope(at, "event_msg",
                $$"""{"type":"token_count","info":{"total_token_usage":{{usage}},"last_token_usage":{{last}} },"rate_limits":{"limit_id":"codex","plan_type":"pro","primary":{"used_percent":{{5 * i}},"window_minutes":300,"resets_at":{{reset}} } } }""")).Append('\n');
        }

        var path = _temp.WriteFile("codex/sessions/" + relative, lines.ToString());
        File.SetLastWriteTimeUtc(path, DateTimeOffset.FromUnixTimeMilliseconds(t + turns * 10 * Minute).UtcDateTime);
        return path;
    }
}
