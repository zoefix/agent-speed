using System.Globalization;
using AgentSpeed.Core.IO.Sqlite;

namespace AgentSpeed.Core.Quota.Cookies;

/// <summary>The rows of each site in one store, at most <see cref="ChromeCookieStore.MaxRows"/> per site.</summary>
internal sealed class CookieRows(int metaVersion, Dictionary<string, List<BrowserCookie>> bySite)
{
    /// <summary>Chromium's <c>meta</c> <c>version</c>; 0 for Firefox.</summary>
    public int MetaVersion { get; } = metaVersion;

    public List<BrowserCookie> Of(string site) => bySite.TryGetValue(site, out var rows) ? rows : [];
}

/// <summary>A private copy of a Chromium <c>Cookies</c> database (QUOTAS 9.10).</summary>
internal static class ChromeCookieStore
{
    public const int MaxRows = 256;

    /// <summary>Microseconds from 1601-01-01 to 1970-01-01.</summary>
    public const long WindowsEpochOffsetUs = 11_644_473_600_000_000;

    private const string Rows = """
        SELECT host_key, name, value, encrypted_value, expires_utc, last_access_utc FROM cookies
        WHERE host_key = ?1 OR host_key = ?2 OR host_key LIKE ?3 ESCAPE '\'
        """;

    /// <exception cref="SqliteException">The copy is no Chromium cookie database.</exception>
    public static CookieRows Read(string path, IReadOnlyList<string> sites)
    {
        using var db = SqliteConnection.Open(path, SqliteOpenMode.ReadWriteCreate);
        var version = MetaVersion(db);
        var bySite = new Dictionary<string, List<BrowserCookie>>(StringComparer.Ordinal);
        using var query = db.Prepare(Rows);
        foreach (var site in sites)
        {
            var (exact, dotted, pattern) = CookieStoreCopy.HostArguments(site);
            query.Bind(1, exact).Bind(2, dotted).Bind(3, pattern);
            var cookies = new List<BrowserCookie>();
            while (cookies.Count < MaxRows && query.Step())
            {
                cookies.Add(new BrowserCookie
                {
                    Host = query.GetString(0) ?? string.Empty,
                    Name = query.GetString(1) ?? string.Empty,
                    Value = query.GetString(2) ?? string.Empty,
                    Encrypted = query.GetBlob(3).ToArray(),
                    ExpiresS = Seconds(query.GetInt64(4)),
                    LastAccessMs = Milliseconds(query.GetInt64(5)),
                });
            }

            query.Reset();
            bySite[site] = cookies;
        }

        return new CookieRows(version, bySite);
    }

    /// <summary><c>expires_utc</c> (µs since 1601, 0 for a session cookie) in Unix seconds.</summary>
    public static long Seconds(long chromeUs) => chromeUs <= 0 ? 0 : Math.Max(1, (chromeUs - WindowsEpochOffsetUs) / 1_000_000);

    public static long Milliseconds(long chromeUs) => chromeUs <= 0 ? 0 : (chromeUs - WindowsEpochOffsetUs) / 1000;

    private static int MetaVersion(SqliteConnection db)
    {
        using var meta = db.Prepare("SELECT value FROM meta WHERE key = 'version'");
        return meta.Step() && int.TryParse(meta.GetString(0), NumberStyles.None, CultureInfo.InvariantCulture, out var version)
            ? version
            : 0;
    }
}
