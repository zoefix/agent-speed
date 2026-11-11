using System.Security.Cryptography;

namespace AgentSpeed.Core.Quota.Cookies;

/// <summary>
/// Where a site's cookies live on this PC (QUOTAS 9.10): Chrome, Edge, Brave, Vivaldi, Chromium (each profile), then Firefox
/// (each profile of <c>profiles.ini</c>). Only the site's rows are read, from private copies. Among the profiles with an
/// unexpired cookie the most recently used one wins, chosen before anything is decrypted, and only its browser's key is
/// unwrapped. A browser whose key cannot be unwrapped is not asked again until <see cref="ResetRefusals"/>. Never throws
/// for I/O; never keeps, logs or prints a cookie value.
/// </summary>
public sealed class BrowserCookieReader : IBrowserSessionReader
{
    private readonly object _gate = new();
    private readonly HashSet<BrowserKind> _refused = [];
    private readonly BrowserRoots _roots;
    private readonly IBrowserKeyUnwrapper _unwrapper;
    private readonly string _tempRoot;
    private bool _swept;

    /// <summary>The current user's browsers, with DPAPI.</summary>
    public BrowserCookieReader()
        : this(BrowserRoots.Current(), DpapiKeyUnwrapper.Instance)
    {
    }

    /// <param name="roots">The only folders read.</param>
    /// <param name="unwrapper">Unwraps the Chromium browsers' cookie keys.</param>
    /// <param name="tempRoot">Where the private copies are made; the user's temporary folder by default.</param>
    public BrowserCookieReader(BrowserRoots roots, IBrowserKeyUnwrapper unwrapper, string? tempRoot = null)
    {
        ArgumentNullException.ThrowIfNull(roots);
        ArgumentNullException.ThrowIfNull(unwrapper);
        _roots = roots;
        _unwrapper = unwrapper;
        _tempRoot = tempRoot ?? Path.GetTempPath();
    }

    public BrowserSession? Session(IReadOnlyList<string> sites, long nowMs, out BrowserSessionMiss miss)
    {
        ArgumentNullException.ThrowIfNull(sites);
        var asked = sites.Where(CookieFiles.IsSite).Distinct(StringComparer.Ordinal).ToList();
        var nowS = nowMs / 1000;
        lock (_gate)
        {
            if (!_swept)
            {
                _swept = true;
                CookieStoreCopy.SweepStale(_tempRoot, DateTime.UtcNow);
            }

            var candidates = Candidates(asked, nowS, out var locked);
            try
            {
                var undecryptable = false;
                // The most recently used profile first; OrderBy is stable, so equal times keep the browser order.
                foreach (var candidate in candidates.OrderByDescending(candidate => candidate.NewestAccessMs))
                {
                    if (Readable(candidate) is not { } cookies)
                    {
                        undecryptable = true;
                        continue;
                    }

                    if (CookieHeader.Join(cookies, candidate.Site, nowS) is { } header)
                    {
                        miss = BrowserSessionMiss.None;
                        return new BrowserSession(candidate.Site, header, UserAgent(candidate), candidate.Browser);
                    }
                }

                miss = locked ? BrowserSessionMiss.Locked
                    : undecryptable ? BrowserSessionMiss.Protected
                    : BrowserSessionMiss.NoSession;
                return null;
            }
            finally
            {
                foreach (var cookie in candidates.SelectMany(candidate => candidate.Cookies))
                {
                    CryptographicOperations.ZeroMemory(cookie.Encrypted);
                }
            }
        }
    }

    public void ResetRefusals()
    {
        lock (_gate)
        {
            _refused.Clear();
        }
    }

