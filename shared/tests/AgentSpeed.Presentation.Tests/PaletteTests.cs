using AgentSpeed.Core;
using AgentSpeed.Presentation.Colors;

namespace AgentSpeed.Presentation.Tests;

public class PaletteTests
{
    private static readonly Rgba LightBodyTop = Rgba.FromHex("#FFFFFF");
    private static readonly Rgba LightBodyBottom = Rgba.FromHex("#E7EBF3");

    public static TheoryData<double, string> DesignStops => new()
    {
        { 0.00, "#7C8798" },
        { 0.10, "#2DD4BF" },
        { 0.30, "#38BDF8" },
        { 0.48, "#818CF8" },
        { 0.64, "#E879F9" },
        { 0.78, "#FB7185" },
        { 0.90, "#FB923C" },
        { 1.00, "#FACC15" },
    };

    private static void AssertClose(Rgba expected, Rgba actual, double tolerance)
    {
        Assert.InRange(actual.R, expected.R - tolerance, expected.R + tolerance);
        Assert.InRange(actual.G, expected.G - tolerance, expected.G + tolerance);
        Assert.InRange(actual.B, expected.B - tolerance, expected.B + tolerance);
        Assert.InRange(actual.A, expected.A - tolerance, expected.A + tolerance);
    }

    [Theory]
    [MemberData(nameof(DesignStops))]
    public void EvaluateAtStopReturnsExactStopColor(double position, string hex)
    {
        Assert.Equal(hex, HeatPalette.Evaluate(position).ToHex());
    }

    [Theory]
    [MemberData(nameof(DesignStops))]
    public void StopsMatchDesign(double position, string hex)
    {
        Assert.Contains(HeatPalette.Stops, s => s.Position == position && s.Color.ToHex() == hex);
    }

    [Fact]
    public void StopsAreStrictlyIncreasingFromZeroToOne()
    {
        Assert.Equal(8, HeatPalette.Stops.Count);
        Assert.Equal(0, HeatPalette.Stops[0].Position);
        Assert.Equal(1, HeatPalette.Stops[^1].Position);
        for (var i = 1; i < HeatPalette.Stops.Count; i++)
        {
            Assert.True(HeatPalette.Stops[i].Position > HeatPalette.Stops[i - 1].Position);
        }
    }

    [Fact]
    public void SampleStaysCloseToEvaluate()
    {
        // The 256-entry table interpolates in gamma RGB, so it cuts the corner at each palette stop by up to ~2.4/255.
        for (var i = 0; i <= 2000; i++)
        {
            var h = i / 2000.0;
            AssertClose(HeatPalette.Evaluate(h), HeatPalette.Sample(h), 3.0 / 255);
        }
    }

    [Fact]
    public void SampleIsExactAtTableEntries()
    {
        for (var i = 0; i < HeatPalette.TableSize; i++)
        {
            var h = i / (double)(HeatPalette.TableSize - 1);
            AssertClose(HeatPalette.Evaluate(h), HeatPalette.Sample(h), 1e-9);
        }
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(double.NaN, 0)]
    [InlineData(double.NegativeInfinity, 0)]
    [InlineData(2, 1)]
    public void OutOfRangeHeatIsClamped(double heat, double clampedTo)
    {
        Assert.Equal(HeatPalette.Evaluate(clampedTo), HeatPalette.Evaluate(heat));
        Assert.Equal(HeatPalette.Sample(clampedTo), HeatPalette.Sample(heat));
    }

    [Fact]
    public void InterpolationIsContinuousBetweenStops()
    {
        var previous = HeatPalette.Evaluate(0);
        for (var i = 1; i <= 1000; i++)
        {
            var current = HeatPalette.Evaluate(i / 1000.0);
            var jump = Math.Max(Math.Abs(current.R - previous.R), Math.Max(Math.Abs(current.G - previous.G), Math.Abs(current.B - previous.B)));
            Assert.True(jump < 0.03, $"Discontinuity at h={i / 1000.0}: {jump}");
            previous = current;
        }
    }

    [Fact]
    public void OkLabRoundTripIsIdentityWithinOneStep()
    {
        var random = new Random(1234);
        for (var i = 0; i < 5000; i++)
        {
            var color = new Rgba(random.NextDouble(), random.NextDouble(), random.NextDouble());
            AssertClose(color, OkLab.FromRgb(color).ToRgb(), 1.0 / 255);
        }

        foreach (var (_, stop) in HeatPalette.Stops)
        {
            Assert.Equal(stop.ToHex(), OkLab.FromRgb(stop).ToRgb().ToHex());
        }
    }

