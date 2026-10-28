using AgentSpeed.Presentation.Tests.TestSupport;
using AgentSpeed.Presentation.Updates;

namespace AgentSpeed.Presentation.Tests;

public class UpdateSourceTests
{
    private static readonly Uri AssetUrl = new("https://github.com/zoefix/agent-speed/releases/download/v1.2.0/AgentSpeed-Windows-x64.zip");

    private static UpdateSource Override(string value)
    {
        var source = UpdateSource.FromOverride(value, out var rejected);
        Assert.False(rejected);
        return source;
    }

    [Fact]
    public void GitHubFeedUrlMatchesSpecification()
    {
        var source = UpdateSource.GitHub;

        Assert.Equal("https://api.github.com/repos/zoefix/agent-speed/releases/latest", source.FeedUrl.AbsoluteUri);
        Assert.Equal(UpdateSource.GitHubFeedUrl, source.FeedUrl.OriginalString);
        Assert.False(source.IsOverride);
        Assert.True(UpdateSource.IsAllowedGitHubUrl(source.FeedUrl));
        Assert.Same(source, UpdateSource.GitHub);
        Assert.Equal(
            ["github.com", "api.github.com", "objects.githubusercontent.com", "release-assets.githubusercontent.com"],
            UpdateSource.AllowedHosts);
        Assert.Equal("AGENT_SPEED_UPDATE_FEED", UpdateSource.FeedOverrideVariable);
    }

    [Theory]
    [InlineData("https://github.com/zoefix/agent-speed/releases/download/v1.2.0/AgentSpeed-Windows-x64.zip")]
    [InlineData("https://api.github.com/repos/zoefix/agent-speed/releases/latest")]
    [InlineData("https://objects.githubusercontent.com/github-production-release-asset-2e65be/1?X-Amz-Signature=abc")]
    [InlineData("https://release-assets.githubusercontent.com/github-production-release-asset/1/2?sp=r&sv=2018-11-09")]
    [InlineData("https://GitHub.COM/zoefix/agent-speed")]
    [InlineData("https://API.GITHUB.COM/repos")]
    [InlineData("HTTPS://github.com/x")]
    [InlineData("https://github.com:443/x")]
    public void AllowsHttpsOnGitHubHosts(string url)
    {
        Assert.True(UpdateSource.IsAllowedGitHubUrl(new Uri(url)));
        Assert.True(UpdateSource.GitHub.IsAllowed(new Uri(url)));
    }

    [Theory]
    [InlineData("http://github.com/x")]
    [InlineData("http://objects.githubusercontent.com/x")]
    [InlineData("https://example.com/x")]
    [InlineData("https://github.com.evil.com/x")]
    [InlineData("https://api.github.com.evil.com/x")]
    [InlineData("https://evilgithub.com/x")]
    [InlineData("https://sub.github.com/x")]
    [InlineData("https://gist.github.com/x")]
    [InlineData("https://uploads.github.com/x")]
    [InlineData("https://githubusercontent.com/x")]
    [InlineData("https://raw.githubusercontent.com/x")]
    [InlineData("https://evil.objects.githubusercontent.com/x")]
    [InlineData("https://github.io/x")]
    [InlineData("https://github.com./x")]
    [InlineData("https://g\u00edthub.com/x")]
    [InlineData("https://github.com:8443/x")]
    [InlineData("https://github.com:80/x")]
    [InlineData("https://user:pass@github.com/x")]
    [InlineData("https://user@github.com/x")]
    [InlineData("https://140.82.112.3/x")]
    [InlineData("https://[2606:50c0:8000::153]/x")]
    [InlineData("https://evil.com#@github.com")]
    [InlineData("https://evil.com/?u=https://github.com/")]
    [InlineData("ftp://github.com/x")]
    [InlineData("wss://github.com/x")]
    [InlineData("file:///github.com/x")]
    public void RejectsEverythingElse(string url)
    {
        Assert.False(UpdateSource.IsAllowedGitHubUrl(new Uri(url)));
        Assert.False(UpdateSource.GitHub.IsAllowed(new Uri(url)));
    }

