using System.Diagnostics;
using AgentSpeed.Presentation.Colors;
using AgentSpeed.Presentation.Heat;
using AgentSpeed.Presentation.Orb;
using AgentSpeed.Presentation.Orb.Styles;
using AgentSpeed.Presentation.Settings;
using AgentSpeed.Presentation.Tests.TestSupport;
using Xunit.Abstractions;

namespace AgentSpeed.Presentation.Tests;

public class OrbMinimalModelTests(ITestOutputHelper output)
{
    private const double Dt = 1.0 / 60;
    private const double R = HeatMath.ReferenceRedline;

    private static (MinimalModel Model, OrbStyleDriver Driver) Create(
        EffectsIntensity level = EffectsIntensity.Standard, double size = 148, bool dark = true, bool reduceMotion = false,
        bool highContrast = false)
    {
        var model = new MinimalModel();
        return (model, new OrbStyleDriver(model, level, size, dark, reduceMotion, highContrast));
    }

    private static void SnapAt(OrbStyleDriver driver, double value)
    {
        driver.Value = value;
        driver.Snap();
    }

    [Theory]
    [InlineData(0.5, false, 0.0)]
    [InlineData(1.0, false, 0.0)]
    [InlineData(1.5, true, 0.5)]
    [InlineData(2.0, true, 1.0)]
    [InlineData(3.0, true, 1.0)]
    public void ProgressIsHeatPlusOverdriveWithASecondLap(double speed, bool secondLap, double lap2)
    {
        var (model, driver) = Create();
        SnapAt(driver, speed * R);
        var h = HeatMath.Heat(speed * R, R);
        var o = HeatMath.Overdrive(speed * R, R);
        Assert.Equal(h + o, model.Progress, 9);
        Assert.True(model.RingVisible);
        Assert.Equal(270, model.StartDegrees);
        Assert.Equal(270 + 360 * Math.Min(1, h + o), model.Lap1EndDegrees, 9);
        Assert.Equal(secondLap, model.HasSecondLap);
        Assert.Equal(secondLap, model.ShadowVisible);
        if (secondLap)
        {
            Assert.Equal(270 + 360 * lap2, model.Lap2EndDegrees, 9);
            Assert.Equal(model.Lap2EndDegrees, model.HeadDegrees, 9);
        }
        else
        {
            Assert.Equal(model.Lap1EndDegrees, model.HeadDegrees, 9);
        }
    }

    [Fact]
    public void HalfTheRedlineFillsTwoThirdsOfALap()
    {
        var (model, driver) = Create();
        SnapAt(driver, R / 2);
        Assert.Equal(270 + 360 * Math.Pow(0.5, 0.6), model.Lap1EndDegrees, 9);
        Assert.Equal(507.5, model.Lap1EndDegrees, 1);
    }

    [Theory]
    [InlineData(64, 2.5 / 64)]
    [InlineData(72, 2.5 / 72)]
    [InlineData(148, 0.034)]
    [InlineData(240, 0.034)]
    public void RingWidthHasATwoAndAHalfPointFloor(double size, double width)
    {
        var (model, driver) = Create(size: size);
        SnapAt(driver, 300);
        Assert.Equal(width, model.RingWidth, 9);
        Assert.Equal(0.32 * width, model.HeadDotRadius, 9);
        Assert.Equal(0.35 * width, model.ShadowSigma, 9);
        Assert.Equal(0.425, MinimalModel.TrackRadius);
    }

    [Fact]
    public void SecondLapHeadCastsAShadowWindow()
    {
        var (model, driver) = Create(dark: false);
        SnapAt(driver, 1.5 * R);
        Assert.True(model.ShadowVisible);
        Assert.Equal(model.HeadDegrees - 0.02 * 180 / Math.PI, model.ShadowStartDegrees, 9);
        Assert.Equal(model.HeadDegrees + 0.07 * 180 / Math.PI, model.ShadowEndDegrees, 9);
        Assert.Equal(0.22, model.ShadowColor.A, 9);
        Assert.Equal(0, model.ShadowColor.R);

        var (darkModel, darkDriver) = Create();
        SnapAt(darkDriver, 1.5 * R);
        Assert.Equal(0.55, darkModel.ShadowColor.A, 9);
    }

