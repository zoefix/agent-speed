using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using AgentSpeed.Core;
using AgentSpeed.Core.History;
using AgentSpeed.Core.Monitoring;
using AgentSpeed.Core.Remote;

namespace AgentSpeed.Relay;

/// <summary>Owns stdin and the relay's lifetime. Only the monitor thread touches the local log readers.</summary>
internal sealed class RelaySession(Stream output, string version)
{
    private readonly object _writeLock = new();
    private long _todayStartMs;
    private volatile bool _inputClosed;

    internal static long NowMs => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    public int Run(Stream input)
    {
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var started = new TaskCompletionSource<MonitorConfiguration>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = Task.Run(() => ReadCommands(input, started, lifetime));
        try
        {
            var configuration = started.Task.WaitAsync(lifetime.Token).GetAwaiter().GetResult();
            var today = Interlocked.Read(ref _todayStartMs);
            var feed = new RelayFeed(this, today);
            var ready = false;
            do
            {
                today = Interlocked.Read(ref _todayStartMs);
                feed.TodayStartMs = today;
                using var monitor = NewMonitor(configuration, feed, today);
                monitor.Seed(NowMs, today, lifetime.Token);
                lifetime.Token.ThrowIfCancellationRequested();
                feed.Publish(monitor, today, ready: !ready);
                ready = true;
                Poll(monitor, feed, lifetime.Token);
            }
            while (!lifetime.IsCancellationRequested);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
            if (!_inputClosed)
            {
                if (started.Task.IsCompletedSuccessfully)
                {
                    Error("silence", "Timed out waiting for a command");
                }
                else
                {
                    Error("badStart", "Timed out waiting for start");
                }

                return 1;
            }
        }
        catch (Exception) when (!lifetime.IsCancellationRequested)
        {
            Error("internal", "The relay could not read local metrics");
            return 1;
        }
        finally
        {
            lifetime.Cancel();
        }

        return 0;
    }

    private void Poll(MonitorCore monitor, RelayFeed feed, CancellationToken cancellation)
    {
        var rediscovery = Stopwatch.StartNew();
        while (!cancellation.WaitHandle.WaitOne(TimeSpan.FromSeconds(1)))
        {
            var now = NowMs;
            var today = Interlocked.Read(ref _todayStartMs);
            feed.TodayStartMs = today;
            if (rediscovery.Elapsed >= TimeSpan.FromSeconds(10))
            {
                monitor.Rediscover(now, today, cancellation);
                if (monitor.RestartRequested)
                {
                    return;
                }

                rediscovery.Restart();
            }

            monitor.PollRecent(now);
            feed.Publish(monitor, today);
        }

        cancellation.ThrowIfCancellationRequested();
    }

    private static MonitorCore NewMonitor(MonitorConfiguration configuration, RelayFeed feed, long today) =>
        new(configuration, SourceLocations.Resolve(configuration), today, feed);

    private async Task ReadCommands(Stream input, TaskCompletionSource<MonitorConfiguration> started, CancellationTokenSource lifetime)
    {
        var cancellation = lifetime.Token;
        var framer = new RemoteLineFramer(RelayLineEncoder.MaxLineBytes);
        var buffer = new byte[32 * 1024];
        try
        {
            while (true)
            {
                var count = await input.ReadAsync(buffer, cancellation).ConfigureAwait(false);
                if (count == 0)
                {
                    return;
                }

                var (lines, oversized) = framer.Consume(buffer.AsSpan(0, count));
                if (oversized > 0 && !started.Task.IsCompletedSuccessfully)
                {
                    Error("badStart", "Start exceeds the line limit");
                    return;
                }

                foreach (var line in lines)
                {
                    cancellation.ThrowIfCancellationRequested();
                    if (HandleCommand(line, started) && started.Task.IsCompletedSuccessfully)
                    {
                        lifetime.CancelAfter(TimeSpan.FromSeconds(150));
                    }
                }
            }
        }
        catch (IOException)
        {
            // The caller closed the SSH channel.
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        finally
        {
            if (!cancellation.IsCancellationRequested)
            {
                _inputClosed = true;
                lifetime.Cancel();
            }
        }
    }

    private bool HandleCommand(byte[] line, TaskCompletionSource<MonitorConfiguration> started)
    {
        try
        {
            using var json = JsonDocument.Parse(line);
            var command = json.RootElement;
            if (command.ValueKind != JsonValueKind.Object || !command.TryGetProperty("t", out var type)
                || type.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            var name = type.GetString();
            if (name == "ping" && Integer(command, "id", out var id) && id is >= -RemoteFields.IntegerLimit and <= RemoteFields.IntegerLimit)
            {
                Write(new { t = "clock", id, now = NowMs });
                return true;
            }

            if (!started.Task.IsCompletedSuccessfully)
            {
                if (name == "start" && Integer(command, "protocol", out var protocol)
                    && protocol == RemoteProtocol.Version && Day(command, out var today)
                    && command.TryGetProperty("sources", out var sources) && sources.ValueKind == JsonValueKind.Array
                    && command.TryGetProperty("zone", out var zone) && zone.ValueKind == JsonValueKind.Object
                    && command.TryGetProperty("prices", out var prices) && prices.ValueKind == JsonValueKind.Object)
                {
                    var enabled = new HashSet<SourceKind>();
                    foreach (var item in sources.EnumerateArray())
                    {
                        if (item.ValueKind == JsonValueKind.String && SourceNames.Parse(item.GetString()) is { } source)
                        {
                            enabled.Add(source);
                        }
                    }

                    Interlocked.Exchange(ref _todayStartMs, today);
                    var arch = RuntimeInformation.ProcessArchitecture == Architecture.X64 ? "x86_64" : "aarch64";
                    Write(new { t = "hello", protocol = RemoteProtocol.Version, version, arch, now = NowMs });
                    started.SetResult(new MonitorConfiguration(enabled));
                    return true;
                }

                return false;
            }

            switch (name)
            {
                case "today" when Day(command, out var today):
                    Interlocked.Exchange(ref _todayStartMs, today);
                    break;
                case "history":
                    Error("historyFailed", "History sync is unavailable on this relay");
                    break;
            }

            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool Integer(JsonElement command, string field, out long value)
    {
        value = 0;
        return command.TryGetProperty(field, out var element) && element.ValueKind == JsonValueKind.Number
            && element.TryGetInt64(out value);
    }

    private static bool Day(JsonElement command, out long today) =>
        Integer(command, "todayStartMs", out today) && RemoteFields.IsTime(today);

    private void Error(string code, string detail) => Write(new { t = "error", code, detail });

    internal void Write<T>(T line)
    {
        var data = JsonSerializer.SerializeToUtf8Bytes(line);
        if (data.Length > RemoteLineFramer.MaxLineBytes)
        {
            return;
        }

        lock (_writeLock)
        {
            output.Write(data);
            output.WriteByte((byte)'\n');
            output.Flush();
        }
    }
}
