using AgentSpeed.Core.Network;

namespace AgentSpeed.Core.Tests;

/// <summary>A transport that answers from a script and records every request; no network.</summary>
internal sealed class ScriptedTransport : IHttpTransport
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Queue<Func<HttpFetchResponse>>> _answers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Func<HttpFetchResponse>> _last = new(StringComparer.Ordinal);
    private readonly List<HttpFetchRequest> _requests = [];

    public IReadOnlyList<HttpFetchRequest> Requests
    {
        get
        {
            lock (_gate)
            {
                return [.. _requests];
            }
        }
    }

    public IReadOnlyList<string> RequestedUrls => [.. Requests.Select(r => r.Url.AbsoluteUri)];

    /// <summary>Queues a response for a URL; the last answer repeats.</summary>
    public void Reply(Uri url, int status = 200, byte[]? body = null, string? etag = null) =>
        Script(url, () => new HttpFetchResponse(status, body, etag, url));

    public void Fail(Uri url, HttpFetchException failure) => Script(url, () => throw failure);

    public void Script(Uri url, Func<HttpFetchResponse> answer)
    {
        lock (_gate)
        {
            if (!_answers.TryGetValue(url.AbsoluteUri, out var queue))
            {
                queue = new Queue<Func<HttpFetchResponse>>();
                _answers[url.AbsoluteUri] = queue;
            }

            queue.Enqueue(answer);
        }
    }

    public Task<HttpFetchResponse> GetAsync(HttpFetchRequest request, CancellationToken cancellationToken = default)
    {
        Func<HttpFetchResponse>? answer;
        lock (_gate)
        {
            _requests.Add(request);
            var key = request.Url.AbsoluteUri;
            if (_answers.TryGetValue(key, out var queue) && queue.Count > 0)
            {
                answer = queue.Dequeue();
                _last[key] = answer;
            }
            else
            {
                answer = _last.GetValueOrDefault(key);
            }
        }

        if (answer is null)
        {
            throw new HttpFetchException(HttpFetchFailure.Transport, "offline");
        }

        var response = answer();
        if (response.Body.Length > request.MaxBytes)
        {
            throw HttpFetchException.TooLarge();
        }

        return Task.FromResult(response);
    }
}

/// <summary>A clock tests move by hand.</summary>
internal sealed class TestClock(DateTimeOffset start)
{
    private readonly object _gate = new();
    private DateTimeOffset _now = start;

    public DateTimeOffset Now
    {
        get
        {
            lock (_gate)
            {
                return _now;
            }
        }
    }

    public Func<DateTimeOffset> Function => () => Now;

    public void Advance(TimeSpan by)
    {
        lock (_gate)
        {
            _now += by;
        }
    }
}

/// <summary>Collects log lines of the network features.</summary>
internal sealed class LogLines
{
    private readonly List<string> _lines = [];

    public IReadOnlyList<string> Lines
    {
        get
        {
            lock (_lines)
            {
                return [.. _lines];
            }
        }
    }

    public void Add(string line)
    {
        lock (_lines)
        {
            _lines.Add(line);
        }
    }
}
