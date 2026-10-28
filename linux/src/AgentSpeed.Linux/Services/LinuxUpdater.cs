using AgentSpeed.Linux.Platform;
using AgentSpeed.Presentation.Updates;

namespace AgentSpeed.Linux.Services;

internal sealed class LinuxUpdater : IDisposable
{
    private readonly object _requestLock = new();
    private readonly HttpClient _http;
    private readonly UpdateSource _source;
    private readonly Func<bool> _networkEnabled;
    private readonly Func<string> _currentVersion;
    private readonly Func<string?> _skippedVersion;
    private readonly Action<DateTimeOffset> _recordSuccessfulCheck;
    private readonly Action<string> _skipVersion;
    private readonly string _updateDirectory;
    private readonly Action<string> _ensurePrivate;
    private readonly Action<string> _deleteTemporaryFile;
    private CancellationTokenSource? _request;
    private bool _disposed;
    private bool _httpDisposed;

    public LinuxUpdater(LinuxApp app)
        : this(
            UpdateSource.FromOverride(Environment.GetEnvironmentVariable(UpdateSource.FeedOverrideVariable), out _),
            () => app.Settings.Current.NetworkEnabled,
            () => app.Version,
            () => app.Settings.Current.SkippedVersion,
            at => app.Settings.Update(s => s with { LastUpdateCheck = at }),
            tag => app.Settings.Update(s => s with { SkippedVersion = tag }),
            Path.Combine(LinuxPaths.Cache, "updates"))
    {
    }

