using System.Numerics;
using AgentSpeed.Presentation.Gauge;
using AgentSpeed.Presentation.Orb.Styles;

namespace AgentSpeed.Presentation.Orb.Gpu.Styles;

/// <summary>
/// The <c>led</c> style's per-pixel layers (DESIGN 2.1) and its layers outside the instrument slot: the corona and warp streaks
/// behind the body (mode 0), the silk core over it (mode 1), the tip flare with the new-peak flash, ring and rays over the
/// gauge (mode 2), and the digit lightning above the readout.
/// </summary>
public sealed partial class LedDraw
{
    public const int HaloMode = 0;
    public const int SilkMode = 1;
    public const int FlareMode = 2;

    /// <summary>The disc flash, gold ring and rays of a new peak last 1.4 s.</summary>
    public const double PeakDuration = 1.4;

    /// <summary>Everything outside the disc fades to zero by this radius (S) in the 1.5 S window.</summary>
    public const double OuterRadius = 0.74;

    public const double SilkRadius = 0.475;

    public double AuraScale => 0.8;

    public double AuraHotMix => 0.25;

    public bool OwnsPulseAndPeak => true;

    /// <summary>Corona amount <c>smoothstep(0.45, 0.85, h)(0.45 + 0.55 o) lvl</c> (Standard and Max).</summary>
    public static double CoronaAmount(double h, double o, double level) => StyleMath.SmoothStep(0.45, 0.85, h) * (0.45 + 0.55 * o) * level;

    /// <summary>Warp streak amount <c>smoothstep(0.32, 0.62, h) lvl</c> (Standard and Max).</summary>
    public static double WarpAmount(double h, double level) => StyleMath.SmoothStep(0.32, 0.62, h) * level;

    /// <summary>Tip flare amount <c>smoothstep(0.5, 0.85, h)(0.55 + 0.9 o) lvl</c> (Standard and Max).</summary>
    public static double FlareAmount(double h, double o, double level) => StyleMath.SmoothStep(0.5, 0.85, h) * (0.55 + 0.9 * o) * level;

    /// <summary>The silk's brightness before the flash, wave and sweep: <c>(0.3 + 0.65 act + 0.3 h + 0.25 o)(1 + 0.35 (lvl − 1))</c>.</summary>
    public static double SilkBrightness(double h, double o, double level) =>
        (0.3 + 0.65 * StyleMath.SmoothStep(0, 0.08, h) + 0.3 * h + 0.25 * o) * (1 + 0.35 * (level - 1));

    public void BuildBehindBody(OrbDrawList list, OrbScene scene, in OrbFrame frame)
    {
        ArgumentNullException.ThrowIfNull(list);
        if (_ctx is not { } ctx || ctx.HighContrast || frame.Thumbnail)
        {
            return;
        }

        FillUniforms(list, ctx, scene, frame);
        if (list.Led.Halo.Z > 0.001 || list.Led.Halo.W > 0.001)
        {
            list.AddLed(HaloMode, ctx.GlowBlend);
        }
    }

    public void BuildOverReadout(OrbDrawList list, OrbScene scene, in OrbFrame frame, OrbReadoutDraw? readout)
    {
        ArgumentNullException.ThrowIfNull(list);
        ArgumentNullException.ThrowIfNull(scene);
        if (_ctx is not { } ctx || ctx.HighContrast || frame.Thumbnail)
        {
            return;
        }

        AddLightning(list, ctx, frame);
        if (readout is not null && readout.ValueCellsText == scene.Odometer.Current)
        {
            _effects.NoteReadout(readout.ValueCellsText, scene.Value, readout.ValueCells, frame.Time);
        }
    }

    /// <summary>The tip flare and, while a new peak lasts, its flash, ring and rays (mode 2).</summary>
    private void AddFlarePass(OrbDrawList list)
    {
        if (list.Led.Peak.Z > 0.001 || list.Led.Peak.X < PeakDuration)
        {
            list.AddLed(FlareMode, _ctx!.GlowBlend);
        }
    }

