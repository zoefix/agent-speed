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
