using AgentSpeed.Core;
using AgentSpeed.Presentation.Colors;
using AgentSpeed.Presentation.Heat;
using AgentSpeed.Presentation.Motion;
using AgentSpeed.Presentation.Orb;
using AgentSpeed.Presentation.Orb.Gpu;
using AgentSpeed.Presentation.Orb.Gpu.Styles;
using AgentSpeed.Presentation.Orb.Styles;
using AgentSpeed.Presentation.Settings;

namespace AgentSpeed.Presentation.Tests.Gpu;

/// <summary>The floating <c>led</c>'s v1.4 draw list (DESIGN 2.1): passes, gating, bounds and the layers around the readout.</summary>
public class LedSilkDrawTests
{
    private const double Redline = 2000;

    private static readonly OrbReadoutStrings Strings = new("tok/s", "NEW PEAK",
        Enum.GetValues<SpeedTitle>().Select(t => t.ToString().ToUpperInvariant()).ToArray(), true);

    private static OrbGpuOrb Create(double size = 148, bool dark = true, EffectsIntensity level = EffectsIntensity.Standard,
        bool reduceMotion = false, bool highContrast = false)
    {
        var effects = new EffectsPolicy(level, reduceMotion, highContrast);
        var orb = new OrbGpuOrb(7, new FakeGlyphs());
        orb.Configure(new OrbGpuConfig(OrbStyle.Led, size, effects, true, true, Redline, Strings, null, SourceSet.Empty),
            new OrbGpuContext(size, 2, dark ? OrbTokens.Dark : OrbTokens.Light, effects, highContrast ? OrbContrastColors.Default : null));
        return orb;
    }

    private static double Drive(OrbGpuOrb orb, double time, double value, double seconds)
    {
        orb.SetData(time, value, HeatMath.RawTier(value, Redline), SpeedTitles.Raw(value, Redline), [], "today", null);
        var end = time + seconds;
        while (time < end)
        {
            time += 1 / 60.0;
            orb.Render(time);
        }

        return time;
    }

    private static HashSet<int> Modes(OrbDrawList list)
    {
        var modes = new HashSet<int>();
        foreach (var batch in list.Batches)
        {
            if (batch.Pass == OrbPass.Led)
            {
                modes.Add(batch.Mode);
            }
        }

        return modes;
    }

    private static int CountShape(OrbDrawList list, OrbShape shape)
    {
        var count = 0;
        foreach (var instance in list.Instances)
        {
            count += instance.Shape == shape ? 1 : 0;
        }

        return count;
    }

    [Fact]
    public void TheFloatingLedTurnsOnTheSilkSceneAndOtherStylesDoNot()
    {
        var orb = Create();
        Assert.True(orb.Scene.SilkEffects);
        Assert.Equal(OrbScene.SilkPulseDuration, orb.Scene.PulseLength);
        orb.Configure(orb.Config! with { Style = OrbStyle.Needle }, orb.Context!);
        Assert.False(orb.Scene.SilkEffects);
        Assert.Equal(OrbScene.PulseDuration, orb.Scene.PulseLength);
    }

    [Fact]
    public void RedlineStandardDrawsEveryPass()
    {
        var orb = Create();
        Drive(orb, 100, 2600, 2.5);
        var list = orb.List;
        Assert.Equal(new HashSet<int> { LedDraw.HaloMode, LedDraw.SilkMode, LedDraw.FlareMode }, Modes(list));
        Assert.True(list.Led.Halo.Z > 0 && list.Led.Halo.W > 0, "corona and warp amounts");
        Assert.True(list.Led.Peak.Z > 0, "tip flare");
        Assert.True(list.Led.Look.Z > 0, "ripples past the redline");
        Assert.Equal(1, CountShape(list, OrbShape.BandArc));
        Assert.Equal(1, list.Led.Heat.W);
    }

    [Fact]
    public void TheHaloPassComesBeforeTheBodyAndTheSilkAfterIt()
    {
        var orb = Create();
        Drive(orb, 100, 2600, 1);
        var batches = orb.List.Batches;
        var halo = -1;
        var silk = -1;
        for (var i = 0; i < batches.Length; i++)
        {
            if (batches[i].Pass == OrbPass.Led && batches[i].Mode == LedDraw.HaloMode)
            {
                halo = i;
            }

            if (batches[i].Pass == OrbPass.Led && batches[i].Mode == LedDraw.SilkMode)
            {
                silk = i;
            }
        }

        Assert.InRange(halo, 0, silk - 1);

        // The body's gradient circle (radius S/2) lies between the two passes.
        var bodyAt = -1;
        var instances = orb.List.Instances;
        for (var i = 0; i < instances.Length; i++)
        {
            if (instances[i].Shape == OrbShape.Circle && Math.Abs(instances[i].B.X - 74) < 1e-3 && instances[i].Extra2.X == (float)OrbFill.Linear)
            {
                bodyAt = i;
                break;
            }
        }

        Assert.InRange(bodyAt, batches[halo].Start, batches[silk].Start - 1);
    }

