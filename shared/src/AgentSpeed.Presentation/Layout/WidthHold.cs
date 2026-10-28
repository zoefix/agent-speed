namespace AgentSpeed.Presentation.Layout;

/// <summary>
/// Content-hugging width that grows immediately and shrinks only after a shorter width has held for
/// <see cref="DelayMs"/> (DESIGN section 3), so the bar item never flickers between widths.
/// </summary>
public sealed class WidthHold(long delayMs = 6000)
{
    public long DelayMs { get; } = delayMs;

    /// <summary>The width currently in use; 0 before the first resolve.</summary>
    public int Applied { get; private set; }

    /// <summary>When a shorter width was first requested, or null when no shrink is pending.</summary>
    public long? PendingSince { get; private set; }

    /// <summary>Time at which a pending shrink becomes due.</summary>
    public long? DueAt => PendingSince + DelayMs;

    public int Resolve(int desired, long nowMs, bool force = false)
    {
        if (force || Applied <= 0 || desired >= Applied || (PendingSince is { } since && nowMs - since >= DelayMs))
        {
            PendingSince = null;
            Applied = desired;
            return desired;
        }

        PendingSince ??= nowMs;
        return Applied;
    }

    /// <summary>The width that would be used now, without changing any state.</summary>
    public int Peek(int desired, long nowMs) =>
        Applied <= 0 || desired >= Applied || (PendingSince is { } since && nowMs - since >= DelayMs) ? desired : Applied;

    public void Reset()
    {
        Applied = 0;
        PendingSince = null;
    }
}
