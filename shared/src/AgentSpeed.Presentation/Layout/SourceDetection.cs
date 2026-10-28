using AgentSpeed.Core;

namespace AgentSpeed.Presentation.Layout;

/// <summary>Which sources the dashboard lists (DESIGN section 4, item 3); Settings always lists every source.</summary>
public static class SourceDetection
{
    /// <summary>Detected: its folder exists or it has data today. Disabled sources that are detected stay listed.</summary>
    public static bool IsDetected(SourceStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        return status.Available || status.SessionCount > 0 || status.Today.Requests > 0;
    }

    /// <summary>
    /// Listed on the dashboard: the source produced output tokens today or is generating right now (its first response
    /// may still be in flight).
    /// </summary>
    public static bool ProducedToday(SourceStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        return status.Today.Output > 0 || status.Live.Output > 0;
    }
}
