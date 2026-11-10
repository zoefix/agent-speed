using System.Globalization;
using AgentSpeed.Core.Locations;

namespace AgentSpeed.Core.Quota.Cookies;

/// <summary>A Chromium-family browser on Windows: its user data folder, which holds <c>Local State</c> and the profiles.</summary>
internal sealed record ChromiumBrowser(BrowserKind Kind, string UserData)
{
    private const int MaxVersionFileBytes = 64;
    private const string ProfilePrefix = "Profile ";

    private static readonly (BrowserKind Kind, string[] Folder)[] Folders =
    [
        (BrowserKind.Chrome, ["Google", "Chrome", "User Data"]),
        (BrowserKind.Edge, ["Microsoft", "Edge", "User Data"]),
        (BrowserKind.Brave, ["BraveSoftware", "Brave-Browser", "User Data"]),
        (BrowserKind.Vivaldi, ["Vivaldi", "User Data"]),
        (BrowserKind.Chromium, ["Chromium", "User Data"]),
    ];

    public string LocalState => Path.Combine(UserData, "Local State");

    /// <summary>Every Chromium browser of 9.10 in the order they are tried; none when the root is not absolute.</summary>
    public static List<ChromiumBrowser> All(string localAppData) =>
        string.IsNullOrEmpty(localAppData) || !Path.IsPathFullyQualified(localAppData)
            ? []
            : [.. Folders.Select(row => new ChromiumBrowser(row.Kind, Path.Combine([localAppData, .. row.Folder])))];

    /// <summary>
    /// <c>Default</c>, then <c>Profile 1</c>, <c>Profile 2</c> … in number order; each with its <c>Network\Cookies</c>
    /// (Chrome 96 and later), else its <c>Cookies</c>.
    /// </summary>
    public List<string> CookieStores()
    {
        List<string> profiles;
        try
        {
            profiles = [.. Directory.EnumerateDirectories(UserData)
                .Select(Path.GetFileName)
                .OfType<string>()
                .Where(name => name.StartsWith(ProfilePrefix, StringComparison.Ordinal))
                .OrderBy(ProfileNumber)
                .ThenBy(name => name, StringComparer.Ordinal)];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return [];
        }

        var stores = new List<string>();
        foreach (var profile in profiles.Prepend("Default"))
        {
            var folder = Path.Combine(UserData, profile);
            var store = new[] { Path.Combine(folder, "Network", "Cookies"), Path.Combine(folder, "Cookies") }
                .FirstOrDefault(LocationPaths.IsRegularFile);
            if (store is not null)
            {
                stores.Add(store);
            }
        }

        return stores;
    }

    /// <summary>The major version from <c>Last Version</c> (<c>142.0.7444.60</c> → 142), for WorkBuddy's user agent.</summary>
    public int? MajorVersion()
    {
        var text = CookieFiles.ReadText(Path.Combine(UserData, "Last Version"), MaxVersionFileBytes);
        var first = text?.Trim().Split('.')[0];
        return int.TryParse(first, NumberStyles.None, CultureInfo.InvariantCulture, out var major) && major is > 0 and < 10_000
            ? major
            : null;
    }

    private static int ProfileNumber(string name) =>
        int.TryParse(name.AsSpan(ProfilePrefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var number)
            ? number
            : int.MaxValue;
}
