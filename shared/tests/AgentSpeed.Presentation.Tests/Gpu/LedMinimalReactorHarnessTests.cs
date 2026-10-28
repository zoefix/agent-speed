using AgentSpeed.Presentation.Colors;
using AgentSpeed.Presentation.Heat;
using AgentSpeed.Presentation.Motion;
using AgentSpeed.Presentation.Orb;
using AgentSpeed.Presentation.Orb.Gpu;
using AgentSpeed.Presentation.Orb.Styles;
using AgentSpeed.Presentation.Settings;

namespace AgentSpeed.Presentation.Tests.Gpu;

/// <summary>
/// Drives one orb of the <c>led</c>, <c>minimal</c> or <c>reactor</c> style the way the orb surface does (scene update,
/// model update, then the draw builder) and checks every frame's draw list.
/// </summary>
internal sealed class LedMinimalReactorHarness
{
    public const double Start = 100;

    private static readonly (double Ratio, double Seconds)[] Climb = [(0.08, 1.5), (0.25, 1.2), (0.5, 1.2), (0.9, 1.5), (1.6, 1)];

    public LedMinimalReactorHarness(OrbStyle style, double size = 148, bool dark = true, EffectsIntensity level = EffectsIntensity.Standard,
        bool reduceMotion = false, bool highContrast = false, int seed = 7)
    {
        Size = size;
        Dark = dark;
        Effects = new EffectsPolicy(level, reduceMotion, highContrast);
        Scene = new OrbScene(seed)
        {
            Effects = Effects,
            Redline = HeatMath.ReferenceRedline,
            SharedSignOfLife = true,
            LedEffects = style == OrbStyle.Led,
            SilkEffects = style == OrbStyle.Led,
            SignOfLifeDuration = style == OrbStyle.Minimal ? 0 : 0.9,
        };
        Draw = OrbStyleDraws.Create(style, Scene, (uint)seed);
        Context = new OrbGpuContext(size, 2, dark ? OrbTokens.Dark : OrbTokens.Light, Effects, highContrast ? OrbContrastColors.Default : null);
        Draw.Configure(Context);
        Time = Start;
        Scene.Snap(Time);
        Draw.Model.Snap(Frame(0));
    }

    public double Size { get; }

    public bool Dark { get; }

    public EffectsPolicy Effects { get; }

    public OrbScene Scene { get; }

    public IOrbStyleDraw Draw { get; }

    public OrbGpuContext Context { get; }

    public OrbDrawList List { get; } = new();

    public double Time { get; private set; }

    /// <summary>The frame the orb surface builds from the scene.</summary>
    public OrbFrame Frame(double dt) => new(Time, dt, Scene.Heat, Scene.Overdrive, Scene.HeatRaw, Scene.OverdriveRaw, Scene.Tier, Size,
        Effects, Dark, Scene.Flash, Scene.SignOfLifeAge);

    /// <summary>Sets the live value to <paramref name="ratio"/> × the redline.</summary>
    public void Speed(double ratio)
    {
        var value = ratio * Scene.Redline;
        Scene.SetTarget(value, HeatMath.RawTier(value, Scene.Redline));
    }

    public void Pulse(double ratio, long outputTokens)
    {
        var speed = ratio * Scene.Redline;
        if (Scene.Effects.Pulses)
        {
            Draw.Model.Pulse(HeatMath.Heat(speed, Scene.Redline), Math.Clamp(Math.Log10(outputTokens) / 3, 0.3, 1.25));
        }

        Scene.TriggerPulse(speed, outputTokens);
    }

    public void Peak()
    {
        Draw.Model.Peak();
        Scene.TriggerPeak();
    }

    /// <summary>Advances one frame and rebuilds the draw list.</summary>
    public void Step(double dt)
    {
        Time += dt;
        Scene.Update(Time);
        var frame = Frame(dt);
        Draw.Model.Update(frame);
        Rebuild(frame);
    }

    public void Rebuild(in OrbFrame frame)
    {
        List.Reset();
        Draw.Build(List, Scene, frame);
    }

    public void Run(double seconds, Action<OrbDrawList>? check = null, double fps = 60)
    {
        var frames = (int)Math.Round(seconds * fps);
        for (var i = 0; i < frames; i++)
        {
            Step(1 / fps);
            check?.Invoke(List);
        }
    }

