using AgentSpeed.Core;
using AgentSpeed.Core.Intelligence;
using AgentSpeed.Presentation.Character;
using AgentSpeed.Presentation.Heat;
using AgentSpeed.Presentation.Colors;
using AgentSpeed.Presentation.Localization;
using AgentSpeed.Presentation.Orb;
using AgentSpeed.Presentation.Orb.Gpu;
using AgentSpeed.Presentation.Orb.Gpu.Styles;
using AgentSpeed.Presentation.Orb.Lines;
using AgentSpeed.Presentation.Perf;
using AgentSpeed.Presentation.Settings.Orbs;
using AgentSpeed.Presentation.Tests.Gpu;
using Xunit.Abstractions;

namespace AgentSpeed.Presentation.Tests.Character;

/// <summary>
/// The models card's place (DESIGN 2a.6, Readouts: under the speed on its line, the spill-over under it, down past the
/// spilled cards or hidden when her right side is crowded) and the card and 首字 row as the character style draws them.
/// </summary>
public class ModelsCardLayoutTests(ITestOutputHelper output)
{
    private const double D = 296;

    private static readonly (double W, double H)[] Sizes = [(80, 40), (90, 18), (78, 34), (70, 30), (74, 16), (86, 16), (96, 36)];

    private static readonly bool[] Shown = [true, true, true, true, true, true, true];

    private static readonly Localizer En = new(Localizer.LoadEmbeddedTable(), LanguageCodes.En);

    /// <summary>A slim body from y 80 to 400 around x 222 (as HudWingsTests), optionally with a staff at x over y0…y1 or wide hair at her left.</summary>
    private static HudFrame Frame((double Y0, double Y1)? staff = null, bool wideHair = false, double staffX = 390)
    {
        var s = new HudSilhouette(0, 444, 4);
        for (var y = 80.0; y <= 400; y += 2)
        {
            s.Add(190, y);
            s.Add(254, y);
        }

        if (staff is { } span)
        {
            for (var y = span.Y0; y <= span.Y1; y += 2)
            {
                s.Add(staffX, y);
            }
        }

        if (wideHair)
        {
            for (var y = 80.0; y <= 300; y += 2)
            {
                s.Add(60, y);
            }
        }

        return new HudFrame((0, 0, 444, 444), s, D, 222, 80, 170, 140);
    }

    /// <summary>The card's narrowest form in these frames: its dots and speeds (the given size is its widest).</summary>
    private const double Narrowest = 60;

    private static HudWings Settled(HudFrame f, bool still = true, double seconds = 1)
    {
        var wings = new HudWings { Still = still, ModelsNarrowest = Narrowest };
        for (var n = 0; n < seconds * 60; n++)
        {
            wings.Update(1 / 60.0, n / 60.0, f, Sizes, Shown);
        }

        return wings;
    }

    private static (double Left, double Top, double Right, double Bottom) Box(HudWings wings, HudCard card)
    {
        var p = wings.Places[(int)card];
        var (w, h) = wings.Sizes[(int)card];
        return (p.X - w / 2, p.Y - h / 2, p.X + w / 2, p.Y + h / 2);
    }

    [Fact]
    public void TheCardHangsUnderTheSpeedWithItsLeftEdgeOnTheSpeedsLine()
    {
        var wings = Settled(Frame());
        var speed = Box(wings, HudCard.Speed);
        var models = Box(wings, HudCard.Models);
        Assert.Equal(HudWings.ModelsPlace.UnderSpeed, wings.ModelsAt);
        Assert.Equal(speed.Left, models.Left, 6);
        Assert.Equal(speed.Bottom + HudWings.ColumnGap * D, models.Top, 6);
        Assert.Equal(1, wings.Places[(int)HudCard.Models].Alpha, 6);
        Assert.True(wings.IsAtHerRight(HudCard.Models));
    }

    [Fact]
    public void SpilledCardsContinueUnderTheModelsCard()
    {
        var wings = Settled(Frame(wideHair: true));
        Assert.True(wings.Split < HudCards.Column.Length);
        var models = Box(wings, HudCard.Models);
        foreach (var card in HudCards.Column.Skip(wings.Split))
        {
            Assert.True(Box(wings, card).Top >= models.Bottom + HudWings.ColumnGap * D - 1e-6, card.ToString());
        }
    }

