using AgentSpeed.Core;
using AgentSpeed.Presentation.Colors;
using AgentSpeed.Presentation.Heat;
using AgentSpeed.Presentation.Motion;
using AgentSpeed.Presentation.Orb;
using AgentSpeed.Presentation.Orb.Gpu;
using AgentSpeed.Presentation.Settings;

namespace AgentSpeed.Presentation.Tests.Gpu;

/// <summary>A glyph source with fixed metrics (no rasterizer on the test host).</summary>
internal sealed class FakeGlyphs : IOrbGlyphs
{
    public int Requests { get; private set; }

    public HashSet<string> Texts { get; } = [];

    public bool TryGet(in OrbTextKey key, out OrbGlyph glyph)
    {
        Requests++;
        Texts.Add(key.Text);
        var advance = (float)(key.Text.Length * key.Size * 0.56 + Math.Max(0, key.Text.Length - 1) * key.Tracking);
        var pad = 2 + 2.4f * key.GlowSigma;
        glyph = new OrbGlyph(advance, key.Size * 0.75f, -pad, -(pad + key.Size * 0.75f), advance + 2 * pad, key.Size + 2 * pad, 0, 0, 0.1f, 0.1f);
        return true;
    }

    public double DigitCell(OrbFont font, double size) => size * 0.56;

    public double CapHeight(OrbFont font, double size) => size * 0.7;
}

public class OrbGpuOrbTests
{
    private const double Redline = 2000;

    private static readonly OrbReadoutStrings Strings = new("tok/s", "NEW PEAK",
        Enum.GetValues<SpeedTitle>().Select(t => t.ToString().ToUpperInvariant()).ToArray(), true);

    private static (OrbGpuOrb Orb, FakeGlyphs Glyphs) Create(OrbStyle style, double size = 148, bool dark = true,
        EffectsIntensity level = EffectsIntensity.Standard, bool highContrast = false, IReadOnlyList<SourceKind>? sources = null)
    {
        var glyphs = new FakeGlyphs();
        var effects = new EffectsPolicy(level, false, highContrast);
        var orb = new OrbGpuOrb(7, glyphs);
        orb.Configure(new OrbGpuConfig(style, size, effects, true, true, Redline, Strings, sources, SourceSet.Empty),
            new OrbGpuContext(size, 2, dark ? OrbTokens.Dark : OrbTokens.Light, effects, highContrast ? OrbContrastColors.Default : null));
        return (orb, glyphs);
    }

    private static double Drive(OrbGpuOrb orb, double time, double value, double seconds)
    {
        orb.SetData(time, value, HeatMath.RawTier(value, Redline), SpeedTitles.Raw(value, Redline),
            value > 0 ? [SourceKind.Codex, SourceKind.ClaudeCode] : [], "today 1.2M · 2 agents", null);
        var end = time + seconds;
        while (time < end)
        {
            time += 1 / 60.0;
            orb.Render(time);
        }

        return time;
    }

    private static void AssertFinite(OrbDrawList list)
    {
        foreach (var instance in list.Instances)
        {
            foreach (var v in new[] { instance.A, instance.B, instance.Color, instance.Color2, instance.Extra, instance.Extra2 })
            {
                Assert.True(float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z) && float.IsFinite(v.W));
            }
        }

