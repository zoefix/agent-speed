namespace AgentSpeed.Core.Remote;

/// <summary>
/// The system OpenSSH client and its argument vector (REMOTE 4.1): Windows' own <c>ssh.exe</c> in System32, started with
/// an argument list, never through a shell.
/// </summary>
public static class RemoteSsh
{
    /// <summary>A development-only hook of debug builds (REMOTE 4.1): an absolute path adds <c>-F &lt;path&gt;</c> to every invocation.</summary>
    public const string ConfigFileVariable = "AGENT_SPEED_SSH_CONFIG";

    public static readonly IReadOnlyList<string> Options =
    [
        "-T",
        "-o", "BatchMode=yes",
        "-o", "ConnectTimeout=15",
        "-o", "ServerAliveInterval=15",
        "-o", "ServerAliveCountMax=3",
        "-o", "Compression=yes",
        "-o", "ControlMaster=no",
        "-o", "ClearAllForwardings=yes",
        "-o", "ForwardAgent=no",
        "-o", "ForwardX11=no",
        "-o", "PermitLocalCommand=no",
        "-o", "RequestTTY=no",
        "-o", "LogLevel=ERROR",
    ];

    /// <summary><c>%SystemRoot%\System32\OpenSSH\ssh.exe</c>, the Windows optional feature "OpenSSH Client".</summary>
    public static string Executable(IReadOnlyDictionary<string, string> environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        var root = environment.TryGetValue("SystemRoot", out var value) && Path.IsPathFullyQualified(value)
            ? value
            : System.Environment.GetFolderPath(System.Environment.SpecialFolder.Windows);
        return Path.Combine(root.Length > 0 ? root : @"C:\Windows", "System32", "OpenSSH", "ssh.exe");
    }

    /// <summary>The arguments after <c>argv[0]</c>; null when the destination is not valid (REMOTE 2.1).</summary>
    public static IReadOnlyList<string>? Arguments(string destination, string remoteCommand, string? configFile = null)
    {
        ArgumentNullException.ThrowIfNull(remoteCommand);
        if (RemoteHostValidation.DestinationIssue(destination) is not null)
        {
            return null;
        }

        var arguments = new List<string>(Options);
        if (configFile is not null)
        {
            arguments.Add("-F");
            arguments.Add(configFile);
        }

        arguments.Add("--");
        arguments.Add(destination);
        arguments.Add(remoteCommand);
        return arguments;
    }

    /// <summary>The file <c>AGENT_SPEED_SSH_CONFIG</c> names in <paramref name="environment"/>; always null in release builds.</summary>
    public static string? DevelopmentConfigFile(IReadOnlyDictionary<string, string> environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
#if DEBUG
        return environment.TryGetValue(ConfigFileVariable, out var path) && Path.IsPathFullyQualified(path) ? path : null;
#else
        return null;
#endif
    }

    /// <summary>The app's environment plus <c>LC_ALL=C</c>, so error messages are the English ones the status reads.</summary>
    public static Dictionary<string, string> Environment(IReadOnlyDictionary<string, string> baseEnvironment)
    {
        ArgumentNullException.ThrowIfNull(baseEnvironment);
        var environment = new Dictionary<string, string>(baseEnvironment, StringComparer.OrdinalIgnoreCase)
        {
            ["LC_ALL"] = "C",
        };
        return environment;
    }

    /// <summary>The current process environment as a dictionary.</summary>
    public static Dictionary<string, string> ProcessEnvironment()
    {
        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (System.Collections.DictionaryEntry entry in System.Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string key && entry.Value is string value)
            {
                environment[key] = value;
            }
        }

        return environment;
    }
}

/// <summary>The relay's architectures (REMOTE 1, 4.2).</summary>
public enum RemoteArch
{
    X86_64,
    Aarch64,
}

public static class RemoteArchNames
{
    public static string Name(this RemoteArch arch) => arch == RemoteArch.X86_64 ? "x86_64" : "aarch64";

