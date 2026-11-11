namespace AgentSpeed.Core.Quota.Cookies;

/// <summary>
/// The only folders the cookie reader looks in (QUOTAS 9.10): the Chromium browsers' user data folders under
/// <paramref name="LocalAppData"/>, and Firefox's <c>profiles.ini</c> in <paramref name="Firefox"/>. A root that is not
/// an absolute path is ignored.
/// </summary>
public sealed record BrowserRoots(string LocalAppData, string Firefox)
{
    /// <summary><c>%LOCALAPPDATA%</c> and <c>%APPDATA%\Mozilla\Firefox</c> of the current user.</summary>
    public static BrowserRoots Current()
    {
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return new BrowserRoots(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            roaming.Length > 0 ? Path.Combine(roaming, "Mozilla", "Firefox") : string.Empty);
    }
}