        foreach (var text in list.Texts)
        {
            foreach (var v in new[] { text.Rect, text.Uv, text.Color, text.Glow, text.Params, text.Clip, text.Fill2, text.Gradient })
            {
                Assert.True(float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z) && float.IsFinite(v.W));
            }
        }
    }

    public static TheoryData<OrbStyle, bool, double> Styles()
    {
        var data = new TheoryData<OrbStyle, bool, double>();
        foreach (var style in OrbStyles.All)
        {
            foreach (var dark in new[] { true, false })
            {
                foreach (var size in new[] { style == OrbStyle.Minimal ? 64.0 : 96, 148, 240 })
                {
                    data.Add(style, dark, size);
                }
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Styles))]
    public void EveryStyleBuildsAFiniteFrameWithItsReadoutThroughTheTour(OrbStyle style, bool dark, double size)
    {
        var (orb, _) = Create(style, size, dark);
        var time = 100.0;
        foreach (var value in new[] { 0, 140, 480, 1040, 1800, 2900, 0 })
        {
            time = Drive(orb, time, value, 1.2);
            AssertFinite(orb.List);
            Assert.True(orb.List.Count > 0);
            Assert.True(orb.List.TextCount >= 1);
            Assert.Equal(orb.Context!.Canvas, orb.List.Canvas);
        }

        orb.Peak(time);
        orb.Pulse(time, 1800, 4000);
        orb.SetHover(time, true);
        time = Drive(orb, time, 1800, 0.5);
        AssertFinite(orb.List);
    }

    [Theory]
    [InlineData(OrbStyle.Led)]
    [InlineData(OrbStyle.Needle)]
    [InlineData(OrbStyle.Liquid)]
    [InlineData(OrbStyle.Reactor)]
    [InlineData(OrbStyle.Minimal)]
    public void AWarmFrameAllocatesNothing(OrbStyle style)
    {
        var (orb, _) = Create(style, 240, level: EffectsIntensity.Max);
        var time = Drive(orb, 100, 2900, 3);
        orb.Render(time += 1 / 60.0);
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 30; i++)
        {
            orb.Render(time += 1 / 60.0);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [Fact]
    public void AnIdleMinimalOrbIsStaticAndItsFrameNeverChanges()
    {
        var (orb, _) = Create(OrbStyle.Minimal);
        var time = Drive(orb, 100, 0, 3);
        var first = orb.List.Fingerprint();
        Assert.Equal(FrameClass.None, orb.Demand);
        orb.Render(time + 5);
        Assert.Equal(first, orb.List.Fingerprint());
    }

    [Fact]
    public void TheLiquidSplitsItsReadoutAtTheSurface()
    {
        var (orb, _) = Create(OrbStyle.Liquid);
        Drive(orb, 100, 1040, 2);
        Assert.True(orb.List.HasLiquid);
        var modes = orb.List.Texts.ToArray().Select(t => (OrbTextClipMode)(int)t.Params.W).ToHashSet();
        Assert.Contains(OrbTextClipMode.AboveLiquid, modes);
        Assert.Contains(OrbTextClipMode.BelowLiquid, modes);
    }

    [Fact]
    public void OnlyDarkOrbsUseAdditiveBlending()
    {
        foreach (var style in OrbStyles.All)
        {
            var (orb, _) = Create(style, dark: false, level: EffectsIntensity.Max);
            Drive(orb, 100, 2900, 2);
            Assert.DoesNotContain(orb.List.Batches.ToArray(), b => b.Blend == OrbBlend.Additive);
        }
    }

    [Fact]
    public void ASubsetOrbDrawsItsSourceBezelInsteadOfTheRim()
    {
        var (all, _) = Create(OrbStyle.Led);
        var (subset, _) = Create(OrbStyle.Led, sources: [SourceKind.Codex, SourceKind.ClaudeCode]);
        Drive(all, 100, 0, 0.2);
        Drive(subset, 100, 0, 0.2);
        Assert.Equal(all.List.Count + 1, subset.List.Count);
    }

    [Fact]
    public void TheWindowMotionScalesAndFadesEverything()
    {
        var (orb, _) = Create(OrbStyle.Needle);
        orb.Render(100, contentScale: 0.6, opacity: 0.3);
        Assert.Equal(0.3, orb.List.Opacity, 6);
        Assert.Equal(0.6, orb.List.ContentScale, 6);
    }

    [Fact]
    public void TheTitleSlideIsStaggeredSoTwoTitlesNeverShowTogether()
    {
        for (var p = 0.0; p <= 1; p += 0.05)
        {
            var (outAlpha, _) = OrbReadoutDraw.TitleOut(p);
            var (inAlpha, _) = OrbReadoutDraw.TitleIn(p);
            Assert.False(outAlpha > 0.2 && inAlpha > 0.2, $"both titles visible at {p}");
        }

        Assert.Equal(1, OrbReadoutDraw.TitleOut(0).Alpha, 6);
        Assert.Equal(1, OrbReadoutDraw.TitleIn(1).Alpha, 6);
    }

    [Fact]
    public void AThemeRenameCrossfadesTheTitleInsteadOfSliding()
    {
        var (orb, glyphs) = Create(OrbStyle.Led);
        var time = Drive(orb, 100, 480, 2);
        var titles = Enum.GetValues<SpeedTitle>().Select(t => "THEME " + t).ToArray();
        orb.Configure(orb.Config! with { Strings = Strings with { Titles = titles } }, orb.Context!);
        orb.Render(time += 0.05);
        glyphs.Texts.Clear();
        orb.Render(time += 0.1);
        Assert.Contains(Strings.Title(orb.Scene.Title), glyphs.Texts);
        Assert.Contains("THEME " + orb.Scene.Title, glyphs.Texts);
        Assert.NotEqual(FrameClass.None, orb.Demand);
        glyphs.Texts.Clear();
        orb.Render(time + 1);
        Assert.DoesNotContain(Strings.Title(orb.Scene.Title), glyphs.Texts);
    }

    [Fact]
    public void ConsecutiveShapesOfOneBlendShareABatch()
    {
        var list = new OrbDrawList();
        list.Add(OrbGpuInstance.Circle(1, 1, 1, Rgba.White));
        list.Add(OrbGpuInstance.Circle(1, 1, 1, Rgba.White));
        list.Add(OrbGpuInstance.Circle(1, 1, 1, Rgba.White), OrbBlend.Additive);
        list.ClipRadius = 5;
        list.Add(OrbGpuInstance.Circle(1, 1, 1, Rgba.White), OrbBlend.Additive);
        Assert.Equal(3, list.BatchCount);
        Assert.Equal(2, list.Batches[0].Count);
    }

    [Fact]
    public void TheMoneyUnitShowsItsUnitAndThePartlyPricedSign()
    {
        var (orb, glyphs) = Create(OrbStyle.Led);
        var time = Drive(orb, 100, 480, 1);
        orb.Scene.SetMoney(new Cost.LiveMoney("0.0042", "US$/s", "$0.0042", AtLeast: true, Unknown: false, 4200, "", ""));
        glyphs.Texts.Clear();
        orb.Render(time += 0.1);
        orb.Render(time += 0.06);
        Assert.Contains("US$/s", glyphs.Texts);
        Assert.Contains("tok/s", glyphs.Texts);
        Assert.Contains("≥", glyphs.Texts);
        glyphs.Texts.Clear();
        orb.Render(time + 1);
        Assert.Contains("US$/s", glyphs.Texts);
        Assert.DoesNotContain("tok/s", glyphs.Texts);
    }

    [Fact]
    public void TheReadoutReportsTheWidthsOfTheUnitHitArea()
    {
        var (led, _) = Create(OrbStyle.Led);
        Drive(led, 100, 480, 0.5);
        Assert.Equal(5 * 0.072 * 148 * 0.56 + 4 * 0.5, led.Readout.UnitWidth, 0);
        var (minimal, _) = Create(OrbStyle.Minimal);
        Drive(minimal, 100, 480, 0.5);
        Assert.True(minimal.Readout.ValueWidth > 0);
    }

    [Fact]
    public void TheBlurredDiscFollowsTheGaussianEdgeProfile()
    {
        // A disc blurred with σ: half intensity on the edge, ≈ 16 % one σ out (erfc), within a few percent.
        var disc = OrbSharedDraw.BlurredDisc(0, 0, 50, 4, Rgba.Black);
        double At(double d)
        {
            var x = Math.Max(0, d - disc.B.X) / disc.B.W;
            return d - disc.B.X <= 0 ? 1 : Math.Exp(-x * x);
        }

        Assert.InRange(At(50), 0.47, 0.53);
        Assert.InRange(At(54), 0.13, 0.19);
    }
}
