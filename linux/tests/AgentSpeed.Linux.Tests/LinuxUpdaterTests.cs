using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentSpeed.Linux.Services;
using AgentSpeed.Presentation.Updates;

namespace AgentSpeed.Linux.Tests;

public sealed class LinuxUpdaterTests
{
    private const long MaxPackageBytes = 300L * 1024 * 1024;
    private static readonly byte[] Package = [1, 2, 3, 4];
    private static readonly string Manifest = $"{Convert.ToHexString(SHA256.HashData(Package)).ToLowerInvariant()}  AgentSpeed-Linux-x64.deb\n";
    private static readonly byte[] Feed = Encoding.UTF8.GetBytes($$"""
        {
          "tag_name": "v2.0.0",
          "name": "2.0.0",
          "assets": [
            { "name": "AgentSpeed-Linux-x64.deb", "browser_download_url": "http://updates.test/package", "size": {{Package.Length}} },
            { "name": "SHA256SUMS.txt", "browser_download_url": "http://updates.test/checksums", "size": {{Encoding.UTF8.GetByteCount(Manifest)}} }
          ]
        }
        """);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DirectoryPreparationFailureReleasesRequestForNextDownload(bool accessDenied)
    {
        using var folder = new TemporaryFolder();
        var handler = new RoutingHandler(RespondToReleaseRequests);
        var failOnce = true;
        using var updater = CreateUpdater(folder.Path, handler,
            ensurePrivate: path =>
            {
                if (failOnce)
                {
                    failOnce = false;
                    if (accessDenied) throw new UnauthorizedAccessException("Simulated directory access failure.");
                    throw new IOException("Simulated cache directory failure.");
                }

                Directory.CreateDirectory(path);
            });

        await updater.CheckAsync();
        Assert.NotNull(updater.Available);

        Assert.Null(await updater.DownloadAsync());
        Assert.Equal("Download or verification failed", updater.Status);

        var downloaded = await updater.DownloadAsync();
        Assert.NotNull(downloaded);
        Assert.True(File.Exists(downloaded));
        Assert.Equal("Verified", updater.Status);
    }

    [Fact]
    public async Task TemporaryFileCleanupFailureDoesNotKeepUpdaterBusy()
    {
        using var folder = new TemporaryFolder();
        var handler = new RoutingHandler(RespondToReleaseRequests);
        var cleanupCalls = 0;
        using var updater = CreateUpdater(folder.Path, handler,
            deleteTemporaryFile: _ =>
            {
                cleanupCalls++;
                throw new UnauthorizedAccessException("Simulated cleanup failure.");
            });

        await updater.CheckAsync();
        var downloaded = await updater.DownloadAsync();

        Assert.NotNull(downloaded);
        Assert.True(File.Exists(downloaded));
        Assert.Equal(1, cleanupCalls);

        await updater.CheckAsync();
        Assert.Equal(2, handler.FeedRequestCount);
    }

    [Fact]
    public async Task CancelledCheckReleasesRequestAndAllowsRetry()
    {
        using var folder = new TemporaryFolder();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var requestCount = 0;
        var handler = new RoutingHandler(async (request, token) =>
        {
            if (request.RequestUri?.AbsolutePath == "/feed" && Interlocked.Increment(ref requestCount) == 1)
            {
                entered.TrySetResult();
                await Task.Delay(Timeout.Infinite, token);
            }

            return await RespondToReleaseRequests(request, token);
        });
        using var updater = CreateUpdater(folder.Path, handler);

        var active = updater.CheckAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await updater.CheckAsync();
        Assert.Equal(1, requestCount);

        updater.Cancel();
        await active.WaitAsync(TimeSpan.FromSeconds(5));
        await updater.CheckAsync();

        Assert.Equal(2, requestCount);
        Assert.Equal("2.0.0", updater.Status);
    }

    [Fact]
    public async Task DisposeDuringFetchDefersHttpDisposalUntilRequestUnwinds()
    {
        using var folder = new TemporaryFolder();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new DisposalSensitiveHandler(entered);
        using var updater = CreateUpdater(folder.Path, handler);

        var check = updater.CheckAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        updater.Dispose();

        await check.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("Update check failed", updater.Status);
        Assert.True(handler.IsDisposed);
        await updater.CheckAsync();
        Assert.Equal(1, handler.RequestCount);
    }

