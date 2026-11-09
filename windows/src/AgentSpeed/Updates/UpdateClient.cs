using System.Buffers;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using AgentSpeed.Presentation.Updates;

namespace AgentSpeed.Updates;

/// <summary>
/// Update network access (UPDATES section 1): the release feeds and the two release assets. Redirects
/// are followed by hand so that every hop is checked against <see cref="UpdateSource"/>; requests carry only Accept,
/// User-Agent (and X-GitHub-Api-Version for the feed), never cookies or credentials.
/// </summary>
internal sealed class UpdateClient : IDisposable
{
    public static readonly TimeSpan FeedTimeout = TimeSpan.FromSeconds(15);

    /// <summary>A download fails when no data arrives for this long.</summary>
    public static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(30);

    private const string FeedAccept = "application/vnd.github+json";
    private const string AssetAccept = "application/octet-stream";
    private const string ApiVersion = "2022-11-28";

    private readonly HttpClient _http;
    private readonly string _userAgent;

    public UpdateClient(UpdateSource source, string currentVersion)
        : this(source, currentVersion, CreateHandler())
    {
    }

    internal UpdateClient(UpdateSource source, string currentVersion, HttpMessageHandler handler)
    {
        Source = source;
        _userAgent = $"AgentSpeed/{currentVersion} (Windows)";
        _http = new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
    }