    [Fact]
    public void OkLabLightnessIsMonotonicForGrays()
    {
        Assert.Equal(0, OkLab.FromRgb(Rgba.Black).L, 6);
        Assert.Equal(1, OkLab.FromRgb(Rgba.White).L, 4);
        var previous = -1.0;
        for (var v = 0; v <= 255; v++)
        {
            var lab = OkLab.FromRgb(new Rgba(v / 255.0, v / 255.0, v / 255.0));
            Assert.True(lab.L > previous, $"Lightness not increasing at {v}");
            Assert.Equal(0, lab.A, 3);
            Assert.Equal(0, lab.B, 3);
            previous = lab.L;
        }
    }

    [Fact]
    public void MixEndpointsAndAlpha()
    {
        var a = Rgba.FromHex("#2DD4BF");
        var b = Rgba.FromHex("#FB7185").WithAlpha(0.5);
        Assert.Equal(a.ToHex(), OkLab.Mix(a, b, 0).ToHex());
        Assert.Equal(b.ToHex(), OkLab.Mix(a, b, 1).ToHex());
        Assert.Equal(0.75, OkLab.Mix(a, b, 0.5).A, 9);
        Assert.Equal(OkLab.Mix(a, b, 1), OkLab.Mix(a, b, 5));
    }

    [Theory]
    [MemberData(nameof(DesignStops))]
    public void DarkenReducesLightnessBy18Percent(double position, string hex)
    {
        _ = position;
        var color = Rgba.FromHex(hex);
        var before = OkLab.FromRgb(color).L;
        var after = OkLab.FromRgb(OkLab.Darken(color, 0.18)).L;
        Assert.InRange(after, 0.82 * before - 0.01, 0.82 * before + 0.01);
    }

    [Fact]
    public void DarkenIsExactForNeutralsAndKeepsAlpha()
    {
        var gray = new Rgba(0.6, 0.6, 0.6, 0.4);
        var darker = OkLab.Darken(gray, 0.18);
        Assert.Equal(0.82 * OkLab.FromRgb(gray).L, OkLab.FromRgb(darker).L, 6);
        Assert.Equal(0.4, darker.A);
        Assert.Equal(gray.ToHex(), OkLab.Darken(gray, 0).ToHex());
        Assert.Equal(0, OkLab.FromRgb(OkLab.Darken(gray, 3)).L, 6);
    }

    [Fact]
    public void LightTierLabelHasThreeToOneContrastForMidHeats()
    {
        for (var i = 32; i <= 90; i++)
        {
            var heat = i / 100.0;
            var label = OrbTokens.Light.TierLabel(heat);
            Assert.True(Rgba.ContrastRatio(label, LightBodyTop) >= 3, $"h={heat} vs top: {Rgba.ContrastRatio(label, LightBodyTop):F2}");
            Assert.True(Rgba.ContrastRatio(label, LightBodyBottom) >= 3, $"h={heat} vs bottom: {Rgba.ContrastRatio(label, LightBodyBottom):F2}");
        }
    }

    [Fact]
    public void LightTierLabelHasThreeToOneContrastAtEveryHeat()
    {
        for (var i = 0; i <= 100; i++)
        {
            var heat = i / 100.0;
            var label = OrbTokens.Light.TierLabel(heat);
            Assert.True(Rgba.ContrastRatio(label, LightBodyTop) >= 3, $"h={heat} vs top: {Rgba.ContrastRatio(label, LightBodyTop):F2}");
            Assert.True(Rgba.ContrastRatio(label, LightBodyBottom) >= 3, $"h={heat} vs bottom: {Rgba.ContrastRatio(label, LightBodyBottom):F2}");
        }
    }

    [Fact]
    public void LightTierLabelIsDarkenedHeatColorAndDarkIsPlain()
    {
        var heat = 0.6;
        Assert.Equal(OkLab.Darken(HeatPalette.Sample(heat), 0.18), OrbTokens.Light.TierLabel(heat));
        Assert.Equal(HeatPalette.Sample(heat), OrbTokens.Dark.TierLabel(heat));
    }

    [Fact]
    public void OrbTokensMatchDesign()
    {
        var dark = OrbTokens.Dark;
        var light = OrbTokens.Light;

        Assert.Same(dark, OrbTokens.For(OrbAppearance.Dark));
        Assert.Same(light, OrbTokens.For(OrbAppearance.Light));

        Assert.Equal("#1B2030", dark.BodyTop.ToHex());
        Assert.Equal("#090B10", dark.BodyBottom.ToHex());
        Assert.Equal(0.88, dark.BodyAlpha);
        Assert.Equal(0.55, dark.AuraOpacityScale);
        Assert.True(dark.AdditiveParticles);
        Assert.Equal(HeatPalette.WhiteHot, dark.LeadingSegment(0.5));

        Assert.Equal("#FFFFFF", light.BodyTop.ToHex());
        Assert.Equal("#E7EBF3", light.BodyBottom.ToHex());
        Assert.Equal(0.94, light.BodyAlpha);
        Assert.Equal(0.32, light.AuraOpacityScale);
        Assert.False(light.AdditiveParticles);
        Assert.Equal(1.2, light.ParticleSizeScale);
        Assert.Equal("#FFB020", light.ParticleHot.ToHex());
        Assert.Equal(0.18, light.TierLabelDarken);
        Assert.Equal(HeatPalette.Sample(0.5), light.LeadingSegment(0.5));
        Assert.Equal(0.08, light.UnlitSegment.A, 9);
        Assert.Equal(0.07, dark.UnlitSegment.A, 9);
    }