    [Fact]
    public void RejectsNullAndRelativeUrls()
    {
        Assert.False(UpdateSource.IsAllowedGitHubUrl(null));
        Assert.False(UpdateSource.IsAllowedGitHubUrl(new Uri("releases/latest", UriKind.Relative)));
        Assert.False(UpdateSource.GitHub.IsAllowed(null));
        Assert.False(Override("http://localhost:8080/latest.json").IsAllowed(new Uri("x.zip", UriKind.Relative)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void BlankOverrideUsesGitHub(string? value)
    {
        var source = UpdateSource.FromOverride(value, out var rejected);

        Assert.Same(UpdateSource.GitHub, source);
        Assert.False(rejected);
        Assert.False(source.IsOverride);
    }

    [Theory]
    [InlineData("http://localhost:8080/latest.json")]
    [InlineData("https://updates.example.test/releases/latest")]
    [InlineData("  http://192.168.1.20/feed.json  ")]
    public void ValidOverrideIsUsed(string value)
    {
        var source = UpdateSource.FromOverride(value, out var rejected);

        Assert.False(rejected);
        Assert.True(source.IsOverride);
        Assert.NotSame(UpdateSource.GitHub, source);
        Assert.Equal(new Uri(value.Trim()), source.FeedUrl);
    }

    [Theory]
    [InlineData("file:///c:/x")]
    [InlineData("file:///etc/feed.json")]
    [InlineData("/etc/feed.json")]
    [InlineData("relative/feed.json")]
    [InlineData("not a url")]
    [InlineData("ftp://x")]
    [InlineData("javascript:alert(1)")]
    [InlineData("https://user:secret@updates.example.test/feed")]
    public void UnusableOverrideFallsBackToGitHub(string value)
    {
        var source = UpdateSource.FromOverride(value, out var rejected);

        Assert.True(rejected);
        Assert.Same(UpdateSource.GitHub, source);
        Assert.False(source.IsOverride);
    }

    [Fact]
    public void OverrideAllowsAnyHttpHostButNoOtherScheme()
    {
        var source = Override("http://localhost:8080/latest.json");

        Assert.True(source.IsAllowed(new Uri("http://localhost:8080/AgentSpeed-Windows-x64.zip")));
        Assert.True(source.IsAllowed(new Uri("http://192.168.1.20:9000/x.zip")));
        Assert.True(source.IsAllowed(new Uri("https://downloads.example.test/x.zip")));
        Assert.True(source.IsAllowed(AssetUrl));
        Assert.False(source.IsAllowed(new Uri("file:///tmp/x.zip")));
        Assert.False(source.IsAllowed(new Uri("ftp://localhost/x.zip")));
        Assert.False(source.IsAllowed(new Uri("http://user:pw@localhost/x.zip")));
        Assert.False(source.IsAllowed(null));
    }

    [Fact]
    public void RedirectToAllowedAbsoluteUrlIsFollowed()
    {
        var location = new Uri("https://release-assets.githubusercontent.com/github-production-release-asset/1/2?sig=abc");
        Assert.Equal(location, UpdateSource.GitHub.ResolveRedirect(AssetUrl, location));

        var objects = new Uri("https://objects.githubusercontent.com/github-production-release-asset-2e65be/3");
        Assert.Equal(objects, UpdateSource.GitHub.ResolveRedirect(location, objects));
    }

    [Theory]
    [InlineData("/zoefix/agent-speed/releases/download/v1.2.1/AgentSpeed-Windows-x64.zip", "https://github.com/zoefix/agent-speed/releases/download/v1.2.1/AgentSpeed-Windows-x64.zip")]
    [InlineData("other.zip", "https://github.com/zoefix/agent-speed/releases/download/v1.2.0/other.zip")]
    [InlineData("../v1.2.1/x.zip", "https://github.com/zoefix/agent-speed/releases/download/v1.2.1/x.zip")]
    [InlineData("?attempt=2", "https://github.com/zoefix/agent-speed/releases/download/v1.2.0/AgentSpeed-Windows-x64.zip?attempt=2")]
    public void RelativeLocationResolvesAgainstCurrentUrl(string location, string expected)
    {
        Assert.Equal(new Uri(expected), UpdateSource.GitHub.ResolveRedirect(AssetUrl, new Uri(location, UriKind.Relative)));
    }

    [Theory]
    [InlineData("http://github.com/zoefix/agent-speed/releases/download/v1.2.0/AgentSpeed-Windows-x64.zip")]
    [InlineData("http://objects.githubusercontent.com/x")]
    [InlineData("https://evil.example/x.zip")]
    [InlineData("https://github.com.evil.example/x.zip")]
    [InlineData("https://github.com:8443/x.zip")]
    [InlineData("https://user@objects.githubusercontent.com/x")]
    [InlineData("https://140.82.112.3/x.zip")]
    [InlineData("file:///etc/passwd")]
    [InlineData("ftp://github.com/x.zip")]
    public void RedirectToDisallowedUrlIsRefused(string location)
    {
        Assert.Null(UpdateSource.GitHub.ResolveRedirect(AssetUrl, new Uri(location)));
    }

    [Fact]
    public void RedirectWithoutLocationIsRefused()
    {
        Assert.Null(UpdateSource.GitHub.ResolveRedirect(AssetUrl, null));
        Assert.Throws<ArgumentNullException>(() => UpdateSource.GitHub.ResolveRedirect(null!, AssetUrl));
    }

    [Fact]
    public void OverrideRedirectsMayUseLocalHttpHosts()
    {
        var source = Override("http://192.168.1.20:8000/latest.json");
        var current = new Uri("http://192.168.1.20:8000/AgentSpeed-Windows-x64.zip");

        Assert.Equal(
            new Uri("http://192.168.1.20:8000/files/AgentSpeed-Windows-x64.zip"),
            source.ResolveRedirect(current, new Uri("/files/AgentSpeed-Windows-x64.zip", UriKind.Relative)));
        Assert.Equal(new Uri("http://10.0.0.5/x.zip"), source.ResolveRedirect(current, new Uri("http://10.0.0.5/x.zip")));
        Assert.Null(source.ResolveRedirect(current, new Uri("file:///etc/passwd")));
        Assert.Null(source.ResolveRedirect(current, null));
    }

    [Fact]
    public void ReleasePageMustBeAllowed()
    {
        var page = "https://github.com/zoefix/agent-speed/releases/tag/v1.2.0";
        Assert.Equal(new Uri(page), UpdateSource.GitHub.ReleasePage(TestReleases.Release(pageUrl: page)));
        Assert.Null(UpdateSource.GitHub.ReleasePage(TestReleases.Release(pageUrl: "https://evil.example/releases/v1.2.0")));
        Assert.Null(UpdateSource.GitHub.ReleasePage(TestReleases.Release(pageUrl: "http://github.com/zoefix/agent-speed/releases")));
        Assert.Null(UpdateSource.GitHub.ReleasePage(TestReleases.Release() with { PageUrl = null }));
        Assert.Null(UpdateSource.GitHub.ReleasePage(null));

        var local = "http://localhost:8080/releases/v1.2.0";
        Assert.Equal(new Uri(local), Override("http://localhost:8080/latest.json").ReleasePage(TestReleases.Release(pageUrl: local)));
    }
}
