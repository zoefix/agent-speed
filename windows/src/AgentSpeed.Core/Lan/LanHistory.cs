namespace AgentSpeed.Core.Lan;

/// <summary>
/// The frame's history (LAN 3.4): the rounded <c>outputTokS</c> once per second on the monotonic clock, sample <c>k</c> at
/// the first tick at or after <c>s0 + k · 1000 ms</c>; a late tick fills the missed samples with its value, a gap of 60 s or
/// more starts over. The newest 60 samples are kept.
/// </summary>
public sealed class LanHistory
{
    public const int Length = 60;
    public const long StepMs = LanFrame.HistoryStepMs;
    public const long RestartMs = 60_000;

    private readonly List<double> _samples = [];
    private long? _s0;
    private long _next;
    private long _lastSampleMs;

    public IReadOnlyList<double> Samples => _samples;

    /// <summary>Takes the tick at monotonic <paramref name="nowMs"/> with the unrounded <paramref name="outputTokS"/>; the samples after it.</summary>
    public IReadOnlyList<double> Tick(long nowMs, double outputTokS)
    {
        var value = LanFrame.TokS(outputTokS);
        if (_s0 is not { } s0 || nowMs - _lastSampleMs >= RestartMs)
        {
            _s0 = nowMs;
            _next = 1;
            _lastSampleMs = nowMs;
            _samples.Clear();
            _samples.Add(value);
            return _samples.ToArray();
        }

        while (nowMs >= s0 + _next * StepMs)
        {
            _samples.Add(value);
            _next++;
            _lastSampleMs = nowMs;
        }

        if (_samples.Count > Length)
        {
            _samples.RemoveRange(0, _samples.Count - Length);
        }

        return _samples.ToArray();
    }

    public void Clear()
    {
        _s0 = null;
        _samples.Clear();
    }
}
