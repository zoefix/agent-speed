using System.Numerics;
using System.Runtime.InteropServices;
using AgentSpeed.Presentation.Colors;
using AgentSpeed.Presentation.Heat;
using AgentSpeed.Presentation.Motion;
using AgentSpeed.Presentation.Orb;
using AgentSpeed.Presentation.Orb.Gpu;
using AgentSpeed.Presentation.Orb.Gpu.Styles;
using AgentSpeed.Presentation.Orb.Styles;
using AgentSpeed.Presentation.Settings;
using AgentSpeed.Presentation.Tests.TestSupport;

namespace AgentSpeed.Presentation.Tests.Gpu;

public class LiquidDrawTests
{
    private const int Budget = 400;
    private const double Dt = 1.0 / 60;
    private const double Tau = 2 * Math.PI;
    private const double R = LiquidGeometry.GlassRadius;

    public static TheoryData<bool, bool, EffectsIntensity, double> Variants()
    {
        var data = new TheoryData<bool, bool, EffectsIntensity, double>();
        foreach (var (dark, hc) in new[] { (true, false), (false, false), (true, true) })
        {
            foreach (var level in new[] { EffectsIntensity.Off, EffectsIntensity.Soft, EffectsIntensity.Standard, EffectsIntensity.Max })
            {
                foreach (var size in new[] { 48.0, 96, 148, 240 })
                {
                    data.Add(dark, hc, level, size);
                }
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Variants))]
    public void TourStaysFiniteWithinBudgetWithBothLiquidPasses(bool dark, bool hc, EffectsIntensity level, double size)
    {
        var rig = new Rig(level, size, dark, hc);
        var most = 0;
        rig.Tour(() =>
        {
            Check(rig.List, dark && !hc, hc);
            most = Math.Max(most, rig.List.Count);
            Assert.Equal(hc, rig.Draw.BelowSurface is null);
        });

        Assert.InRange(most, 1, Budget);
    }

    [Fact]
    public void ReduceMotionTourStaysValid()
    {
        foreach (var dark in new[] { true, false })
        {
            var rig = new Rig(EffectsIntensity.Max, 148, dark, reduceMotion: true);
            rig.Tour(() =>
            {
                Check(rig.List, dark, false);
                Assert.Equal(0, rig.List.Liquid.Surface.Y);
            });
        }
    }

    [Theory]
    [InlineData(EffectsIntensity.Soft, 148.0)]
    [InlineData(EffectsIntensity.Standard, 148.0)]
    [InlineData(EffectsIntensity.Max, 240.0)]
    [InlineData(EffectsIntensity.Standard, 96.0)]
    public void ShaderSurfaceMatchesTheModel(EffectsIntensity level, double size)
    {
        var rig = new Rig(level, size, dark: true);
        var model = (LiquidModel)rig.Draw.Model;
        var ripples = 0;
        var currents = 0;
        rig.Tour(() =>
        {
            ripples = Math.Max(ripples, model.RippleCount);
            currents += CompareSurface(rig, model) ? 1 : 0;
        });

        // Long enough for the current drift to wrap (400π) at least once.
        var drift = model.CurrentDrift;
        var wrapped = false;
        rig.Drive(1.8, 210, () =>
        {
            currents += CompareSurface(rig, model) ? 1 : 0;
            wrapped |= model.CurrentDrift < drift;
            drift = model.CurrentDrift;
        });
        Assert.True(ripples > 0);
        Assert.True(wrapped);
        Assert.Equal(size >= 120, currents > 0);
    }

    [Fact]
    public void ParticlesNeedMotion()
    {
        Assert.True(Particles(EffectsIntensity.Standard, false) > 10);
        Assert.True(Particles(EffectsIntensity.Soft, false) > 0);
        Assert.Equal(0, Particles(EffectsIntensity.Off, false));
        Assert.Equal(0, Particles(EffectsIntensity.Max, true));

        static int Particles(EffectsIntensity level, bool reduceMotion)
        {
            var rig = new Rig(level, 148, dark: true, reduceMotion: reduceMotion);
            var most = 0;
            rig.Drive(1.8, 3, () => most = Math.Max(most, ClippedCount(rig.List)));
            return most;
        }
    }

