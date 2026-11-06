using AgentSpeed.Core.Locations;

namespace AgentSpeed.Presentation.Settings.Locations;

/// <summary>
/// Editing a source's location list through the settings (METRICS 2.1, DESIGN 5). An edit stores <c>sourceLocations</c>; the
/// single-folder values of earlier versions are never written by it.
/// </summary>
public static class LocationEditing
{
    /// <summary>Adds a picked location; the settings change only when the outcome changed the list.</summary>
    public static (AppSettings Settings, LocationAddOutcome Outcome) AddLocation(
        this AppSettings settings, LocationFolder folder, string? path, IReadOnlyList<string>? automaticKeys = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var (lists, outcome) = settings.Locations.Add(path, folder, automaticKeys);
        return outcome is LocationAddOutcome.Added or LocationAddOutcome.RestoredAutomatic
            ? (settings.WithLocations(lists), outcome)
            : (settings, outcome);
    }

    /// <summary>Removes an entry; the only remaining one stays.</summary>
    public static AppSettings RemoveLocation(this AppSettings settings, LocationFolder folder, DataLocation location)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var lists = settings.Locations;
        return lists.CanRemove(folder) ? settings.WithLocations(lists.Remove(location, folder)) : settings;
    }

    /// <summary>Back to <c>[automatic]</c>; nothing changes while the list already is.</summary>
    public static AppSettings ResetLocations(this AppSettings settings, LocationFolder folder)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var lists = settings.Locations;
        return lists.CanReset(folder) ? settings.WithLocations(lists.Reset(folder)) : settings;
    }
}
