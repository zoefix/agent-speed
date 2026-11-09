using System.IO;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using AgentSpeed.Presentation.Updates;
using AgentSpeed.Updates;

namespace AgentSpeed.Windows.Tests;

public class UpdateClientTests
{
    private const string Releases = UpdateSource.GitHubReleasesUrl;
    private const string Tag = "v99.0.0";
    private static readonly string Package = UpdatePackage.AssetNameFor(RuntimeInformation.ProcessArchitecture)!;
    private const string Atom = """
        <feed xmlns="http://www.w3.org/2005/Atom">
          <entry><title>Preview</title><link rel="alternate" href="https://github.com/zoefix/agent-speed/releases/tag/v100.0.0-beta"/></entry>
          <entry>
            <title>Agent Speed 99</title><updated>2026-11-09T00:00:00Z</updated>
            <link rel="alternate" href="https://github.com/zoefix/agent-speed/releases/tag/v99.0.0"/>
            <content type="html">&lt;h2&gt;New &amp;amp; improved&lt;/h2&gt;
        &lt;ul&gt;&lt;li&gt;Update checks work&lt;/li&gt;&lt;/ul&gt;</content>
          </entry>
        </feed>
        """;

    [Fact]
    public async Task SuccessfulApiDoesNotUseFallback()
    {
        using var handler = new ScriptedHandler(_ => Json("{\"tag_name\":\"v99.0.0\"}"));
        using var client = new UpdateClient(UpdateSource.GitHub, "1.6.1", handler);
        Assert.Equal(Tag, (await client.FetchLatestAsync(default))?.Tag);
        Assert.Equal([UpdateSource.GitHubFeedUrl], handler.Urls);
    }

    [Theory]
    [InlineData(403)]
    [InlineData(429)]
    [InlineData(503)]
    [InlineData(0)]
    public async Task UnavailableApiFallsBackToInstallableStableRelease(int status)
    {
        using var handler = FallbackHandler(status);
        using var client = new UpdateClient(UpdateSource.GitHub, "1.6.1", handler);
        var release = Assert.IsType<ReleaseInfo>(await client.FetchLatestAsync(default));
        Assert.Equal(Tag, release.Tag);
        Assert.Equal("Agent Speed 99", release.Name);
        Assert.Contains(ReleaseNotes.Parse(release.Notes), b => b.Kind == NoteBlockKind.Heading && b.Text == "New & improved");
        Assert.Contains(ReleaseNotes.Parse(release.Notes), b => b.Kind == NoteBlockKind.Bullet && b.Text == "Update checks work");
        var plan = UpdatePackage.Plan(release, RuntimeInformation.ProcessArchitecture, UpdateSource.GitHub);
        Assert.Equal(1234, plan.Package.Size);
        Assert.Equal(200, plan.Checksums.Size);
        Assert.Equal(new Uri($"{Releases}/download/{Tag}/{Package}"), plan.Package.DownloadUrl);
        Assert.Equal(6, handler.Urls.Count);
    }

    [Fact]
    public async Task LocalFeedOverrideNeverContactsGitHubOnFailure()
    {
        var source = UpdateSource.FromOverride("http://localhost:1234/feed", out _);
        using var handler = new ScriptedHandler(_ => new HttpResponseMessage(HttpStatusCode.Forbidden));
        using var client = new UpdateClient(source, "1.6.1", handler);
        Assert.Equal(UpdateFailure.RateLimited, (await Assert.ThrowsAsync<UpdateException>(() => client.FetchLatestAsync(default))).Failure);
        Assert.Equal([source.FeedUrl.AbsoluteUri], handler.Urls);
    }

