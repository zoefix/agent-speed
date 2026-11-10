using AgentSpeed.Core.Quota;
using AgentSpeed.Core.Quota.Network;

namespace AgentSpeed.Core.Remote;

/// <summary>
/// The host's clock offset <c>δ = remote − local</c> (REMOTE 6), from <c>ping</c> / <c>clock</c> pairs:
/// <c>δ = now_remote − (t_send + t_receive) / 2</c>, the sample with the smallest round trip among the last 8. Until the
/// first sample, <c>hello.now</c> gives <c>δ</c>. Not thread-safe.
/// </summary>
public sealed class RemoteClockEstimator
{
    public const int SampleLimit = 8;
    public const long SkewThresholdMs = 5000;

    private readonly List<(double OffsetMs, long RoundTripMs)> _samples = [];
    private double? _helloOffsetMs;

    public IReadOnlyList<(double OffsetMs, long RoundTripMs)> Samples => _samples;

    public void NoteHello(long remoteNowMs, long localMs) => _helloOffsetMs = (double)remoteNowMs - localMs;

    /// <summary>A <c>clock</c> reply to a ping sent at <paramref name="sendMs"/> and received at <paramref name="receiveMs"/> (both local).</summary>
    public void AddSample(long sendMs, long receiveMs, long remoteNowMs)
    {
        if (receiveMs < sendMs)
        {
            return;
        }

        _samples.Add(((double)remoteNowMs - (((double)sendMs + receiveMs) / 2), receiveMs - sendMs));
        if (_samples.Count > SampleLimit)
        {
            _samples.RemoveRange(0, _samples.Count - SampleLimit);
        }
    }

    /// <summary><c>δ</c> in whole milliseconds; null before <c>hello</c>.</summary>
    public long? OffsetMs
    {
        get
        {
            double? offset = _helloOffsetMs;
            if (_samples.Count > 0)
            {
                var best = _samples[0];
                foreach (var sample in _samples)
                {
                    if (sample.RoundTripMs < best.RoundTripMs)
                    {
                        best = sample;
                    }
                }

                offset = best.OffsetMs;
            }

            return offset is { } value ? (long)Math.Round(value, MidpointRounding.AwayFromZero) : null;
        }
    }

    public bool IsSkewedNow => OffsetMs is { } offset && IsSkewed(offset);

    public static bool IsSkewed(long offsetMs) => Math.Abs(offsetMs) >= SkewThresholdMs;
}

internal static class RelayMessageTimes
{
    /// <summary>
    /// The message with every time of an <c>agent</c>, <c>response</c>, <c>tool</c> or <c>quota</c> line converted to local time
    /// (<c>t − δ</c>); null when a converted time leaves the years 2000–2100 (the line is then invalid). Durations are unchanged.
    /// </summary>
    public static RelayMessage? Converted(this RelayMessage message, long delta)
    {
        switch (message)
        {
            case RelayMessage.Agent { Value: var agent }:
                if (!ShiftOptional(agent.InFlightStartMs, delta, out var inFlight) || !ShiftOptional(agent.LastLineMs, delta, out var lastLine))
                {
                    return null;
                }

                var actions = new List<Work.WorkAction>(agent.Actions.Count);
                foreach (var action in agent.Actions)
                {
                    if (Shift(action.AtMs, delta) is not { } call || !ShiftOptional(action.ResultMs, delta, out var result))
                    {
                        return null;
                    }

                    actions.Add(action with { AtMs = call, ResultMs = result });
                }

                return new RelayMessage.Agent(agent with { InFlightStartMs = inFlight, LastLineMs = lastLine, Actions = actions });
            case RelayMessage.Response { Value: var response }:
                var record = response.Record;
                if (Shift(record.EndMs, delta) is not { } end || !ShiftOptional(record.StartMs, delta, out var start)
                    || !ShiftOptional(record.FirstMs, delta, out var first))
                {
                    return null;
                }

                return new RelayMessage.Response(response with { Record = record with { EndMs = end, StartMs = start, FirstMs = first } });
            case RelayMessage.Tool { Value: var tool }:
                if (Shift(tool.Call.AtMs, delta) is not { } atMs || tool.Call.ResultMs is not { } resultMs || Shift(resultMs, delta) is not { } resultLocal)
                {
                    return null;
                }

                return new RelayMessage.Tool(tool with { Call = tool.Call with { AtMs = atMs, ResultMs = resultLocal } });
            case RelayMessage.Quota { Value: var report }:
                return Converted(report, delta) is { } local ? new RelayMessage.Quota(local) : null;
            default:
                return message;
        }
    }

    /// <summary>The snapshot's time, every reset and the time of an <c>ok</c> status.</summary>
    private static RemoteQuotaReport? Converted(RemoteQuotaReport report, long delta)
    {
        var snapshot = report.Snapshot;
        if (Shift(snapshot.ObservedMs, delta) is not { } observed)
        {
            return null;
        }

        var windows = new List<QuotaWindow>(snapshot.Windows.Count);
        foreach (var window in snapshot.Windows)
        {
            if (!ShiftOptional(window.ResetsAtMs, delta, out var resets))
            {
                return null;
            }

            windows.Add(window with { ResetsAtMs = resets });
        }

        var status = report.Status;
        if (status.Kind == QuotaStatusKind.Ok)
        {
            if (Shift(status.AtMs, delta) is not { } at)
            {
                return null;
            }

            status = status with { AtMs = at };
        }

        return report with { Snapshot = snapshot with { ObservedMs = observed, Windows = windows }, Status = status };
    }

    private static long? Shift(long ms, long delta)
    {
        try
        {
            var local = checked(ms - delta);
            return RemoteFields.IsTime(local) ? local : null;
        }
        catch (OverflowException)
        {
            return null;
        }
    }

    private static bool ShiftOptional(long? ms, long delta, out long? shifted)
    {
        shifted = null;
        if (ms is not { } value)
        {
            return true;
        }

        shifted = Shift(value, delta);
        return shifted is not null;
    }
}
