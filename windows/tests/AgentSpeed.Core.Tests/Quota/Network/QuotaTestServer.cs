using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using AgentSpeed.Core.Quota.Network;

namespace AgentSpeed.Core.Tests.Quota.Network;

/// <summary>One request as the stub server read it off the wire.</summary>
internal sealed record WireRequest(string Method, string Path, IReadOnlyList<KeyValuePair<string, string>> Headers, byte[] Body)
{
    public string? Header(string name) => Headers.FirstOrDefault(h => string.Equals(h.Key, name, StringComparison.OrdinalIgnoreCase)).Value;

    public IReadOnlyList<string> HeaderNames => [.. Headers.Select(h => h.Key.ToLowerInvariant())];

    public string BodyText => Encoding.UTF8.GetString(Body);
}

/// <summary>A canned answer: status, extra header lines and body.</summary>
internal sealed record WireAnswer(int Status, string Body = "", IReadOnlyList<string>? Headers = null, bool Chunked = false);

/// <summary>
/// A minimal HTTP/1.1 server on 127.0.0.1 (optionally TLS with a self-signed certificate) for the quota transport tests:
/// it records each request and answers from <see cref="Answer"/>; one request per connection.
/// </summary>
internal sealed class QuotaTestServer : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly X509Certificate2? _certificate;
    private readonly List<WireRequest> _requests = [];
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;

    public QuotaTestServer(bool tls = false)
    {
        if (tls)
        {
            using var key = RSA.Create(2048);
            var request = new CertificateRequest("CN=127.0.0.1", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            using var created = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
            // SChannel needs a stored key: the machine store works in an SSH logon too; without PersistKeySet the key goes on dispose.
            var pfx = created.Export(X509ContentType.Pfx);
            try
            {
                _certificate = X509CertificateLoader.LoadPkcs12(pfx, null, X509KeyStorageFlags.MachineKeySet);
            }
            catch (CryptographicException)
            {
                _certificate = X509CertificateLoader.LoadPkcs12(pfx, null);
            }
        }

        _listener.Start();
        _loop = Task.Run(LoopAsync);
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public string Scheme => _certificate is null ? "http" : "https";

    public Uri Url(string pathAndQuery) => new($"{Scheme}://127.0.0.1:{Port}{pathAndQuery}");

    /// <summary>The answer to a request; 200 with <c>{}</c> by default.</summary>
    public Func<WireRequest, WireAnswer> Answer { get; set; } = _ => new WireAnswer(200, "{}");

    public IReadOnlyList<WireRequest> Requests
    {
        get
        {
            lock (_requests)
            {
                return [.. _requests];
            }
        }
    }

    private async Task LoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_stop.Token).ConfigureAwait(false);
            }
            catch (Exception e) when (e is OperationCanceledException or SocketException or ObjectDisposedException)
            {
                return;
            }

            _ = Task.Run(() => ServeAsync(client));
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                Stream stream = client.GetStream();
                if (_certificate is not null)
                {
                    var ssl = new SslStream(stream, leaveInnerStreamOpen: false);
                    await ssl.AuthenticateAsServerAsync(_certificate).ConfigureAwait(false);
                    stream = ssl;
                }

                await using (stream.ConfigureAwait(false))
                {
                    var request = await ReadAsync(stream).ConfigureAwait(false);
                    if (request is null)
                    {
                        return;
                    }

                    lock (_requests)
                    {
                        _requests.Add(request);
                    }

                    var answer = Answer(request);
                    var body = Encoding.UTF8.GetBytes(answer.Body);
                    var head = new StringBuilder($"HTTP/1.1 {answer.Status} X\r\nConnection: close\r\n");
                    foreach (var line in answer.Headers ?? [])
                    {
                        head.Append(line).Append("\r\n");
                    }

                    head.Append(answer.Chunked ? "Transfer-Encoding: chunked\r\n" : $"Content-Length: {body.Length}\r\n").Append("\r\n");
                    await stream.WriteAsync(Encoding.ASCII.GetBytes(head.ToString())).ConfigureAwait(false);
                    if (answer.Chunked)
                    {
                        for (var i = 0; i < body.Length; i += 8192)
                        {
                            var part = body.AsMemory(i, Math.Min(8192, body.Length - i));
                            await stream.WriteAsync(Encoding.ASCII.GetBytes($"{part.Length:x}\r\n")).ConfigureAwait(false);
                            await stream.WriteAsync(part).ConfigureAwait(false);
                            await stream.WriteAsync("\r\n"u8.ToArray()).ConfigureAwait(false);
                        }

                        await stream.WriteAsync("0\r\n\r\n"u8.ToArray()).ConfigureAwait(false);
                    }
                    else
                    {
                        await stream.WriteAsync(body).ConfigureAwait(false);
                    }

                    await stream.FlushAsync().ConfigureAwait(false);
                }
            }
            catch (Exception e) when (e is IOException or SocketException or System.Security.Authentication.AuthenticationException or ObjectDisposedException)
            {
                // A client that gave up (a refused certificate, a cap) ends its connection.
            }
        }
    }

    private static async Task<WireRequest?> ReadAsync(Stream stream)
    {
        var buffer = new List<byte>();
        var one = new byte[1];
        while (buffer.Count < 64 * 1024)
        {
            if (await stream.ReadAsync(one).ConfigureAwait(false) == 0)
            {
                return null;
            }

            buffer.Add(one[0]);
            if (buffer.Count >= 4 && buffer[^4] == '\r' && buffer[^3] == '\n' && buffer[^2] == '\r' && buffer[^1] == '\n')
            {
                break;
            }
        }

        var lines = Encoding.ASCII.GetString([.. buffer]).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        var first = lines[0].Split(' ');
        var headers = lines.Skip(1).Select(l => l.Split(':', 2)).Select(p => new KeyValuePair<string, string>(p[0].Trim(), p[1].Trim())).ToList();
        var length = headers.FirstOrDefault(h => h.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)).Value is { } text
            ? int.Parse(text, System.Globalization.CultureInfo.InvariantCulture)
            : 0;
        var body = new byte[length];
        await stream.ReadExactlyAsync(body).ConfigureAwait(false);
        return new WireRequest(first[0], first[1], headers, body);
    }

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Stop();
        try
        {
            _loop.Wait(2000);
        }
        catch (AggregateException)
        {
        }

        _certificate?.Dispose();
        _stop.Dispose();
    }
}