    [Theory]
    [InlineData("checksum")]
    [InlineData("manifest")]
    [InlineData("size")]
    public async Task VerificationFailureDoesNotPublishAPackageOrBlockAVerifiedRetry(string fault)
    {
        using var folder = new TemporaryFolder();
        var badChecksum = true;
        var handler = new RoutingHandler((request, token) =>
        {
            if (request.RequestUri?.AbsolutePath == "/checksums" && badChecksum && fault != "size")
            {
                badChecksum = false;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent(fault == "manifest" ? "malformed manifest" : new string('0', 64) + "  AgentSpeed-Linux-x64.deb\n") });
            }
            if (request.RequestUri?.AbsolutePath == "/package" && badChecksum && fault == "size")
            {
                badChecksum = false;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([9]) });
            }
            return RespondToReleaseRequests(request, token);
        });
        using var updater = CreateUpdater(folder.Path, handler);
        await updater.CheckAsync();
        Assert.Null(await updater.DownloadAsync());
        Assert.Empty(Directory.EnumerateFiles(folder.Path, "*", SearchOption.AllDirectories));
        var verified = await updater.DownloadAsync();
        Assert.NotNull(verified);
        Assert.Equal(Package, await File.ReadAllBytesAsync(verified));
    }

    [Fact]
    public async Task DisabledNetworkMakesNoRequestsAndCanBeEnabledAgain()
    {
        using var folder = new TemporaryFolder();
        var enabled = false;
        var handler = new RoutingHandler(RespondToReleaseRequests);
        using var updater = new LinuxUpdater(UpdateSource.FromOverride("http://updates.test/feed", out _),
            () => enabled, () => "1.0.0", () => null, _ => { }, _ => { }, folder.Path, handler);
        var changes = 0;
        updater.Changed += () => changes++;
        await updater.CheckAsync();
        Assert.Null(await updater.DownloadAsync());
        Assert.Null(updater.Status);
        Assert.Equal(0, changes);
        Assert.Equal(0, handler.FeedRequestCount);
        enabled = true;
        await updater.CheckAsync();
        Assert.NotNull(updater.Available);
        Assert.Equal(1, changes);
        enabled = false;
        var statusBeforeDisabledDownload = updater.Status;
        Assert.Null(await updater.DownloadAsync());
        Assert.Equal(statusBeforeDisabledDownload, updater.Status);
        Assert.Equal(1, changes);
        Assert.False(Directory.Exists(folder.Path));
        Assert.Equal(1, handler.FeedRequestCount);
    }

    [Theory]
    [InlineData(false, true, 4L)]
    [InlineData(true, false, 4L)]
    [InlineData(true, true, 0L)]
    [InlineData(true, true, MaxPackageBytes + 1)]
    public async Task UnusableLinuxAssetsReportFailureWithoutOfferingOrDownloading(bool package, bool checksums, long size)
    {
        using var folder = new TemporaryFolder();
        var feed = FeedWithAssets(package, checksums, size);
        var handler = new RoutingHandler((request, token) => request.RequestUri?.AbsolutePath == "/feed"
            ? Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(feed) })
            : RespondToReleaseRequests(request, token));
        using var updater = CreateUpdater(folder.Path, handler);
        var changes = 0;
        updater.Changed += () => changes++;

        await updater.CheckAsync();

        Assert.Null(updater.Available);
        Assert.Equal("Download or verification failed", updater.Status);
        Assert.Equal(1, changes);
        Assert.Equal(1, handler.RequestCount);

        Assert.Null(await updater.DownloadAsync());
        Assert.Equal("Download or verification failed", updater.Status);
        Assert.Equal(2, changes);
        Assert.Equal(1, handler.RequestCount);
        Assert.False(Directory.Exists(folder.Path));
    }

    private static LinuxUpdater CreateUpdater(
        string updateDirectory,
        HttpMessageHandler handler,
        Action<string>? ensurePrivate = null,
        Action<string>? deleteTemporaryFile = null) =>
        new(
            UpdateSource.FromOverride("http://updates.test/feed", out _),
            networkEnabled: () => true,
            currentVersion: () => "1.0.0",
            skippedVersion: () => null,
            recordSuccessfulCheck: _ => { },
            skipVersion: _ => { },
            updateDirectory,
            handler,
            ensurePrivate ?? (path => Directory.CreateDirectory(path)),
            deleteTemporaryFile ?? (path => File.Delete(path)));

    private static Task<HttpResponseMessage> RespondToReleaseRequests(HttpRequestMessage request, CancellationToken token)
    {
        var content = request.RequestUri?.AbsolutePath switch
        {
            "/feed" => new ByteArrayContent(Feed),
            "/checksums" => new ByteArrayContent(Encoding.UTF8.GetBytes(Manifest)),
            "/package" => new ByteArrayContent(Package),
            _ => new ByteArrayContent([]),
        };
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
    }

    private static byte[] FeedWithAssets(bool package, bool checksums, long packageSize)
    {
        var assets = new List<object>();
        if (package)
        {
            assets.Add(new { name = "AgentSpeed-Linux-x64.deb", browser_download_url = "http://updates.test/package", size = packageSize });
        }
        if (checksums)
        {
            assets.Add(new { name = "SHA256SUMS.txt", browser_download_url = "http://updates.test/checksums", size = Encoding.UTF8.GetByteCount(Manifest) });
        }

        return JsonSerializer.SerializeToUtf8Bytes(new { tag_name = "v2.0.0", name = "2.0.0", assets });
    }

    private sealed class RoutingHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> route) : HttpMessageHandler
    {
        private int _feedRequestCount;
        private int _requestCount;

        public int FeedRequestCount => Volatile.Read(ref _feedRequestCount);
        public int RequestCount => Volatile.Read(ref _requestCount);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requestCount);
            if (request.RequestUri?.AbsolutePath == "/feed")
            {
                Interlocked.Increment(ref _feedRequestCount);
            }

            return route(request, cancellationToken);
        }
    }

    private sealed class DisposalSensitiveHandler(TaskCompletionSource entered) : HttpMessageHandler
    {
        private int _disposed;
        private int _requestCount;

        public bool IsDisposed => Volatile.Read(ref _disposed) != 0;
        public int RequestCount => Volatile.Read(ref _requestCount);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requestCount);
            entered.TrySetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                await Task.Yield();
                if (IsDisposed)
                {
                    throw new ObjectDisposedException(nameof(DisposalSensitiveHandler));
                }

                throw;
            }

            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Feed) };
        }

        protected override void Dispose(bool disposing)
        {
            Volatile.Write(ref _disposed, 1);
            base.Dispose(disposing);
        }
    }

    private sealed class TemporaryFolder : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "agent-speed-update-tests-" + Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
