using System.Reflection;
using System.Security.Cryptography;

namespace AgentSpeed.Core.Remote;

/// <summary>One bundled relay executable with its digest (REMOTE 3.1).</summary>
/// <param name="Sha256">Lowercase hex SHA-256 of the bytes <paramref name="Open"/> gives.</param>
/// <param name="InstalledName">The name it is installed under on a host (REMOTE 4.2).</param>
/// <param name="Open">Opens its bytes for the upload.</param>
/// <param name="Path">The file it was read from; null for an embedded relay.</param>
public sealed record RemoteRelayExecutable(RemoteArch Arch, string Sha256, long Size, string InstalledName, Func<Stream> Open, string? Path = null);

/// <summary>
/// Finds the relay executables (REMOTE 3.1): embedded in the app as <c>AgentSpeed.Relay.linux-&lt;arch&gt;</c> with a
/// build-time <c>.sha256</c> next to it, so the in-app updater, which replaces only the executable, always brings matching
/// relays; or, for development, the folder named by <c>AGENT_SPEED_RELAY_DIR</c>. The digest is computed from the bytes
/// themselves (an embedded relay whose bytes disagree with its build-time digest is refused) and cached, for a folder by
/// size and modification time. Thread-safe.
/// </summary>
public sealed class RemoteRelayBundle
{
    public const string OverrideVariable = "AGENT_SPEED_RELAY_DIR";
    public const string ResourcePrefix = "AgentSpeed.Relay.linux-";

    private readonly Lock _lock = new();
    private readonly Dictionary<RemoteArch, ((long Size, DateTime Modified) Stamp, RemoteRelayExecutable Executable)> _cache = [];

    /// <summary>The relays of a folder (development, tests).</summary>
    public RemoteRelayBundle(string? directory, string version)
    {
        Directory = directory;
        Version = version;
    }

    private RemoteRelayBundle(Assembly resources, string version)
    {
        Resources = resources;
        Version = version;
    }

    public string? Directory { get; }

    /// <summary>The assembly the relays are embedded in; null for a folder.</summary>
    public Assembly? Resources { get; }

    /// <summary>The app version (<c>0.0.0</c> for a development build).</summary>
    public string Version { get; }

    /// <summary>The relays embedded in <paramref name="assembly"/>.</summary>
    public static RemoteRelayBundle FromResources(Assembly assembly, string version)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        return new RemoteRelayBundle(assembly, version);
    }

    /// <summary>
    /// The app's relays: the override folder when <c>AGENT_SPEED_RELAY_DIR</c> is set, else the ones embedded in
    /// <paramref name="resources"/> (the entry assembly when null).
    /// </summary>
    public static RemoteRelayBundle Current(string version, IReadOnlyDictionary<string, string>? environment = null, Assembly? resources = null)
    {
        environment ??= RemoteSsh.ProcessEnvironment();
        if (environment.TryGetValue(OverrideVariable, out var overrideDirectory) && overrideDirectory.Length > 0)
        {
            return new RemoteRelayBundle(overrideDirectory, version);
        }

        return (resources ?? Assembly.GetEntryAssembly()) is { } assembly ? FromResources(assembly, version) : new RemoteRelayBundle((string?)null, version);
    }

    /// <summary>The executable for <paramref name="arch"/> with its digest; null when it is missing, unreadable or does not match its digest.</summary>
    public RemoteRelayExecutable? Executable(RemoteArch arch) => Resources is { } assembly ? Embedded(assembly, arch) : FromFolder(arch);

    private RemoteRelayExecutable? Embedded(Assembly assembly, RemoteArch arch)
    {
        lock (_lock)
        {
            if (_cache.TryGetValue(arch, out var cached))
            {
                return cached.Executable;
            }
        }

        var name = ResourcePrefix + arch.Name();
        string digest;
        long size;
        using (var stream = assembly.GetManifestResourceStream(name))
        {
            if (stream is null || stream.Length == 0)
            {
                return null;
            }

            size = stream.Length;
            digest = Convert.ToHexStringLower(SHA256.HashData(stream));
        }

        using (var expected = assembly.GetManifestResourceStream(name + ".sha256"))
        {
            if (expected is not null && !string.Equals(new StreamReader(expected).ReadToEnd().Trim(), digest, StringComparison.OrdinalIgnoreCase))
            {
                RemoteLog.Write($"Embedded relay for {arch.Name()} does not match its digest");
                return null;
            }
        }

        var executable = new RemoteRelayExecutable(arch, digest, size, RemoteRelayName.Installed(Version, arch, digest),
            () => assembly.GetManifestResourceStream(name) ?? throw new IOException("The embedded relay is gone."));
        lock (_lock)
        {
            _cache[arch] = ((size, DateTime.MinValue), executable);
        }

        return executable;
    }

    private RemoteRelayExecutable? FromFolder(RemoteArch arch)
    {
        if (Directory is null)
        {
            return null;
        }

        var path = System.IO.Path.Combine(Directory, RemoteRelayName.Bundled(arch));
        FileInfo info;
        try
        {
            info = new FileInfo(path);
            if (!info.Exists || info.Length <= 0 || (info.Attributes & FileAttributes.Directory) != 0)
            {
                return null;
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return null;
        }

        var stamp = (info.Length, info.LastWriteTimeUtc);
        lock (_lock)
        {
            if (_cache.TryGetValue(arch, out var cached) && cached.Stamp == stamp)
            {
                return cached.Executable;
            }
        }

        string digest;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan);
            digest = Convert.ToHexStringLower(SHA256.HashData(stream));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            RemoteLog.Write($"Bundled relay for {arch.Name()} is unreadable");
            return null;
        }

        var executable = new RemoteRelayExecutable(arch, digest, info.Length, RemoteRelayName.Installed(Version, arch, digest),
            () => new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read), path);
        lock (_lock)
        {
            _cache[arch] = (stamp, executable);
        }

        return executable;
    }
}