    [Theory]
    [InlineData("https://evil.example/latest")]
    [InlineData(Releases + "/tag/v100.0.0-beta")]
    [InlineData("https://github.com/other/repo/releases/tag/v99.0.0")]
    public async Task FallbackRejectsUnsafeOrWrongRelease(string location)
    {
        using var handler = FallbackHandler(403, latestLocation: location);
        using var client = new UpdateClient(UpdateSource.GitHub, "1.6.1", handler);
        Assert.Equal(UpdateFailure.Invalid, (await Assert.ThrowsAsync<UpdateException>(() => client.FetchLatestAsync(default))).Failure);
        Assert.DoesNotContain(handler.Urls, u => u.Contains("/download/", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0L)]
    [InlineData(UpdatePackage.MaxAssetBytes + 1)]
    public async Task FallbackRejectsMissingOrInvalidAssetSize(long? size)
    {
        using var handler = FallbackHandler(403, packageSize: size);
        using var client = new UpdateClient(UpdateSource.GitHub, "1.6.1", handler);
        Assert.Equal(UpdateFailure.Invalid, (await Assert.ThrowsAsync<UpdateException>(() => client.FetchLatestAsync(default))).Failure);
    }

    [Theory]
    [InlineData("<feed/>")]
    [InlineData("<!DOCTYPE feed [<!ENTITY secret SYSTEM 'file:///private'>]><feed>&secret;</feed>")]
    public async Task MalformedOrExternalEntityFeedCannotReportSuccess(string atom)
    {
        using var handler = FallbackHandler(403, atom: atom);
        using var client = new UpdateClient(UpdateSource.GitHub, "1.6.1", handler);
        Assert.Equal(UpdateFailure.Invalid, (await Assert.ThrowsAsync<UpdateException>(() => client.FetchLatestAsync(default))).Failure);
    }

    [Fact]
    public async Task CallerCancellationDoesNotStartFallback()
    {
        using var cancel = new CancellationTokenSource();
        using var handler = new ScriptedHandler(_ =>
        {
            cancel.Cancel();
            throw new OperationCanceledException(cancel.Token);
        });
        using var client = new UpdateClient(UpdateSource.GitHub, "1.6.1", handler);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.FetchLatestAsync(cancel.Token));
        Assert.Single(handler.Urls);
    }

    private static ScriptedHandler FallbackHandler(int status, string? latestLocation = null, long? packageSize = 1234, string atom = Atom) =>
        new(request => request.RequestUri!.AbsoluteUri switch
        {
            UpdateSource.GitHubFeedUrl => status == 0 ? throw new HttpRequestException("offline") : new HttpResponseMessage((HttpStatusCode)status),
            Releases + "/latest" => Redirect(latestLocation ?? Releases + "/tag/" + Tag),
            var url when url.Contains("/tag/", StringComparison.Ordinal) => Json("unused page body"),
            Releases + ".atom" => Json(atom),
            var url when url.EndsWith(Package, StringComparison.Ordinal) => HeadersOnly(packageSize),
            var url when url.EndsWith(UpdatePackage.ChecksumsName, StringComparison.Ordinal) => HeadersOnly(200),
            _ => throw new InvalidOperationException("Unexpected URL"),
        });

    private static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK) { Content = new StringContent(value, Encoding.UTF8) };

    private static HttpResponseMessage Redirect(string url)
    {
        var response = new HttpResponseMessage(HttpStatusCode.Found);
        response.Headers.Location = new Uri(url);
        return response;
    }

    private static HttpResponseMessage HeadersOnly(long? size)
    {
        var content = new UnreadableContent();
        content.Headers.ContentLength = size;
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    private sealed class UnreadableContent : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            throw new InvalidOperationException("Update checks must not read installer bodies.");
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
    }

    private sealed class ScriptedHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<string> Urls { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Null(request.Headers.Authorization);
            Assert.False(request.Headers.Contains("Cookie"));
            if (request.RequestUri!.Host == "github.com") Assert.False(request.Headers.Contains("X-GitHub-Api-Version"));
            Urls.Add(request.RequestUri.AbsoluteUri);
            var response = respond(request);
            response.RequestMessage = request;
            return Task.FromResult(response);
        }
    }
}
