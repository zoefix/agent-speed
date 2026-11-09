using System.Buffers;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using AgentSpeed.Core.Network;
using AgentSpeed.Presentation.Updates;

namespace AgentSpeed.Presentation.Characters;

/// <summary>Fetches a pack's bytes into a new file; the app streams them over HTTPS, tests copy a local file.</summary>
public interface ICharacterPackFetcher
{
    /// <param name="progress">Fraction 0…1 of <paramref name="expectedSize"/> received.</param>
    /// <exception cref="CharacterInstallException">Disallowed URL, network failure or wrong size.</exception>
    /// <exception cref="OperationCanceledException">Cancelled.</exception>
    Task FetchAsync(Uri url, string destination, long expectedSize, IProgress<double>? progress, CancellationToken cancellationToken);
}

public enum CharacterInstallFailure
{
    DisallowedUrl,
    Network,
    Checksum,
    InvalidPack,
    NewerApp,
}

public sealed class CharacterInstallException : Exception
{
    public CharacterInstallException()
    {
    }

    public CharacterInstallException(string message)
        : base(message)
    {
    }

    public CharacterInstallException(string message, Exception inner)
        : base(message, inner)
    {
    }

    public CharacterInstallException(CharacterInstallFailure failure, string reason, Exception? inner = null)
        : base(reason, inner)
    {
        Failure = failure;
    }

    public CharacterInstallFailure Failure { get; }
}

/// <summary>
/// The pack download of NETWORK row Characters: HTTPS on the release hosts only, every redirect hop checked, no cookies,
/// credentials or cache, an exact size (refused as soon as more arrives), a stall timeout.
/// </summary>
public sealed class HttpCharacterPackFetcher : ICharacterPackFetcher, IDisposable
{
    public const int MaxRedirects = 5;
    public static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(30);

    private readonly HttpClient _http;
    private readonly string _userAgent;

    public HttpCharacterPackFetcher(string appVersion)
        : this(appVersion, new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            Credentials = null,
            DefaultProxyCredentials = null,
            PreAuthenticate = false,
            UseProxy = true,
            AutomaticDecompression = DecompressionMethods.None,
            ActivityHeadersPropagator = null,
            ConnectTimeout = HttpFetchRequest.DefaultTimeout,
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            MaxResponseHeadersLength = 64,
        })
    {
    }

    internal HttpCharacterPackFetcher(string appVersion, HttpMessageHandler handler)
    {
        _userAgent = HttpFetchRequest.UserAgentFor(appVersion);
        _http = new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
    }

    public async Task FetchAsync(Uri url, string destination, long expectedSize, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(url);
        if (!CharacterCatalog.Permits(url))
        {
            throw new CharacterInstallException(CharacterInstallFailure.DisallowedUrl, "disallowed URL");
        }

        using var stall = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        stall.CancelAfter(StallTimeout);
        var buffer = ArrayPool<byte>.Shared.Rent(81920);
        try
        {
            var current = url;
            for (var hop = 0; ; hop++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, current) { Version = HttpVersion.Version11 };
                request.Headers.TryAddWithoutValidation("User-Agent", _userAgent);
                request.Headers.TryAddWithoutValidation("Accept", "application/octet-stream");
                using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, stall.Token).ConfigureAwait(false);
                var status = (int)response.StatusCode;
                if (status is 301 or 302 or 303 or 307 or 308)
                {
                    var location = response.Headers.Location;
                    if (location is null || hop >= MaxRedirects)
                    {
                        throw new CharacterInstallException(CharacterInstallFailure.Network, "bad redirect");
                    }

                    var next = location.IsAbsoluteUri ? location : new Uri(current, location);
                    if (!CharacterCatalog.Permits(next))
                    {
                        throw new CharacterInstallException(CharacterInstallFailure.DisallowedUrl, "redirect left the allowed hosts");
                    }

                    current = next;
                    continue;
                }

                if (status is < 200 or > 299)
                {
                    throw new CharacterInstallException(CharacterInstallFailure.Network, $"HTTP {status}");
                }

                if (response.Content.Headers.ContentLength is { } declared && declared != expectedSize)
                {
                    throw new CharacterInstallException(CharacterInstallFailure.Checksum, "unexpected size");
                }

                var input = await response.Content.ReadAsStreamAsync(stall.Token).ConfigureAwait(false);
                await using (input.ConfigureAwait(false))
                {
                    var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous);
                    await using (output.ConfigureAwait(false))
                    {
                        long total = 0;
                        int read;
                        while ((read = await input.ReadAsync(buffer, stall.Token).ConfigureAwait(false)) > 0)
                        {
                            total += read;
                            if (total > expectedSize)
                            {
                                throw new CharacterInstallException(CharacterInstallFailure.Checksum, "larger than declared");
                            }

                            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                            stall.CancelAfter(StallTimeout);
                            progress?.Report((double)total / expectedSize);
                        }

                        if (total != expectedSize)
                        {
                            throw new CharacterInstallException(CharacterInstallFailure.Network, "smaller than declared");
                        }
                    }
                }

                return;
            }
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new CharacterInstallException(CharacterInstallFailure.Network, "stalled", ex);
        }
        catch (HttpRequestException ex)
        {
            throw new CharacterInstallException(CharacterInstallFailure.Network, ex.HttpRequestError.ToString(), ex);
        }
        catch (IOException ex)
        {
            throw new CharacterInstallException(CharacterInstallFailure.Network, "interrupted", ex);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public void Dispose() => _http.Dispose();
}

