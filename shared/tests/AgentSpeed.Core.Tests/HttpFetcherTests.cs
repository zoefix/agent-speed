using System.Net;
using System.Net.Http;
using AgentSpeed.Core.Network;

namespace AgentSpeed.Core.Tests;

public sealed class HttpFetcherTests : IDisposable
{
    private static readonly HashSet<string> Hosts = ["a.test", "b.test"];

    private readonly StubHandler _handler = new();
    private readonly HttpClientTransport _transport;

    public HttpFetcherTests()
    {
        _transport = new HttpClientTransport(_handler);
    }

    public void Dispose() => _transport.Dispose();

    [Fact]
    public async Task OnlyHttpsOnTheHostListIsAsked()
    {
        foreach (var url in new[] { "http://a.test/x", "https://c.test/x", "https://user@a.test/x", "https://user:pw@a.test/x", "https://a.test:8443/x" })
        {
            var error = await Assert.ThrowsAsync<HttpFetchException>(() => _transport.GetAsync(Request(url)));
            Assert.Equal(HttpFetchFailure.DisallowedUrl, error.Failure);
            Assert.Equal("disallowed URL", error.Reason);
        }

        Assert.Equal(0, _handler.RequestCount);
        var request = Request("https://a.test/x");
        Assert.True(request.Permits(new Uri("https://A.TEST:443/x")));
        Assert.False(request.Permits(new Uri("ftp://a.test/x")));
        Assert.False(request.Permits(new Uri("/x", UriKind.Relative)));
        Assert.False(request.Permits(null));
    }

    [Fact]
    public async Task AResponseWithItsETagAndOnlyTheAllowedHeaders()
    {
        _handler.Set("https://a.test/ok", new Stub { ETag = "\"abc\"", ContentLength = 5, Chunks = ["hello"u8.ToArray()] });
        var response = await _transport.GetAsync(Request("https://a.test/ok") with { IfNoneMatch = "\"old\"" });
        Assert.Equal(200, response.Status);
        Assert.Equal("hello"u8.ToArray(), response.Body);
        Assert.Equal("\"abc\"", response.ETag);
        Assert.Equal(new Uri("https://a.test/ok"), response.FinalUrl);
        var headers = _handler.HeadersOf("https://a.test/ok");
        Assert.Equal(["Accept", "If-None-Match", "User-Agent"], headers.Keys.Order(StringComparer.Ordinal));
        Assert.Equal("AgentSpeed/1.0.0 (Windows)", headers["User-Agent"]);
        Assert.Equal("application/json", headers["Accept"]);
        Assert.Equal("\"old\"", headers["If-None-Match"]);
        Assert.Equal(HttpMethod.Get, _handler.LastMethod);
    }