    private static SocketsHttpHandler CreateHandler() => new()
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        AutomaticDecompression = DecompressionMethods.None,
        Credentials = null,
        PreAuthenticate = false,
        ConnectTimeout = FeedTimeout,
        PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30),
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        MaxResponseHeadersLength = 64,
    };

    public UpdateSource Source { get; }

    /// <summary>The latest offerable release, or null when the feed has none (including HTTP 404: no releases yet).</summary>
    /// <exception cref="UpdateException">Network, rate limit or invalid feed.</exception>
    public async Task<ReleaseInfo?> FetchLatestAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await FetchFeedAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (UpdateException ex) when (!Source.IsOverride
            && ex.Failure is UpdateFailure.Network or UpdateFailure.RateLimited
            && !cancellationToken.IsCancellationRequested)
        {
            return await FetchPublicLatestAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<ReleaseInfo?> FetchFeedAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(FeedTimeout);
        try
        {
            using var response = await SendAsync(Source.FeedUrl, FeedAccept, feed: true, timeout.Token).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return null;
            }

            EnsureSuccess(response);
            if (response.Content.Headers.ContentLength > ReleaseFeed.MaxFeedBytes)
            {
                throw new UpdateException(UpdateFailure.Invalid, "The release feed is too large.");
            }

            var bytes = await ReadLimitedAsync(response.Content, ReleaseFeed.MaxFeedBytes, timeout.Token).ConfigureAwait(false);
            return ReleaseFeed.Parse(bytes);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new UpdateException(UpdateFailure.Network, "The release feed timed out.", ex);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            throw new UpdateException(UpdateFailure.Network, "The release feed could not be read.", ex);
        }
    }

    private async Task<ReleaseInfo?> FetchPublicLatestAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(FeedTimeout);
        try
        {
            // GitHub's latest redirect excludes drafts and pre-releases; the Atom feed alone does not.
            using var latest = await SendAsync(new Uri(UpdateSource.GitHubReleasesUrl + "/latest"), "text/html",
                feed: false, timeout.Token).ConfigureAwait(false);
            if (latest.StatusCode == HttpStatusCode.NotFound)
            {
                return null;
            }

            EnsureSuccess(latest);
            var page = latest.RequestMessage?.RequestUri
                ?? throw new UpdateException(UpdateFailure.Invalid, "The latest release has no page URL.");
            using var atom = await SendAsync(new Uri(UpdateSource.GitHubReleasesUrl + ".atom"), "application/atom+xml",
                feed: false, timeout.Token).ConfigureAwait(false);
            EnsureSuccess(atom);
            if (atom.Content.Headers.ContentLength > ReleaseFeed.MaxFeedBytes)
            {
                throw new UpdateException(UpdateFailure.Invalid, "The public release feed is too large.");
            }

            var bytes = await ReadLimitedAsync(atom.Content, ReleaseFeed.MaxFeedBytes, timeout.Token).ConfigureAwait(false);
            var release = GitHubReleaseFallback.Parse(page, bytes);
            var packageName = UpdatePackage.AssetNameFor(RuntimeInformation.ProcessArchitecture)
                ?? throw new UpdateException(UpdateFailure.Invalid, "No package for this architecture.");
            var root = UpdateSource.GitHubReleasesUrl + "/download/" + Uri.EscapeDataString(release.Tag) + "/";
            var package = await FetchAssetMetadataAsync(root, packageName, UpdatePackage.MaxAssetBytes, timeout.Token).ConfigureAwait(false);
            var checksums = await FetchAssetMetadataAsync(root, UpdatePackage.ChecksumsName, UpdatePackage.MaxChecksumsBytes, timeout.Token).ConfigureAwait(false);
            return release with { Assets = [package, checksums] };
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new UpdateException(UpdateFailure.Network, "The public release check timed out.", ex);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            throw new UpdateException(UpdateFailure.Network, "The public release could not be read.", ex);
        }
    }

    private async Task<ReleaseAsset> FetchAssetMetadataAsync(string root, string name, long maxBytes, CancellationToken cancellationToken)
    {
        var url = new Uri(root + name);
        using var response = await SendAsync(url, AssetAccept, feed: false, cancellationToken).ConfigureAwait(false);
        EnsureSuccess(response);
        // Read headers only, never download an installer during an update check. The existing install path still
        // checks the exact size and SHA-256 of the package before extracting or replacing anything.
        if (response.Content.Headers.ContentLength is not { } size || size <= 0 || size > maxBytes)
        {
            throw new UpdateException(UpdateFailure.Invalid, $"{name} has an invalid size.");
        }

        return new ReleaseAsset(name, url, size);
    }

    /// <summary>Streams an asset to a new file; the size must equal the size the feed declared.</summary>
    /// <exception cref="UpdateException">Network failure, disallowed URL or wrong size.</exception>
    public async Task DownloadAsync(ReleaseAsset asset, string path, IProgress<long>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(asset);
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        stall.CancelAfter(StallTimeout);
        var buffer = ArrayPool<byte>.Shared.Rent(81920);
        try
        {
            using var response = await SendAsync(asset.DownloadUrl, AssetAccept, feed: false, stall.Token).ConfigureAwait(false);
            EnsureSuccess(response);
            if (response.Content.Headers.ContentLength is { } declared && declared != asset.Size)
            {
                throw new UpdateException(UpdateFailure.Invalid, $"{asset.Name} has an unexpected size.");
            }

            await using var input = await response.Content.ReadAsStreamAsync(stall.Token).ConfigureAwait(false);
            await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous);
            long total = 0;
            int read;
            while ((read = await input.ReadAsync(buffer, stall.Token).ConfigureAwait(false)) > 0)
            {
                total += read;
                if (total > asset.Size)
                {
                    throw new UpdateException(UpdateFailure.Invalid, $"{asset.Name} is larger than declared.");
                }

                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                stall.CancelAfter(StallTimeout);
                progress?.Report(total);
            }

            if (total != asset.Size)
            {
                throw new UpdateException(UpdateFailure.Invalid, $"{asset.Name} is smaller than declared.");
            }

            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new UpdateException(UpdateFailure.Network, $"Downloading {asset.Name} stalled.", ex);
        }
        catch (HttpRequestException ex)
        {
            throw new UpdateException(UpdateFailure.Network, $"Downloading {asset.Name} failed.", ex);
        }
        catch (IOException ex)
        {
            throw new UpdateException(UpdateFailure.Network, $"Downloading {asset.Name} was interrupted.", ex);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public void Dispose() => _http.Dispose();

    private async Task<HttpResponseMessage> SendAsync(Uri url, string accept, bool feed, CancellationToken cancellationToken)
    {
        if (!Source.IsAllowed(url))
        {
            throw new UpdateException(UpdateFailure.Invalid, "The URL is not on the allowed hosts.");
        }

        var current = url;
        for (var hop = 0; ; hop++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, current) { Version = HttpVersion.Version11 };
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(accept));
            request.Headers.TryAddWithoutValidation("User-Agent", _userAgent);
            if (feed)
            {
                request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", ApiVersion);
            }

            var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (!IsRedirect(response.StatusCode))
            {
                return response;
            }

            var location = response.Headers.Location;
            response.Dispose();
            if (hop >= UpdateSource.MaxRedirects)
            {
                throw new UpdateException(UpdateFailure.Invalid, "Too many redirects.");
            }

            current = Source.ResolveRedirect(current, location)
                ?? throw new UpdateException(UpdateFailure.Invalid, "A redirect left the allowed hosts.");
        }
    }

    private static bool IsRedirect(HttpStatusCode status) => status is HttpStatusCode.MovedPermanently or HttpStatusCode.Found
        or HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;

    private static void EnsureSuccess(HttpResponseMessage response)
    {
        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
        {
            throw new UpdateException(UpdateFailure.RateLimited, $"HTTP {(int)response.StatusCode} (rate limited).");
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new UpdateException(UpdateFailure.Network, $"HTTP {(int)response.StatusCode}.");
        }
    }

    private static async Task<byte[]> ReadLimitedAsync(HttpContent content, int limit, CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[16384];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > limit)
            {
                throw new UpdateException(UpdateFailure.Invalid, "The response is too large.");
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }
}
