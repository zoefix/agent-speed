using System.Collections.Immutable;
using System.Text;

namespace AgentSpeed.Core.Remote;

/// <summary>What the probe's output says (REMOTE 4.2 step 1).</summary>
public enum RemoteProbeVerdictKind
{
    Supported,
    UnsupportedSystem,
    CannotVerify,

    /// <summary>The output is not the probe's (a login banner on stdout, a cut-off stream): treated as an exit.</summary>
    Malformed,
}

/// <param name="Arch">The relay to run, for <see cref="RemoteProbeVerdictKind.Supported"/>.</param>
/// <param name="System">The <c>uname -s -m</c> text, for <see cref="RemoteProbeVerdictKind.UnsupportedSystem"/>.</param>
public readonly record struct RemoteProbeVerdict(RemoteProbeVerdictKind Kind, RemoteArch Arch = RemoteArch.X86_64, string System = "");

/// <summary>The output of the probe command (REMOTE 4.2 step 1).</summary>
/// <param name="System"><c>uname -s -m</c> as printed, printable ASCII only and at most 64 characters.</param>
/// <param name="HashTool">The hash command the host has; null when it has none.</param>
/// <param name="Hashes">Lowercase SHA-256 of each existing relay file, by its name (only safe names are kept).</param>
/// <param name="IsComplete">The script ran to its last line.</param>
public sealed record RemoteProbeResult(
    string System,
    string Kernel,
    string Machine,
    string? HashTool,
    ImmutableDictionary<string, string> Hashes,
    bool IsComplete)
{
    public const int MaxOutputBytes = 64 * 1024;

    /// <summary>Reads the probe's stdout; lines it does not know are ignored (a shell may print a banner first).</summary>
    public static RemoteProbeResult Parse(ReadOnlySpan<byte> output)
    {
        string system = string.Empty, kernel = string.Empty, machine = string.Empty;
        string? tool = null;
        var hashes = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
        var complete = false;
        var text = Encoding.UTF8.GetString(output[..Math.Min(output.Length, MaxOutputBytes)]);
        foreach (var line in Lines(text))
        {
            if (line.StartsWith("system ", StringComparison.Ordinal))
            {
                system = Displayable(line[7..]);
                var words = system.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                kernel = words.Length > 0 ? words[0] : string.Empty;
                machine = words.Length > 1 ? words[^1] : string.Empty;
            }
            else if (line.StartsWith("tool ", StringComparison.Ordinal))
            {
                var value = line[5..];
                tool = value == "none" ? null : Displayable(value);
            }
            else if (line.StartsWith("hash ", StringComparison.Ordinal))
            {
                var parts = line[5..].Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length != 2)
                {
                    continue;
                }

                var digest = parts[0].ToLowerInvariant();
                var name = parts[1];
                if (RemoteCommand.IsDigest(digest) && RemoteRelayName.IsSafe(name)
                    && name.StartsWith(RemoteRelayName.InstalledPrefix, StringComparison.Ordinal))
                {
                    hashes[name] = digest;
                }
            }
            else if (line == "end")
            {
                complete = true;
            }
        }

        return new RemoteProbeResult(system, kernel, machine, tool, hashes.ToImmutable(), complete);
    }

    public RemoteProbeVerdict Verdict
    {
        get
        {
            if (!IsComplete || Kernel.Length == 0)
            {
                return new(RemoteProbeVerdictKind.Malformed);
            }

            if (Kernel != "Linux" || RemoteArchNames.FromMachine(Machine) is not { } arch)
            {
                return new(RemoteProbeVerdictKind.UnsupportedSystem, System: System);
            }

            return HashTool is null ? new(RemoteProbeVerdictKind.CannotVerify) : new(RemoteProbeVerdictKind.Supported, arch);
        }
    }

    /// <summary>The installed relay matches the bundled one.</summary>
    public bool HasInstalled(string name, string sha256) =>
        Hashes.TryGetValue(name, out var digest) && digest == sha256.ToLowerInvariant();

    /// <summary>The hash the upload script printed (<c>hash &lt;hex&gt;</c>), lowercase; null when absent.</summary>
    public static string? UploadedHash(ReadOnlySpan<byte> output)
    {
        var text = Encoding.UTF8.GetString(output[..Math.Min(output.Length, MaxOutputBytes)]);
        foreach (var line in Lines(text))
        {
            if (line.StartsWith("hash ", StringComparison.Ordinal) && line[5..].ToLowerInvariant() is var digest && RemoteCommand.IsDigest(digest))
            {
                return digest;
            }
        }

        return null;
    }

    /// <summary>Lines split at any newline (<c>\n</c>, <c>\r\n</c>, <c>\r</c>), empty ones dropped.</summary>
    private static IEnumerable<string> Lines(string text) =>
        text.Split(['\n', '\r', '\v', '\f', (char)0x85, (char)0x2028, (char)0x2029], StringSplitOptions.RemoveEmptyEntries);

    private static string Displayable(string text)
    {
        var kept = new StringBuilder(Math.Min(text.Length, 64));
        foreach (var c in text)
        {
            if (c is >= ' ' and <= '~')
            {
                kept.Append(c);
                if (kept.Length == 64)
                {
                    break;
                }
            }
        }

        return kept.ToString().Trim(' ', '\t');
    }
}
