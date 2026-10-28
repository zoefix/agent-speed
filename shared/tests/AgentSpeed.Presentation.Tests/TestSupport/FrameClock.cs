namespace AgentSpeed.Presentation.Tests.TestSupport;

/// <summary>Deterministic 60 fps clock; time is derived from an integer frame count so it never drifts.</summary>
internal sealed class FrameClock
{
    public const int Fps = 60;
    public const double FrameSeconds = 1.0 / Fps;

    public FrameClock(double startSeconds = 0)
    {
        StartSeconds = startSeconds;
    }

    public double StartSeconds { get; }

    public long Frame { get; private set; }

    public double Now => StartSeconds + Frame / (double)Fps;

    public static int FramesFor(double seconds) => (int)Math.Round(seconds * Fps);

    /// <summary>Advances by whole frames, invoking <paramref name="tick"/> with the new time after each.</summary>
    public void Advance(double seconds, Action<double> tick) => AdvanceFrames(FramesFor(seconds), tick);

    public void AdvanceFrames(int frames, Action<double> tick)
    {
        for (var i = 0; i < frames; i++)
        {
            Frame++;
            tick(Now);
        }
    }

    /// <summary>Advances until <see cref="Now"/> reaches <paramref name="seconds"/> (absolute).</summary>
    public void AdvanceTo(double seconds, Action<double> tick)
    {
        var target = (long)Math.Round((seconds - StartSeconds) * Fps);
        AdvanceFrames((int)Math.Max(0, target - Frame), tick);
    }
}