    [Fact]
    public void HighContrastIsASolidHighlightFill()
    {
        var rig = new Rig(EffectsIntensity.Standard, 148, dark: true, highContrast: true);
        rig.Drive(1.2, 2);
        var u = rig.List.Liquid;
        var highlight = OrbGpuInstance.ToVector(OrbContrastColors.Default.Highlight);
        Assert.Equal(highlight with { W = 0.35f }, u.Liquid);
        Assert.Equal(highlight, u.Meniscus);
        Assert.Equal(Vector4.Zero, u.Spill);
        Assert.Equal(Vector4.Zero, u.Glow);
        Assert.Equal(Vector4.Zero, u.Current);
        Assert.Equal(0, u.MeniscusGlow.W);
        Assert.Equal(0, u.Flags.Y);
        Assert.Null(rig.Draw.BelowSurface);
        Assert.Equal(0, ClippedCount(rig.List));
    }

    [Fact]
    public void ReadoutBelowTheSurfaceHoldsOnTheLiquid()
    {
        var dark = new Rig(EffectsIntensity.Standard, 148, dark: true);
        dark.Drive(0.5, 2);
        var below = Assert.NotNull(dark.Draw.BelowSurface);
        Assert.Equal(Rgba.White.WithAlpha(0.97), below.Value);
        Assert.Equal(Rgba.White.WithAlpha(0.80), below.Unit);
        Assert.Equal(Rgba.White.WithAlpha(0.94), below.Title);
        Assert.Equal(Rgba.Black.WithAlpha(0.55), below.Shadow);
        Assert.Equal(0.015 * 148, below.ShadowSigma, 9);

        var light = new Rig(EffectsIntensity.Standard, 148, dark: false);
        light.Drive(0.5, 2);
        below = Assert.NotNull(light.Draw.BelowSurface);
        Assert.Null(below.Shadow);
        Assert.False(below.ValueGlow);
        Assert.Equal(((LiquidModel)light.Draw.Model).ReadoutBelowTitleColor, below.Title);
    }

    [Fact]
    public void BodyDropsTheLensAndTakesTheLiquidGradient()
    {
        var dark = new Rig(EffectsIntensity.Standard, 148, dark: true);
        Assert.Equal(new OrbBodyLook(LiquidModel.DarkBodyTop, LiquidModel.DarkBodyBottom, false), dark.Draw.Body);
        var light = new Rig(EffectsIntensity.Standard, 148, dark: false);
        Assert.Equal(new OrbBodyLook(LiquidModel.LightBodyTop, LiquidModel.LightBodyBottom, false), light.Draw.Body);
    }

