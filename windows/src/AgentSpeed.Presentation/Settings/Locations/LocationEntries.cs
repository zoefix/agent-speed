using AgentSpeed.Core;
using AgentSpeed.Core.Locations;

namespace AgentSpeed.Presentation.Settings.Locations;

/// <summary>The Settings side of the location lists (DESIGN 5): the entries as shown, captions and refusal texts.</summary>
public static class LocationEntries
{
    private static string UserHome => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>The list as Settings shows it, in list order.</summary>
    public static IReadOnlyList<LocationEntry> Of(LocationFolder folder, LocationLists lists)
    {
        ArgumentNullException.ThrowIfNull(lists);
        return [.. lists[folder].Select(location => Entry(folder, location))];
    }

    public static LocationEntry Entry(LocationFolder folder, DataLocation location)
    {
        var all = location.Path is { } path ? [path] : AutomaticLocations.All(folder);
        var shown = location.Path is { } user ? [user] : AutomaticLocations.Shown(folder);
        var lines = shown.Select(p => new LocationLine(p, DisplayPath(p), AutomaticLocations.IsReadable(folder, p))).ToList();
        var reveal = all.FirstOrDefault(p => AutomaticLocations.IsReadable(folder, p)) ?? all.FirstOrDefault(p => AutomaticLocations.Exists(folder, p));
        var isFile = location.Path is { } file && AutomaticLocations.AcceptsFile(folder) && IsFile(file);
        return new LocationEntry(location, lines, all.Any(p => AutomaticLocations.IsReadable(folder, p)), reveal, isFile);
    }

    /// <summary>The path for display: the home folder as <c>~</c> (DESIGN 5); the full path stays the tooltip.</summary>
    public static string DisplayPath(string path) => DisplayPath(path, UserHome);

    public static string DisplayPath(string path, string home)
    {
        ArgumentNullException.ThrowIfNull(path);
        var trimmed = home.TrimEnd('/', '\\');
        if (trimmed.Length == 0 || !path.StartsWith(trimmed, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            return path;
        }

        if (path.Length == trimmed.Length)
        {
            return "~";
        }

        return path[trimmed.Length] is '/' or '\\' ? "~" + path[trimmed.Length..] : path;
    }

    /// <summary>What the group's caption explains about the folder (DESIGN 5), if anything.</summary>
    public static string? FootnoteKey(LocationFolder folder) => folder switch
    {
        LocationFolder.Claude => "settings.sources.sharedRoot",
        LocationFolder.Dsh => "settings.sources.dshHint",
        LocationFolder.OpenCode => "settings.sources.opencodeHint",
        LocationFolder.Gemini => "settings.sources.geminiHint",
        LocationFolder.Omp => "settings.sources.ompHint",
        LocationFolder.Antigravity => "settings.sources.antigravityHint",
        LocationFolder.WorkBuddy => "settings.sources.workbuddyHint",
        LocationFolder.WorkBuddyAi => "settings.sources.workbuddyAiHint",
        LocationFolder.ZCode => "settings.sources.zcodeHint",
        LocationFolder.Kimi => "settings.sources.kimiHint",
        LocationFolder.GrokBuild => "settings.sources.grokBuildHint",
        LocationFolder.Pi => "settings.sources.piHint",
        LocationFolder.OpenClaw => "settings.sources.openclawHint",
        LocationFolder.Hermes => "settings.sources.hermesHint",
        LocationFolder.Droid => "settings.sources.droidHint",
        LocationFolder.Devin => "settings.sources.devinHint",
        LocationFolder.Qoder => "settings.sources.qoderHint",
        LocationFolder.QoderCli => "settings.sources.qoderCliHint",
        _ => null,
    };

    /// <summary>Why a pick was refused (DESIGN 5), or null when nothing needs saying.</summary>
    public static string? RefusalText(LocationAddOutcome outcome, Localization.Localizer loc)
    {
        ArgumentNullException.ThrowIfNull(loc);
        return outcome switch
        {
            LocationAddOutcome.AlreadyListed => loc["settings.sources.alreadyListed"],
            LocationAddOutcome.AlreadyAutomatic => loc["settings.sources.alreadyDefault"],
            LocationAddOutcome.Full => LimitText(loc),
            _ => null,
        };
    }

    public static string LimitText(Localization.Localizer loc)
    {
        ArgumentNullException.ThrowIfNull(loc);
        return loc.Format("settings.sources.limit", LocationLists.MaxEntries);
    }

    /// <summary>The folder a picker opens in: the first location of the list that exists, else the default location.</summary>
    public static string? PickerStart(LocationFolder folder, LocationLists lists)
    {
        ArgumentNullException.ThrowIfNull(lists);
        foreach (var entry in Of(folder, lists))
        {
            if (entry.RevealPath is { } path)
            {
                return IsFile(path) ? Path.GetDirectoryName(path) : path;
            }
        }

        return null;
    }

    private static bool IsFile(string path)
    {
        try
        {
            return File.Exists(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }
}
