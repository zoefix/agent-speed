using System.Globalization;
using AgentSpeed.Presentation.Character;

namespace AgentSpeed.Presentation.Orb.Gpu.Styles;

/// <summary>
/// The inputs of a living character for one frame (CHARACTERS 5, <i>Behavior</i>, <i>Inputs</i>): the agents' phase,
/// wait, idle time, hour and events from the orb's feed, and the pointer, read here only while her style draws.
/// </summary>
public sealed partial class CharacterDraw
{
    /// <summary>Slack around her drawn outline for a click (DIP).</summary>
    public const double ClickSlack = 2;

    /// <summary>Slack around her hit capsule for <c>over</c> (DIP), as the orb window's hit test.</summary>
    public const double HoverSlack = 1;

    private int? _seenPeaks;
    private int? _seenTroubles;
    private int? _seenClicks;
    private (double X, double Y)? _lastFace;
    private (double X, double Y)? _lastPointer;

    /// <summary>The scale the scene is drawn at around the canvas center (appear, vanish and hover); set by the orb before each frame.</summary>
    public double ContentScale { get; set; } = 1;

    /// <summary>The inputs for <paramref name="performer"/> this frame, or null for a pack that is not living or an orb without live inputs.</summary>
    internal BehaviorInputs? BehaviorInputsFor(OrbGpuContext ctx, double time, OrbCharacterLive? live, CharacterPerformer performer)
    {
        if (live is null || !performer.Living)
        {
            return null;
        }

        var inputs = new BehaviorInputs { Phase = live.Phase, Hour = live.Hour };
        if (live.Phase == AgentActivity.Generating && live.WaitingSince is { } since)
        {
            inputs.Wait = Math.Max(0, time - since);
        }

        if (live.Phase == AgentActivity.Idle)
        {
            inputs.Idle = Math.Max(0, time - live.IdleSince);
        }

        inputs.NewPeak = Fresh(live.Peaks, ref _seenPeaks) && live.CelebratesPeaks;
        inputs.Trouble = Fresh(live.Troubles, ref _seenTroubles);

        var g = GeometryOf(ctx, performer);
        var face = performer.FaceCenter((g.FeetX, g.FeetY), g.PointsPerPixel);
        (double X, double Y)? point = null;
        if (live.Scripted)
        {
            if (live.ScriptedCursor is { } scripted)
            {
                point = (face.X + scripted.X * g.Height, face.Y + scripted.Y * g.Height);
            }
        }
        else if (live.Pointer?.PointerFraction is { } fraction)
        {
            point = OrbCharacterLive.CanvasPoint(fraction, ctx.Canvas, ContentScale);
        }

        _lastFace = face;
        _lastPointer = point;
        if (point is { } p)
        {
            inputs.Cursor = Cursor(p, face, g.Height);
            inputs.Over = live.Pointer?.IsDragging != true && OverCapsule(p, ctx.CenterX, ctx.CenterY, ctx.Size, g.BodyDx);
        }

        if (Fresh(live.Clicks, ref _seenClicks))
        {
            var press = live.Scripted ? point : OrbCharacterLive.CanvasPoint(live.ClickFraction, ctx.Canvas, ContentScale);
            inputs.Click = press is { } c && _silhouette?.Contains(c.X, c.Y, ClickSlack) == true;
        }

        return inputs;
    }

    /// <summary>The pointer relative to her face in her heights (screen axes, y down); null beyond her look range.</summary>
    public static (double X, double Y)? Cursor((double X, double Y) point, (double X, double Y) face, double height)
    {
        if (!(height > 0))
        {
            return null;
        }

        var x = (point.X - face.X) / height;
        var y = (point.Y - face.Y) / height;
        return Math.Sqrt(x * x + y * y) <= CharacterBehavior.LookRange ? (x, y) : null;
    }

    /// <summary>Whether a canvas point (DIP, y down) is inside her hit capsule (DESIGN 2a.6), moved with her side steps.</summary>
    public static bool OverCapsule((double X, double Y) point, double centerX, double centerY, double diameter, double bodyDx) =>
        OrbHitShape.Contains(OrbStyle.Character, point.X - centerX - bodyDx, point.Y - centerY, diameter, HoverSlack);

    /// <summary>Whether a count moved since the last frame; the first frame only takes it, so events from before her first frame never fire.</summary>
    internal static bool Fresh(int count, ref int? seen)
    {
        var fresh = seen is { } last && last != count;
        seen = count;
        return fresh;
    }

    /// <summary>Her mood, pose, face, gaze, mouth and action this frame, for the frame harness's trace (allocates).</summary>
    public string BehaviorTrace
    {
        get
        {
            if (_stage?.Current is not { } performer)
            {
                return "-";
            }

            var d = performer.Director;
            var pose = performer.Pack.Poses[performer.ActivePose].Name;
            var angle = d.Value("AngleX") is { } a ? a.ToString("0.00", CultureInfo.InvariantCulture) : "-";
            var text = string.Create(CultureInfo.InvariantCulture,
                $"mood={d.Mood?.Key() ?? "-"} pose={pose} face={d.Expression ?? "-"} gaze={d.GazePatch ?? "-"} mouth={d.MouthShape ?? "-"} action={d.ActiveGesture ?? "-"} angleX={angle} acting={performer.IsActing}");
            if (_lastFace is { } f)
            {
                text += string.Create(CultureInfo.InvariantCulture, $" faceAt={f.X:0.0},{f.Y:0.0}");
            }

            if (_lastPointer is { } p)
            {
                text += string.Create(CultureInfo.InvariantCulture, $" pointer={p.X:0.0},{p.Y:0.0}");
            }

            return text;
        }
    }
}
