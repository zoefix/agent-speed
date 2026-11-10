using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;

namespace AgentSpeed.Core.Quota.Network;

/// <summary>
/// The quota requests over <see cref="HttpClient"/> (NETWORK 9): no cookies, credentials or cache; the system proxy for
/// vendors and none for loopback; redirects refused; TLS 1.2 or later; a self-signed certificate accepted from 127.0.0.1
/// alone (its handler only ever connects there); <c>Content-Length</c> and the stream held to the cap.
/// </summary>
public sealed class QuotaHttpTransport : IQuotaHttpTransport, IDisposable
{
    private const int ChunkSize = 16 * 1024;

    private readonly HttpClient _vendor;
    private readonly HttpClient _loopback;

    public QuotaHttpTransport()
        : this(VendorHandler(), LoopbackHandler())
    {
    }

    /// <summary>Tests pass handlers (a stub server's certificate trust); production uses the defaults.</summary>
    internal QuotaHttpTransport(HttpMessageHandler vendor, HttpMessageHandler loopback)
    {
        _vendor = new HttpClient(vendor, disposeHandler: true) { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
        _loopback = new HttpClient(loopback, disposeHandler: true) { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
    }

    /// <summary>Hosts a test stub may stand in for (tests only; production permits NETWORK 9's hosts alone).</summary>
    internal Func<Uri, bool> Permits { get; init; } = QuotaHosts.Permits;

    public async Task<QuotaHttpResponse> SendAsync(QuotaHttpRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!Permits(request.Url))
        {
            throw new QuotaHttpException(QuotaHttpFailure.DisallowedUrl, "disallowed URL");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(request.Timeout);
        try
        {
            using var message = Message(request);
            var client = request.IsLoopback ? _loopback : _vendor;
            using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            var status = (int)response.StatusCode;
            if (status is >= 300 and <= 399)
            {
                throw new QuotaHttpException(QuotaHttpFailure.Redirected, "redirect refused");
            }

            var date = response.Headers.Date?.ToUnixTimeMilliseconds();
            if (status is < 200 or > 299)
            {
                return new QuotaHttpResponse(status, [], date);
            }

            if (response.Content.Headers.ContentLength > request.Cap)
            {
                throw new QuotaHttpException(QuotaHttpFailure.TooLarge, "response too large");
            }

            var body = await ReadCappedAsync(response.Content, request.Cap, timeout.Token).ConfigureAwait(false);
            return new QuotaHttpResponse(status, body, date);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new QuotaHttpException(QuotaHttpFailure.Transport, "timeout");
        }
        catch (HttpRequestException ex)
        {
            throw new QuotaHttpException(QuotaHttpFailure.Transport, ex.HttpRequestError == HttpRequestError.Unknown ? "transport error" : ex.HttpRequestError.ToString());
        }
        catch (IOException)
        {
            throw new QuotaHttpException(QuotaHttpFailure.Transport, "connection interrupted");
        }
    }

    public void Dispose()
    {
        _vendor.Dispose();
        _loopback.Dispose();
    }

    internal static SocketsHttpHandler VendorHandler() => new()
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        Credentials = null,
        DefaultProxyCredentials = null,
        PreAuthenticate = false,
        UseProxy = true,
        AutomaticDecompression = DecompressionMethods.None,
        ActivityHeadersPropagator = null,
        ConnectTimeout = QuotaHttpRequest.VendorTimeout,
        PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30),
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        MaxResponseHeadersLength = 64,
        SslOptions = new SslClientAuthenticationOptions { EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13 },
    };

    /// <summary>127.0.0.1 only, no proxy, and the language server's self-signed certificate accepted there.</summary>
    internal static SocketsHttpHandler LoopbackHandler() => new()
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        Credentials = null,
        UseProxy = false,
        AutomaticDecompression = DecompressionMethods.None,
        ActivityHeadersPropagator = null,
        ConnectTimeout = QuotaHttpRequest.LoopbackTimeout,
        PooledConnectionLifetime = TimeSpan.FromSeconds(30),
        MaxResponseHeadersLength = 64,
        SslOptions = new SslClientAuthenticationOptions
        {
            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
            RemoteCertificateValidationCallback = static (_, _, _, _) => true,
        },
        ConnectCallback = static async (context, cancellationToken) =>
        {
            if (context.DnsEndPoint.Host != QuotaHosts.Loopback)
            {
                throw new HttpRequestException(HttpRequestError.ConnectionError, "loopback only");
            }

            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(IPAddress.Loopback, context.DnsEndPoint.Port), cancellationToken).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        },
    };

    /// <summary>The section's headers in order, <c>Accept-Language: *</c> as the macOS app sends, and the body's type.</summary>
    private static HttpRequestMessage Message(QuotaHttpRequest request)
    {
        var message = new HttpRequestMessage(request.Method, request.Url) { Version = HttpVersion.Version11 };
        string? contentType = null;
        foreach (var (name, value) in request.Headers)
        {
            if (string.Equals(name, "Content-Type", StringComparison.OrdinalIgnoreCase))
            {
                contentType = value;
                continue;
            }

            if (!message.Headers.TryAddWithoutValidation(name, value))
            {
                throw new QuotaHttpException(QuotaHttpFailure.DisallowedUrl, "header refused");
            }
        }

        message.Headers.TryAddWithoutValidation("Accept-Language", "*");
        if (request.Body is { } body)
        {
            message.Content = new ByteArrayContent(body);
            if (contentType is not null)
            {
                message.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
            }
        }

        return message;
    }

    private static async Task<byte[]> ReadCappedAsync(HttpContent content, int limit, CancellationToken cancellationToken)
    {
        var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            using var buffer = new MemoryStream();
            var chunk = new byte[ChunkSize];
            int read;
            while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + read > limit)
                {
                    throw new QuotaHttpException(QuotaHttpFailure.TooLarge, "response too large");
                }

                buffer.Write(chunk, 0, read);
            }

            return buffer.ToArray();
        }
    }
}
