namespace AgentSpeed.Core.Locations;

/// <summary>
/// The folders an automatic entry stands for in Settings (DESIGN 5): the default locations of METRICS 2 as a user would pick
/// them (data folders, not their <c>projects</c> or <c>conversations</c> folders), from this process's environment.
/// </summary>
public static class AutomaticLocations
{
    /// <summary>Every default location of the folder, in reading order.</summary>
    public static IReadOnlyList<string> All(LocationFolder folder) => folder switch
    {
        LocationFolder.Antigravity => LiveMonitor.DefaultAntigravityStores,
        LocationFolder.WorkBuddy => [LiveMonitor.DefaultWorkBuddyFolder],
        LocationFolder.WorkBuddyAi => [LiveMonitor.DefaultWorkBuddyAiFolder],
        LocationFolder.Devin => [LiveMonitor.DefaultDevinRoot],
        LocationFolder.Qoder => LiveMonitor.DefaultQoderRoots,
        _ => LocationRoots.Automatic(folder),
    };

    /// <summary>
    /// What the automatic entry's row names (DESIGN 5): omp and Pi only their default root although profiles and Pi's custom
    /// session folder are read too; Antigravity the stores found, or all three when none is; OpenClaw the state folders found,
    /// or the default one when none is; every other source all of its defaults.
    /// </summary>
    public static IReadOnlyList<string> Shown(LocationFolder folder)
    {
        switch (folder)
        {
            case LocationFolder.Omp:
                return [LiveMonitor.DefaultOmpRoot];
            case LocationFolder.Pi:
                return [LiveMonitor.DefaultPiRoot];
            case LocationFolder.Antigravity:
            {
                var stores = All(folder);
                var found = stores.Where(store => IsReadable(folder, store)).ToList();
                return found.Count > 0 ? found : stores;
            }

            case LocationFolder.OpenClaw:
            {
                var found = All(folder).Where(root => IsReadable(folder, root)).ToList();
                return found.Count > 0 ? found : [LiveMonitor.DefaultOpenClawRoot];
            }

            default:
                return All(folder);
        }
    }

    /// <summary>
    /// A location of the folder exists and is read: a folder (OpenCode, ZCode and OpenClaw also a database file); an Antigravity
    /// <c>*-backup</c> store never is (METRICS 4.8); a Devin folder only while it holds the CLI's database (METRICS 4.17), a Qoder
    /// IDE folder only while it holds the chat database (METRICS 4.18).
    /// </summary>
    public static bool IsReadable(LocationFolder folder, string path)
    {
        if (string.IsNullOrEmpty(path) || (folder == LocationFolder.Antigravity && Antigravity.AntigravityPaths.IsBackupStore(path)))
        {
            return false;
        }

        if (folder == LocationFolder.Devin)
        {
            // The automatic entry's database may have any name ($CHISEL_SESSION_DB).
            var automatic = Devin.DevinRoot.Database(LiveMonitor.DefaultDevinDatabase);
            return Devin.DevinPaths.DatabaseOf(path) is not null
                || (string.Equals(path, automatic.DisplayFolder, StringComparison.OrdinalIgnoreCase) && automatic.DatabaseNow is not null);
        }

        if (folder == LocationFolder.Qoder)
        {
            return Qoder.QoderPaths.DatabaseOf(path) is not null;
        }

        try
        {
            return Directory.Exists(path) || (AcceptsFile(folder) && File.Exists(path));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>The folder or file exists at all (Show in File Explorer works for it); a Devin or Qoder IDE folder holds the database.</summary>
    public static bool Exists(LocationFolder folder, string path)
    {
        if (folder is LocationFolder.Devin or LocationFolder.Qoder)
        {
            return IsReadable(folder, path);
        }

        try
        {
            return !string.IsNullOrEmpty(path) && (Directory.Exists(path) || (AcceptsFile(folder) && File.Exists(path)));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>A user location of these sources may name a database file instead of its folder (METRICS 2 table).</summary>
    public static bool AcceptsFile(LocationFolder folder) =>
        folder is LocationFolder.OpenCode or LocationFolder.ZCode or LocationFolder.OpenClaw;
}
