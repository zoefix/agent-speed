using System.Collections.Immutable;
using AgentSpeed.Core;
using AgentSpeed.Core.Quota;
using AgentSpeed.Presentation.Character;
using AgentSpeed.Presentation.Characters;
using AgentSpeed.Presentation.Colors;
using AgentSpeed.Presentation.Heat;
using AgentSpeed.Presentation.Localization;
using AgentSpeed.Presentation.Orb;
using AgentSpeed.Presentation.Orb.Gpu;
using AgentSpeed.Presentation.Orb.Gpu.Styles;
using AgentSpeed.Presentation.Quota;
using AgentSpeed.Presentation.Settings.Orbs;
using AgentSpeed.Presentation.Tests.Gpu;
using AgentSpeed.Presentation.Titles;

namespace AgentSpeed.Presentation.Tests.Character;

/// <summary>The 額度 card of a character orb (DESIGN 2a.6, 額度; QUOTAS 6).</summary>
public sealed class QuotaCardTests
{
    private const long Hour = 3_600_000;
    private const long Now = 1794338773000;
    private static readonly Localizer En = new(Localizer.LoadEmbeddedTable(), LanguageCodes.En);
    private static readonly Localizer ZhHant = new(Localizer.LoadEmbeddedTable(), LanguageCodes.ZhHant);

    private static QuotaState State(bool claudeCode = true, ImmutableHashSet<SourceKind>? active = null)
    {
        var snapshots = ImmutableDictionary.CreateBuilder<SourceKind, QuotaSnapshot>();
        snapshots[SourceKind.ClaudeDesktop] = new QuotaSnapshot(SourceKind.ClaudeDesktop, Now - 60_000,
            [QuotaWindow.Of(QuotaWindowKind.ModelWeek, 27, Now + 50 * Hour, "Fable"), QuotaWindow.Of(QuotaWindowKind.SevenDay, 86, Now + 50 * Hour)]);
        snapshots[SourceKind.Codex] = new QuotaSnapshot(SourceKind.Codex, Now, [QuotaWindow.Of(QuotaWindowKind.SevenDay, 23, Now + 130 * Hour)]);
        if (claudeCode)
        {
            snapshots[SourceKind.ClaudeCode] = new QuotaSnapshot(SourceKind.ClaudeCode, Now,
                [QuotaWindow.Of(QuotaWindowKind.FiveHour, 41, Now + 3 * Hour + 720_000), QuotaWindow.Of(QuotaWindowKind.SevenDay, 96, Now + 20 * Hour)]);
        }

        return new QuotaState { Snapshots = snapshots.ToImmutable(), Active = active ?? [.. snapshots.Keys] };
    }

    [Fact]
    public void TheCardListsActiveSourcesInSourceOrderWithOnlyTheirWindows()
    {
        var card = OrbQuotaCard.Of(State(), _ => true, Now, En, TimeZoneInfo.Utc);
        Assert.Equal([SourceKind.Codex, SourceKind.ClaudeCode, SourceKind.ClaudeDesktop], card.Sources.Select(s => s.Source));
        Assert.Equal(["7d"], card.Sources[0].Windows.Select(w => w.Label));
        Assert.Equal(["5h", "7d"], card.Sources[1].Windows.Select(w => w.Label));
        Assert.Equal(["7d", "Fable 7d"], card.Sources[2].Windows.Select(w => w.Label));
        Assert.Equal(["3h", "20h"], card.Sources[1].Windows.Select(w => w.Reset));
        Assert.Equal([QuotaTone.Normal, QuotaTone.Critical], card.Sources[1].Windows.Select(w => w.Tone));
        Assert.Equal(QuotaTone.Warning, card.Sources[2].Windows[0].Tone);
        Assert.StartsWith("Quota: Codex 7d 23% used, resets ", card.Spoken, StringComparison.Ordinal);
    }

    [Fact]
    public void OnlyRunningSignedInTrackedSourcesShow()
    {
        var state = State(active: [SourceKind.Codex]);
        Assert.Equal([SourceKind.Codex], OrbQuotaCard.Of(state, _ => true, Now, En).Sources.Select(s => s.Source));
        Assert.True(OrbQuotaCard.Of(state, s => s != SourceKind.Codex, Now, En).IsEmpty);
        Assert.Null(OrbQuotaCard.Empty.Spoken);
    }