    [Fact]
    public void SameSeedDrawsTheSameFrames()
    {
        var a = new Rig(EffectsIntensity.Max, 148, dark: true, seed: 9);
        var b = new Rig(EffectsIntensity.Max, 148, dark: true, seed: 9);
        var prints = new List<ulong>();
        a.Tour(() => prints.Add(a.List.Fingerprint()));
        var index = 0;
        b.Tour(() => Assert.Equal(prints[index++], b.List.Fingerprint()));
        Assert.Equal(prints.Count, index);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void WarmBuildAllocatesNothing(bool dark, bool hc)
    {
        var rig = new Rig(EffectsIntensity.Max, 148, dark, hc);
        rig.Drive(1.8, 1.5);
        rig.Pulse();
        rig.Drive(1.8, 0.1);
        Assert.True(hc || ClippedCount(rig.List) > 0);
        AllocationProbe.AssertNone(rig.Build);
    }

    /// <summary>Evaluates the shader's surface and currents from the uniforms against the model; false when no current is drawn.</summary>
    private static bool CompareSurface(Rig rig, LiquidModel model)
    {
        var u = rig.List.Liquid;
        double s = u.Frame.Z;
        double cx = u.Frame.X;
        double cy = u.Frame.Y;
        for (var i = 0; i <= 8; i++)
        {
            var x = -R + 2 * R * i / 8;
            Near(cy + model.SurfaceY(x) * s, WaveY(u, cx + x * s, back: false));
            if (u.Surface.W > 0)
            {
                Near(cy + model.BackSurfaceY(x) * s, WaveY(u, cx + x * s, back: true));
            }
        }

        if (u.Current.W <= 0)
        {
            return false;
        }

        double level = u.Surface.X;
        var bottom = cy + R * s;
        for (var k = 0; k < 3; k++)
        {
            for (var i = 0; i <= 8; i++)
            {
                var t = i / 8.0;
                var phase = u.CurrentGeom.Z * (0.35 + 0.2 * k) + 2.1 * k;
                var y = level + (bottom - level) * (0.25 + 0.25 * k) + 0.03 * s * Math.Sin(t * 7 + phase)
                    + 0.015 * s * Math.Sin(t * 13 - phase * 1.7);
                Near(cy + model.CurrentY(k, t) * s, y);
            }
        }

        return true;
    }

    private static void Near(double expected, double actual) =>
        Assert.True(Math.Abs(expected - actual) < 0.01, $"{actual} differs from {expected}");

    /// <summary><c>orb_liquid_wave_y</c> of the liquid shader.</summary>
    private static double WaveY(in OrbLiquidUniforms u, double x, bool back)
    {
        double s = u.Frame.Z;
        var xr = x - u.Frame.X;
        var offset = back ? 1.7 : 0;
        var a = u.Surface.Y * (back ? 0.75 : 1);
        var y = u.Surface.X - (back ? 0.008 * s : 0);
        if (a > 0)
        {
            y += a * (0.55 * Math.Sin(xr / (0.9 * s) * Tau + u.Phases.X + offset)
                + 0.30 * Math.Sin(xr / (0.47 * s) * Tau - u.Phases.Y + offset * 1.3)
                + 0.15 * Math.Sin(xr / (0.23 * s) * Tau + u.Phases.Z + offset * 0.7));
        }

        if (u.Surface.Z > 0)
        {
            y += u.Surface.Z * Math.Sin(xr / (0.11 * s) * Tau + u.Phases.W + offset) * (0.7 + 0.3 * Math.Sin(xr / (0.05 * s) + u.Phases.W * 1.7));
        }

        foreach (var ripple in new[] { u.Ripple0, u.Ripple1, u.Ripple2, u.Ripple3 })
        {
            if (ripple.W <= 0)
            {
                continue;
            }

            var d = Math.Abs(x - ripple.X) - ripple.Y;
            var k = d / (0.075 * s);
            y -= ripple.Z * Math.Exp(-k * k) * Math.Cos(Tau * d / (0.1 * s)) * (back ? 0.6 : 1);
        }

        return y;
    }

    private static void Check(OrbDrawList list, bool additive, bool hc)
    {
        Assert.InRange(list.Count, 1, Budget);
        foreach (var value in MemoryMarshal.Cast<OrbGpuInstance, float>(list.Instances))
        {
            Assert.True(float.IsFinite(value));
        }

        Assert.True(list.HasLiquid);
        foreach (var value in MemoryMarshal.Cast<OrbLiquidUniforms, float>(new ReadOnlySpan<OrbLiquidUniforms>(in list.Liquid)))
        {
            Assert.True(float.IsFinite(value));
        }

        var covered = 0;
        var passes = 0;
        foreach (var batch in list.Batches)
        {
            if (batch.Pass == OrbPass.Liquid)
            {
                Assert.Equal(passes++, batch.Mode);
                Assert.Equal(covered, batch.Start);
                continue;
            }

            Assert.Equal(covered, batch.Start);
            covered += batch.Count;
            Assert.True(additive || batch.Blend == OrbBlend.Over, "additive light outside the dark appearance");
        }

        Assert.Equal(2, passes);
        Assert.Equal(list.Count, covered);
        foreach (var item in list.Instances)
        {
            Assert.InRange(item.Color.W, 0, 1);
            Assert.InRange(item.Color2.W, 0, 1);
            Assert.True(item.B.X >= 0 && item.B.Y >= 0 && item.B.W >= 0);
            Assert.True(!hc || item.B.W == 0, "glow in high contrast");
        }
    }

    /// <summary>Instances clipped to the glass: the particles.</summary>
    private static int ClippedCount(OrbDrawList list)
    {
        var count = 0;
        foreach (var batch in list.Batches)
        {
            count += batch.Pass == OrbPass.Shapes && batch.Clip > 0 && Math.Abs(batch.Clip - R * list.Canvas / 1.5) < 0.01 ? batch.Count : 0;
        }

        return count;
    }

    /// <summary>The frame flow of the floating orb: scene update, model update, build.</summary>
    private sealed class Rig
    {
        private readonly bool _dark;
        private readonly double _size;
        private readonly EffectsPolicy _effects;
        private double _now = 600;

        public Rig(EffectsIntensity level, double size, bool dark, bool highContrast = false, bool reduceMotion = false, int seed = 5)
        {
            _dark = dark;
            _size = size;
            _effects = new EffectsPolicy(level, reduceMotion, highContrast);
            Scene = new OrbScene(3)
            {
                Effects = _effects,
                Redline = HeatMath.ReferenceRedline,
                LedEffects = false,
                SharedSignOfLife = true,
                SignOfLifeDuration = LiquidModel.SignOfLifeDuration,
            };
            Draw = new LiquidDraw(seed);
            var tokens = OrbTokens.For(dark ? OrbAppearance.Dark : OrbAppearance.Light);
            Draw.Configure(new OrbGpuContext(size, 1.5, tokens, _effects, highContrast ? OrbContrastColors.Default : null));
            List.Canvas = size * 1.5;
            Scene.Snap(_now);
            Frame = Make(0);
            Draw.Model.Snap(Frame);
        }

        public OrbScene Scene { get; }

        public LiquidDraw Draw { get; }

        public OrbDrawList List { get; } = new();

        public OrbFrame Frame { get; private set; }

        /// <summary>Idle → cruise → redline → overdrive (pulses and a new peak) → idle with a sign of life.</summary>
        public void Tour(Action check)
        {
            Drive(0, 1.5, check);
            Drive(0.1, 1.5, check);
            Drive(0.85, 2, check);
            Speed(1.8);
            Peak();
            for (var i = 0; i < 6; i++)
            {
                Pulse(1000 + 3000 * i);
                Drive(1.8, 0.4, check);
            }

            Drive(0, 7, check);
        }

        public void Speed(double ratio)
        {
            var value = ratio * Scene.Redline;
            Scene.SetTarget(value, HeatMath.RawTier(value, Scene.Redline));
        }

        public void Pulse(long tokens = 4000)
        {
            if (Scene.TriggerPulse(Scene.TargetValue, tokens))
            {
                Draw.Model.Pulse(Scene.HeatRaw, Math.Clamp(Math.Log10(tokens) / 3, 0.3, 1.25));
            }
        }

        public void Peak()
        {
            Scene.TriggerPeak();
            Draw.Model.Peak();
        }

        public void Build()
        {
            List.Reset();
            Draw.Build(List, Scene, Frame);
        }

        public void Drive(double ratio, double seconds, Action? check = null)
        {
            Speed(ratio);
            var frames = (int)Math.Round(seconds / Dt);
            for (var i = 0; i < frames; i++)
            {
                _now += Dt;
                Scene.Update(_now);
                Frame = Make(Dt);
                Draw.Model.Update(Frame);
                Build();
                check?.Invoke();
            }
        }

        private OrbFrame Make(double dt) => new(_now, dt, Scene.Heat, Scene.Overdrive, Scene.HeatRaw, Scene.OverdriveRaw, Scene.Tier, _size,
            _effects, _dark, Scene.Flash, Scene.SignOfLifeAge);
    }
}
