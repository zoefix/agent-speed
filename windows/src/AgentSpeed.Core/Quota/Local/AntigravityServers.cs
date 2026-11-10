using System.Globalization;
using System.Text.Json;
using AgentSpeed.Core.Quota.Vendors;

namespace AgentSpeed.Core.Quota.Local;

/// <summary>One address to ask a language server on. <see cref="ToString"/> never shows the token.</summary>
public sealed record AntigravityEndpoint(string Scheme, int Port, string? Token)
{
    public override string ToString() => $"{Scheme}:{Port}";
}

/// <summary>A running Antigravity language server and the addresses to ask it on (QUOTAS 9.4), in order.</summary>
public sealed record AntigravityServer
{
    public AntigravityServer(int pid, IEnumerable<AntigravityEndpoint> endpoints)
    {
        Pid = pid;
        var seen = new HashSet<(string, int)>();
        Endpoints = [.. endpoints.Where(e => e.Port is >= 1 and <= 65535 && seen.Add((e.Scheme, e.Port))).Take(8)];
    }

    public int Pid { get; }

    /// <summary>Duplicates dropped (the first kept), at most 8.</summary>
    public IReadOnlyList<AntigravityEndpoint> Endpoints { get; init; }

    /// <summary>The same server without any token (a 401/403 made the reader forget it).</summary>
    public AntigravityServer WithoutTokens() => this with { Endpoints = [.. Endpoints.Select(e => e with { Token = null })] };

    public bool Equals(AntigravityServer? other) => other is not null && Pid == other.Pid && Endpoints.SequenceEqual(other.Endpoints);

    public override int GetHashCode() => HashCode.Combine(Pid, Endpoints.Count);
}

/// <summary>Finds the running servers; never starts Antigravity.</summary>
public interface IAntigravityLocator
{
    IReadOnlyList<AntigravityServer> Servers();
}

/// <summary>The rules of QUOTAS 9.4 <i>Servers</i> and <i>Ports</i> on Windows, as pure functions over what a process listing found.</summary>
public static class AntigravityRules
{
    /// <summary>
    /// A <c>language_server_*</c> executable of Antigravity: its arguments name <c>--app_data_dir</c> and antigravity, or it
    /// lies inside an <c>Antigravity</c> or <c>Antigravity IDE</c> install folder.
    /// </summary>
    public static bool IsLanguageServer(string executable, IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(executable);
        ArgumentNullException.ThrowIfNull(arguments);
        var name = Path.GetFileName(executable.Replace('/', '\\'));
        if (!name.StartsWith("language_server_", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var folders = executable.Replace('/', '\\').Split('\\');
        if (folders.Any(f => f.Equals("Antigravity", StringComparison.OrdinalIgnoreCase) || f.Equals("Antigravity IDE", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        var flagged = arguments.Any(a => a == "--app_data_dir" || a.StartsWith("--app_data_dir=", StringComparison.Ordinal));
        return flagged && arguments.Any(a => a.Contains("antigravity", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary><c>--name value</c> or <c>--name=value</c>.</summary>
    public static string? Flag(string name, IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        for (var i = 0; i < arguments.Count; i++)
        {
            if (arguments[i] == name && i + 1 < arguments.Count)
            {
                return arguments[i + 1];
            }

            if (arguments[i].StartsWith(name + "=", StringComparison.Ordinal))
            {
                return arguments[i][(name.Length + 1)..];
            }
        }

        return null;
    }

    /// <summary>
    /// The extension server's port first (its own token, else <c>--csrf_token</c>), then each listening loopback port over
    /// HTTP and then HTTPS with <c>--csrf_token</c>.
    /// </summary>
    public static AntigravityServer LanguageServer(int pid, IReadOnlyList<string> arguments, IReadOnlyList<int> listening)
    {
        ArgumentNullException.ThrowIfNull(listening);
        var csrf = Flag("--csrf_token", arguments);
        var endpoints = new List<AntigravityEndpoint>();
        if (Flag("--extension_server_port", arguments) is { } text && int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var port))
        {
            endpoints.Add(new AntigravityEndpoint("http", port, Flag("--extension_server_csrf_token", arguments) ?? csrf));
        }

        foreach (var listen in listening)
        {
            endpoints.Add(new AntigravityEndpoint("http", listen, csrf));
            endpoints.Add(new AntigravityEndpoint("https", listen, csrf));
        }

        return new AntigravityServer(pid, endpoints);
    }

    /// <summary>A daemon file <c>ls_*.json</c>: <c>{pid, csrfToken, httpsPort, httpPort}</c>.</summary>
    public static AntigravityServer? Daemon(ReadOnlyMemory<byte> data)
    {
        if (data.Length > 64 << 10)
        {
            return null;
        }

        using var document = VendorValue.ParseObject(data);
        if (document is null)
        {
            return null;
        }

        var root = document.RootElement;
        if (Integer(VendorValue.Get(root, "pid")) is not { } pid || pid <= 0 || pid > int.MaxValue)
        {
            return null;
        }

        var token = VendorValue.String(VendorValue.Get(root, "csrfToken"));
        var endpoints = new List<AntigravityEndpoint>();
        if (Integer(VendorValue.Get(root, "httpPort")) is { } http)
        {
            endpoints.Add(new AntigravityEndpoint("http", (int)Math.Clamp(http, 0, 65536), token));
        }

        if (Integer(VendorValue.Get(root, "httpsPort")) is { } https)
        {
            endpoints.Add(new AntigravityEndpoint("https", (int)Math.Clamp(https, 0, 65536), token));
        }

        var server = new AntigravityServer((int)pid, endpoints);
        return server.Endpoints.Count == 0 ? null : server;
    }

    /// <summary>The daemon folders' <c>ls_*.json</c> files, at most 16 per folder.</summary>
    public static IReadOnlyList<string> DaemonFiles(string home)
    {
        var files = new List<string>();
        foreach (var name in new[] { "antigravity", "antigravity-ide" })
        {
            var folder = Path.Combine(home, ".gemini", name, "daemon");
            try
            {
                if (Directory.Exists(folder))
                {
                    files.AddRange(Directory.EnumerateFiles(folder, "ls_*.json").Order(StringComparer.Ordinal).Take(16));
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // An unreadable folder lists nothing.
            }
        }

        return files;
    }

    private static long? Integer(JsonElement? value) =>
        value is { ValueKind: JsonValueKind.Number } number && number.TryGetInt64(out var integer) ? integer : null;
}