    [Fact]
    public void ACrowdedRightSideSendsTheCardDownAndThenHidesIt()
    {
        var crowded = Frame(staff: (195, 260));
        var wings = Settled(crowded);
        Assert.Equal(HudWings.ModelsPlace.BelowSpill, wings.ModelsAt);
        var models = Box(wings, HudCard.Models);
        Assert.True(models.Top >= 260 && models.Top <= Box(wings, HudCard.Speed).Bottom + HudWings.ColumnGap * D + 0.3 * D);
        Assert.Equal(Box(wings, HudCard.Speed).Left, models.Left, 6);
        Assert.Null(crowded.Silhouette.Extent(models.Top + 1, models.Bottom - 1) is { } e && e.Right > models.Left ? e : null);

        var full = Settled(Frame(staff: (195, 400)));
        Assert.Equal(HudWings.ModelsPlace.Hidden, full.ModelsAt);
        Assert.Equal(0, full.Places[(int)HudCard.Models].Alpha);
        Assert.Equal(0, full.Presence[(int)HudCard.Models]);
    }

    [Fact]
    public void TheCardStepsRightOfAnArmUnderTheSpeed()
    {
        var wings = Settled(Frame(staff: (195, 260), staffX: 300));
        var models = Box(wings, HudCard.Models);
        Assert.Equal(HudWings.ModelsPlace.UnderSpeed, wings.ModelsAt);
        Assert.Equal(300 + HudWings.Gap * D, models.Left, 6);
        Assert.Equal(444 - HudWings.Margin * D - (300 + HudWings.Gap * D), wings.ModelsWidth, 6);
    }

    [Fact]
    public void ANarrowRoomKeepsTheCardUnderTheSpeedAsLongAsItsNarrowestFormFits()
    {
        var f = Frame(staff: (195, 260), staffX: 360);
        var room = 444 - HudWings.Margin * D - (360 + HudWings.Gap * D);
        var narrow = new HudWings { Still = true, ModelsNarrowest = room - 1 };
        narrow.Update(1 / 60.0, 0, f, Sizes, Shown);
        Assert.Equal(HudWings.ModelsPlace.UnderSpeed, narrow.ModelsAt);
        Assert.Equal(room, narrow.ModelsWidth, 6);
        var wide = new HudWings { Still = true, ModelsNarrowest = room + 1 };
        wide.Update(1 / 60.0, 0, f, Sizes, Shown);
        Assert.Equal(HudWings.ModelsPlace.BelowSpill, wide.ModelsAt);
    }

    [Fact]
    public void AHiddenCardComesBackAfterItFitsWithRoomToSpareFor1point2Seconds()
    {
        var wings = new HudWings { ModelsNarrowest = Narrowest };
        var t = 0.0;
        void Run(HudFrame f, double seconds)
        {
            for (var n = 0; n < seconds * 60; n++, t += 1 / 60.0)
            {
                wings.Update(1 / 60.0, t, f, Sizes, Shown);
            }
        }

        Run(Frame(staff: (195, 400)), 2);
        Assert.Equal(HudWings.ModelsPlace.Hidden, wings.ModelsAt);

        // Room for the narrowest form but not 0.04 D more: it stays hidden.
        var x = 444 - HudWings.Margin * D - Narrowest - 0.02 * D - HudWings.Gap * D;
        Run(Frame(staff: (195, 400), staffX: x), 3);
        Assert.Equal(HudWings.ModelsPlace.Hidden, wings.ModelsAt);

        Run(Frame(), 1.0);
        Assert.Equal(HudWings.ModelsPlace.Hidden, wings.ModelsAt);
        Run(Frame(), 0.3);
        Assert.Equal(HudWings.ModelsPlace.UnderSpeed, wings.ModelsAt);
    }

