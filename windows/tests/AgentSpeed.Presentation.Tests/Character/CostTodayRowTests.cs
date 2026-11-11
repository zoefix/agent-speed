using AgentSpeed.Core;
using AgentSpeed.Presentation.Character;
using AgentSpeed.Presentation.Cost;
using AgentSpeed.Presentation.Heat;
using AgentSpeed.Presentation.Localization;
using AgentSpeed.Presentation.Orb;
using AgentSpeed.Presentation.Orb.Gpu.Styles;

namespace AgentSpeed.Presentation.Tests.Character;

/// <summary>
/// The 費用 card's 今日 row (DESIGN 2a.6; user's choice, 2026-11-11): today's cost of the billable sources under the live
/// rate; the card stays while there is a cost today, and the rate row leaves it once the money stops.
/// </summary>
public sealed class CostTodayRowTests
{
    private static readonly Localizer En = new(Localizer.LoadEmbeddedTable(), LanguageCodes.En);

    private static readonly LiveMoney Money = new("0.0042", "US$/s", "$", false, false, 4200, "", "");

    private static readonly CostToday Today = new("$12.34", false, false, "today's cost 12.34 US dollars");

    private static CostReading Reading(long micro, int unpriced = 0, long requests = 3) =>
        CostReading.Zero with { Today = new CostTally(micro, unpriced), TodayRequests = requests };

    [Fact]
    public void TodayFollowsTheBillableTally()
    {
        var display = new CostDisplay(En);
        var priced = Assert.IsType<CostToday>(display.Today(Reading(12_340_000)));
        Assert.EndsWith("12.34", priced.Amount, StringComparison.Ordinal);
        Assert.False(priced.AtLeast || priced.Unknown);
        Assert.Contains("12.34", priced.Spoken, StringComparison.Ordinal);
        Assert.True(display.Today(Reading(12_340_000, unpriced: 1))!.AtLeast);
        var unknown = display.Today(Reading(0, unpriced: 3))!;
        Assert.True(unknown.Unknown);
        Assert.Equal("–", unknown.Amount);
        Assert.Null(display.Today(Reading(0, requests: 0)));
    }

    [Fact]
    public void TheRateRowLeavesOnlyWhileTheTodayRowStays()
    {
        var readings = new OrbCharacterReadings();
        readings.Apply(null, Money, true, 10, costToday: Today);
        Assert.Equal(1, readings.CostRatePresence(10));
        readings.Apply(null, Money with { Value = 0 }, false, 11, costToday: Today);
        var quiet = readings.Cost.QuietAt;
        Assert.Equal(1, readings.CostRatePresence(quiet - 0.1));
        Assert.True(readings.CostRateMoving(quiet + 0.2));
        Assert.Equal(0, readings.CostRatePresence(quiet + OrbCharacterReadings.CostRateFade));
        Assert.Equal(0, readings.CostRateShare(quiet + 1));

        // Back to loud: the card opens, then the rate fades in.
        var back = quiet + 5;
        readings.Apply(null, Money, true, back, costToday: Today);
        Assert.Equal(0, readings.CostRateAlpha(back + 0.2));
        Assert.True(readings.CostRateShare(back + 0.2) > 0.5);
        Assert.Equal(1, readings.CostRatePresence(back + OrbCharacterReadings.CostRateFade));

        // Without a 今日 row the whole card fades instead, the rate row kept whole.
        var alone = new OrbCharacterReadings();
        alone.Apply(null, Money, true, 10);
        alone.Apply(null, Money with { Value = 0 }, false, 11);
        Assert.Equal(1, alone.CostRatePresence(alone.Cost.QuietAt + 1));
    }

    [Fact]
    public void TheCardStaysWithItsTodayRowWhileTheOrbRests()
    {
        var pack = CharacterPack.Load(TinyPack.Make(TinyPack.Bands(("idle", TinyPack.RigJson()), ("boost", TinyPack.RigJson()))));
        var (orb, glyphs) = CharacterDrawTests.Create(pack, 148);
        orb.SetCharacter(orb.Character! with { CostTodayGlyph = "Today" });
        var time = 10.0;
        orb.SnapNext();
        for (var n = 0; n < 1500; n++, time += 1 / 60.0)
        {
            if (n % 30 == 0)
            {
                orb.SetData(time, 0, HeatMath.RawTier(0, 2000), SpeedTitles.Raw(0, 2000), [], "info", null);
                orb.SetCharacterReadings(time, null, null, false, costToday: Today);
            }

            orb.Render(time);
        }

        var draw = Assert.IsType<CharacterDraw>(orb.Style);
        Assert.True(draw.Wings.Places[(int)HudCard.Cost].Alpha > 0.9);
        Assert.Contains("Today", glyphs.Texts);
    }
}
