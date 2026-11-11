using System.Collections.Immutable;
using AgentSpeed.Core;
using AgentSpeed.Core.Quota;
using AgentSpeed.Core.Quota.Estimate;
using AgentSpeed.Presentation.Character;
using AgentSpeed.Presentation.Characters;
using AgentSpeed.Presentation.Colors;
using AgentSpeed.Presentation.Heat;
using AgentSpeed.Presentation.Localization;
using AgentSpeed.Presentation.Orb;
using AgentSpeed.Presentation.Orb.Gpu;
using AgentSpeed.Presentation.Orb.Gpu.Styles;
using AgentSpeed.Presentation.Quota;
using AgentSpeed.Presentation.Tests.Gpu;
using AgentSpeed.Presentation.Titles;

namespace AgentSpeed.Presentation.Tests.Character;

/// <summary>The 額度 card with estimates (DESIGN 2a.6, 額度: the <c>≈</c> percent and no amount, Room; QUOTAS 10.7).</summary>
public sealed class QuotaCardEstimateTests
{
    private const long Hour = 3_600_000;
    private const long Now = 1794338773000;
    private const long Dollar = 1_000_000;
    private static readonly StringTable Table = Localizer.LoadEmbeddedTable();
    private static readonly Localizer En = new(Table, LanguageCodes.En);

    private static readonly QuotaWindow Codex = QuotaWindow.Of(QuotaWindowKind.SevenDay, 23, Now + 130 * Hour);
    private static readonly QuotaWindow Five = QuotaWindow.Of(QuotaWindowKind.FiveHour, 41, Now + 3 * Hour);
    private static readonly QuotaWindow Week = QuotaWindow.Of(QuotaWindowKind.SevenDay, 96, Now + 20 * Hour);

    private static QuotaWindowEstimate Estimate(QuotaWindow window, double live, long used, long? cap) =>
        new(Now - 60_000, live, live - window.UsedPercent >= 0.5, window.ResetsAtMs, false, used, 0, cap, cap is null ? null : cap * 97 / 100,
            cap is null ? null : cap * 103 / 100, cap is null ? EstimateConfidence.None : EstimateConfidence.High, 0.9, cap is null ? null : cap * (long)live / 100,
            6, "pro", null);

    private static QuotaState State(bool estimates = true)
    {
        var snapshots = ImmutableDictionary<SourceKind, QuotaSnapshot>.Empty
            .Add(SourceKind.Codex, new QuotaSnapshot(SourceKind.Codex, Now, [Codex]))
            .Add(SourceKind.ClaudeCode, new QuotaSnapshot(SourceKind.ClaudeCode, Now, [Five, Week]));
        var state = new QuotaState { Snapshots = snapshots, Active = [.. snapshots.Keys] };
        if (!estimates)
        {
            return state;
        }

        static QuotaEstimateKey Key(SourceKind source, QuotaWindow window) => new(null, source, EstimateNames.WindowId(window));
        return state with
        {
            Estimates = ImmutableDictionary<QuotaEstimateKey, QuotaWindowEstimate>.Empty
                .Add(Key(SourceKind.Codex, Codex), Estimate(Codex, 25.6, 7712 * Dollar / 100, 318 * Dollar))
                .Add(Key(SourceKind.ClaudeCode, Five), Estimate(Five, 41.2, 0, 75 * Dollar))
                .Add(Key(SourceKind.ClaudeCode, Week), Estimate(Week, 96, 161_240 * Dollar / 100, 1702 * Dollar)),
        };
    }

    // MARK: Content

    [Fact]
    public void TheCardCarriesTheLivePercentButNoAmount()
    {
        var card = OrbQuotaCard.Of(State(), _ => true, Now, En, TimeZoneInfo.Utc);
        var codex = Assert.Single(card.Sources[0].Windows);
        Assert.Equal((26, true), (codex.Percent, codex.Estimated));
        var claude = card.Sources[1].Windows;
        Assert.Equal((41, false), (claude[0].Percent, claude[0].Estimated));
        Assert.Equal((96, false), (claude[1].Percent, claude[1].Estimated));

        // The card reads as before the estimate, plus "estimated" while extrapolated; never an amount or a cap.
        Assert.Contains("Codex 7d 26% used, resets ", card.Spoken, StringComparison.Ordinal);
        Assert.Contains(", estimated", card.Spoken, StringComparison.Ordinal);
        Assert.DoesNotContain("Dollar", card.Spoken, StringComparison.Ordinal);
        Assert.DoesNotContain("cap", card.Spoken, StringComparison.Ordinal);
        Assert.Equal(2, card.Spoken!.Split(", estimated").Length);
    }

    [Fact]
    public void TheCardIsValueEqualWhileNothingChanges()
    {
        var a = OrbQuotaCard.Of(State(), _ => true, Now, En, TimeZoneInfo.Utc);
        var b = OrbQuotaCard.Of(State(), _ => true, Now, En, TimeZoneInfo.Utc);
        Assert.Equal(a, b);
        Assert.NotEqual(a, OrbQuotaCard.Of(State(estimates: false), _ => true, Now, En, TimeZoneInfo.Utc));
    }

    [Fact]
    public void WithoutEstimatesTheCardIsTodaysCard()
    {
        var card = OrbQuotaCard.Of(State(estimates: false), _ => true, Now, En, TimeZoneInfo.Utc);
        Assert.All(card.Sources.SelectMany(s => s.Windows), w => Assert.False(w.Estimated));
        Assert.Equal(OrbQuotaCard.Of(State(estimates: false).CardItems(_ => true, _ => true, [], new HashSet<string>()), Now, En, TimeZoneInfo.Utc), card);
    }