/// <summary>
/// Downloads, verifies and installs one pack (CHARACTERS 2): the exact size and SHA-256 of the catalog entry before
/// anything is unpacked, safe archive entries, a private work folder in the store, a valid manifest with the entry's id,
/// then an atomic move into place. Nothing is left behind on failure; nothing from a pack is ever executed.
/// </summary>
public sealed class CharacterInstaller(CharacterStore store, ICharacterPackFetcher fetcher)
{
    /// <exception cref="CharacterInstallException">The download or a check failed.</exception>
    /// <exception cref="CharacterPackException">The pack is invalid, needs a newer app, or has another id.</exception>
    /// <exception cref="OperationCanceledException">Cancelled.</exception>
    public async Task<InstalledCharacter> InstallAsync(CharacterCatalog.Entry entry, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (!entry.IsValid)
        {
            throw new CharacterInstallException(CharacterInstallFailure.InvalidPack, "invalid catalog entry");
        }

        var workspace = store.CreateWorkspace();
        try
        {
            var archive = Path.Combine(workspace, "pack.zip");
            await fetcher.FetchAsync(entry.Url, archive, entry.Size, progress, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (new FileInfo(archive).Length != entry.Size)
            {
                throw new CharacterInstallException(CharacterInstallFailure.Checksum, "wrong size");
            }

            var digest = await ChecksumManifest.ComputeAsync(archive, cancellationToken).ConfigureAwait(false);
            if (!ChecksumManifest.Matches(Convert.FromHexString(entry.Sha256), digest))
            {
                throw new CharacterInstallException(CharacterInstallFailure.Checksum, "checksum mismatch");
            }

            var unpacked = Path.Combine(workspace, "pack");
            try
            {
                await Task.Run(() => CharacterArchive.Extract(archive, unpacked, cancellationToken), cancellationToken).ConfigureAwait(false);
            }
            catch (InvalidDataException ex)
            {
                throw new CharacterInstallException(CharacterInstallFailure.InvalidPack, "damaged archive", ex);
            }

            cancellationToken.ThrowIfCancellationRequested();
            return store.Install(unpacked, entry.Id);
        }
        finally
        {
            CharacterStore.TryDelete(workspace);
        }
    }

    /// <summary>SHA-256 of a file as lowercase hex.</summary>
    public static string Sha256Hex(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }
}