    [Fact]
    public void TheLoweredCardMovesBackUpAfterFourSeconds()
    {
        var wings = new HudWings { ModelsNarrowest = Narrowest };
        var t = 0.0;
        void Run(HudFrame f, double seconds)
        {
            for (var n = 0; n < seconds * 60; n++, t += 1 / 60.0)
            {
                wings.Update(1 / 60.0, t, f, Sizes, Shown);
            }
        }

        Run(Frame(staff: (195, 260)), 2);
        Assert.Equal(HudWings.ModelsPlace.BelowSpill, wings.ModelsAt);
        Run(Frame(), 3.9);
        Assert.Equal(HudWings.ModelsPlace.BelowSpill, wings.ModelsAt);
        Assert.Equal(1, wings.Places[(int)HudCard.Models].Alpha, 6);
        Run(Frame(), 0.2);
        Assert.Equal(HudWings.ModelsPlace.BelowSpill, wings.ModelsAt);
        Assert.True(wings.Places[(int)HudCard.Models].Alpha < 1, "it fades out where it was");
        Run(Frame(), 0.2);
        Assert.Equal(HudWings.ModelsPlace.UnderSpeed, wings.ModelsAt);
        Assert.True(wings.Places[(int)HudCard.Models].Alpha < 1, "and in at its new place");
        Run(Frame(), 0.5);
        Assert.Equal(1, wings.Places[(int)HudCard.Models].Alpha, 6);
    }

    [Fact]
    public void APassingReachMovesNothingAndALastingOneMovesTheCard()
    {
        var wings = Settled(Frame(), still: false, seconds: 2);
        var t = 2.0;
        for (var n = 0; n < 30; n++, t += 1 / 60.0)
        {
            wings.Update(1 / 60.0, t, Frame(staff: (195, 260)), Sizes, Shown);
        }

        Assert.Equal(HudWings.ModelsPlace.UnderSpeed, wings.ModelsAt);
        for (var n = 0; n < 90; n++, t += 1 / 60.0)
        {
            wings.Update(1 / 60.0, t, Frame(staff: (195, 260)), Sizes, Shown);
        }

        Assert.Equal(HudWings.ModelsPlace.BelowSpill, wings.ModelsAt);
    }

    // MARK: Drawn

    private static CharacterPack Pack() =>
        CharacterPack.Load(TinyPack.Make(TinyPack.Bands(("idle", TinyPack.RigJson()), ("boost", TinyPack.RigJson()))));

    private static (OrbGpuOrb Orb, FakeGlyphs Glyphs) Run(double size, IReadOnlyList<OrbModelRow> models, OrbLatency? latency, CharacterReadouts? readouts = null)
    {
        var (orb, glyphs) = CharacterDrawTests.Create(Pack(), size);
        orb.SetCharacter(orb.Character! with { Readouts = readouts ?? orb.Character.Readouts, ModelsMore = En["character.models.more"] });

        var sums = new IntelligenceSums(Output: 1200, Thinking: 528, ThinkingBasis: 1200) { Score = TestSums.Live(1286.25) };
        var performance = OrbLinesText.Intelligence(PerfSetReading.Of(sums, []), sums, generating: true, En);
        var time = 10.0;
        orb.SnapNext();
        for (var n = 0; n < 120; n++, time += 1 / 60.0)
        {
            if (n % 30 == 0)
            {
                orb.SetData(time, 600, HeatMath.RawTier(600, 2000), SpeedTitles.Raw(600, 2000), [SourceKind.ClaudeCode], "info", null);
                orb.SetCharacterReadings(time, performance, null, true, latency, models);
            }

            orb.Render(time);
        }

        return (orb, glyphs);
    }

    private static readonly OrbModelRow[] Five =
    [
        new("claude-opus-5-5", 106, Rgba.FromHex("#D97757")), new("gpt-6.1-sol", 92, null), new("glm-5", 64, null), new("kimi-k3", 40, null),
        new("gemini-4-argon", null, null),
    ];

