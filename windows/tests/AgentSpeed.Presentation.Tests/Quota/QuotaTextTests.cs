using System.Text.Json;
using AgentSpeed.Core;
using AgentSpeed.Core.Quota;
using AgentSpeed.Presentation.Localization;
using AgentSpeed.Presentation.Quota;
using AgentSpeed.Presentation.Tests.TestSupport;

namespace AgentSpeed.Presentation.Tests.Quota;

/// <summary>The text cases of spec/fixtures/quota-cases.json in the four languages, in Asia/Taipei (QUOTAS 7, 10).</summary>
public sealed class QuotaTextTests
{
    private static readonly Lazy<JsonElement> Cases = new(() =>
        JsonDocument.Parse(File.ReadAllBytes(RepositoryPaths.FixtureFile("quota-cases.json"))).RootElement.Clone());

    private static readonly StringTable Table = Localizer.LoadEmbeddedTable();

    private static JsonElement Root => Cases.Value;

    private static long Now => Root.GetProperty("now").GetInt64();

    private static TimeZoneInfo Zone => TimeZoneInfo.FindSystemTimeZoneById(Root.GetProperty("timeZone").GetString()!);

    private static Localizer Loc(string language) => new(Table, language);

    [Fact]
    public void ResetTextsInEveryLanguage()
    {
        foreach (var c in Root.GetProperty("resetTexts").EnumerateArray())
        {
            var resets = c.GetProperty("resetsAtMs").GetInt64();
            foreach (var lang in c.GetProperty("long").EnumerateObject())
            {
                Assert.Equal(lang.Value.GetString(), QuotaText.ResetLong(resets, Now, Loc(lang.Name), Zone));
            }

            foreach (var lang in c.GetProperty("short").EnumerateObject())
            {
                Assert.Equal(lang.Value.GetString(), QuotaText.ResetShort(resets, Now, Loc(lang.Name), Zone));
            }
        }
    }

    [Fact]
    public void AgeTextsInEveryLanguage()
    {
        foreach (var c in Root.GetProperty("ageTexts").EnumerateArray())
        {
            var observed = c.GetProperty("observedMs").GetInt64();
            foreach (var lang in c.GetProperty("expected").EnumerateObject())
            {
                Assert.Equal(lang.Value.ValueKind == JsonValueKind.Null ? null : lang.Value.GetString(), QuotaText.Age(observed, Now, Loc(lang.Name)));
            }
        }
    }

    [Fact]
    public void LabelsFollowTheLanguage()
    {
        foreach (var pair in Root.GetProperty("labels").EnumerateArray())
        {
            var w = pair[0];
            var window = new QuotaWindow(
                w.GetProperty("kind").GetString() switch
                {
                    "fiveHour" => QuotaWindowKind.FiveHour,
                    "sevenDay" => QuotaWindowKind.SevenDay,
                    "modelWeek" => QuotaWindowKind.ModelWeek,
                    "month" => QuotaWindowKind.Month,
                    "credits" => QuotaWindowKind.Credits,
                    _ => QuotaWindowKind.Other,
                },
                w.GetProperty("name").ValueKind == JsonValueKind.Null ? null : w.GetProperty("name").GetString(),
                w.GetProperty("minutes").ValueKind == JsonValueKind.Null ? null : w.GetProperty("minutes").GetInt32(),
                1, null);
            foreach (var lang in pair[1].EnumerateObject())
            {
                Assert.Equal(lang.Value.GetString(), QuotaWindows.Label(window, QuotaText.Labels(Loc(lang.Name))));
            }
        }
    }

    [Theory]
    [InlineData(LanguageCodes.ZhHant, "時間進度 60%")]
    [InlineData(LanguageCodes.En, "60% of the window has passed")]
    public void PaceText(string language, string expected) => Assert.Equal(expected, QuotaText.Pace(0.6, Loc(language)));

