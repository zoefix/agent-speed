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

public class NeedleDrawTests
{
    private const int Budget = 400;
    private const double Dt = 1.0 / 60;

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
    public void TourStaysFiniteWithinBudgetAndBlendsByAppearance(bool dark, bool hc, EffectsIntensity level, double size)
    {
        var rig = new Rig(level, size, dark, hc);
        var most = 0;
        rig.Tour(() =>
        {
            Check(rig.List, dark && !hc, hc);
            most = Math.Max(most, rig.List.Count);
        });

        Assert.InRange(most, 1, Budget);
    }

    [Fact]
    public void ReduceMotionTourStaysValid()
    {
        foreach (var dark in new[] { true, false })
        {
            var rig = new Rig(EffectsIntensity.Max, 148, dark, reduceMotion: true);
            rig.Tour(() => Check(rig.List, dark, false));
        }
    }

    [Fact]
    public void RedlineGlowsTheRedZoneFromStandard()
    {
        var radius = NeedleGeometry.RedZoneRadius * 148;
        Assert.True(HasRedGlow(EffectsIntensity.Standard));
        Assert.True(HasRedGlow(EffectsIntensity.Max));
        Assert.False(HasRedGlow(EffectsIntensity.Soft));

        bool HasRedGlow(EffectsIntensity level)
        {
            var rig = new Rig(level, 148, dark: true);
            rig.Drive(0.9, 2);
            foreach (var item in rig.List.Instances)
            {
                if (item.Shape == OrbShape.BandArc && Math.Abs(item.B.X - radius) < 0.01 && item.Extra.X == 0 && item.B.W > 0)
                {
                    return true;
                }
            }

            return false;
        }
    }

    [Fact]
    public void PinnedNeedleThrowsSparksFromStandardOnly()
    {
        Assert.True(SparkCount(EffectsIntensity.Standard, dark: true) > 0);
        Assert.True(SparkCount(EffectsIntensity.Max, dark: false) > 0);
        Assert.Equal(0, SparkCount(EffectsIntensity.Soft, dark: true));

        static int SparkCount(EffectsIntensity level, bool dark)
        {
            var rig = new Rig(level, 148, dark);
            rig.Drive(1.8, 1.5);
            var model = (NeedleModel)rig.Draw.Model;
            var capsules = Count(rig.List, OrbShape.Capsule);
            // 41 ticks and the spine; the rest are sparks.
            Assert.Equal(model.Sparks.Length, capsules - NeedleGeometry.TickCount - 1);
            return model.Sparks.Length;
        }
    }

    [Theory]
    [InlineData(48.0, 9, false)]
    [InlineData(96.0, 9, true)]
    [InlineData(148.0, 41, true)]
    public void LevelOfDetailFollowsTheSize(double size, int ticks, bool hub)
    {
        var rig = new Rig(EffectsIntensity.Standard, size, dark: true);
        rig.Drive(0.3, 2);
        Assert.Equal(ticks + 1, Count(rig.List, OrbShape.Capsule));
        var hubRadius = (float)(NeedleGeometry.HubRadius * size);
        var hasHub = false;
        var lamps = 0;
        foreach (var item in rig.List.Instances)
        {
            hasHub |= item.Shape == OrbShape.Circle && Math.Abs(item.B.X - hubRadius) < 0.001;
            lamps += item.Shape == OrbShape.Circle && Math.Abs(item.B.X - NeedleGeometry.LampSize * size) < 0.001 ? 1 : 0;
        }

        Assert.Equal(hub, hasHub);
        Assert.Equal(size >= 120 ? NeedleGeometry.LampCount : 0, lamps);
    }

    [Fact]
    public void LightTraceRunsBetweenLabelColors()
    {
        var rig = new Rig(EffectsIntensity.Standard, 148, dark: false);
        rig.Drive(0.4, 3);
        var model = (NeedleModel)rig.Draw.Model;
        var runs = 0;
        foreach (var item in rig.List.Instances)
        {
            if (item.Shape == OrbShape.BandArc && Math.Abs(item.B.X - NeedleGeometry.TraceRadius * 148) < 0.01)
            {
                runs++;
                Assert.Equal((float)OrbFill.Solid, item.Extra2.X);
            }
        }

        Assert.Equal(Math.Max(2, (int)Math.Ceiling(model.TraceEnd * 16)), runs);
    }

    [Fact]
    public void DarkTraceSamplesThePalette()
    {
        var rig = new Rig(EffectsIntensity.Standard, 148, dark: true);
        rig.Drive(0.4, 3);
        var model = (NeedleModel)rig.Draw.Model;
        var trace = Find(rig.List, item => item.Shape == OrbShape.BandArc && Math.Abs(item.B.X - NeedleGeometry.TraceRadius * 148) < 0.01);
        Assert.Equal((float)OrbFill.Palette, trace.Extra2.X);
        Assert.Equal((float)model.TraceEnd, trace.Extra2.Z, 5);
        Assert.Equal((float)(model.TraceOpacityStart / model.TraceOpacityEnd), trace.Extra2.W, 5);
    }