    [Theory]
    [InlineData(EffectsIntensity.Off, false)]
    [InlineData(EffectsIntensity.Soft, false)]
    [InlineData(EffectsIntensity.Max, false)]
    [InlineData(EffectsIntensity.Max, true)]
    public void LevelsGateThePassesAndParticles(EffectsIntensity level, bool reduceMotion)
    {
        var orb = Create(level: level, reduceMotion: reduceMotion);
        var time = Drive(orb, 100, 2600, 2.5);
        orb.Peak(time);
        Drive(orb, time, 2600, 0.3);
        var list = orb.List;
        var full = level >= EffectsIntensity.Standard && !reduceMotion;
        Assert.Contains(LedDraw.SilkMode, Modes(list));
        Assert.Equal(full, Modes(list).Contains(LedDraw.HaloMode));
        Assert.Equal(full, list.Led.Peak.Z > 0);
        Assert.Equal(full, CountShape(list, OrbShape.Stroke) > 0);
        Assert.Equal(full, orb.Style is LedDraw { Effects.Particles.Count: > 0 });
        Assert.Equal(full, list.Led.Look.Z > 0);
        if (!full)
        {
            Assert.Equal(0, list.Led.Halo.Z);
            Assert.Equal(0, list.Led.Halo.W);
        }
    }

    [Fact]
    public void MaxScalesTheAmountsByOnePointSix()
    {
        var standard = Create();
        var max = Create(level: EffectsIntensity.Max);
        Drive(standard, 100, 2600, 1);
        Drive(max, 100, 2600, 1);
        Assert.Equal(standard.List.Led.Halo.Z * 1.6, max.List.Led.Halo.Z, 4);
        Assert.Equal(standard.List.Led.Halo.W * 1.6, max.List.Led.Halo.W, 4);
        Assert.Equal(standard.List.Led.Peak.Z * 1.6, max.List.Led.Peak.Z, 4);
        Assert.Equal(standard.List.Led.Look.Y * 1.21, max.List.Led.Look.Y, 4);
    }

    [Fact]
    public void ThumbnailsAndHighContrastSkipTheHalo()
    {
        var thumbnail = Create(48, level: EffectsIntensity.Max);
        Drive(thumbnail, 100, 2600, 1);
        Assert.Equal(new HashSet<int> { LedDraw.SilkMode }, Modes(thumbnail.List));

        var contrast = Create(highContrast: true);
        Drive(contrast, 100, 2600, 1);
        Assert.Empty(Modes(contrast.List));
    }

    [Fact]
    public void EverythingOutsideTheDiscStaysWithinTheWindow()
    {
        var orb = Create(240, level: EffectsIntensity.Max);
        var time = Drive(orb, 100, 3600, 2);
        orb.Pulse(time, 3600, 40_000);
        orb.Peak(time);
        var outer = LedDraw.OuterRadius * 240;
        for (var i = 0; i < 90; i++)
        {
            time = Drive(orb, time, 3600, 1 / 60.0);
            var list = orb.List;
            Assert.Equal(outer, list.Led.Quad0.X, 3);
            foreach (var instance in list.Instances)
            {
                if (instance.Shape == OrbShape.Ring)
                {
                    Assert.InRange(instance.B.X, 0, outer + 1e-3);
                }
            }

            if (orb.Style is LedDraw led)
            {
                foreach (var p in led.Effects.Particles.Particles)
                {
                    Assert.True(Math.Sqrt(p.X * p.X + p.Y * p.Y) < LedDraw.OuterRadius);
                }
            }

            var quad = list.Led.Quad2;
            Assert.InRange(quad.X, 0, quad.Z);
            Assert.InRange(quad.Z, quad.X, (float)list.Canvas);
        }
    }

    [Fact]
    public void LightningIsDrawnAboveTheReadout()
    {
        var orb = Create();
        var time = Drive(orb, 100, 1500, 1);
        ((LedDraw)orb.Style!).Effects.ForceLightning();
        time = Drive(orb, time, 1500, 2 / 60.0);
        var list = orb.List;
        Assert.True(list.OverlayBatchStart >= 0);
        var strokesAfter = 0;
        var batches = list.Batches;
        for (var b = list.OverlayBatchStart; b < batches.Length; b++)
        {
            for (var i = batches[b].Start; i < batches[b].Start + batches[b].Count; i++)
            {
                strokesAfter += list.Instances[i].Shape == OrbShape.Stroke ? 1 : 0;
            }
        }

        Assert.True(strokesAfter >= LedLightning.MainPoints - 1, $"{strokesAfter} bolt strokes above the text");
        Assert.True(((LedDraw)orb.Style!).Effects.Lightning.ZapCount > 0);
    }