    [Fact]
    public void ColorsRunFromTheDarkenedLabelColorToTheHead()
    {
        var (model, driver) = Create();
        SnapAt(driver, 100);
        var h = Math.Round(HeatMath.Heat(100, R) * 255) / 255;
        var head = HeatPalette.Sample(h);
        OrbNeedleModelTests.AssertColor(head, model.HeadColor);
        OrbNeedleModelTests.AssertColor(OkLab.Darken(head, 0.28), model.StartColor);
        OrbNeedleModelTests.AssertColor(OkLab.Mix(head, HeatPalette.WhiteHot, 0.6), model.HeadDotColor);
        OrbNeedleModelTests.AssertColor(Rgba.Lerp(model.StartColor, model.HeadColor, 0.5), model.Lap1ColorAt(0.5));
        Assert.Equal(0.07 * 1.15, model.TrackColor.A, 9);

        var (light, lightDriver) = Create(dark: false);
        SnapAt(lightDriver, 100);
        var label = StyleColors.Label(h, false);
        OrbNeedleModelTests.AssertColor(label, light.HeadColor);
        OrbNeedleModelTests.AssertColor(OkLab.Darken(label, 0.10), light.StartColor);
        OrbNeedleModelTests.AssertColor(label, light.HeadDotColor);
        Assert.Equal(0.08 * 1.15, light.TrackColor.A, 9);
    }

    [Theory]
    [InlineData(10, 0)]
    [InlineData(100, 0.35)]
    [InlineData(200, 0.35)]
    public void GlowShowsFromBoostUpInDark(double value, double opacity)
    {
        var (model, driver) = Create();
        SnapAt(driver, value);
        Assert.Equal(opacity, model.RingGlowOpacity, 9);
        Assert.Equal(opacity > 0 ? 0.0125 : 0, model.RingGlowSigma, 9);

        var (light, lightDriver) = Create(dark: false);
        SnapAt(lightDriver, value);
        Assert.Equal(0, light.RingGlowOpacity);
    }

    [Fact]
    public void RedlineGlowBreathesAtOneHertz()
    {
        var (model, driver) = Create();
        SnapAt(driver, 350);
        Assert.Equal(SpeedTier.Redline, driver.Frame.Tier);
        double low = 1, high = 0, lowSigma = 1, highSigma = 0;
        var peaks = 0;
        double a = model.RingGlowOpacity, b = a;
        for (var i = 0; i < 300; i++)
        {
            driver.Step(Dt);
            var c = model.RingGlowOpacity;
            if (b > a && b >= c)
            {
                peaks++;
            }

            a = b;
            b = c;
            low = Math.Min(low, c);
            high = Math.Max(high, c);
            lowSigma = Math.Min(lowSigma, model.RingGlowSigma);
            highSigma = Math.Max(highSigma, model.RingGlowSigma);
            Assert.Equal(0.6 * model.Breath, model.HeadDotOpacity, 9);
            Assert.Equal(FrameClass.Fps15, model.Demand);
        }

        Assert.InRange(peaks, 4, 5);
        Assert.Equal(0.35, low, 3);
        Assert.Equal(0.7, high, 3);
        Assert.Equal(0.0125, lowSigma, 4);
        Assert.Equal(0.025, highSigma, 4);
    }

    [Fact]
    public void MaxGlowIsThirtyPercentWider()
    {
        var (model, driver) = Create(EffectsIntensity.Max, reduceMotion: false);
        SnapAt(driver, 100);
        Assert.Equal(0.0125 * 1.3, model.RingGlowSigma, 9);
    }