    [Theory]
    [InlineData(SourceKind.Codex, "#34D399")]
    [InlineData(SourceKind.ClaudeCode, "#FF8A65")]
    [InlineData(SourceKind.ClaudeDesktop, "#B39DFF")]
    [InlineData(SourceKind.DeepSeek, "#4D6BFE")]
    [InlineData(SourceKind.OpenCode, "#F4B13E")]
    [InlineData(SourceKind.Gemini, "#34B8F0")]
    [InlineData(SourceKind.Omp, "#84CC16")]
    [InlineData(SourceKind.Antigravity, "#EC4899")]
    [InlineData(SourceKind.WorkBuddy, "#11A68B")]
    [InlineData(SourceKind.WorkBuddyAi, "#007AB4")]
    public void SourceColorsMatchDesign(SourceKind source, string hex)
    {
        Assert.Equal(hex, HeatPalette.SourceColor(source).ToHex());
    }

    [Fact]
    public void EverySourceHasItsOwnColor()
    {
        var colors = Enum.GetValues<SourceKind>().Select(s => HeatPalette.SourceColor(s).ToHex()).ToArray();
        Assert.Equal(colors.Length, colors.Distinct().Count());
        Assert.DoesNotContain(HeatPalette.Stops[0].Color.ToHex(), colors);
        Assert.DoesNotContain(HeatPalette.Neutral.ToHex(), colors);
    }

    [Fact]
    public void HighlightBlendsTowardWhiteHotByOverdrive()
    {
        Assert.Equal("#FFF7D6", HeatPalette.WhiteHot.ToHex());
        Assert.Equal(HeatPalette.Sample(0.7).ToHex(), HeatPalette.Highlight(0.7, 0).ToHex());
        Assert.Equal(HeatPalette.WhiteHot.ToHex(), HeatPalette.Highlight(0.7, 1).ToHex());
        Assert.Equal(HeatPalette.WhiteHot.ToHex(), HeatPalette.Highlight(0.7, 9).ToHex());
    }

    [Theory]
    [InlineData(0, 16, 0)]
    [InlineData(1, 16, 15)]
    [InlineData(0.5, 3, 1)]
    [InlineData(-4, 8, 0)]
    [InlineData(double.NaN, 8, 0)]
    [InlineData(9, 8, 7)]
    public void BucketQuantizesHeat(double heat, int buckets, int expected)
    {
        Assert.Equal(expected, HeatPalette.Bucket(heat, buckets));
    }

    [Theory]
    [InlineData("#7C8798")]
    [InlineData("7C8798")]
    [InlineData("#80FB7185")]
    [InlineData("#00000000")]
    public void HexRoundTrips(string hex)
    {
        var normalized = hex.StartsWith('#') ? hex : "#" + hex;
        Assert.Equal(normalized, Rgba.FromHex(hex).ToHex());
    }

    [Theory]
    [InlineData("#FFF")]
    [InlineData("")]
    [InlineData("#GGGGGG")]
    public void InvalidHexThrows(string hex)
    {
        Assert.ThrowsAny<FormatException>(() => Rgba.FromHex(hex));
    }

    [Fact]
    public void ContrastRatioMatchesWcagReferencePoints()
    {
        Assert.Equal(21, Rgba.ContrastRatio(Rgba.Black, Rgba.White), 6);
        Assert.Equal(21, Rgba.ContrastRatio(Rgba.White, Rgba.Black), 6);
        Assert.Equal(1, Rgba.ContrastRatio(LightBodyBottom, LightBodyBottom), 9);
        Assert.Equal(1, Rgba.ContrastRatio(Rgba.Black.WithAlpha(0), Rgba.White), 9);
        Assert.Equal(4.54, Rgba.ContrastRatio(Rgba.FromHex("#767676"), Rgba.White), 2);
    }

    [Fact]
    public void CompositeOverBlendsStraightAlpha()
    {
        var result = Rgba.CompositeOver(Rgba.Black.WithAlpha(0.5), Rgba.White);
        Assert.Equal(0.5, result.R, 9);
        Assert.Equal(1, result.A, 9);
        Assert.Equal(Rgba.Transparent, Rgba.CompositeOver(Rgba.Transparent, Rgba.Transparent));
    }
}
