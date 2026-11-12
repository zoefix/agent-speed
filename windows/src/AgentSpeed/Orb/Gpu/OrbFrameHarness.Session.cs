using System.Diagnostics;
using System.Globalization;
using System.IO;
using AgentSpeed.Core;
using AgentSpeed.Presentation.Character;
using AgentSpeed.Presentation.Colors;
using AgentSpeed.Presentation.Heat;
using AgentSpeed.Presentation.Localization;
using AgentSpeed.Presentation.Motion;
using AgentSpeed.Presentation.Orb;
using AgentSpeed.Presentation.Orb.Gpu;
using AgentSpeed.Presentation.Orb.Gpu.Styles;
using AgentSpeed.Presentation.Settings;

namespace AgentSpeed.Orb.Gpu;

/// <summary>
/// A living character's scripted session (<c>AGENT_SPEED_ORB_SESSION=&lt;file.json&gt;</c>, <see cref="CharacterSessionScript"/>)
/// in place of the character sheets: the first pack at 148 pt dark, stepped every 1/60 s through the script's seconds with
/// its phases, waits, idle time, events, clicks and cursor as her live inputs and its <c>speed</c> as the orb's value;
/// frames at the script's <c>shots</c> (else every 0.5 s) into <c>character-session/frame-T.png</c>.
/// <c>AGENT_SPEED_ORB_TRACE=1</c> writes her mood, pose, face, gaze, mouth and action at every shot to
/// <c>character-session-trace.txt</c>.
/// </summary>
internal static partial class OrbFrameHarness
{
    private static bool CharacterSession(GpuDevice device, OffscreenTarget target, CharacterPack pack, double scale, OrbReadoutStrings strings,
        string info, Localizer loc, string directory, System.Text.StringBuilder report)
    {
        var path = Environment.GetEnvironmentVariable("AGENT_SPEED_ORB_SESSION");
        if (CharacterSessionScript.Load(path) is not { } script)
        {
            if (!string.IsNullOrEmpty(path))
            {
                report.AppendLine(CultureInfo.InvariantCulture, $"session: cannot read {path}");
            }

            return false;
        }

        var folder = Path.Combine(directory, "character-session");
        Directory.CreateDirectory(folder);
        var trace = Environment.GetEnvironmentVariable("AGENT_SPEED_ORB_TRACE") == "1" ? new System.Text.StringBuilder() : null;
        var effects = new EffectsPolicy(EffectsIntensity.Standard);
        var d = OrbStyles.FootprintDiameter(OrbStyle.Character, 148);
        var orb = new OrbGpuOrb(0x9E3779B9u, device.Atlas.ForScale(scale));
        var context = new OrbGpuContext(d, scale, OrbTokens.For(OrbAppearance.Dark), effects);
        var live = new OrbCharacterLive { Scripted = true };
        orb.SetCharacter(CharacterContent(pack, loc) with { Live = live });
        orb.Configure(new OrbGpuConfig(OrbStyle.Character, d, effects, true, true, Redline, strings, null, SourceSet.Empty), context);
        var pixels = (int)Math.Round(d * 1.5 * scale);
        var seconds = script.Seconds ?? 30;
        var shots = script.Shots.Count > 0 ? script.Shots : Enumerable.Range(0, (int)(seconds / 0.5) + 1).Select(i => i * 0.5).ToArray();
        var origin = 400.0;
        var nextShot = 0;
        var nextTick = 0.0;
        var frames = 0;
        var watch = Stopwatch.StartNew();
        for (var n = 0; n <= (int)Math.Round(seconds * 60); n++)
        {
            var t = n / 60.0;
            var time = origin + t;
            var peak = script.Apply(t, Step, time, live);
            if (t >= nextTick || peak)
            {
                nextTick = t + 0.5;
                var value = script.SpeedAt(t) ?? 0;
                SourceKind[] agents = value > 0 ? [SourceKind.ClaudeCode] : [];
                orb.SetData(time, value, HeatMath.RawTier(value, Redline), SpeedTitles.Raw(value, Redline), agents, info, null);
                FeedCharacter(orb, loc, time, value);
            }

            if (peak)
            {
                orb.Peak(time);
            }

            if (n == 0)
            {
                orb.SnapNext();
            }

            device.Atlas.BeginFrame();
            orb.Render(time);
            frames++;
            while (nextShot < shots.Count && t >= shots[nextShot] - 1e-9)
            {
                var name = shots[nextShot].ToString("000.00", CultureInfo.InvariantCulture);
                if (SettledCapture(device, target, orb, pixels) is { } bitmap)
                {
                    WriteSheet([[bitmap]], true, scale, Path.Combine(folder, $"frame-{name}.png"));
                }

                trace?.AppendLine(CultureInfo.InvariantCulture,
                    $"t={name} {(orb.Style as CharacterDraw)?.BehaviorTrace ?? "-"} wait={(live.WaitingSince is { } w ? (time - w).ToString("0.0", CultureInfo.InvariantCulture) : "-")}");
                nextShot++;
            }
        }

        if (trace is not null)
        {
            File.WriteAllText(Path.Combine(directory, "character-session-trace.txt"), trace.ToString());
        }

        report.AppendLine(CultureInfo.InvariantCulture,
            $"character session: {pack.Id}, {frames} frames over {seconds:0.0} s, {nextShot} shots, {watch.Elapsed.TotalSeconds:0.0} s wall");
        return true;
    }

    /// <summary>The frame as it is, once the atlases it asks for have decoded (they load off the render thread).</summary>
    private static System.Windows.Media.Imaging.BitmapSource? SettledCapture(GpuDevice device, OffscreenTarget target, OrbGpuOrb orb, int pixels)
    {
        var deadline = Stopwatch.StartNew();
        var bytes = target.Render(device, orb.List, pixels, pixels);
        while ((device.Current.Character?.PendingDecodes ?? 0) > 0 && deadline.Elapsed.TotalSeconds < 10)
        {
            Thread.Sleep(30);
            bytes = target.Render(device, orb.List, pixels, pixels);
        }

        bytes = target.Render(device, orb.List, pixels, pixels);
        return bytes is null ? null : OffscreenTarget.ToBitmap(bytes, pixels, pixels);
    }
}