    /// <summary>Every profile's unexpired rows of the sites, without decrypting anything.</summary>
    private List<Candidate> Candidates(List<string> sites, long nowS, out bool locked)
    {
        var found = new List<Candidate>();
        locked = false;
        if (sites.Count == 0)
        {
            return found;
        }

        foreach (var browser in ChromiumBrowser.All(_roots.LocalAppData))
        {
            foreach (var store in browser.CookieStores())
            {
                var outcome = CookieStoreCopy.Read(store, _tempRoot, path => ChromeCookieStore.Read(path, sites), out var rows);
                locked |= outcome == CopyOutcome.Locked;
                if (rows is null)
                {
                    continue;
                }

                foreach (var site in sites)
                {
                    Add(found, new Candidate(browser.Kind, site, CookieHeader.Usable(rows.Of(site), site, nowS))
                    {
                        MetaVersion = rows.MetaVersion,
                        Chromium = browser,
                    });
                }
            }
        }

        foreach (var profile in FirefoxCookieStore.Profiles(_roots.Firefox))
        {
            var store = Path.Combine(profile, "cookies.sqlite");
            var outcome = CookieStoreCopy.Read(store, _tempRoot, path => FirefoxCookieStore.Read(path, sites), out var rows);
            locked |= outcome == CopyOutcome.Locked;
            if (rows is null)
            {
                continue;
            }

            foreach (var site in sites)
            {
                Add(found, new Candidate(BrowserKind.Firefox, site, CookieHeader.Usable(rows.Of(site), site, nowS))
                {
                    FirefoxProfile = profile,
                });
            }
        }

        return found;
    }

    private static void Add(List<Candidate> found, Candidate candidate)
    {
        if (candidate.Cookies.Count > 0)
        {
            found.Add(candidate);
        }
    }

    /// <summary>
    /// The candidate's rows with every value in plain text; null when any of them cannot be decrypted (<c>v20</c>, a refused
    /// key), since the session would then be incomplete.
    /// </summary>
    private List<BrowserCookie>? Readable(Candidate candidate)
    {
        if (!candidate.Cookies.Any(cookie => cookie.NeedsDecryption))
        {
            return candidate.Cookies;
        }

        if (candidate.Chromium is not { } browser || _refused.Contains(browser.Kind)
            || candidate.Cookies.Any(cookie => cookie.NeedsDecryption && !ChromeCookieCrypto.IsV10OrV11(cookie.Encrypted)))
        {
            return null;
        }

        if (ChromeLocalState.Key(browser, _unwrapper) is not { } key)
        {
            _refused.Add(browser.Kind);
            return null;
        }

        try
        {
            var cookies = new List<BrowserCookie>(candidate.Cookies.Count);
            foreach (var cookie in candidate.Cookies)
            {
                if (!cookie.NeedsDecryption)
                {
                    cookies.Add(cookie);
                }
                else if (ChromeCookieCrypto.DecryptGcm(cookie.Encrypted, key, candidate.MetaVersion) is { } value)
                {
                    cookies.Add(cookie.WithValue(value));
                }
                else
                {
                    return null;
                }
            }

            return cookies;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    private static string UserAgent(Candidate candidate) => candidate.Browser == BrowserKind.Firefox
        ? BrowserUserAgent.Firefox(
            BrowserUserAgent.WindowsToken,
            (candidate.FirefoxProfile is { } profile ? FirefoxCookieStore.Version(profile) : null) ?? BrowserUserAgent.DefaultFirefoxVersion)
        : BrowserUserAgent.Chrome(
            BrowserUserAgent.WindowsToken,
            candidate.Chromium?.MajorVersion() ?? BrowserUserAgent.DefaultChromeMajor);

    /// <summary>One profile's usable rows of one site.</summary>
    private sealed class Candidate(BrowserKind browser, string site, List<BrowserCookie> cookies)
    {
        public BrowserKind Browser { get; } = browser;

        public string Site { get; } = site;

        public List<BrowserCookie> Cookies { get; } = cookies;

        public long NewestAccessMs { get; } = cookies.Count == 0 ? 0 : cookies.Max(cookie => cookie.LastAccessMs);

        public int MetaVersion { get; init; }

        public ChromiumBrowser? Chromium { get; init; }

        public string? FirefoxProfile { get; init; }
    }
}
