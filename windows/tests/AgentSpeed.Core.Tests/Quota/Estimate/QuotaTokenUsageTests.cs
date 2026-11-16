using System.Text.Json;
using AgentSpeed.Core.Parsing;
using AgentSpeed.Core.Pricing;
using AgentSpeed.Core.Quota;
using AgentSpeed.Core.Quota.Estimate;
using AgentSpeed.Core.Quota.Network;
using AgentSpeed.Core.Remote;
using AgentSpeed.Core.Reporting;
using static AgentSpeed.Core.Tests.Quota.Estimate.EstimateFixture;

namespace AgentSpeed.Core.Tests.Quota.Estimate;

/// <summary>
/// The token counts of each quota entry's own spend (QUOTAS 10.3 <i>Token counts</i>, REPORTING-EXTENSIONS 12): what each
/// source reports, the sums and entries the fixtures give, the spend's counts, the per-minute sums and the history's stored form.
/// </summary>
public sealed class QuotaTokenUsageTests
{
    private static JsonElement Cases => Root.GetProperty("tokenUsage");

    private static JsonElement Json(QuotaTokenUsage? usage) => Parse(ReportQuotas.TokenUsage(usage).ToCompactString());

    // Fixtures

    [Fact]
    public void EachSourceKeepsTheDetailsItsLogsReport()
    {
        var sources = Cases.GetProperty("sources");
        Assert.Equal(7, sources.GetArrayLength());
        foreach (var c in sources.EnumerateArray())
        {
            var u = c.GetProperty("usage");
            var usage = new TokenCounts(Input: u.GetProperty("input").GetInt64(), CachedInput: u.GetProperty("cachedInput").GetInt64(),
                CacheWriteInput: u.GetProperty("cacheWriteInput").GetInt64(), Output: u.GetProperty("output").GetInt64(),
                Reasoning: u.GetProperty("reasoning").GetInt64());
            var got = QuotaTokenCounts.Of(Source(c.GetProperty("source").GetString()), usage);
            Assert.True(Counts(c, "expected") == got, c.GetProperty("why").GetString());
        }
    }

    [Fact]
    public void SumsMatchTheReference()
    {
        var ledger = LedgerFrom(Cases.GetProperty("rows"));
        var sums = Cases.GetProperty("sums");
        Assert.Equal(9, sums.GetArrayLength());
        foreach (var c in sums.EnumerateArray())
        {
            var got = ledger.TokenUsage(c.GetProperty("accounts")[0].GetString()!, OptionalString(c, "window"), c.GetProperty("a").GetInt64(),
                c.GetProperty("b").GetInt64());
            AssertSame(c.GetProperty("expected"), Json(got), c.GetProperty("name").GetString()!);
        }
    }

    [Fact]
    public void EntriesMatchTheReference()
    {
        var ledger = LedgerFrom(Cases.GetProperty("rows"));
        var input = Cases.GetProperty("input");
        var basis = QuotaEstimateFixtureTests.Basis(input, QuotaEstimateFixtureTests.EstimateOf(input.GetProperty("estimate")));
        var tag = AccountKeys.ReportTag(Cases.GetProperty("origin").GetString()!, input.GetProperty("account").GetString()!);
        var entries = Cases.GetProperty("entries");
        Assert.Equal(2, entries.GetArrayLength());
        foreach (var c in entries.EnumerateArray())
        {
            var entry = QuotaEntryBuilder.Build(basis, tag, c.GetProperty("now").GetInt64(), ledger, 8);
            AssertSame(c.GetProperty("expected"), EntryJson(entry, tokenUsage: true), c.GetProperty("name").GetString()!);
            Assert.False(EntryJson(entry).TryGetProperty("tokenUsage", out _), "no member without the extension");
            Assert.Equal("tokenUsage", EntryJson(entry, tokenUsage: true).EnumerateObject().Last().Name);
        }
    }

    // The spend's counts