/// <summary>
/// The real transport pointed at a stub: every vendor URL keeps its path but goes to the stub on 127.0.0.1 (the original
/// host is recorded), so each route's request crosses HTTP exactly as it would to its vendor.
/// </summary>
internal sealed class StubbedVendorTransport(QuotaTestServer server) : IQuotaHttpTransport, IDisposable
{
    private readonly QuotaHttpTransport _inner = new(QuotaHttpTransport.LoopbackHandler(), QuotaHttpTransport.LoopbackHandler())
    {
        Permits = url => url.Host == "127.0.0.1",
    };

    private readonly List<QuotaHttpRequest> _sent = [];

    public IReadOnlyList<QuotaHttpRequest> Sent
    {
        get
        {
            lock (_sent)
            {
                return [.. _sent];
            }
        }
    }

    public Task<QuotaHttpResponse> SendAsync(QuotaHttpRequest request, CancellationToken cancellationToken = default)
    {
        Assert.True(QuotaHosts.Permits(request.Url), "a request to a host outside NETWORK 9: " + request.Url.Host);
        lock (_sent)
        {
            _sent.Add(request);
        }

        var target = request.IsLoopback ? request.Url : server.Url(request.Url.PathAndQuery);
        return _inner.SendAsync(new QuotaHttpRequest(request.Method, target, request.Headers, request.Body), cancellationToken);
    }

    public void Dispose() => _inner.Dispose();
}

/// <summary>A transport that answers from a script by host and path, and records every request; no network.</summary>
internal sealed class ScriptedQuotaTransport : IQuotaHttpTransport
{
    private readonly List<QuotaHttpRequest> _requests = [];

    /// <summary>The answer to a request; a 500 by default. Throwing a <see cref="QuotaHttpException"/> fails it.</summary>
    public Func<QuotaHttpRequest, QuotaHttpResponse> Answer { get; set; } = _ => QuotaHttpResponse.Of(500);

    public IReadOnlyList<QuotaHttpRequest> Requests
    {
        get
        {
            lock (_requests)
            {
                return [.. _requests];
            }
        }
    }

    public int Count(string host) => Requests.Count(r => r.Url.Host == host);

    public Task<QuotaHttpResponse> SendAsync(QuotaHttpRequest request, CancellationToken cancellationToken = default)
    {
        Assert.True(QuotaHosts.Permits(request.Url), "a request to a host outside NETWORK 9: " + request.Url.Host);
        lock (_requests)
        {
            _requests.Add(request);
        }

        try
        {
            return Task.FromResult(Answer(request));
        }
        catch (QuotaHttpException e)
        {
            return Task.FromException<QuotaHttpResponse>(e);
        }
    }

    public static QuotaHttpResponse Json(string body, int status = 200, long? dateMs = null) => new(status, Encoding.UTF8.GetBytes(body), dateMs);
}
