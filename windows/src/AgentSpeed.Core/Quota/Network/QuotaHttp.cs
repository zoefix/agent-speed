namespace AgentSpeed.Core.Quota.Network;

/// <summary>
/// One quota request (QUOTAS 9.1, NETWORK 9): only the headers its section lists plus the user agent, HTTPS to one of
/// the hosts of NETWORK 9 (or the loopback language server), redirects refused, a body cap and a timeout.
/// <see cref="ToString"/> never shows a header value: they carry credentials.
/// </summary>
public sealed class QuotaHttpRequest
{
    public const int MaxBytes = 256 * 1024;
    public static readonly TimeSpan VendorTimeout = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan LoopbackTimeout = TimeSpan.FromSeconds(2);

    public QuotaHttpRequest(HttpMethod method, Uri url, IReadOnlyList<KeyValuePair<string, string>> headers, byte[]? body = null)
    {
        Method = method;
        Url = url;
        Headers = headers;
        Body = body;
        Timeout = QuotaHosts.IsLoopback(url) ? LoopbackTimeout : VendorTimeout;
    }

    public HttpMethod Method { get; }

    public Uri Url { get; }

    /// <summary>In order; <c>User-Agent</c> included.</summary>
    public IReadOnlyList<KeyValuePair<string, string>> Headers { get; }

    public byte[]? Body { get; }

    public TimeSpan Timeout { get; init; }

    public int Cap { get; init; } = MaxBytes;

    public bool IsLoopback => QuotaHosts.IsLoopback(Url);

    public string? Header(string name) =>
        Headers.FirstOrDefault(h => string.Equals(h.Key, name, StringComparison.OrdinalIgnoreCase)) is { Key: not null } found ? found.Value : null;

    public override string ToString() => $"{Method} {Url.Host}";
}

/// <summary>A response; the body is read for 2xx statuses only.</summary>
public sealed record QuotaHttpResponse(int Status, byte[] Body, long? DateMs = null)
{
    public static QuotaHttpResponse Of(int status) => new(status, []);
}

public enum QuotaHttpFailure
{
    DisallowedUrl,

    /// <summary>A redirect is never followed and counts as a failure.</summary>
    Redirected,
    TooLarge,
    Transport,
}

/// <summary>A failed request; the message is a category only, never a URL, header or body.</summary>
public sealed class QuotaHttpException(QuotaHttpFailure failure, string reason) : Exception(reason)
{
    public QuotaHttpFailure Failure { get; } = failure;
}

/// <summary>The network seam: the app uses <see cref="QuotaHttpTransport"/>, tests a scripted one.</summary>
public interface IQuotaHttpTransport
{
    /// <exception cref="QuotaHttpException">Disallowed URL, redirect, too large, or a transport failure.</exception>
    Task<QuotaHttpResponse> SendAsync(QuotaHttpRequest request, CancellationToken cancellationToken = default);
}