    [Fact]
    public void TheApproxFadesWithItsPercent()
    {
        var motion = new OrbQuotaMotion();
        var plain = new OrbQuotaWindow("7d", 23, 0.23, QuotaTone.Normal, "Fri", false);
        var source = new OrbQuotaSource(SourceKind.Codex, "Codex", Rgba.White, [plain]);
        motion.Sync([source], 0, reduceMotion: false, atOnce: true);
        var window = motion.Sources[0].Windows[0];
        Assert.Equal(1, window.PercentAlpha(0), 9);

        motion.Sync([source with { Windows = [plain with { Estimated = true }] }], 1, false, false);
        Assert.False(window.OldEstimated);
        Assert.InRange(window.PercentAlpha(1.1), 0.4, 0.6);
        Assert.True(motion.IsMoving(1.1));
        Assert.False(motion.IsMoving(1.5));

        motion.Sync([source with { Windows = [plain with { Percent = 24, Estimated = true }] }], 2, false, false);
        Assert.True(window.OldEstimated);
        Assert.Equal("23%", window.OldPercent);
    }

    // MARK: Drawn

    private static CharacterPack Pack() =>
        CharacterPack.Load(TinyPack.Make(TinyPack.Bands(("idle", TinyPack.RigJson()), ("boost", TinyPack.RigJson()))));

    private static (OrbGpuOrb Orb, FakeGlyphs Glyphs, double Time) Run(double size, OrbQuotaCard card, int frames = 120)
    {
        var (orb, glyphs) = CharacterDrawTests.Create(Pack(), size);
        orb.SetCharacter(orb.Character! with { Quota = card });
        var time = 10.0;
        orb.SnapNext();
        for (var n = 0; n < frames; n++, time += 1 / 60.0)
        {
            if (n % 30 == 0)
            {
                orb.SetData(time, 30, HeatMath.RawTier(30, 2000), SpeedTitles.Raw(30, 2000), [SourceKind.ClaudeCode], "info", null);
            }

            orb.Render(time);
        }

        return (orb, glyphs, time);
    }

    [Fact]
    public void TheCardDrawsTheApproxAndNoAmount()
    {
        var card = OrbQuotaCard.Of(State(), s => s == SourceKind.Codex, Now, En, TimeZoneInfo.Utc);
        var (orb, glyphs, _) = Run(148, card);
        var draw = Assert.IsType<CharacterDraw>(orb.Style);
        Assert.Equal(CharacterDraw.QuotaForm.Full, draw.QuotaShape);
        Assert.Contains(QuotaEstimateText.Approx, glyphs.Texts);
        Assert.DoesNotContain("$", glyphs.Texts);
    }

    [Fact]
    public void TheRoomDropsTheResetsThenShortensTheBarsAndThePercentCellHoldsTheApprox()
    {
        var card = OrbQuotaCard.Of(State(), _ => true, Now, En, TimeZoneInfo.Utc);
        var (orb, _, time) = Run(148, card);
        var draw = Assert.IsType<CharacterDraw>(orb.Style);
        var full = draw.QuotaWidthOf(CharacterDraw.QuotaForm.Full, time);
        var noResets = draw.QuotaWidthOf(CharacterDraw.QuotaForm.NoResets, time);
        var shortBars = draw.QuotaWidthOf(CharacterDraw.QuotaForm.ShortBars, time);
        Assert.True(full > noResets && noResets > shortBars, $"{full} {noResets} {shortBars}");
        Assert.Equal(["Full", "NoResets", "ShortBars", "Compact"], Enum.GetNames<CharacterDraw.QuotaForm>());

        // The percent cell is as wide as ≈100% whether or not a window is extrapolated: estimates never widen the card.
        var (plain, _, plainTime) = Run(148, OrbQuotaCard.Of(State(estimates: false), _ => true, Now, En, TimeZoneInfo.Utc));
        var plainDraw = Assert.IsType<CharacterDraw>(plain.Style);
        Assert.Equal(full, plainDraw.QuotaWidthOf(CharacterDraw.QuotaForm.Full, plainTime), 6);
    }

    [Fact]
    public void AWarmFrameWithEstimatedPercentsAllocatesNoMoreThanWithout()
    {
        static long Allocated(OrbQuotaCard card)
        {
            var (orb, _, time) = Run(148, card, 60);
            for (var n = 0; n < 60; n++, time += 1 / 60.0)
            {
                orb.Render(time);
            }

            // The least of three rounds: a round that catches a one-off allocation (a tiered recompile, a pool
            // growing) says nothing about the frame itself.
            var least = long.MaxValue;
            for (var round = 0; round < 3; round++)
            {
                var before = GC.GetAllocatedBytesForCurrentThread();
                for (var n = 0; n < 240; n++, time += 1 / 60.0)
                {
                    orb.Render(time);
                }

                least = Math.Min(least, GC.GetAllocatedBytesForCurrentThread() - before);
            }

            return least;
        }

        var card = OrbQuotaCard.Of(State(), _ => true, Now, En, TimeZoneInfo.Utc);
        Assert.Contains(card.Sources.SelectMany(s => s.Windows), w => w.Estimated);
        var without = Allocated(OrbQuotaCard.Empty);
        var with = Allocated(card);
        Assert.True(with - without <= 240 * 64, $"{with} B with the card, {without} B without over 240 frames");
    }
}