    [Fact]
    public void AWeeklyOnlyAccountShowsOneLineWithoutAPlaceholder()
    {
        var card = OrbQuotaCard.Of(State(), s => s == SourceKind.Codex, Now, ZhHant);
        var window = Assert.Single(Assert.Single(card.Sources).Windows);
        Assert.Equal("7d", window.Label);
    }

    [Fact]
    public void ABarEasesAndAResetEmptiesItMoreSlowly()
    {
        var motion = new OrbQuotaMotion();
        var source = new OrbQuotaSource(SourceKind.Codex, "Codex", Rgba.White, [new OrbQuotaWindow("5h", 40, 0.4, QuotaTone.Normal, "3h", false)]);
        motion.Sync([source], 0, reduceMotion: false, atOnce: true);
        var window = motion.Sources[0].Windows[0];
        Assert.Equal(0.4, window.Bar(0), 9);

        motion.Sync([source with { Windows = [new OrbQuotaWindow("5h", 60, 0.6, QuotaTone.Normal, "3h", false)] }], 1, false, false);
        Assert.InRange(window.Bar(1.2), 0.45, 0.6);
        Assert.Equal(0.6, window.Bar(1.4), 9);
        Assert.InRange(window.PercentAlpha(1.1), 0.4, 0.6);
        Assert.Equal("40%", window.OldPercent);

        motion.Sync([source with { Windows = [new OrbQuotaWindow("5h", 0, 0, QuotaTone.Normal, "", true)] }], 2, false, false);
        Assert.True(window.Bar(2.4) > 0.05, "a reset empties the bar over 0.6 s");
        Assert.Equal(0, window.Bar(2.6), 9);
        Assert.Equal("3h", window.OldReset);
    }

    [Fact]
    public void ReduceMotionChangesLengthsAtOnceAndKeepsTheFades()
    {
        var motion = new OrbQuotaMotion();
        var a = new OrbQuotaSource(SourceKind.Codex, "Codex", Rgba.White, [new OrbQuotaWindow("7d", 20, 0.2, QuotaTone.Normal, "Fri", false)]);
        var b = new OrbQuotaSource(SourceKind.ClaudeCode, "Claude Code", Rgba.White, [new OrbQuotaWindow("7d", 50, 0.5, QuotaTone.Normal, "Fri", false)]);
        motion.Sync([a], 0, reduceMotion: true, atOnce: true);
        motion.Sync([a with { Windows = [a.Windows[0] with { Bar = 0.8, Percent = 80 }] }, b], 1, true, false);
        Assert.Equal(0.8, motion.Sources[0].Windows[0].Bar(1), 9);
        Assert.InRange(motion.Sources[1].Alpha(1.1), 0.3, 0.5);
        Assert.True(motion.IsMoving(1.1));

        motion.Sync([b], 2, true, false);
        Assert.True(motion.Sources[0].Leaving);
        Assert.InRange(motion.Sources[0].Alpha(2.1), 0.5, 0.7);
        motion.Sync([b], 3, true, false);
        Assert.Single(motion.Sources);
    }

    // MARK: Drawn

    private static CharacterPack Pack() =>
        CharacterPack.Load(TinyPack.Make(TinyPack.Bands(("idle", TinyPack.RigJson()), ("boost", TinyPack.RigJson()))));

    private static (OrbGpuOrb Orb, FakeGlyphs Glyphs) Run(double size, OrbQuotaCard card, CharacterReadouts? readouts = null)
    {
        var (orb, glyphs) = CharacterDrawTests.Create(Pack(), size);
        orb.SetCharacter(orb.Character! with { Readouts = readouts ?? orb.Character.Readouts, Quota = card });
        var time = 10.0;
        orb.SnapNext();
        for (var n = 0; n < 120; n++, time += 1 / 60.0)
        {
            if (n % 30 == 0)
            {
                orb.SetData(time, 30, HeatMath.RawTier(30, 2000), SpeedTitles.Raw(30, 2000), [SourceKind.ClaudeCode], "info", null);
            }

            orb.Render(time);
        }

        return (orb, glyphs);
    }