    [Fact]
    public async Task WithoutAStoredETagTwoHeadersAreSent()
    {
        _handler.Set("https://a.test/plain", new Stub { Chunks = ["{}"u8.ToArray()] });
        await _transport.GetAsync(Request("https://a.test/plain"));
        Assert.Equal(["Accept", "User-Agent"], _handler.HeadersOf("https://a.test/plain").Keys.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task AnETagThatCouldInjectAHeaderIsNotSent()
    {
        _handler.Set("https://a.test/inject", new Stub { ETag = "\"é\"", Chunks = ["{}"u8.ToArray()] });
        var response = await _transport.GetAsync(Request("https://a.test/inject") with { IfNoneMatch = "\"x\"\r\nCookie: a=b" });
        Assert.Equal(["Accept", "User-Agent"], _handler.HeadersOf("https://a.test/inject").Keys.Order(StringComparer.Ordinal));
        Assert.Null(response.ETag);
    }

    [Fact]
    public async Task NotModifiedIsAnOrdinaryResponse()
    {
        _handler.Set("https://a.test/304", new Stub { Status = 304, ETag = "\"abc\"" });
        var response = await _transport.GetAsync(Request("https://a.test/304") with { IfNoneMatch = "\"abc\"" });
        Assert.Equal(304, response.Status);
        Assert.Empty(response.Body);
        Assert.Equal("\"abc\"", response.ETag);
        Assert.False(response.IsSuccess);
    }

    [Fact]
    public async Task ErrorStatusesAreReturnedWithoutTheirBody()
    {
        var stub = new Stub { Status = 404, Chunks = ["nope"u8.ToArray()] };
        _handler.Set("https://a.test/404", stub);
        var response = await _transport.GetAsync(Request("https://a.test/404"));
        Assert.Equal(404, response.Status);
        Assert.Empty(response.Body);
        Assert.Equal(0, stub.ChunksRead);
    }

    [Fact]
    public async Task TheCapAppliesToContentLength()
    {
        var declared = new Stub { ContentLength = 2048, Chunks = [new byte[2048]] };
        _handler.Set("https://a.test/declared", declared);
        var error = await Assert.ThrowsAsync<HttpFetchException>(() => _transport.GetAsync(Request("https://a.test/declared")));
        Assert.Equal(HttpFetchFailure.TooLarge, error.Failure);
        Assert.Equal("response too large", error.Reason);
        Assert.Equal(0, declared.ChunksRead);
    }

    [Fact]
    public async Task TheCapAppliesWhileStreaming()
    {
        var streamed = new Stub { Chunks = [new byte[600], new byte[600], new byte[600]] };
        _handler.Set("https://a.test/streamed", streamed);
        var error = await Assert.ThrowsAsync<HttpFetchException>(() => _transport.GetAsync(Request("https://a.test/streamed")));
        Assert.Equal(HttpFetchFailure.TooLarge, error.Failure);
        Assert.Equal(2, streamed.ChunksRead);
        Assert.True(streamed.Disposed, "the body stream is closed at once");

        _handler.Set("https://a.test/exact", new Stub { Chunks = [new byte[1000], new byte[24]] });
        Assert.Equal(1024, (await _transport.GetAsync(Request("https://a.test/exact"))).Body.Length);
    }

    [Fact]
    public async Task RedirectsStayOnTheHostList()
    {
        _handler.Set("https://a.test/away", new Stub { Redirect = "https://evil.test/x" });
        var away = await Assert.ThrowsAsync<HttpFetchException>(() => _transport.GetAsync(Request("https://a.test/away")));
        Assert.Equal(HttpFetchFailure.DisallowedUrl, away.Failure);
        _handler.Set("https://a.test/downgrade", new Stub { Redirect = "http://b.test/x" });
        var downgrade = await Assert.ThrowsAsync<HttpFetchException>(() => _transport.GetAsync(Request("https://a.test/downgrade")));
        Assert.Equal(HttpFetchFailure.DisallowedUrl, downgrade.Failure);
        Assert.Equal(0, _handler.RequestsTo("https://evil.test/x") + _handler.RequestsTo("http://b.test/x"));

        _handler.Set("https://a.test/hop", new Stub { Status = 301, Redirect = "https://b.test/target" });
        _handler.Set("https://b.test/target", new Stub { Chunks = ["{}"u8.ToArray()], ETag = "\"t\"" });
        var response = await _transport.GetAsync(Request("https://a.test/hop") with { IfNoneMatch = "\"old\"" });
        Assert.Equal("{}"u8.ToArray(), response.Body);
        Assert.Equal(new Uri("https://b.test/target"), response.FinalUrl);
        Assert.Equal("\"t\"", response.ETag);
        Assert.Equal(["Accept", "If-None-Match", "User-Agent"], _handler.HeadersOf("https://b.test/target").Keys.Order(StringComparer.Ordinal));

        _handler.Set("https://a.test/relative", new Stub { Status = 307, Redirect = "/landing" });
        _handler.Set("https://a.test/landing", new Stub { Chunks = ["[]"u8.ToArray()] });
        Assert.Equal("[]"u8.ToArray(), (await _transport.GetAsync(Request("https://a.test/relative"))).Body);
    }

    [Fact]
    public async Task RedirectLoopsEnd()
    {
        _handler.Set("https://a.test/loop", new Stub { Redirect = "https://a.test/loop" });
        var error = await Assert.ThrowsAsync<HttpFetchException>(() => _transport.GetAsync(Request("https://a.test/loop")));
        Assert.Equal(HttpFetchFailure.Transport, error.Failure);
        Assert.Equal("too many redirects", error.Reason);
        Assert.Equal(HttpClientTransport.MaxRedirects + 1, _handler.RequestsTo("https://a.test/loop"));
    }

    [Fact]
    public async Task ARedirectWithoutALocationIsAnOrdinaryStatus()
    {
        _handler.Set("https://a.test/nowhere", new Stub { Status = 302 });
        Assert.Equal(302, (await _transport.GetAsync(Request("https://a.test/nowhere"))).Status);
    }

    [Fact]
    public async Task TimeoutsAndTransportErrorsHaveShortReasons()
    {
        _handler.Set("https://a.test/slow", new Stub { Delay = TimeSpan.FromSeconds(30) });
        var slow = await Assert.ThrowsAsync<HttpFetchException>(() =>
            _transport.GetAsync(Request("https://a.test/slow") with { Timeout = TimeSpan.FromMilliseconds(100) }));
        Assert.Equal("timeout", slow.Reason);

        _handler.Set("https://a.test/dns", new Stub { Throw = new HttpRequestException(HttpRequestError.NameResolutionError, "No such host is known. (a.test:443)") });
        var dns = await Assert.ThrowsAsync<HttpFetchException>(() => _transport.GetAsync(Request("https://a.test/dns")));
        Assert.Equal(HttpFetchFailure.Transport, dns.Failure);
        Assert.Equal("NameResolutionError", dns.Reason);
        Assert.DoesNotContain("a.test", dns.Reason, StringComparison.Ordinal);

        _handler.Set("https://a.test/reset", new Stub { Throw = new IOException("reset by a.test") });
        Assert.Equal("connection interrupted", (await Assert.ThrowsAsync<HttpFetchException>(() => _transport.GetAsync(Request("https://a.test/reset")))).Reason);

        var error = await Assert.ThrowsAsync<HttpFetchException>(() => _transport.GetAsync(Request("https://a.test/unscripted")));
        Assert.Equal("ConnectionError", error.Reason);
    }

    [Fact]
    public async Task TheCallersCancellationIsNotATimeout()
    {
        _handler.Set("https://a.test/slow", new Stub { Delay = TimeSpan.FromSeconds(30) });
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _transport.GetAsync(Request("https://a.test/slow"), cancel.Token));
    }

    [Fact]
    public void TheRealHandlerSendsNothingExtra()
    {
        using var handler = HttpClientTransport.CreateHandler();
        Assert.False(handler.AllowAutoRedirect);
        Assert.False(handler.UseCookies);
        Assert.Null(handler.Credentials);
        Assert.Null(handler.DefaultProxyCredentials);
        Assert.False(handler.PreAuthenticate);
        Assert.True(handler.UseProxy);
        Assert.Null(handler.Proxy);
        Assert.Equal(DecompressionMethods.None, handler.AutomaticDecompression);
        Assert.Null(handler.ActivityHeadersPropagator);
        Assert.Equal(TimeSpan.FromSeconds(15), handler.ConnectTimeout);
        Assert.Equal(TimeSpan.FromSeconds(15), Request("https://a.test/x").Timeout);
        Assert.Equal("AgentSpeed/1.2.3 (Windows)", HttpFetchRequest.UserAgentFor("1.2.3"));
        using var transport = new HttpClientTransport();
    }

    private static HttpFetchRequest Request(string url, int maxBytes = 1024) =>
        new(new Uri(url), "AgentSpeed/1.0.0 (Windows)", maxBytes, Hosts);

    private sealed class Stub
    {
        public int Status { get; init; } = 200;

        public string? ETag { get; init; }

        public long? ContentLength { get; init; }

        public IReadOnlyList<byte[]> Chunks { get; init; } = [];

        public string? Redirect { get; init; }

        public TimeSpan Delay { get; init; }

        public Exception? Throw { get; init; }

        public int ChunksRead { get; set; }

        public bool Disposed { get; set; }
    }

    /// <summary>Serves stubs in-process, so <see cref="HttpClientTransport"/> runs for real without network.</summary>
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, Stub> _stubs = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Dictionary<string, string>> _headers = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _counts = new(StringComparer.Ordinal);

        public int RequestCount => _counts.Values.Sum();

        public HttpMethod? LastMethod { get; private set; }

        public void Set(string url, Stub stub) => _stubs[new Uri(url).AbsoluteUri] = stub;

        public Dictionary<string, string> HeadersOf(string url) => _headers[new Uri(url).AbsoluteUri];

        public int RequestsTo(string url) => _counts.GetValueOrDefault(new Uri(url).AbsoluteUri);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var key = request.RequestUri!.AbsoluteUri;
            LastMethod = request.Method;
            _counts[key] = _counts.GetValueOrDefault(key) + 1;
            _headers[key] = request.Headers.NonValidated.ToDictionary(h => h.Key, h => h.Value.ToString(), StringComparer.Ordinal);
            if (!_stubs.TryGetValue(key, out var stub))
            {
                throw new HttpRequestException(HttpRequestError.ConnectionError, "Connection refused (" + request.RequestUri.Host + ")");
            }

            if (stub.Throw is { } failure)
            {
                throw failure;
            }

            if (stub.Delay > TimeSpan.Zero)
            {
                await Task.Delay(stub.Delay, cancellationToken);
            }

            var response = new HttpResponseMessage((HttpStatusCode)(stub.Redirect is not null && stub.Status == 200 ? 302 : stub.Status))
            {
                RequestMessage = request,
                Content = new StreamContent(new ChunkStream(stub)),
            };
            if (stub.Redirect is { } location)
            {
                response.Headers.Location = new Uri(location, UriKind.RelativeOrAbsolute);
            }

            if (stub.ETag is { } etag)
            {
                response.Headers.TryAddWithoutValidation("ETag", etag);
            }

            if (stub.ContentLength is { } length)
            {
                response.Content.Headers.ContentLength = length;
            }

            return response;
        }
    }

    /// <summary>A non-seekable body that hands out one chunk per read, so a test sees how far the reader got.</summary>
    private sealed class ChunkStream(Stub stub) : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            if (stub.ChunksRead >= stub.Chunks.Count)
            {
                return 0;
            }

            var chunk = stub.Chunks[stub.ChunksRead];
            if (chunk.Length > buffer.Length)
            {
                throw new InvalidOperationException("test chunks must fit one read");
            }

            chunk.CopyTo(buffer);
            stub.ChunksRead++;
            return chunk.Length;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Read(buffer.Span));

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            Task.FromResult(Read(buffer.AsSpan(offset, count)));

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            stub.Disposed = true;
            base.Dispose(disposing);
        }
    }
}