    private void FillUniforms(OrbDrawList list, OrbGpuContext ctx, OrbScene scene, in OrbFrame frame)
    {
        var gates = _effects.Gates;
        var phases = _effects.Phases;
        var s = ctx.Size;
        var h = frame.Unavailable ? 0 : StyleMath.Clamp01(frame.Heat);
        var o = frame.Unavailable ? 0 : StyleMath.Clamp01(frame.Overdrive);
        var level = gates.Level;
        var halo = gates.Halo && !frame.Unavailable;
        var motion = gates.Motion && !frame.Unavailable;
        var peak = frame.Effects.PeakBurst && scene.PeakAge is { } age && age < PeakDuration ? age : PeakDuration + 1;
        var flare = motion ? FlareAmount(h, o, level) : 0;

        ref var u = ref list.Led;
        u.Frame = new Vector4((float)ctx.CenterX, (float)ctx.CenterY, (float)s, list.AlphaScale);
        u.Heat = new Vector4((float)h, (float)o, (float)StyleMath.SmoothStep(0, 0.08, h), (float)level);
        u.Look = new Vector4(ctx.Dark ? 1 : 0, (float)SilkBrightness(h, o, level), gates.Ripples ? (float)Math.Min(1, 2.5 * o) : 0,
            gates.SilkWave ? 1 : 0);
        u.Silk = new Vector4((float)phases.SilkFolds, (float)phases.SilkNoise, (float)phases.Light, (float)phases.Clock);
        u.Halo = new Vector4((float)phases.Corona, (float)phases.Warp, halo ? (float)CoronaAmount(h, o, level) : 0,
            halo ? (float)WarpAmount(h, level) : 0);
        u.Peak = new Vector4((float)peak, (float)StyleMath.Clamp01(frame.Flash), (float)flare, frame.Effects.SlideTransitions ? 1 : 0);

        var layout = Readout;
        var shift = frame.ReadoutShift;
        var tip = GaugeLayout.AngleForFraction(StyleMath.Clamp01(scene.GaugeHeat)) * Math.PI / 180;
        u.Tip = new Vector4((float)tip, (float)(0.05 - (layout.ValueBaseline + shift)),
            (float)(-((layout.UnitBaseline ?? layout.ValueBaseline) + shift) - 0.034), 0);

        var count = scene.PulseCount;
        var first = Math.Max(0, count - MaxWaves);
        for (var i = 0; i < MaxWaves; i++)
        {
            var index = first + i;
            if (index >= count)
            {
                u.SetWave(i, Vector4.Zero);
                continue;
            }

            var wave = scene.PulseAt(index);
            u.SetWave(i, new Vector4((float)wave.Progress, (float)Math.Clamp(wave.Strength, 0.3, 1), (float)wave.Heat, wave.Progress < 1 ? 1 : 0));
        }

        var pad = 2 * ctx.Pixel;
        u.Quad0 = new Vector4((float)(OuterRadius * s), 0, 0, 0);
        var silk = (float)(SilkRadius * s + pad);
        u.Quad1 = new Vector4((float)ctx.CenterX - silk, (float)ctx.CenterY - silk, (float)ctx.CenterX + silk, (float)ctx.CenterY + silk);
        u.Quad2 = peak < PeakDuration ? new Vector4(0, 0, (float)ctx.Canvas, (float)ctx.Canvas) : FlareQuad(ctx, tip, o);
    }

    /// <summary>The tip flare's bounds: six decay lengths along the gauge and across it.</summary>
    private static Vector4 FlareQuad(OrbGpuContext ctx, double tip, double o)
    {
        var s = ctx.Size;
        var along = 6 * (0.05 + 0.16 * o) * s;
        var across = 6 * (0.035 + 0.09 * o) * s;
        var (x, y) = ctx.Polar(tip * 180 / Math.PI, GaugeLayout.Radius);
        var cos = Math.Abs(Math.Cos(tip));
        var sin = Math.Abs(Math.Sin(tip));
        var halfX = sin * along + cos * across;
        var halfY = cos * along + sin * across;
        var canvas = ctx.Canvas;
        return new Vector4((float)Math.Max(0, x - halfX), (float)Math.Max(0, y - halfY), (float)Math.Min(canvas, x + halfX),
            (float)Math.Min(canvas, y + halfY));
    }
}
