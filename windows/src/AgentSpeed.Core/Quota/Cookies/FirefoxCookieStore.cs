using AgentSpeed.Core.IO.Sqlite;
using AgentSpeed.Core.Locations;

namespace AgentSpeed.Core.Quota.Cookies;

/// <summary>Firefox's profiles (<c>profiles.ini</c>) and their <c>cookies.sqlite</c> (<c>moz_cookies</c>, plain values).</summary>
internal static class FirefoxCookieStore
{
    private const int MaxIniBytes = 1 << 20;
    private const int MaxCompatibilityBytes = 64 << 10;
    private const int MaxVersionLength = 32;

    /// <summary>Above this, <c>expiry</c> is in milliseconds (newer Firefox) rather than seconds.</summary>
    private const long MillisecondExpiry = 100_000_000_000;

    private const string Rows = """
        SELECT host, name, value, expiry, lastAccessed FROM moz_cookies
        WHERE host = ?1 OR host = ?2 OR host LIKE ?3 ESCAPE '\'
        """;

    /// <summary>The profile folders of <c>profiles.ini</c> in the Firefox folder <paramref name="root"/>.</summary>
    public static List<string> Profiles(string root)
    {
        if (string.IsNullOrEmpty(root) || !Path.IsPathFullyQualified(root))
        {
            return [];
        }

        var text = CookieFiles.ReadText(Path.Combine(root, "profiles.ini"), MaxIniBytes);
        return text is null ? [] : ParseProfiles(text, root);
    }

    /// <summary>
    /// Every <c>[Profile…]</c> section's <c>Path</c>, each once: relative to <paramref name="root"/> unless
    /// <c>IsRelative=0</c>. A relative path that leaves <paramref name="root"/> is ignored.
    /// </summary>
    public static List<string> ParseProfiles(string text, string root)
    {
        var folders = new List<string>();
        var seen = new HashSet<string>(LocationPaths.Comparer);
        var inProfile = false;
        string? path = null;
        var relative = true;

        void Flush()
        {
            if (inProfile && Resolve(path, relative, root) is { } folder && seen.Add(folder))
            {
                folders.Add(folder);
            }

            path = null;
            relative = true;
        }

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith('['))
            {
                Flush();
                inProfile = line.StartsWith("[Profile", StringComparison.Ordinal);
                continue;
            }

            var equals = line.IndexOf('=', StringComparison.Ordinal);
            if (equals <= 0)
            {
                continue;
            }

            var key = line[..equals].Trim();
            var value = line[(equals + 1)..].Trim();
            if (key == "Path")
            {
                path = value;
            }
            else if (key == "IsRelative")
            {
                relative = value != "0";
            }
        }

        Flush();
        return folders;
    }

    /// <exception cref="SqliteException">The copy is no Firefox cookie database.</exception>
    public static CookieRows Read(string path, IReadOnlyList<string> sites)
    {
        using var db = SqliteConnection.Open(path, SqliteOpenMode.ReadWriteCreate);
        var bySite = new Dictionary<string, List<BrowserCookie>>(StringComparer.Ordinal);
        using var query = db.Prepare(Rows);
        foreach (var site in sites)
        {
            var (exact, dotted, pattern) = CookieStoreCopy.HostArguments(site);
            query.Bind(1, exact).Bind(2, dotted).Bind(3, pattern);
            var cookies = new List<BrowserCookie>();
            while (cookies.Count < ChromeCookieStore.MaxRows && query.Step())
            {
                var expiry = query.GetInt64(3);
                cookies.Add(new BrowserCookie
                {
                    Host = query.GetString(0) ?? string.Empty,
                    Name = query.GetString(1) ?? string.Empty,
                    Value = query.GetString(2) ?? string.Empty,
                    ExpiresS = expiry > MillisecondExpiry ? expiry / 1000 : Math.Max(0, expiry),
                    LastAccessMs = Math.Max(0, query.GetInt64(4) / 1000),
                });
            }

            query.Reset();
            bySite[site] = cookies;
        }

        return new CookieRows(0, bySite);
    }

    /// <summary><c>LastVersion=144.0_20251020/20251020</c> of the profile's <c>compatibility.ini</c> → <c>144.0</c>.</summary>
    public static string? Version(string profile)
    {
        var text = CookieFiles.ReadText(Path.Combine(profile, "compatibility.ini"), MaxCompatibilityBytes);
        if (text is null)
        {
            return null;
        }

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (!line.StartsWith("LastVersion=", StringComparison.Ordinal))
            {
                continue;
            }

            var value = line["LastVersion=".Length..].Split('_')[0];
            if (value.Length is > 0 and <= MaxVersionLength && char.IsAsciiDigit(value[0])
                && value.All(c => char.IsAsciiDigit(c) || c == '.'))
            {
                return value;
            }
        }

        return null;
    }

    private static string? Resolve(string? path, bool relative, string root)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Contains('\0', StringComparison.Ordinal))
        {
            return null;
        }

        try
        {
            if (!relative)
            {
                return Path.IsPathFullyQualified(path) ? Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)) : null;
            }

            var top = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
            var full = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(Path.Combine(top, path.Replace('/', Path.DirectorySeparatorChar))));
            return full.StartsWith(top + Path.DirectorySeparatorChar, LocationPaths.Comparison) ? full : null;
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }
}