    [Fact]
    public void TheCardDrawsItsSourcesLabelsPercentsAndResets()
    {
        var (orb, glyphs) = Run(148, OrbQuotaCard.Of(State(claudeCode: false), _ => true, Now, En, TimeZoneInfo.Utc));
        var draw = Assert.IsType<CharacterDraw>(orb.Style);
        Assert.True(draw.Wings.Places[(int)HudCard.Quota].Alpha > 0.9);
        Assert.Equal(draw.Wings.Split <= HudCards.Column.IndexOf(HudCard.Quota), draw.Wings.IsAtHerRight(HudCard.Quota));
        Assert.Superset(new HashSet<string> { "Codex", "Claude Desktop", "7d", "Fable 7d" }, glyphs.Texts);
        Assert.Contains(glyphs.Texts, t => t.Contains('%', StringComparison.Ordinal) || t == "%");
        Assert.Equal(2, draw.QuotaLines.Count);
    }

    [Fact]
    public void TheCardStaysAwayWhileSwitchedOffOrEmpty()
    {
        var (off, offGlyphs) = Run(148, OrbQuotaCard.Of(State(), _ => true, Now, En), CharacterReadouts.Default with { Quota = false });
        Assert.Equal(0, Assert.IsType<CharacterDraw>(off.Style).Wings.Places[(int)HudCard.Quota].Alpha);
        Assert.DoesNotContain("Fable 7d", offGlyphs.Texts);

        var (empty, _) = Run(148, OrbQuotaCard.Empty);
        Assert.Equal(0, Assert.IsType<CharacterDraw>(empty.Style).Wings.Places[(int)HudCard.Quota].Alpha);
    }

    [Fact]
    public void BelowNinetySixTheCardListsItsFirstSourceWithoutResets()
    {
        var (orb, glyphs) = Run(72, OrbQuotaCard.Of(State(), _ => true, Now, En, TimeZoneInfo.Utc));
        var draw = Assert.IsType<CharacterDraw>(orb.Style);
        Assert.Single(draw.QuotaLines.Sources);
        Assert.Equal(SourceKind.Codex, draw.QuotaLines.Sources[0].Data.Source);
        Assert.NotEqual(CharacterDraw.QuotaForm.Full, draw.QuotaShape);
        Assert.DoesNotContain("Claude Desktop", glyphs.Texts);
    }

    [Fact]
    public void TheCardIsAsWideAsItsFormAndFitsTheRoom()
    {
        var (orb, _) = Run(148, OrbQuotaCard.Of(State(), _ => true, Now, En, TimeZoneInfo.Utc));
        var draw = Assert.IsType<CharacterDraw>(orb.Style);
        var size = draw.Wings.Sizes[(int)HudCard.Quota];
        Assert.True(size.W > 0 && size.H > 0);
        Assert.True(draw.Wings.QuotaNarrowest <= size.W + 0.5);
        Assert.True(size.W <= draw.Wings.QuotaRoom + 0.5 || draw.QuotaShape == CharacterDraw.QuotaForm.Compact,
            $"{size.W} in {draw.Wings.QuotaRoom} as {draw.QuotaShape}");
    }

    [Fact]
    public void AWarmFrameWithTheCardAllocatesNoMoreThanWithout()
    {
        static long Allocated(OrbQuotaCard card)
        {
            var (orb, _) = Run(148, card);
            var time = 20.0;
            for (var n = 0; n < 60; n++, time += 1 / 60.0)
            {
                orb.Render(time);
            }

            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var n = 0; n < 240; n++, time += 1 / 60.0)
            {
                orb.Render(time);
            }

            return GC.GetAllocatedBytesForCurrentThread() - before;
        }

        var without = Allocated(OrbQuotaCard.Empty);
        var with = Allocated(OrbQuotaCard.Of(State(), _ => true, Now, En, TimeZoneInfo.Utc));
        // Its texts are shared strings: a warm frame adds at most a few bytes for the card.
        Assert.True(with - without <= 240 * 64, $"{with} B with the card, {without} B without over 240 frames");
    }

    [Fact]
    public void TheOrbSpeaksTheQuotaLast()
    {
        var card = OrbQuotaCard.Of(State(), _ => true, Now, En);
        var sentence = OrbCharacterSpeech.Sentence(new OrbCharacterReadings(), CharacterReadouts.Default, 0, En, "US dollars", card);
        Assert.Equal(card.Spoken, sentence);
        Assert.Null(OrbCharacterSpeech.Sentence(new OrbCharacterReadings(), CharacterReadouts.Default with { Quota = false }, 0, En, "US dollars", card));
    }
}