    [Theory]
    [InlineData(EffectsIntensity.Standard, true, 0.7)]
    [InlineData(EffectsIntensity.Off, false, 0)]
    public void ReduceMotionAndOffHoldTheRedlineSteady(EffectsIntensity level, bool reduceMotion, double glow)
    {
        var (model, driver) = Create(level, reduceMotion: reduceMotion);
        SnapAt(driver, 350);
        for (var i = 0; i < 90; i++)
        {
            driver.Step(Dt);
            Assert.Equal(1, model.Breath);
            Assert.Equal(glow, model.RingGlowOpacity, 9);
            Assert.Equal(glow > 0 ? 0.025 : 0, model.RingGlowSigma, 9);
            Assert.Equal(0.6, model.HeadDotOpacity, 9);
            Assert.Equal(FrameClass.None, model.Demand);
        }
    }

    [Fact]
    public void LightRedlineBreathesOnlyTheHeadDot()
    {
        var (model, driver) = Create(dark: false);
        SnapAt(driver, 350);
        double high = 0, low = 1;
        for (var i = 0; i < 90; i++)
        {
            driver.Step(Dt);
            Assert.Equal(0, model.RingGlowOpacity);
            high = Math.Max(high, model.HeadDotOpacity);
            low = Math.Min(low, model.HeadDotOpacity);
        }

        Assert.Equal(0.6, high, 3);
        Assert.Equal(0, low, 3);
        Assert.Equal(FrameClass.Fps15, model.Demand);
    }

    [Fact]
    public void PulseBlinksTheHeadDotAndGlow()
    {
        var (model, driver) = Create();
        SnapAt(driver, 100);
        Assert.False(model.HeadDotVisible);
        Assert.Equal(FrameClass.None, model.Demand);
        model.Pulse(0.4, 1);
        driver.Step(Dt);
        Assert.True(model.HeadDotVisible);
        Assert.Equal(1, model.HeadDotOpacity, 9);
        Assert.Equal(0.65, model.RingGlowOpacity, 9);
        Assert.Equal(0.0225, model.RingGlowSigma, 9);
        Assert.Equal(FrameClass.Fps30, model.Demand);
        driver.Run(0.2);
        Assert.Equal(0.5, model.HeadDotOpacity, 1);
        driver.Run(0.2);
        Assert.False(model.HeadDotVisible);
        Assert.Equal(0.35, model.RingGlowOpacity, 9);
        Assert.Equal(FrameClass.None, model.Demand);
    }

    [Fact]
    public void EventsWakeAStaticOrbImmediately()
    {
        var (model, driver) = Create();
        SnapAt(driver, 100);
        Assert.Equal(FrameClass.None, model.Demand);
        model.Pulse(0.4, 1);
        Assert.Equal(FrameClass.Fps30, model.Demand);
        driver.Run(0.5);
        Assert.Equal(FrameClass.None, model.Demand);
        model.Peak();
        Assert.Equal(FrameClass.Fps30, model.Demand);
    }

    [Fact]
    public void ReduceMotionBlinkIsACrossfade()
    {
        var (model, driver) = Create(reduceMotion: true);
        SnapAt(driver, 100);
        model.Pulse(0.4, 1);
        var samples = new List<double>();
        for (var i = 0; i < 30; i++)
        {
            driver.Step(Dt);
            samples.Add(model.HeadDotOpacity);
        }

        Assert.Equal(0, samples[0], 9);
        Assert.Equal(0.5, samples[6], 9);
        Assert.Equal(1, samples[12], 9);
        Assert.Equal(0.5, samples[18], 9);
        Assert.Equal(0, samples[24], 9);
    }

    [Fact]
    public void OffHasNoBlink()
    {
        var (model, driver) = Create(EffectsIntensity.Off);
        SnapAt(driver, 100);
        model.Pulse(0.4, 1);
        driver.Step(Dt);
        Assert.False(model.HeadDotVisible);
        Assert.Equal(0, model.RingGlowOpacity);
        Assert.Equal(FrameClass.None, model.Demand);
    }

