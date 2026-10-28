using AgentSpeed.Core;

namespace AgentSpeed.Presentation.History;

/// <summary>
/// Today shown on its own is still being written (DESIGN 4b): while the window is on screen its logs are re-read quietly,
/// every <see cref="RefreshInterval"/> and when the view opens, without the analysing progress in the footer.
/// </summary>
public static class LiveDay
{
    /// <summary>How often today's logs are re-read while today is shown on its own (as on macOS).</summary>
    public static TimeSpan RefreshInterval { get; } = TimeSpan.FromSeconds(30);

    /// <summary>The range is the single day that is still being written.</summary>
    public static bool Shows(DayRange range, DateOnly today) => range.IsSingleDay && range.Start == today;

    /// <summary>
    /// A quiet refresh may start from this state: the history has loaded and no visible analysis is running (that one
    /// keeps showing its progress).
    /// </summary>
    public static bool CanRefreshQuietly(HistoryState state) => state.Kind is HistoryStateKind.Ready or HistoryStateKind.Failed;

    /// <summary>While a quiet refresh runs, the footer keeps its last line instead of the analysing progress.</summary>
    public static bool HidesState(HistoryState state, bool quiet) => quiet && state.Kind == HistoryStateKind.Analyzing;

    /// <summary>A quiet refresh hides the progress at most this long, should its analysis never report back.</summary>
    public static TimeSpan QuietLimit { get; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// A quiet refresh requested at <paramref name="requestedAt"/> is over once an analysis finished after it, or failed,
    /// or after <see cref="QuietLimit"/>.
    /// </summary>
    public static bool EndsQuiet(HistoryState state, DateTimeOffset requestedAt, DateTimeOffset now) =>
        state.Kind == HistoryStateKind.Failed
        || (state.Kind == HistoryStateKind.Ready && state.UpdatedAt >= requestedAt)
        || now - requestedAt > QuietLimit;
}