    [Fact]
    public void AResponseCarriesItsCountsAndWhetherItsModelIsPublic()
    {
        var spend = new EstimateSpend(() => PriceBook.Bundled);
        var usage = new TokenCounts(Input: 1300, CachedInput: 1000, CacheWriteInput: 200, Output: 80, Reasoning: 30);
        var known = new ParsedResponse("a", SourceKind.ClaudeCode, "/a", "claude-opus-5-5", 1_000, 2_000, usage);
        var item = spend.Of(known, 5)!;
        Assert.Equal(new QuotaTokenCounts(1300, 80, 1000, 200, null), item.Counts);
        Assert.True(item.PublicModel);
        Assert.Equal("claude-opus-5-5", item.Model);

        var raw = spend.Of(known with { Model = "Acme-Internal-7" }, null)!;
        Assert.False(raw.PublicModel, "kept locally to match, never sent");
        Assert.Equal("acme-internal-7", raw.Model);
        Assert.False(spend.Of(known with { Model = null }, null)!.PublicModel);

        var host = spend.Of(known with { Host = "H", Source = SourceKind.Gemini }, 5)!;
        Assert.Equal(new QuotaTokenCounts(1300, 80, 1000, null, 30), host.Counts);
    }

    [Fact]
    public void PerMinuteSumsKeepUnmeasuredAndMissingDetails()
    {
        var a = new EstimateResponse(SourceKind.Gemini, null, 0, 1_000, "gemini-3.1-pro-preview", 1, 12, Counts: new QuotaTokenCounts(10, 2, 4, null, 1));
        var b = a with { Counts = new QuotaTokenCounts(5, 1, 1, null, 0), PublicModel = false };
        var summed = Assert.Single(EstimateSpend.PerMinute([a, b]));
        Assert.Equal(new QuotaTokenCounts(15, 3, 5, null, 1), summed.Counts);
        Assert.False(summed.PublicModel);
        Assert.Null(Assert.Single(EstimateSpend.PerMinute([a, a with { Counts = null }])).Counts);
    }

    [Fact]
    public void AHostsFoldedResponsesKeepTheirCounts()
    {
        const long now = 1_794_000_000_000;
        var estimator = new QuotaEstimator(null, null, clock: () => now);
        var window = QuotaWindow.Of(QuotaWindowKind.SevenDay, 20, now + 50 * Hour);
        estimator.HostQuota(new RemoteQuotaReport("H", new QuotaSnapshot(SourceKind.Gemini, now - Minute, [window]), true, true,
            QuotaRouteStatus.Ok(now)));
        var end = SpendLedger.MinuteOf(now - 2 * Hour) + 10_000;
        estimator.Spent(new ParsedResponse("h1", SourceKind.Gemini, "a", "gemini-3.1-pro-preview", end - 5_000, end,
            new TokenCounts(Input: 900, CachedInput: 400, Output: 120, Reasoning: 60), CostMicroUsd: 1_000_000) { Host = "H" });
        estimator.Spent(new ParsedResponse("h2", SourceKind.Gemini, "a", "gemini-3.1-pro-preview", end - 3_000, end + 2_000,
            new TokenCounts(Input: 100, CachedInput: 50, Output: 30, Reasoning: 10), CostMicroUsd: 500_000) { Host = "H" });
        estimator.Spent(new ParsedResponse("h3", SourceKind.Gemini, "a", "acme-gem-9", end - 3_000, end + 3_000,
            new TokenCounts(Input: 70, Output: 7)) { Host = "H" });
        Assert.True(estimator.BuildIfDue());

        var model = estimator.Latest!.Model;
        var account = model.HostAccountOf("H", SourceKind.Gemini, now)!;
        var usage = model.Ledger.TokenUsage(account, null, now - 3 * Hour, now)!;
        Assert.Equal(new QuotaTokenCounts(1070, 157, 450, null, 70), usage.Counts);
        Assert.Equal([new QuotaTokenModel("gemini-3.1-pro-preview", 1000, 150), new QuotaTokenModel(null, 70, 7)], usage.Models);
        var entry = Assert.Single(estimator.Entries(now, "https://radar.example", _ => true));
        Assert.Equal(usage.Counts, entry.TokenUsage!.Counts);
        Assert.DoesNotContain("acme-gem-9", ReportQuotas.Entry(entry, tokenUsage: true).ToCompactString(), StringComparison.Ordinal);
    }