    [Fact]
    public void NewPeakTintsTheRingGold()
    {
        var (model, driver) = Create();
        SnapAt(driver, 100);
        var key = model.ColorKey;
        model.Peak();
        for (var k = 0; k < 50; k++)
        {
            driver.Step(Dt);
            var tau = k * Dt;
            var expected = tau < 0.7 ? Math.Sin(Math.PI * tau / 0.7) : 0;
            Assert.True(Math.Abs(expected - model.GoldTint) <= 1 / 64.0 + 1e-9);
            if (k == 21)
            {
                OrbNeedleModelTests.AssertColor(Rgba.FromHex("#FACC15"), model.HeadColor, 3);
                Assert.NotEqual(key, model.ColorKey);
                Assert.Equal(FrameClass.Fps30, model.Demand);
            }
        }

        Assert.Equal(key, model.ColorKey);
        Assert.Equal(FrameClass.None, model.Demand);
    }

    [Fact]
    public void LightGoldIsDeeper()
    {
        var (model, driver) = Create(dark: false);
        SnapAt(driver, 100);
        model.Peak();
        driver.Run(0.36);
        OrbNeedleModelTests.AssertColor(Rgba.FromHex("#C99700"), model.HeadColor, 2);
    }

    [Fact]
    public void ReduceMotionGoldIsACrossfade()
    {
        var (model, driver) = Create(reduceMotion: true);
        SnapAt(driver, 100);
        model.Peak();
        var samples = new List<double>();
        for (var k = 0; k < 45; k++)
        {
            driver.Step(Dt);
            samples.Add(model.GoldTint);
        }

        Assert.Equal(0.5, samples[6], 9);
        Assert.Equal(1, samples[12], 9);
        Assert.Equal(1, samples[30], 9);
        Assert.Equal(0.5, samples[36], 9);
        Assert.Equal(0, samples[42], 9);
    }

    [Fact]
    public void TierFlashBrightensTheRing()
    {
        var (model, driver) = Create();
        SnapAt(driver, 100);
        var head = model.HeadColor;
        var key = model.ColorKey;
        driver.Flash = 1;
        driver.Step(Dt);
        Assert.True(OkLab.FromRgb(model.HeadColor).L > OkLab.FromRgb(head).L + 0.02);
        Assert.Equal(0.35 * 1.25, model.RingGlowOpacity, 9);
        Assert.NotEqual(key, model.ColorKey);
        Assert.Equal(FrameClass.Fps30, model.Demand);
    }

    [Fact]
    public void IdleDrawsOnlyTheTrackAndNoSignOfLife()
    {
        var (model, driver) = Create(EffectsIntensity.Max);
        SnapAt(driver, 0);
        Assert.False(model.RingVisible);
        Assert.Equal(0, model.Progress);
        Assert.Equal(FrameClass.None, model.Demand);
        driver.SignOfLifeAge = 0.3;
        driver.Run(1);
        Assert.Equal(FrameClass.None, model.Demand);
        Assert.False(model.RingVisible);
        Assert.False(model.HeadDotVisible);
    }

    [Fact]
    public void EnteringIdleEasesTheRingAway()
    {
        var (model, driver) = Create();
        SnapAt(driver, 30);
        var previous = model.Progress;
        Assert.True(previous > 0.2);
        driver.Value = 0;
        var biggestStep = 0.0;
        for (var i = 0; i < 120; i++)
        {
            driver.Step(Dt);
            biggestStep = Math.Max(biggestStep, Math.Abs(model.Progress - previous));
            previous = model.Progress;
        }

        Assert.True(biggestStep < 0.03, $"step {biggestStep}");
        Assert.Equal(0, model.Progress);
        Assert.False(model.RingVisible);
        Assert.Equal(FrameClass.None, model.Demand);
    }

    [Fact]
    public void DemandIsThirtyOnlyWhileSettling()
    {
        var (model, driver) = Create();
        SnapAt(driver, 100);
        Assert.Equal(FrameClass.None, model.Demand);
        driver.Value = 200;
        driver.Step(Dt);
        Assert.Equal(FrameClass.Fps30, model.Demand);
        driver.Run(3);
        Assert.Equal(FrameClass.None, model.Demand);
        Assert.Equal(HeatMath.Heat(200, R), model.Progress, 3);
    }