    /// <summary>
    /// Idle (with a sign of life) → cruise → boost → overdrive tier → redline → full overdrive with pulses and a new peak →
    /// idle again.
    /// </summary>
    public void Scenario(Action<OrbDrawList> check, double fps = 60)
    {
        Run(3.2, check, fps);
        foreach (var (ratio, seconds) in Climb)
        {
            Speed(ratio);
            Run(seconds, check, fps);
        }

        Pulse(1.6, 2_000);
        Run(0.3, check, fps);
        Peak();
        Run(0.4, check, fps);
        Pulse(2, 40_000);
        Speed(2);
        Run(2, check, fps);
        Speed(0);
        Run(3, check, fps);
    }

    /// <summary>Dark, light and high contrast (on either) × every effects level × S 64, 96, 148 and 240.</summary>
    public static TheoryData<bool, bool, EffectsIntensity, double> Matrix()
    {
        var data = new TheoryData<bool, bool, EffectsIntensity, double>();
        foreach (var (dark, highContrast) in new[] { (true, false), (false, false), (true, true), (false, true) })
        {
            foreach (var level in new[] { EffectsIntensity.Off, EffectsIntensity.Soft, EffectsIntensity.Standard, EffectsIntensity.Max })
            {
                foreach (var size in new[] { 64.0, 96, 148, 240 })
                {
                    data.Add(dark, highContrast, level, size);
                }
            }
        }

        return data;
    }

    /// <summary>Runs the scenario and checks every frame; returns the largest instance count.</summary>
    public int CheckScenario(int budget)
    {
        var most = 0;
        Scenario(list =>
        {
            DrawListChecks.AssertValid(list, Dark, Effects.HighContrast, budget);
            if (Effects.HighContrast)
            {
                DrawListChecks.AssertContrastColors(list, Context.Contrast);
            }

            most = Math.Max(most, list.Count);
        });
        return most;
    }

    /// <summary>Fingerprints of every frame of the scenario.</summary>
    public List<ulong> Fingerprints()
    {
        var prints = new List<ulong>();
        Scenario(list => prints.Add(list.Fingerprint()));
        return prints;
    }
}

/// <summary>Checks shared by the draw builder tests.</summary>
internal static class DrawListChecks
{
    public static void AssertValid(OrbDrawList list, bool dark, bool highContrast, int budget)
    {
        Assert.InRange(list.Count, 0, budget);
        foreach (var instance in list.Instances)
        {
            AssertFinite(instance.A);
            AssertFinite(instance.B);
            AssertFinite(instance.Color);
            AssertFinite(instance.Color2);
            AssertFinite(instance.Extra);
            AssertFinite(instance.Extra2);
            Assert.InRange(instance.Color.W, 0f, 1f);
            Assert.InRange(instance.Color2.W, 0f, 1f);
            Assert.True(instance.B.W >= 0, "negative glow radius");
        }

        var total = 0;
        foreach (var batch in list.Batches)
        {
            if (batch.Pass == OrbPass.Led)
            {
                Assert.True(list.HasLed);
                Assert.Equal(0, batch.Count);
                Assert.InRange(batch.Mode, 0, 2);
            }
            else
            {
                Assert.Equal(OrbPass.Shapes, batch.Pass);
            }

            Assert.Equal(0f, batch.Clip);
            if (!dark || highContrast)
            {
                Assert.Equal(OrbBlend.Over, batch.Blend);
            }

            total += batch.Count;
        }

        Assert.Equal(list.Count, total);
    }

    /// <summary>Every color of a high-contrast frame comes from the theme's palette.</summary>
    public static void AssertContrastColors(OrbDrawList list, OrbContrastColors palette)
    {
        foreach (var instance in list.Instances)
        {
            Assert.True(IsPalette(instance.Color, palette), $"{instance.Shape} color {instance.Color} is not a theme color");
        }
    }

    private static bool IsPalette(System.Numerics.Vector4 color, OrbContrastColors palette) =>
        Same(color, palette.WindowText) || Same(color, palette.Window) || Same(color, palette.Highlight) || Same(color, palette.GrayText);

    private static bool Same(System.Numerics.Vector4 color, Rgba rgb) =>
        Math.Abs(color.X - rgb.R) < 1e-4 && Math.Abs(color.Y - rgb.G) < 1e-4 && Math.Abs(color.Z - rgb.B) < 1e-4;

    private static void AssertFinite(System.Numerics.Vector4 v) =>
        Assert.True(float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z) && float.IsFinite(v.W), $"non-finite {v}");
}
