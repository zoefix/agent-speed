using AgentSpeed.Core.Monitoring;

namespace AgentSpeed.Core.Remote;

/// <summary>What a host's connection reports, delivered in order.</summary>
internal abstract record RemoteHostEventKind
{
    private RemoteHostEventKind()
    {
    }

    public sealed record Status(RemoteHostStatus Value) : RemoteHostEventKind;

    /// <summary>A validated relay line, times already converted to local time (REMOTE 6). <paramref name="Seeded"/> is true before <c>ready</c>.</summary>
    public sealed record Message(RelayMessage Value, bool Seeded) : RemoteHostEventKind;

    /// <summary>The history schedule (REMOTE 8.2) wants a sync: answer with the digests held for the host.</summary>
    public sealed record HistoryDue : RemoteHostEventKind;
}

/// <param name="Connection">Counts the host's connection attempts; the messages of one connection share it, so a reconnect is visible.</param>
internal sealed record RemoteHostEvent(string HostId, int Connection, RemoteHostEventKind Kind);

/// <summary>What a connection sends with <c>start</c> and keeps in step afterwards.</summary>
/// <param name="Sources">The app's enabled sources (REMOTE 3.3); a change means a new connection.</param>
/// <param name="Watch">Live reporting's watch (METRICS 11.10).</param>
/// <param name="Quota">What the relay reads for quotas (REMOTE 11.1); sent with <c>start</c> and whenever it changes.</param>
internal sealed record RemoteSessionSettings(SourceSet Sources, RemotePriceTableJson Prices, bool Watch = false, RemoteQuotaConfig? Quota = null);

/// <summary>Everything a connection uses from the outside world, replaceable in tests.</summary>
internal sealed record RemoteEnvironment(
    IRemoteTransport Transport,
    RemoteRelayBundle Relays,
    IRemoteScheduling Scheduling,
    Func<TimeZoneInfo> TimeZone,
    IReadOnlyDictionary<string, string> ProcessEnvironment)
{
    /// <param name="relays">The assembly the relays are embedded in (REMOTE 3.1); the entry assembly when null.</param>
    public static RemoteEnvironment System(string appVersion, System.Reflection.Assembly? relays = null)
    {
        var environment = RemoteSsh.ProcessEnvironment();
        return new RemoteEnvironment(ProcessRemoteTransport.Instance, RemoteRelayBundle.Current(appVersion, environment, relays),
            SystemRemoteScheduling.Instance, static () => TimeZoneInfo.Local, environment);
    }

    public string SshExecutable => RemoteSsh.Executable(ProcessEnvironment);

    /// <summary>Local midnight of the day containing <paramref name="date"/> in the app's time zone, in Unix milliseconds.</summary>
    public long TodayStartMs(DateTimeOffset date) => LocalDay.StartMs(date.ToUnixTimeMilliseconds(), TimeZone());

    /// <summary>The next local midnight after <paramref name="date"/>.</summary>
    public DateTimeOffset NextMidnight(DateTimeOffset date)
    {
        var zone = TimeZone();
        var start = LocalDay.StartMs(date.ToUnixTimeMilliseconds(), zone);
        var probe = DateTimeOffset.FromUnixTimeMilliseconds(start).AddHours(26);
        var next = LocalDay.StartMs(probe.ToUnixTimeMilliseconds(), zone);
        return next > date.ToUnixTimeMilliseconds() ? DateTimeOffset.FromUnixTimeMilliseconds(next) : date.AddDays(1);
    }
}
