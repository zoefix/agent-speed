namespace AgentSpeed.Core.Remote;

/// <summary>A configured remote host as the live views name it.</summary>
/// <param name="Name">The display name (REMOTE 2.1).</param>
internal sealed record RemoteHostInfo(string Id, string Name);

/// <summary>What a monitor session applies for the remote hosts (REMOTE 7), in order.</summary>
internal abstract record RemoteFeedChange
{
    private RemoteFeedChange()
    {
    }

    /// <summary>The configured hosts in settings order; an empty list means no remote host is configured.</summary>
    public sealed record Hosts(IReadOnlyList<RemoteHostInfo> Value) : RemoteFeedChange;

    /// <summary>The host left the settings: its agents and responses leave every live value.</summary>
    public sealed record RemoveHost(string Id) : RemoteFeedChange;

    public sealed record Status(string Host, RemoteHostStatus Value) : RemoteFeedChange;

    public sealed record Report(string Host, RemoteSourceReport Value) : RemoteFeedChange;

    public sealed record Agent(RemoteAgent Value, string? ParentId) : RemoteFeedChange;

    public sealed record AgentRemoved(string Id) : RemoteFeedChange;

    /// <summary>The host's complete parent map, replacing the previous one.</summary>
    public sealed record Parents(string Host, IReadOnlyDictionary<string, string> Links) : RemoteFeedChange;

    /// <summary><paramref name="Live"/> is false for seeded lines (before <c>ready</c>) and for a replay: no events.</summary>
    public sealed record Response(RemoteResponse Value, bool Live) : RemoteFeedChange;

    public sealed record Tool(RemoteToolCompletion Value) : RemoteFeedChange;

    /// <summary>The host cannot be seen: every agent of it becomes idle without an in-flight start.</summary>
    public sealed record Idle(string Host) : RemoteFeedChange;
}
