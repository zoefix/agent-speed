namespace AgentSpeed.Presentation.Motion;

/// <summary>
/// The time step of a window's motion frames (drag follow, layout springs). Starting frames that already run keeps the
/// running step: every pointer move restarts them during a drag, and resetting there would shrink each frame's step to the
/// time since the last move, so the follow spring would lag further the faster the mouse reports.
/// </summary>
public struct MotionFrames
{
    private const double MaxStep = 0.05;
    private double _last;

    public bool Running { get; private set; }

    /// <summary>Starts the frames at <paramref name="now"/> (seconds); false when they already run.</summary>
    public bool Start(double now)
    {
        if (Running)
        {
            return false;
        }

        Running = true;
        _last = now;
        return true;
    }

    public void Stop() => Running = false;

    /// <summary>Seconds since the previous frame (or the start), at most 0.05 s.</summary>
    public double Step(double now)
    {
        var dt = Math.Clamp(now - _last, 0, MaxStep);
        _last = now;
        return dt;
    }
}