    /// <summary><c>uname -m</c> → architecture: <c>x86_64</c> / <c>amd64</c> and <c>aarch64</c> / <c>arm64</c>; null for anything else.</summary>
    public static RemoteArch? FromMachine(string machine) => machine.ToLowerInvariant() switch
    {
        "x86_64" or "amd64" => RemoteArch.X86_64,
        "aarch64" or "arm64" => RemoteArch.Aarch64,
        _ => null,
    };
}

/// <summary>The file names of REMOTE 3.1 and 4.2.</summary>
public static class RemoteRelayName
{
    public const string InstalledPrefix = "agent-speed-relay-";

    /// <summary><c>relay\agent-speed-relay-linux-&lt;arch&gt;</c> next to the app.</summary>
    public static string Bundled(RemoteArch arch) => "agent-speed-relay-linux-" + arch.Name();

    /// <summary>
    /// <c>agent-speed-relay-&lt;version&gt;-&lt;arch&gt;-&lt;hash12&gt;</c>. Characters of the version outside
    /// <c>[A-Za-z0-9._-]</c> become <c>-</c>, so the name stays safe to insert into a remote command.
    /// </summary>
    public static string Installed(string version, RemoteArch arch, string sha256)
    {
        ArgumentNullException.ThrowIfNull(version);
        ArgumentNullException.ThrowIfNull(sha256);
        var bytes = System.Text.Encoding.UTF8.GetBytes(version);
        var clean = new string([.. bytes.Take(64).Select(b => b < 0x80 && RemoteHostValidation.IsNameChar((char)b) ? (char)b : '-')]);
        return InstalledPrefix + (clean.Length == 0 ? "0" : clean) + "-" + arch.Name() + "-" + sha256.ToLowerInvariant()[..Math.Min(12, sha256.Length)];
    }

    internal static bool IsSafe(string name) =>
        name.Length is > 0 and <= 255 && name.All(RemoteHostValidation.IsNameChar) && name[0] != '-' && name is not "." and not "..";
}

/// <summary>
/// The three remote commands (REMOTE 4.2, 4.3): <c>sh -c '&lt;script&gt;' agent-speed &lt;arguments&gt;</c>. The scripts are
/// fixed; the arguments are the validated folder (unquoted, so the remote shell expands <c>~</c>), a safe file name and a hex
/// digest. Every builder returns null for a value that fails validation.
/// </summary>
public static class RemoteCommand
{
    public const int MismatchStatus = 3;
    public const int WriteFailedStatus = 4;
    public const int NoHashToolStatus = 5;

    public static string? Probe(string folder) =>
        CheckedFolder(folder) is { } checkedFolder ? Command(RemoteScripts.Probe, checkedFolder) : null;

    public static string? Upload(string folder, string fileName, string sha256) =>
        CheckedFolder(folder) is { } checkedFolder && RemoteRelayName.IsSafe(fileName) && IsDigest(sha256)
            ? Command(RemoteScripts.Upload, checkedFolder, fileName, sha256.ToLowerInvariant())
            : null;

    public static string? Run(string folder, string fileName) =>
        CheckedFolder(folder) is { } checkedFolder && RemoteRelayName.IsSafe(fileName)
            ? Command(RemoteScripts.Run, checkedFolder, fileName)
            : null;

    internal static string Command(string script, params string[] arguments)
    {
        if (!RemoteScripts.IsQuotable(script))
        {
            throw new InvalidOperationException("A remote script must be quotable.");
        }

        return string.Join(' ', ["sh", "-c", "'" + script + "'", "agent-speed", .. arguments]);
    }

    internal static bool IsDigest(string text) => text.Length == 64 && text.All(char.IsAsciiHexDigit);

    private static string? CheckedFolder(string folder) =>
        RemoteHostValidation.FolderIssue(folder) is null ? RemoteHostValidation.NormalizedFolder(folder) : null;
}
