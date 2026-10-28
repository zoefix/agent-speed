using AgentSpeed.Presentation.Layout;
using AgentSpeed.Presentation.Settings.Orbs;
using Avalonia.Platform;

namespace AgentSpeed.Linux.Platform;

/// <summary>Adapts the Linux window layer to the shared orb placement rules.</summary>
internal static class LinuxOrbLayout
{
    internal static string Key(Screen screen) => screen.DisplayName
        ?? $"display-{screen.Bounds.X}-{screen.Bounds.Y}-{screen.Bounds.Width}-{screen.Bounds.Height}";

    internal static IReadOnlyList<OrbDisplay> Displays(IReadOnlyList<Screen> screens, Screen? primary)
    {
        var primaryKey = primary is null ? null : Key(primary);
        return screens.Select(screen => new OrbDisplay(Key(screen),
            new RectD(screen.WorkingArea.X, screen.WorkingArea.Y, screen.WorkingArea.Width, screen.WorkingArea.Height),
            screen.Scaling, string.Equals(Key(screen), primaryKey, StringComparison.Ordinal))).ToArray();
    }

    internal static OrbLayoutResult Resolve(OrbsDocument document, IReadOnlyList<OrbDisplay> displays) =>
        OrbArrangement.Resolve(document, displays);

    internal static OrbLayoutResult PlaceNew(
        OrbsDocument document,
        IReadOnlyList<OrbDisplay> displays,
        IReadOnlyDictionary<string, PointD>? centers,
        string newId,
        string? fromId) => OrbArrangement.PlaceNew(document, displays, centers, newId, fromId);
}
