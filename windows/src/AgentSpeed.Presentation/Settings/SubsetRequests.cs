using AgentSpeed.Core;

namespace AgentSpeed.Presentation.Settings;

/// <summary>
/// The subsets whose measured peak the live monitor must report (<c>LiveMonitor.SetSubsets</c>, METRICS 6.8, DESIGN 13): one
/// per orb or board that tracks fewer than every source on every host, so a limited orb's or board's reading has its own
/// peak and can celebrate it.
/// </summary>
public static class SubsetRequests
{
    public static IReadOnlyList<SubsetKey> For(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var keys = new List<SubsetKey>();
        foreach (var orb in settings.Orbs?.Orbs ?? [])
        {
            Add(keys, orb.Hosts.SubsetKey(orb.TrackedSources));
        }

        foreach (var board in settings.Boards?.Boards ?? [])
        {
            Add(keys, board.Hosts.SubsetKey(board.TrackedSources));
        }

        return keys;
    }

    private static void Add(List<SubsetKey> keys, SubsetKey? key)
    {
        if (key is { } subset && !keys.Contains(subset))
        {
            keys.Add(subset);
        }
    }
}
