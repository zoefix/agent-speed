using System.Security.Cryptography;

namespace AgentSpeed.Core.Lan;

/// <summary>
/// The feed's frames (LAN 3.3, 3.4): one per engine tick while it runs, <c>seq</c> + 1 each, <c>run</c> random per launch.
/// Stopping drops the history and the last frame; <c>seq</c> continues. Not thread-safe; the owner ticks it on one thread.
/// </summary>
public sealed class LanFeed
{
    private readonly LanHistory _history = new();
    private long _seq;

    public LanFeed(string? run = null)
    {
        Run = run ?? NewRun();
    }

    public string Run { get; }

    public bool Running { get; private set; }

    /// <summary>The frame of the latest tick, null before the first one after a start.</summary>
    public LanFrame? Latest { get; private set; }

    public long Seq => _seq;

    public static string NewRun() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8));

    /// <summary>Builds the tick's frame; also whether it changed from the previous one (the first after a start always has).</summary>
    public (LanFrame Frame, bool Changed) Tick(long monotonicMs, long wallMs, string instance, string name, LanTickValues values)
    {
        ArgumentNullException.ThrowIfNull(values);
        Running = true;
        var history = _history.Tick(monotonicMs, values.Live.OutputTokS);
        var frame = LanFrame.Build(instance, name, Run, ++_seq, wallMs, values, history);
        var changed = frame.ChangedFrom(Latest);
        Latest = frame;
        return (frame, changed);
    }

    /// <summary>A frame for <i>Send Test</i> while the feed does not run: the values with a one-sample history.</summary>
    public LanFrame BuildOnce(long wallMs, string instance, string name, LanTickValues values)
    {
        ArgumentNullException.ThrowIfNull(values);
        return LanFrame.Build(instance, name, Run, ++_seq, wallMs, values, [LanFrame.TokS(values.Live.OutputTokS)]);
    }

    public void Stop()
    {
        Running = false;
        Latest = null;
        _history.Clear();
    }
}