    [Fact]
    public void HighContrastUsesTheSystemColorsWithoutGlows()
    {
        var contrast = OrbContrastColors.Default;
        var rig = new Rig(EffectsIntensity.Standard, 148, dark: true, highContrast: true);
        rig.Drive(1.5, 2);
        var text = OrbGpuInstance.ToVector(contrast.WindowText);
        var blade = Find(rig.List, item => item.Shape == OrbShape.Blade);
        Assert.Equal(text, blade.Color);
        foreach (var item in rig.List.Instances)
        {
            Assert.Equal(0, item.B.W);
        }
    }

    [Fact]
    public void SameSeedDrawsTheSameFrames()
    {
        var a = new Rig(EffectsIntensity.Max, 148, dark: true, seed: 11);
        var b = new Rig(EffectsIntensity.Max, 148, dark: true, seed: 11);
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
        rig.Drive(1.8, 1);
        rig.Pulse();
        rig.Step();
        Assert.True(hc || ((NeedleModel)rig.Draw.Model).Sparks.Length > 0);
        AllocationProbe.AssertNone(rig.Build);
    }

    private static void Check(OrbDrawList list, bool additive, bool hc)
    {
        Assert.InRange(list.Count, 1, Budget);
        foreach (var value in MemoryMarshal.Cast<OrbGpuInstance, float>(list.Instances))
        {
            Assert.True(float.IsFinite(value));
        }

        var covered = 0;
        foreach (var batch in list.Batches)
        {
            Assert.Equal(OrbPass.Shapes, batch.Pass);
            Assert.Equal(covered, batch.Start);
            covered += batch.Count;
            Assert.True(additive || batch.Blend == OrbBlend.Over, "additive light outside the dark appearance");
        }

        Assert.Equal(list.Count, covered);
        Assert.False(list.HasLiquid);
        foreach (var item in list.Instances)
        {
            Assert.InRange(item.Color.W, 0, 1);
            Assert.InRange(item.Color2.W, 0, 1);
            Assert.True(item.B.X >= 0 && item.B.Y >= 0 && item.B.W >= 0);
            Assert.True(!hc || item.B.W == 0, "glow in high contrast");
        }
    }

    private static int Count(OrbDrawList list, OrbShape shape)
    {
        var count = 0;
        foreach (var item in list.Instances)
        {
            count += item.Shape == shape ? 1 : 0;
        }

        return count;
    }

    private static OrbGpuInstance Find(OrbDrawList list, Func<OrbGpuInstance, bool> match)
    {
        foreach (var item in list.Instances)
        {
            if (match(item))
            {
                return item;
            }
        }

        throw new InvalidOperationException("No such instance.");
    }

    /// <summary>The frame flow of the floating orb: scene update, model update, build.</summary>
    private sealed class Rig
    {
        private readonly bool _dark;
        private readonly double _size;
        private readonly EffectsPolicy _effects;
        private double _now = 600;

        public Rig(EffectsIntensity level, double size, bool dark, bool highContrast = false, bool reduceMotion = false, uint seed = 5)
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
                SignOfLifeDuration = NeedleModel.SignOfLifeDuration,
            };
            Draw = new NeedleDraw(seed);
            var tokens = OrbTokens.For(dark ? OrbAppearance.Dark : OrbAppearance.Light);
            Draw.Configure(new OrbGpuContext(size, 1.5, tokens, _effects, highContrast ? OrbContrastColors.Default : null));
            Scene.Snap(_now);
            Frame = Make(0);
            Draw.Model.Snap(Frame);
        }

        public OrbScene Scene { get; }

        public NeedleDraw Draw { get; }

        public OrbDrawList List { get; } = new();

        public OrbFrame Frame { get; private set; }

        /// <summary>Idle → cruise → redline → overdrive (pulses and a new peak) → idle with a sign of life.</summary>
        public void Tour(Action check)
        {
            Run(0, 1.5, check);
            Run(0.1, 1.5, check);
            Run(0.85, 2, check);
            Speed(1.8);
            Peak();
            for (var i = 0; i < 6; i++)
            {
                Pulse(1000 + 3000 * i);
                Run(1.8, 0.4, check);
            }

            Run(0, 7, check);
        }

        public void Drive(double ratio, double seconds) => Run(ratio, seconds, null);

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

        public void Step()
        {
            _now += Dt;
            Scene.Update(_now);
            Frame = Make(Dt);
            Draw.Model.Update(Frame);
            Build();
        }

        public void Build()
        {
            List.Reset();
            Draw.Build(List, Scene, Frame);
        }

        private void Run(double ratio, double seconds, Action? check)
        {
            Speed(ratio);
            var frames = (int)Math.Round(seconds / Dt);
            for (var i = 0; i < frames; i++)
            {
                Step();
                check?.Invoke();
            }
        }

        private OrbFrame Make(double dt) => new(_now, dt, Scene.Heat, Scene.Overdrive, Scene.HeatRaw, Scene.OverdriveRaw, Scene.Tier, _size,
            _effects, _dark, Scene.Flash, Scene.SignOfLifeAge);
    }
}