    [Fact]
    public void HighContrastHasNoGlowOrShadow()
    {
        var (model, driver) = Create(EffectsIntensity.Max, highContrast: true);
        SnapAt(driver, 1.5 * R);
        Assert.True(model.HighContrast);
        Assert.Equal(0, model.RingGlowOpacity);
        Assert.False(model.ShadowVisible);
        Assert.True(model.RingVisible);
    }

    [Fact]
    public void SnapClearsTransientsWithoutCatchUp()
    {
        var (model, driver) = Create();
        SnapAt(driver, 100);
        model.Pulse(0.5, 1);
        model.Peak();
        driver.Step(Dt);
        Assert.True(model.HeadDotVisible);
        driver.Value = 1.5 * R;
        driver.Snap();
        Assert.Equal(1.5, model.Progress, 9);
        Assert.Equal(0, model.GoldTint);
        Assert.Equal(model.Breath * 0.6, model.HeadDotOpacity, 9);
        driver.Step(Dt);
        Assert.Equal(FrameClass.Fps15, model.Demand);
    }

    [Fact]
    public void GeometryKeyChangesOnlyWithTheRing()
    {
        var (model, driver) = Create();
        SnapAt(driver, 350);
        var key = model.GeometryKey;
        var colors = model.ColorKey;
        driver.Run(2);
        Assert.Equal(key, model.GeometryKey);
        Assert.Equal(colors, model.ColorKey);
        driver.Value = 360;
        driver.Run(0.2);
        Assert.NotEqual(key, model.GeometryKey);
    }

    [Fact]
    public void ProgressDoesNotDependOnTheFrameRate()
    {
        double At(double fps, double seconds)
        {
            var (model, driver) = Create();
            SnapAt(driver, 1.6 * R);
            driver.Value = 0;
            driver.Run(seconds, fps);
            return model.Progress;
        }

        foreach (var t in new[] { 1.0 / 3, 2.0 / 3, 1.0 })
        {
            var reference = At(240, t);
            Assert.Equal(reference, At(30, t), 6);
            Assert.Equal(reference, At(72, t), 6);
        }
    }

    [Fact]
    public void UpdateStaysFarBelowTheFrameBudget()
    {
        var (model, driver) = Create(EffectsIntensity.Max);
        SnapAt(driver, 1.5 * R);
        const int frames = 5000;
        var watch = Stopwatch.StartNew();
        for (var i = 0; i < frames; i++)
        {
            if (i % 10 == 0)
            {
                model.Pulse(1, 1);
            }

            driver.Value = 1.5 * R + i % 100;
            driver.Step(Dt);
        }

        var perFrame = watch.Elapsed.TotalMilliseconds / frames;
        output.WriteLine($"minimal Update at Max redline: {perFrame * 1000:F1} µs per frame");
        Assert.True(perFrame < 0.25, $"{perFrame:F4} ms per frame");
    }

    [Fact]
    public void UpdatesDoNotAllocate()
    {
        var (model, driver) = Create(EffectsIntensity.Max);
        SnapAt(driver, 0);
        Exercise(model, driver, 600);
        AllocationProbe.AssertNone(() => Exercise(model, driver, 1000));
    }

    private static void Exercise(MinimalModel model, OrbStyleDriver driver, int frames)
    {
        for (var i = 0; i < frames; i++)
        {
            var phase = i % 600;
            driver.Value = phase < 100 ? 0 : phase < 200 ? 60 : phase < 400 ? 700 : 300;
            driver.Flash = phase % 150 < 20 ? 1 - phase % 150 / 20.0 : 0;
            if (i % 17 == 0)
            {
                model.Pulse(0.7, 1);
            }

            if (i % 250 == 0)
            {
                model.Peak();
            }

            driver.Step(i % 4 == 0 ? 1.0 / 30 : Dt);
            if (i % 997 == 0)
            {
                driver.Snap();
            }
        }
    }
}