    [Fact]
    public void SpokenWindows()
    {
        var loc = Loc(LanguageCodes.ZhHant);
        var snapshot = new QuotaSnapshot(SourceKind.ClaudeDesktop, Now,
            [QuotaWindow.Of(QuotaWindowKind.SevenDay, 94, Now + 3 * 3_600_000), QuotaWindow.Of(QuotaWindowKind.ModelWeek, 27, null, "Fable")]);
        var shown = QuotaWindows.Shown(snapshot, Now);
        Assert.Equal("7d 已用 94%，3 小時後重置", QuotaText.Spoken(shown[0], Now, loc, Zone));
        Assert.Equal("Fable 7d 已用 27%", QuotaText.Spoken(shown[1], Now, loc, Zone));
    }

    [Fact]
    public void UpdatedShowsTheTimeTodayAndTheDateOtherwise()
    {
        var loc = Loc(LanguageCodes.En);
        var today = QuotaText.Updated(Now - 60_000, Now, loc, Zone);
        var earlier = QuotaText.Updated(Now - 3 * 86_400_000L, Now, loc, Zone);
        Assert.StartsWith("Updated ", today, StringComparison.Ordinal);
        Assert.DoesNotContain("/", today, StringComparison.Ordinal);
        Assert.Contains("/", earlier, StringComparison.Ordinal);
    }
}

/// <summary>The dashboard's quota lines (DESIGN 4, Quota lines).</summary>
public sealed class QuotaLinesTests
{
    private const long Now = 1794338773000;
    private static readonly Localizer En = new(Localizer.LoadEmbeddedTable(), LanguageCodes.En);

    [Fact]
    public void LinesCarryTonePaceResetAndAge()
    {
        var snapshot = new QuotaSnapshot(SourceKind.ClaudeCode, Now - 2 * 3_600_000L,
        [
            QuotaWindow.Of(QuotaWindowKind.SevenDay, 96, Now + 3 * 86_400_000L),
            QuotaWindow.Of(QuotaWindowKind.FiveHour, 80, Now + 2 * 3_600_000L),
            QuotaWindow.Of(QuotaWindowKind.ModelWeek, 10, null, "Fable"),
        ]);
        var lines = QuotaLines.Of(snapshot, Now, En, TimeZoneInfo.Utc);
        Assert.Equal(["5h", "7d", "Fable 7d"], lines.Select(l => l.Label));
        Assert.Equal([QuotaTone.Warning, QuotaTone.Critical, QuotaTone.Normal], lines.Select(l => l.Tone));
        Assert.Equal(0.6, lines[0].Pace!.Value, 9);
        Assert.Null(lines[2].Pace);
        Assert.Equal("resets in 2 h", lines[0].ResetLong);
        Assert.Equal(string.Empty, lines[2].ResetLong);
        Assert.Equal(" · updated 2 h ago", lines[0].Age);
        Assert.Contains("60% of the window has passed", lines[0].Tooltip, StringComparison.Ordinal);
        Assert.Equal("80%", lines[0].PercentText);
        Assert.Equal("Fable 7d 10% used", lines[2].Spoken);
    }

    [Fact]
    public void ARecentQuotaListsItsSourceAndAnOldOneDoesNot()
    {
        var recent = new QuotaSnapshot(SourceKind.Codex, Now - 3_600_000L, [QuotaWindow.Of(QuotaWindowKind.SevenDay, 1, null)]);
        var old = new QuotaSnapshot(SourceKind.ClaudeDesktop, Now - 25 * 3_600_000L, [QuotaWindow.Of(QuotaWindowKind.SevenDay, 1, null)]);
        var empty = new QuotaSnapshot(SourceKind.ClaudeCode, Now, []);
        var state = new QuotaState
        {
            Snapshots = System.Collections.Immutable.ImmutableDictionary<SourceKind, QuotaSnapshot>.Empty
                .Add(SourceKind.Codex, recent).Add(SourceKind.ClaudeDesktop, old).Add(SourceKind.ClaudeCode, empty),
        };
        Assert.True(state.ListsSource(SourceKind.Codex, Now));
        Assert.False(state.ListsSource(SourceKind.ClaudeDesktop, Now));
        Assert.False(state.ListsSource(SourceKind.ClaudeCode, Now));
        Assert.Equal(state, state with { });
    }
}