    [Fact]
    public void TheReadoutNotesEveryCellOfTheValue()
    {
        var orb = Create();
        Drive(orb, 100, 700, 1);
        var cells = orb.Readout.ValueCells;
        Assert.Equal(orb.Scene.Odometer.Current, orb.Readout.ValueCellsText);
        Assert.Equal(orb.Readout.ValueCellsText.Length, cells.Length);
        for (var i = 1; i < cells.Length; i++)
        {
            Assert.True(cells[i].X > cells[i - 1].X);
            Assert.Equal(cells[0].Y, cells[i].Y, 9);
        }

        // Centered on the disc, the em box above the value's baseline (c + 0.012 S).
        Assert.InRange((cells[0].X + cells[^1].X) / 2, -0.01, 0.01);
        Assert.Equal(0.012 - OrbReadoutDraw.CellCenter * 0.24, cells[0].Y, 3);
        Assert.Equal(OrbReadoutDraw.CellHeight * 0.24, cells[0].Height, 3);
    }

    [Fact]
    public void TheValueGetsItsBackingWideGlowAndSplitAtTheRedline()
    {
        var plain = Create(dark: true);
        Drive(plain, 100, 120, 2);
        var cruising = plain.List.TextCount;
        var orb = Create(dark: true);
        Drive(orb, 100, 2600, 2);
        var colors = new HashSet<(float, float, float)>();
        foreach (var text in orb.List.Texts)
        {
            colors.Add((text.Color.X, text.Color.Y, text.Color.Z));
        }

        Assert.Contains((1f, 60 / 255f, 140 / 255f), colors);
        Assert.Contains((60 / 255f, 210 / 255f, 1f), colors);
        Assert.True(orb.List.TextCount > cruising + 3 * 4, $"{orb.List.TextCount} vs {cruising}");

        var light = Create(dark: false);
        Drive(light, 100, 2600, 2);
        foreach (var text in light.List.Texts)
        {
            Assert.NotEqual((1f, 60 / 255f, 140 / 255f), (text.Color.X, text.Color.Y, text.Color.Z));
        }
    }

    [Fact]
    public void TheSceneLeansAndJittersHarderAndTheCometSpeedsUp()
    {
        var scene = new OrbScene(3) { SilkEffects = true, Redline = Redline };
        var classic = new OrbScene(3) { Redline = Redline };
        foreach (var s in new[] { scene, classic })
        {
            s.Snap(0);
            s.SetTarget(4000, SpeedTier.Redline);
            for (var t = 1; t <= 180; t++)
            {
                s.Update(t / 60.0);
            }
        }

        Assert.Equal(OrbScene.SilkLeanDegrees * scene.Heat, scene.SkewDegrees, 2);
        Assert.Equal(OrbScene.LeanDegrees * classic.Heat, classic.SkewDegrees, 2);
        Assert.InRange(Math.Abs(scene.JitterX), 0, 0.9 + 1.2 + 1e-9);
        Assert.Equal(0, scene.Particles.Count);
        Assert.True(classic.Particles.Count > 0);
    }

    [Fact]
    public void ResponseWavesLastLongerAndMoveTheSilk()
    {
        var orb = Create();
        var time = Drive(orb, 100, 1500, 1);
        orb.Pulse(time, 1500, 4000);
        time = Drive(orb, time, 1500, 0.75);
        Assert.Equal(1, orb.Scene.PulseCount);
        Assert.Equal(1, orb.List.Led.Wave0.W);
        Assert.Equal(0, orb.List.Led.Wave1.W);
        Assert.Equal(1, orb.List.Led.Look.W);
        var waveRings = 0;
        foreach (var instance in orb.List.Instances)
        {
            waveRings += instance.Shape == OrbShape.Ring && instance.Extra.X == 0 ? 1 : 0;
        }

        Assert.Equal(2, waveRings);
        Drive(orb, time, 1500, 0.15);
        Assert.Equal(0, orb.Scene.PulseCount);
    }

    [Fact]
    public void AWarmLedFrameWithEveryEffectAllocatesNothing()
    {
        var orb = Create(240, level: EffectsIntensity.Max);
        var time = Drive(orb, 100, 3600, 2);
        orb.Peak(time);
        orb.Pulse(time, 3600, 40_000);
        ((LedDraw)orb.Style!).Effects.ForceLightning();
        time = Drive(orb, time, 3600, 0.2);
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 20; i++)
        {
            orb.Render(time += 1 / 60.0);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }
}
