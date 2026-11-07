using System.Collections.Immutable;
using System.Text.RegularExpressions;
using AgentSpeed.Core.Reporting;

namespace AgentSpeed.Core.Lan;

/// <summary>A device the app sends frames to (LAN 2.1 <c>targets[]</c>).</summary>
public sealed record LanTarget(string Id, string Name, LanTargetKind Kind, string Address, string? Key, bool Enabled)
{
    public const int KeyLimit = 128;

    /// <summary>The name, or the address while the name is empty (DESIGN 14).</summary>
    public string DisplayName => Name.Length > 0 ? Name : Address;

    public LanTargetAddress? Parsed => LanTargetAddress.Parse(Kind, Address);

    /// <summary>Never prints the key.</summary>
    public override string ToString() => $"LanTarget {{ Id = {Id}, Kind = {Kind}, Enabled = {Enabled} }}";
}

/// <summary>The server's part of the document (LAN 2.1 <c>server</c>).</summary>
public sealed record LanServerSettings(bool Enabled = false, int Port = LanSettings.DefaultPort, string? Key = null)
{
    public override string ToString() => $"LanServerSettings {{ Enabled = {Enabled}, Port = {Port} }}";
}

/// <summary>
/// The settings value <c>lan</c> (LAN 2.1). A null <see cref="Instance"/> or server key means "create one when needed": the
/// instance the first time the document is written, the key when the server is first turned on.
/// </summary>
public sealed partial record LanSettings(string? Instance, LanServerSettings Server, ImmutableArray<LanTarget> Targets)
{
    public const int Version = 1;
    public const int DefaultPort = 7247;
    public const int MinPort = 1024;
    public const int MaxPort = 65535;
    public const int MaxTargets = 8;

    public static LanSettings Default { get; } = new(null, new LanServerSettings(), []);

    public bool CanAdd => Targets.Length < MaxTargets;

    /// <summary>The server is on or a target exists: About adds <c>privacy.lan</c> (DESIGN 14).</summary>
    public bool IsInUse => Server.Enabled || !Targets.IsEmpty;

    /// <summary>The server is on or any target is enabled (the feed runs while the network switch is on, LAN 3.3).</summary>
    public bool WantsFeed => Server.Enabled || Targets.Any(t => t.Enabled);

    public static bool IsValidPort(int port) => port is >= MinPort and <= MaxPort;

    public static bool IsUuid(string? value) => value is not null && UuidPattern().IsMatch(value);

    public LanTarget? Target(string id) => Targets.FirstOrDefault(t => t.Id == id);

    /// <summary>With an instance id (created once, LAN 2.1).</summary>
    public LanSettings WithInstance() => IsUuid(Instance) ? this : this with { Instance = ReportCanonical.NewUuid() };

    /// <summary>The server switched; turning it on creates the access key when there is none.</summary>
    public LanSettings WithServerEnabled(bool enabled) => (this with
    {
        Server = Server with { Enabled = enabled, Key = enabled && !LanKey.IsValid(Server.Key) ? LanKey.New() : Server.Key },
    }).WithInstance();

    public LanSettings WithPort(int port) => IsValidPort(port) ? (this with { Server = Server with { Port = port } }).WithInstance() : this;

    public LanSettings WithNewKey() => (this with { Server = Server with { Key = LanKey.New() } }).WithInstance();

    /// <summary>Adds a target at the end, or replaces the one with the same id; the ninth is not added.</summary>
    public LanSettings Saving(LanTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        var index = Targets.FindIndex(t => t.Id == target.Id);
        if (index >= 0)
        {
            return (this with { Targets = Targets.SetItem(index, target) }).WithInstance();
        }

        return CanAdd ? (this with { Targets = Targets.Add(target) }).WithInstance() : this;
    }

    public LanSettings Removing(string id) => (this with { Targets = Targets.RemoveAll(t => t.Id == id) }).WithInstance();

    public LanSettings SettingEnabled(string id, bool enabled)
    {
        var index = Targets.FindIndex(t => t.Id == id);
        return index < 0 ? this : (this with { Targets = Targets.SetItem(index, Targets[index] with { Enabled = enabled }) }).WithInstance();
    }

    /// <summary>A new target's fields after the rules of LAN 2.1, or null when the address is invalid.</summary>
    public static LanTarget? NewTarget(string? id, string? name, LanTargetKind kind, string? address, string? key, bool enabled = true)
    {
        var trimmed = address is null ? string.Empty : LanText.Trim(address);
        if (LanTargetAddress.Parse(kind, trimmed) is null)
        {
            return null;
        }

        var keyText = key is null ? null : LanText.Field(key, 1, LanTarget.KeyLimit);
        return new LanTarget(IsUuid(id) ? id! : ReportCanonical.NewUuid(), LanText.Field(name, 0, LanText.NameLimit) ?? string.Empty, kind, trimmed,
            keyText, enabled);
    }

    public bool Equals(LanSettings? other) =>
        other is not null && Instance == other.Instance && Server == other.Server && Targets.SequenceEqual(other.Targets);

    public override int GetHashCode() => HashCode.Combine(Instance, Server, Targets.Length);

    [GeneratedRegex("^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}\\z", RegexOptions.CultureInvariant)]
    private static partial Regex UuidPattern();
}

internal static class LanImmutableExtensions
{
    public static int FindIndex<T>(this ImmutableArray<T> items, Func<T, bool> match)
    {
        for (var i = 0; i < items.Length; i++)
        {
            if (match(items[i]))
            {
                return i;
            }
        }

        return -1;
    }
}