    /// <summary>Injectable dependencies for deterministic updater tests; the handler is owned by this updater.</summary>
    internal LinuxUpdater(
        UpdateSource source,
        Func<bool> networkEnabled,
        Func<string> currentVersion,
        Func<string?> skippedVersion,
        Action<DateTimeOffset> recordSuccessfulCheck,
        Action<string> skipVersion,
        string updateDirectory,
        HttpMessageHandler? handler = null,
        Action<string>? ensurePrivate = null,
        Action<string>? deleteTemporaryFile = null)
    {
        _source = source;
        _networkEnabled = networkEnabled;
        _currentVersion = currentVersion;
        _skippedVersion = skippedVersion;
        _recordSuccessfulCheck = recordSuccessfulCheck;
        _skipVersion = skipVersion;
        _updateDirectory = updateDirectory;
        _ensurePrivate = ensurePrivate ?? (path => LinuxPaths.EnsurePrivate(path));
        _deleteTemporaryFile = deleteTemporaryFile ?? (path => File.Delete(path));
        _http = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }, disposeHandler: true)
        {
            Timeout = TimeSpan.FromMinutes(10),
        };
    }

    public ReleaseInfo? Available { get; private set; }
    public event Action? Changed;
    public string? Status { get; private set; }

    public async Task CheckAsync(bool manual = true)
    {
        var request = TryBeginRequest(TimeSpan.FromSeconds(15));
        if (request is null)
        {
            return;
        }

        try
        {
            using var data = new MemoryStream();
            await Fetch(_source.FeedUrl, data, ReleaseFeed.MaxFeedBytes, request.Token);
            var release = ReleaseFeed.Parse(data.ToArray());
            var candidate = SemanticVersion.TryParse(_currentVersion(), out var current)
                && UpdateSchedule.Evaluate(current, release, _skippedVersion(), manual) == UpdateOffer.Available
                    ? release
                    : null;
            var asset = candidate?.FindAsset("AgentSpeed-Linux-x64.deb");
            var sums = candidate?.FindAsset("SHA256SUMS.txt");
            if (candidate is null)
            {
                Available = null;
                Status = "Up to date";
            }
            else if (asset is null || sums is null || asset.Size is <= 0 or > 300L * 1024 * 1024)
            {
                Available = null;
                Status = "Download or verification failed";
            }
            else
            {
                Available = candidate;
                Status = candidate.DisplayVersion;
            }

            _recordSuccessfulCheck(DateTimeOffset.UtcNow);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException or UpdateException)
        {
            Status = "Update check failed";
        }
        finally
        {
            FinishRequest(request);
        }
    }

    public async Task<string?> DownloadAsync()
    {
        if (!CanBeginRequest())
        {
            return null;
        }

        if (Available is not { } release
            || release.FindAsset("AgentSpeed-Linux-x64.deb") is not { } asset
            || release.FindAsset("SHA256SUMS.txt") is not { } sums)
        {
            return FailDownload();
        }

        if (asset.Size is <= 0 or > 300L * 1024 * 1024)
        {
            return FailDownload();
        }

        var request = TryBeginRequest(TimeSpan.FromMinutes(10));
        if (request is null)
        {
            return null;
        }

        string? temp = null;
        try
        {
            var directory = Path.Combine(_updateDirectory, release.DisplayVersion);
            _ensurePrivate(directory);
            var final = Path.Combine(directory, asset.Name);
            temp = final + ".partial";

            using var manifest = new MemoryStream();
            await Fetch(sums.DownloadUrl, manifest, ChecksumManifest.MaxBytes, request.Token);
            var expected = ChecksumManifest.Find(System.Text.Encoding.UTF8.GetString(manifest.ToArray()), asset.Name);
            if (expected is null)
            {
                throw new InvalidDataException("Missing checksum.");
            }

            await using (var file = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                await Fetch(asset.DownloadUrl, file, 300L * 1024 * 1024, request.Token);
            }

            if (new FileInfo(temp).Length != asset.Size)
            {
                throw new InvalidDataException("Asset size mismatch.");
            }

            var actual = await ChecksumManifest.ComputeAsync(temp, request.Token);
            if (!ChecksumManifest.Matches(expected, actual))
            {
                throw new InvalidDataException("Checksum mismatch.");
            }

            File.Move(temp, final, true);
            Status = "Verified";
            return final;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or HttpRequestException or OperationCanceledException)
        {
            Status = "Download or verification failed";
            return null;
        }
        finally
        {
            try
            {
                if (temp is not null)
                {
                    _deleteTemporaryFile(temp);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A failed best-effort cleanup must not keep the updater busy.
            }
            finally
            {
                FinishRequest(request);
            }
        }
    }

    private async Task Fetch(Uri uri, Stream output, long limit, CancellationToken token)
    {
        for (var redirects = 0; redirects <= UpdateSource.MaxRedirects; redirects++)
        {
            if (!_source.IsAllowed(uri))
            {
                throw new IOException("Disallowed update host.");
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.UserAgent.ParseAdd($"AgentSpeed/{_currentVersion()} (Linux)");
            if (uri == _source.FeedUrl)
            {
                request.Headers.Accept.ParseAdd("application/vnd.github+json");
                request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
            }

            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            if ((int)response.StatusCode is >= 300 and < 400)
            {
                uri = _source.ResolveRedirect(uri, response.Headers.Location) ?? throw new IOException("Disallowed redirect.");
                continue;
            }

            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > limit)
            {
                throw new IOException("Update response too large.");
            }

            await using var body = await response.Content.ReadAsStreamAsync(token);
            var buffer = new byte[81920];
            long total = 0;
            for (int count; (count = await body.ReadAsync(buffer, token)) > 0;)
            {
                total += count;
                if (total > limit)
                {
                    throw new IOException("Update response too large.");
                }

                await output.WriteAsync(buffer.AsMemory(0, count), token);
            }

            return;
        }

        throw new IOException("Too many update redirects.");
    }

    public void Cancel()
    {
        lock (_requestLock)
        {
            _request?.Cancel();
        }
    }

    public void Skip()
    {
        if (Available is not { } release)
        {
            return;
        }

        _skipVersion(release.Tag);
        Available = null;
        Changed?.Invoke();
    }

    public void Dispose()
    {
        var disposeHttp = false;
        lock (_requestLock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (_request is null)
            {
                _httpDisposed = true;
                disposeHttp = true;
            }
            else
            {
                _request.Cancel();
            }
        }

        if (disposeHttp)
        {
            _http.Dispose();
        }
    }

    private CancellationTokenSource? TryBeginRequest(TimeSpan timeout)
    {
        lock (_requestLock)
        {
            if (_disposed || _request is not null || !_networkEnabled())
            {
                return null;
            }

            return _request = new CancellationTokenSource(timeout);
        }
    }

    private bool CanBeginRequest()
    {
        lock (_requestLock)
        {
            return !_disposed && _request is null && _networkEnabled();
        }
    }

    private void FinishRequest(CancellationTokenSource request)
    {
        var disposeHttp = false;
        lock (_requestLock)
        {
            if (ReferenceEquals(_request, request))
            {
                _request = null;
                request.Dispose();
                if (_disposed && !_httpDisposed)
                {
                    _httpDisposed = true;
                    disposeHttp = true;
                }
            }
        }

        if (disposeHttp)
        {
            _http.Dispose();
        }

        Changed?.Invoke();
    }

    private string? FailDownload()
    {
        Status = "Download or verification failed";
        Changed?.Invoke();
        return null;
    }
}