    [Fact]
    public void TheCardShowsFourRowsAndTheMoreLine()
    {
        var (orb, glyphs) = Run(148, Five, null);
        var draw = Assert.IsType<CharacterDraw>(orb.Style);
        Assert.True(draw.Wings.Places[(int)HudCard.Models].Alpha > 0.9);
        Assert.Superset(new HashSet<string> { "claude-opus-5-5", "gpt-6.1-sol", "glm-5", "kimi-k3", "+1 more", "tok/s" }, glyphs.Texts);
        Assert.DoesNotContain("gemini-4-argon", glyphs.Texts);
        Assert.Equal(5, draw.ModelLines.Count);
    }

    [Fact]
    public void BelowNinetySixTheCardKeepsTwoRows()
    {
        var (orb, glyphs) = Run(72, Five[..3], null);
        var draw = Assert.IsType<CharacterDraw>(orb.Style);
        Assert.Contains("claude-opus-5-5", glyphs.Texts);
        Assert.Contains("gpt-6.1-sol", glyphs.Texts);
        Assert.DoesNotContain("glm-5", glyphs.Texts);
        Assert.Contains("+1 more", glyphs.Texts);
        Assert.Equal(3, draw.ModelLines.Count);
    }

    [Fact]
    public void ALongNameLosesItsDateAndIsCutAtItsEndToNineT()
    {
        var (orb, glyphs) = Run(148, [new OrbModelRow("gemini-4-argon-preview-2026-11-10", 50, null), new OrbModelRow("claude-sonnet-4-5-20250929", 40, null)],
            null);
        Assert.Equal(9, Assert.IsType<CharacterDraw>(orb.Style).ModelNameRoom);
        Assert.Contains("claude-sonnet-4-5", glyphs.Texts);
        var cut = Assert.Single(glyphs.Texts, t => t.StartsWith("gemini-4-argon", StringComparison.Ordinal) && t.EndsWith('…'));
        Assert.True(cut.Length * 8 * 0.56 <= 9 * 8, cut);
        Assert.DoesNotContain(glyphs.Texts, t => t.Contains("2026", StringComparison.Ordinal) || t.Contains("20250929", StringComparison.Ordinal));
    }

    [Fact]
    public void SmallerOrbsNarrowTheNamesThenKeepOnlyTheDotsAndSpeeds()
    {
        var (mid, midGlyphs) = Run(92, Five[..3], null);
        var narrowed = Assert.IsType<CharacterDraw>(mid.Style);
        Assert.InRange(narrowed.ModelNameRoom, OrbModelsNames.Narrowest, OrbModelsNames.Widest - OrbModelsNames.Step);
        Assert.Contains(midGlyphs.Texts, t => t.StartsWith("claude", StringComparison.Ordinal) && t.EndsWith('…'));
        Assert.True(narrowed.Wings.Places[(int)HudCard.Models].Alpha > 0.9);

        var (small, _) = Run(72, Five[..3], null);
        var dots = Assert.IsType<CharacterDraw>(small.Style);
        Assert.Equal(0, dots.ModelNameRoom);
        Assert.Equal(HudWings.ModelsPlace.UnderSpeed, dots.Wings.ModelsAt);
        Assert.True(dots.Wings.Places[(int)HudCard.Models].Alpha > 0.9);
        Assert.Equal(dots.Wings.ModelsNarrowest, dots.Wings.Sizes[(int)HudCard.Models].W, 3);
        Assert.True(dots.Wings.ModelsNarrowest <= dots.Wings.ModelsWidth);
    }

    [Fact]
    public void TheCardStaysAwayWhileSwitchedOff()
    {
        var (orb, glyphs) = Run(148, Five, null, CharacterReadouts.Default with { Model = true, Models = false });
        var draw = Assert.IsType<CharacterDraw>(orb.Style);
        Assert.Equal(0, draw.Wings.Places[(int)HudCard.Models].Alpha);
        Assert.DoesNotContain("+1 more", glyphs.Texts);
    }

