using System.Runtime.Versioning;

namespace AgentSpeed.Core.Traffic.Engine;

public enum TrafficPlatformKind
{
    MacOS,
    Windows,
}

/// <summary>
/// How paths and file names compare on a platform (METRICS 10.5): <c>/</c> and <c>\</c> are equivalent, <c>~</c> is the
/// home folder, <c>%VAR%</c> expands, Windows paths compare case-insensitively; file names drop <c>.exe</c> on Windows
/// and keep their case.
/// </summary>
internal sealed class TrafficPlatform
{
    private readonly KeyValuePair<string, string>[] _environment;
    private readonly string _home;

    /// <param name="app">Agent Speed's own process: it and its descendants are never agent processes (METRICS 10.5).</param>
    public TrafficPlatform(TrafficPlatformKind kind, string home, IReadOnlyDictionary<string, string>? environment = null, ProcessIdentity? app = null)
    {
        Kind = kind;
        App = app;
        _home = (home ?? string.Empty).Replace('\\', '/');
        _environment = environment is null
            ? []
            : [.. environment.Select(e => new KeyValuePair<string, string>($"%{e.Key}%", e.Value.Replace('\\', '/')))];
    }

    /// <summary>This Windows user: home folder, <c>%LOCALAPPDATA%</c> and <c>%APPDATA%</c>, and this process.</summary>
    [SupportedOSPlatform("windows")]
    public static TrafficPlatform CurrentWindows() => new(
        TrafficPlatformKind.Windows,
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        new Dictionary<string, string>
        {
            ["LOCALAPPDATA"] = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            ["APPDATA"] = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        },
        Windows.ProcessReader.Current());

    public TrafficPlatformKind Kind { get; }

    /// <summary>Agent Speed's own process, when known.</summary>
    public ProcessIdentity? App { get; }

    public bool IsWindows => Kind == TrafficPlatformKind.Windows;

    public StringComparison PathComparison => IsWindows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public string Normalize(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return string.Empty;
        }

        var normalized = path.Replace('\\', '/');
        if (normalized.StartsWith("~/", StringComparison.Ordinal))
        {
            normalized = _home + normalized[1..];
        }

        if (normalized.Contains('%'))
        {
            foreach (var (key, value) in _environment)
            {
                normalized = normalized.Replace(key, value, IsWindows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
            }
        }

        normalized = normalized.TrimEnd('/');
        return IsWindows ? normalized.ToLowerInvariant() : normalized;
    }

    /// <summary>
    /// The executable's file name: as is on macOS; lower-case without <c>.exe</c> on Windows, where an image path has the
    /// case the process was launched with (METRICS 10.5).
    /// </summary>
    public string Name(string? executable)
    {
        var path = (executable ?? string.Empty).Replace('\\', '/');
        var slash = path.LastIndexOf('/');
        var name = slash >= 0 ? path[(slash + 1)..] : path;
        if (!IsWindows)
        {
            return name;
        }

        name = name.ToLowerInvariant();
        return name.EndsWith(".exe", StringComparison.Ordinal) ? name[..^4] : name;
    }

    /// <summary><paramref name="path"/> lies under (or is) one of <paramref name="prefixes"/> (already normalized, without a trailing slash).</summary>
    public bool IsUnder(string path, IReadOnlyList<string> prefixes)
    {
        var candidate = Normalize(path) + "/";
        foreach (var prefix in prefixes)
        {
            if (candidate.StartsWith(prefix + "/", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Normalizes a prefix list once.</summary>
    public string[] Prefixes(params string[] prefixes) => [.. prefixes.Select(Normalize)];
}