    [Fact]
    public void AbsurdStoredCountsEndUnsafeInsteadOfOverflowing()
    {
        var ledger = new SpendLedger();
        var huge = new QuotaTokenCounts(long.MaxValue - 1, 1, null, null, null);
        for (var minute = 0; minute < 3; minute++)
        {
            ledger.Add("a", SourceKind.Codex, "gpt-5.5", minute * Minute, 1, counts: huge);
        }

        Assert.Null(ledger.TokenUsage("a", null, 0, 3 * Minute));
        Assert.Equal(long.MaxValue, QuotaTokenCounts.Add(long.MaxValue - 1, long.MaxValue - 1));
        Assert.Equal(long.MaxValue, huge.Adding(huge).Input);
    }

    [Fact]
    public void ARowOfAnotherModelFilterOrAccountIsNotCounted()
    {
        var ledger = new SpendLedger();
        ledger.Add("a", SourceKind.ClaudeCode, "claude-fable-5-2", 0, 1, counts: new QuotaTokenCounts(10, 1, 5, 2, null));
        ledger.Add("a", SourceKind.ClaudeCode, "claude-opus-5-5", 0, 1, counts: null);
        ledger.Add("b", SourceKind.ClaudeCode, "claude-fable-5-2", 0, 1, counts: new QuotaTokenCounts(99, 9, 0, 0, null));
        Assert.Null(ledger.TokenUsage("a", null, 0, Minute));
        var fable = ledger.TokenUsage("a", "Fable", 0, Minute)!;
        Assert.Equal(new QuotaTokenCounts(10, 1, 5, 2, null), fable.Counts);
        Assert.Equal([new QuotaTokenModel("claude-fable-5-2", 10, 1)], fable.Models);
        Assert.Equal(QuotaTokenCounts.Zero, ledger.TokenUsage("c", null, 0, Minute)!.Counts);
        Assert.Empty(ledger.TokenUsage("c", null, 0, Minute)!.Models);
    }

    // The history's stored form

    [Fact]
    public void TheStoredFormKeepsTheCountsAndOldRowsStayUnmeasured()
    {
        Assert.Equal(2, EstimateFileShare.Version);
        var counts = new QuotaTokenCounts(900, 90, 800, null, 9);
        var share = new EstimateFileShare(
        [
            new EstimateResponse(SourceKind.Codex, "/a", 1, 2, "gpt-5.5", 3, 990, "openai", Counts: counts),
            new EstimateResponse(SourceKind.Gemini, null, null, 4, "acme", null, 5, Counts: null, PublicModel: false),
        ], []);
        var back = EstimateFileShare.Decode(share.Encode(), "/a")!;
        Assert.Equal(share.Responses, back.Responses);
        Assert.Equal([counts, null], back.Responses.Select(r => r.Counts));
        Assert.Equal([true, false], back.Responses.Select(r => r.PublicModel));

        var v1 = EstimateFileShare.Decode("""{"r":[["codex","gpt-5.5",1,2,3,990,1,"openai",false]],"l":[]}"""u8, "/a")!;
        var old = Assert.Single(v1.Responses);
        Assert.Null(old.Counts);
        Assert.True(old.PublicModel);
        Assert.Equal(990, old.Tokens);

        foreach (var broken in new[]
        {
            """{"r":[["codex","gpt-5.5",1,2,3,990,1,null,false,[1,2],true]],"l":[]}""",
            """{"r":[["codex","gpt-5.5",1,2,3,990,1,null,false,[1,-2,null,null,null],true]],"l":[]}""",
            """{"r":[["codex","gpt-5.5",1,2,3,990,1,null,false,[1,2,-1,null,null],true]],"l":[]}""",
            """{"r":[["codex","gpt-5.5",1,2,3,990,1,null,false,[1,2,null,null,1.5],true]],"l":[]}""",
            """{"r":[["codex","gpt-5.5",1,2,3,990,1,null,false,[1,2,null,null,null],1]],"l":[]}""",
            """{"r":[["codex","gpt-5.5",1,2,3,990,1,null,false,{"input":1},true]],"l":[]}""",
            """{"r":[["codex","gpt-5.5",1,2,3,990,1,null,false,null]],"l":[]}""",
        })
        {
            Assert.Null(EstimateFileShare.Decode(System.Text.Encoding.UTF8.GetBytes(broken), "/a"));
        }
    }
}