    [Fact]
    public void TheLatencyRowJoinsThePerformanceCard()
    {
        var latency = OrbLatency.Of(1180, En);
        var (withRow, glyphs) = Run(148, [], latency);
        var (without, plain) = Run(148, [], null);
        Assert.Contains("TTFT", glyphs.Texts);
        Assert.DoesNotContain("TTFT", plain.Texts);
        var a = Assert.IsType<CharacterDraw>(withRow.Style).Wings.Sizes[(int)HudCard.Performance];
        var b = Assert.IsType<CharacterDraw>(without.Style).Wings.Sizes[(int)HudCard.Performance];
        Assert.True(a.H > b.H + 8, $"{a.H} vs {b.H}");

        var (off, offGlyphs) = Run(148, [], latency, CharacterReadouts.Default with { Model = true, Latency = false });
        Assert.DoesNotContain("TTFT", offGlyphs.Texts);
        Assert.Equal(b.H, Assert.IsType<CharacterDraw>(off.Style).Wings.Sizes[(int)HudCard.Performance].H, 6);
    }

    /// <summary>
    /// <c>AGENT_SPEED_CHARACTER_PACKS</c> (a folder of unpacked packs): at 148, 112, 92 and 72 pt every character dances
    /// through three bands for 30 s with three running models. At 148 pt the card finds room beside her nearly all the time
    /// and rarely moves; the smaller sizes are reported with how often the names are whole, shortened or dropped.
    /// </summary>
    [Fact]
    public void TheCardFindsRoomBesideEveryCharacter()
    {
        if (Environment.GetEnvironmentVariable("AGENT_SPEED_CHARACTER_PACKS") is not { Length: > 0 } root)
        {
            return;
        }

        OrbModelRow[] rows = [Five[0], Five[1], new("gemini-4-argon-preview-20261101", null, null)];
        var sums = new IntelligenceSums(Output: 1200, Thinking: 528, ThinkingBasis: 1200) { Score = TestSums.Live(1286.25) };
        var performance = OrbLinesText.Intelligence(PerfSetReading.Of(sums, []), sums, generating: true, En);
        foreach (var size in new double[] { 148, 112, 92, 72 })
        {
            double shownAll = 0, framesAll = 0;
            foreach (var dir in Directory.GetDirectories(root).Where(p => File.Exists(Path.Combine(p, "manifest.json"))).Order(StringComparer.Ordinal))
            {
                var (orb, _) = CharacterDrawTests.Create(CharacterPack.Load(dir), size);
                orb.SetCharacter(orb.Character! with { ModelsMore = En["character.models.more"] });
                var time = 10.0;
                int shown = 0, frames = 0, moves = 0, whole = 0, dots = 0, widths = 0;
                var room = OrbModelsNames.Widest;
                var last = HudWings.ModelsPlace.UnderSpeed;
                foreach (var value in new double[] { 100, 600, 1500 })
                {
                    for (var n = 0; n < 600; n++, time += 1 / 60.0)
                    {
                        if (n % 30 == 0)
                        {
                            orb.SetData(time, value, HeatMath.RawTier(value, 2000), SpeedTitles.Raw(value, 2000), [SourceKind.ClaudeCode], "info", null);
                            orb.SetCharacterReadings(time, performance, null, true, OrbLatency.Of(1200, En), rows);
                        }

                        orb.Render(time);
                        var draw = (CharacterDraw)orb.Style!;
                        var wings = draw.Wings;
                        frames++;
                        if (wings.Places[(int)HudCard.Models].Alpha > 0.5)
                        {
                            shown++;
                            whole += draw.ModelNameRoom == OrbModelsNames.Widest ? 1 : 0;
                            dots += draw.ModelNameRoom == 0 ? 1 : 0;
                        }

                        moves += wings.ModelsAt != last ? 1 : 0;
                        last = wings.ModelsAt;
                        widths += draw.ModelNameRoom != room ? 1 : 0;
                        room = draw.ModelNameRoom;
                    }
                }

                shownAll += shown;
                framesAll += frames;
                output.WriteLine($"S{size} {Path.GetFileName(dir)}: shown {100.0 * shown / frames:0}% (whole {whole}, shortened {shown - whole - dots}, dots {dots}), {moves} moves, {widths} width changes");
                if (size >= 148)
                {
                    Assert.True(shown > 0.9 * frames && moves <= 8, $"{Path.GetFileName(dir)}: shown {shown}/{frames}, {moves} moves");
                }
            }

            output.WriteLine($"S{size}: shown {100 * shownAll / framesAll:0.0}% of all frames");
        }
    }
}
