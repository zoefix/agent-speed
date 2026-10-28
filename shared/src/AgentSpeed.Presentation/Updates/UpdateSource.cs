namespace AgentSpeed.Presentation.Updates;

/// <summary>
/// Where releases come from and which URLs may be fetched (UPDATES sections 1 and 6). The GitHub source allows only
/// https on the GitHub hosts, checked for the first URL and after every redirect. The override from
/// <see cref="FeedOverrideVariable"/> exists for local testing: it can only be set by the local user, so it lifts the
/// host allow-list (http allowed) for the feed and for the asset URLs that feed lists.
/// </summary>
public sealed class UpdateSource
{
    public const string GitHubFeedUrl = "https://api.github.com/repos/zoefix/agent-speed/releases/latest";
    public const string FeedOverrideVariable = "AGENT_SPEED_UPDATE_FEED";
    public const int MaxRedirects = 5;

    private static readonly string[] Hosts =
    [
        "github.com",
        "api.github.com",
        "objects.githubusercontent.com",
        "release-assets.githubusercontent.com",
    ];

    private UpdateSource(Uri feedUrl, bool isOverride)
    {
        FeedUrl = feedUrl;
        IsOverride = isOverride;
    }

    public static UpdateSource GitHub { get; } = new(new Uri(GitHubFeedUrl, UriKind.Absolute), isOverride: false);

    public static IReadOnlyList<string> AllowedHosts => Hosts;

    public Uri FeedUrl { get; }

    /// <summary>True when the feed came from <see cref="FeedOverrideVariable"/>; the host allow-list is then off.</summary>
    public bool IsOverride { get; }

    /// <summary>
    /// The source for an environment value: null or blank means GitHub; an unusable value also falls back to GitHub
    /// and sets <paramref name="rejected"/>.
    /// </summary>
    public static UpdateSource FromOverride(string? value, out bool rejected)
    {
        rejected = false;
        if (string.IsNullOrWhiteSpace(value))
        {
            return GitHub;
        }

        if (ReleaseFeed.WebUrl(value.Trim()) is { } uri)
        {
            return new UpdateSource(uri, isOverride: true);
        }

        rejected = true;
        return GitHub;
    }

    /// <summary>https on an allow-listed GitHub host and the default port, without credentials.</summary>
    public static bool IsAllowedGitHubUrl(Uri? uri) =>
        uri is { IsAbsoluteUri: true }
        && uri.Scheme == Uri.UriSchemeHttps
        && uri.HostNameType == UriHostNameType.Dns
        && uri.IsDefaultPort
        && string.IsNullOrEmpty(uri.UserInfo)
        && Array.IndexOf(Hosts, uri.IdnHost.ToLowerInvariant()) >= 0;

    public bool IsAllowed(Uri? uri) => IsOverride
        ? uri is { IsAbsoluteUri: true } && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
          && string.IsNullOrEmpty(uri.UserInfo)
        : IsAllowedGitHubUrl(uri);

    /// <summary>The next URL of a redirect (a relative Location resolves against the current URL), or null when it may not be followed.</summary>
    public Uri? ResolveRedirect(Uri current, Uri? location)
    {
        ArgumentNullException.ThrowIfNull(current);
        if (location is null)
        {
            return null;
        }

        var next = location.IsAbsoluteUri ? location : Uri.TryCreate(current, location, out var resolved) ? resolved : null;
        return IsAllowed(next) ? next : null;
    }

    /// <summary>The release page to open, if it is safe to hand to the browser.</summary>
    public Uri? ReleasePage(ReleaseInfo? release) => release?.PageUrl is { } page && IsAllowed(page) ? page : null;
}
