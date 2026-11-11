namespace AgentSpeed.Core.Remote;

/// <summary>
/// One remote host of the settings (REMOTE 2.1). <see cref="ExtraJson"/> keeps the members a newer version wrote, so
/// saving never drops them.
/// </summary>
public sealed record RemoteHost
{
    /// <summary>The host id of this computer (REMOTE 2.2); never a remote host's id.</summary>
    public const string LocalId = "local";

    public const string DefaultRelayFolder = "~/.cache/agent-speed";

    private readonly string? _name;
    private readonly string? _relayFolder;

    public RemoteHost(string destination, string? id = null)
    {
        Id = id ?? NewId();
        Destination = destination;
    }

    /// <summary>Upper-case UUID string, never reused.</summary>
    public string Id { get; init; }

    /// <summary>What the user would type after <c>ssh</c>; validated by <see cref="RemoteHostValidation.DestinationIssue"/>.</summary>
    public string Destination { get; init; }

    /// <summary>Custom display name (1–32 characters, trimmed); null = automatic.</summary>
    public string? Name
    {
        get => _name;
        init => _name = RemoteHostValidation.CleanName(value);
    }

    public bool Enabled { get; init; } = true;

    /// <summary>Null = <c>~/.cache/agent-speed</c>.</summary>
    public string? RelayFolder
    {
        get => _relayFolder;
        init => _relayFolder = string.IsNullOrEmpty(value) ? null : value;
    }

    /// <summary>The host's Claude Code status line bridge (REMOTE 11.3); absent = false. Changing it never reconnects.</summary>
    public bool QuotaClaudeCli { get; init; }

    /// <summary>Unknown members as one JSON object text; null when there are none.</summary>
    public string? ExtraJson { get; init; }

    /// <summary>A new upper-case UUID, as the settings keep them.</summary>
    public static string NewId() => Guid.NewGuid().ToString("D").ToUpperInvariant();

    /// <summary>The folder on the host, as inserted (unquoted) into the remote commands.</summary>
    public string EffectiveRelayFolder => RemoteHostValidation.NormalizedFolder(RelayFolder ?? DefaultRelayFolder);

    /// <summary>Both <see cref="Destination"/> and <see cref="RelayFolder"/> pass REMOTE 2.1; otherwise the host shows <c>invalidDestination</c>.</summary>
    public bool IsConnectable => RemoteHostValidation.DestinationIssue(Destination) is null
        && (RelayFolder is not { } folder || RemoteHostValidation.FolderIssue(folder) is null);

    /// <summary>The host part of <see cref="Destination"/>: without <c>ssh://</c>, user and port.</summary>
    public string AutomaticName => RemoteHostValidation.AutomaticName(Destination);

    /// <summary>What reconnects a running connection when it changes (an edit): everything but the name and the switches.</summary>
    internal (string Destination, string Folder) ConnectionIdentity => (Destination, EffectiveRelayFolder);
}
