using System.Text;
using System.Text.RegularExpressions;

namespace AgentSpeed.Core.Hermes;

/// <summary>What one check of a profile's <c>config.yaml</c> found (METRICS 4.15 Codex runtime).</summary>
internal enum ConfigCheck
{
    /// <summary>Missing, not a regular file, or larger than 1 MiB: the switch is off.</summary>
    Off,

    /// <summary>Read: the switch takes the value the file selects.</summary>
    Read,

    /// <summary>Unchanged since the last successful read: the switch keeps its value.</summary>
    Unchanged,

    /// <summary>Could not be read (no permission, sharing violation, I/O error): the switch keeps its value; the next check reads again.</summary>
    Failed,
}

/// <summary>
/// The keys of a profile's <c>config.yaml</c> (METRICS 4.15): <c>model.openai_runtime</c>, the Codex runtime switch of a log
/// set, and the configured <c>agent.reasoning_effort</c> and <c>agent.service_tier</c>. A tolerant line reader for the block
/// layout Hermes writes, not a YAML parser; no other key is read.
/// </summary>
internal static partial class HermesConfig
{
    public const int MaxBytes = 1 << 20;

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);

    /// <summary>Whether config.yaml text selects the Codex runtime: the last <c>openai_runtime</c> key line of a top-level <c>model</c> block decides.</summary>
    public static bool SelectsCodexRuntime(string text)
    {
        if (text.StartsWith('\uFEFF'))
        {
            text = text[1..];
        }

        bool codex = false, inModel = false;
        int? indent = null;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.EndsWith('\r') ? raw[..^1] : raw;
            var rest = line.AsSpan().TrimStart(" \t");
            if (rest.IsEmpty || rest[0] == '#')
            {
                continue;
            }

            var body = line.AsSpan().TrimStart(' ');
            var depth = line.Length - body.Length;
            if (depth == 0)
            {
                inModel = ModelHeader().IsMatch(line);
                indent = null;
                continue;
            }

            if (!inModel)
            {
                continue;
            }

            indent ??= depth;
            if (depth == indent && RuntimeKey().Match(body.ToString()) is { Success: true } key)
            {
                codex = CodexValue().IsMatch(key.Groups[1].Value);
            }
        }

        return codex;
    }

    /// <summary>
    /// The scalars <c>reasoning_effort</c> and <c>service_tier</c> of a top-level <c>agent</c> block, read like the Codex
    /// runtime key: the last key line of each decides, and a value that is no plain scalar is null.
    /// </summary>
    public static IReadOnlyDictionary<string, string?> AgentScalars(string text)
    {
        if (text.StartsWith('\uFEFF'))
        {
            text = text[1..];
        }

        var found = new Dictionary<string, string?>(StringComparer.Ordinal);
        var inAgent = false;
        int? indent = null;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.EndsWith('\r') ? raw[..^1] : raw;
            var rest = line.AsSpan().TrimStart(" \t");
            if (rest.IsEmpty || rest[0] == '#')
            {
                continue;
            }

            var body = line.AsSpan().TrimStart(' ');
            var depth = line.Length - body.Length;
            if (depth == 0)
            {
                inAgent = AgentHeader().IsMatch(line);
                indent = null;
                continue;
            }

            if (!inAgent)
            {
                continue;
            }

            indent ??= depth;
            if (depth == indent && AgentKey().Match(body.ToString()) is { Success: true } key)
            {
                var value = Scalar().Match(key.Groups["rest"].Value);
                found[key.Groups["key"].Value] = !value.Success ? null
                    : value.Groups[1].Success ? value.Groups[1].Value : value.Groups[2].Success ? value.Groups[2].Value : value.Groups[3].Value;
            }
        }

        return found;
    }

    /// <summary>The configured effort and tier of config.yaml text (EXECUTION-MODES, Configured values).</summary>
    public static ExecutionModes ConfiguredModes(string text)
    {
        var scalars = AgentScalars(text);
        return ExecutionModeNative.HermesEffort(scalars.GetValueOrDefault("reasoning_effort"), ExecutionModeEvidence.Configured, ExecutionModeScope.Configuration)
            .Merge(ExecutionModeNative.HermesServiceTier(scalars.GetValueOrDefault("service_tier"), ExecutionModeEvidence.Configured, ExecutionModeScope.Configuration));
    }

    /// <summary>
    /// Checks the file: when it is a regular file (a link is followed) whose modification time or size differs from
    /// <paramref name="known"/>, reads it with read, write and delete sharing and closes it right away.
    /// </summary>
    /// <param name="known">The (modification ticks, size) of the last successful read; null before the first.</param>
    public static ConfigCheck Check(string? path, (long Ticks, long Size)? known, out (long Ticks, long Size)? signature, out bool codex, out ExecutionModes configured)
    {
        signature = null;
        codex = false;
        configured = ExecutionModes.Empty;
        if (path is null)
        {
            return ConfigCheck.Off;
        }

        try
        {
            var info = new FileInfo(path);
            var target = info.LinkTarget is null ? info : info.ResolveLinkTarget(returnFinalTarget: true);
            if (target is not FileInfo { Exists: true } file || (file.Attributes & FileAttributes.Directory) != 0)
            {
                return ConfigCheck.Off;
            }

            signature = (file.LastWriteTimeUtc.Ticks, file.Length);
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            return ConfigCheck.Off;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException
            or System.Security.SecurityException)
        {
            return ConfigCheck.Failed;
        }

        if (signature == known)
        {
            return ConfigCheck.Unchanged;
        }

        if (signature.Value.Size > MaxBytes)
        {
            return ConfigCheck.Read;
        }

        byte[] data;
        int length;
        try
        {
            using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            data = new byte[MaxBytes + 1];
            length = 0;
            while (length < data.Length)
            {
                var read = RandomAccess.Read(handle, data.AsSpan(length), length);
                if (read <= 0)
                {
                    break;
                }

                length += read;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            signature = null;
            return ConfigCheck.Failed;
        }

        if (length <= MaxBytes)
        {
            var text = Utf8.GetString(data, 0, length);
            codex = SelectsCodexRuntime(text);
            configured = ConfiguredModes(text);
        }

        return ConfigCheck.Read;
    }

    [GeneratedRegex("^(?:model|\"model\"|'model')[ \\t]*:(?:[ \\t]+(?:#.*)?)?\\z", RegexOptions.CultureInvariant)]
    private static partial Regex ModelHeader();

    [GeneratedRegex("^(?:openai_runtime|\"openai_runtime\"|'openai_runtime')[ \\t]*:(|[ \\t].*)\\z", RegexOptions.CultureInvariant)]
    private static partial Regex RuntimeKey();

    [GeneratedRegex("^(?:agent|\"agent\"|'agent')[ \\t]*:(?:[ \\t]+(?:#.*)?)?\\z", RegexOptions.CultureInvariant)]
    private static partial Regex AgentHeader();

    [GeneratedRegex(
        "^(?:(?<key>reasoning_effort|service_tier)|\"(?<key>reasoning_effort|service_tier)\"|'(?<key>reasoning_effort|service_tier)')[ \\t]*:(?<rest>|[ \\t].*)\\z",
        RegexOptions.CultureInvariant)]
    private static partial Regex AgentKey();

    [GeneratedRegex(
        "^[ \\t]+(?:\"[ \\t]*([A-Za-z0-9._:-]+)[ \\t]*\"|'[ \\t]*([A-Za-z0-9._:-]+)[ \\t]*'|([A-Za-z0-9._:-]+))(?:[ \\t]+#.*)?[ \\t]*\\z",
        RegexOptions.CultureInvariant)]
    private static partial Regex Scalar();

    // ASCII case-insensitive, as Python's re.I | re.A (no Kelvin sign for k).
    [GeneratedRegex(
        "^[ \\t]+(?:\"[ \\t]*" + Value + "[ \\t]*\"|'[ \\t]*" + Value + "[ \\t]*'|" + Value + ")(?:[ \\t]+#.*)?[ \\t]*\\z",
        RegexOptions.CultureInvariant)]
    private static partial Regex CodexValue();

    private const string Value = "[cC][oO][dD][eE][xX]_[aA][pP][pP]_[sS][eE][rR][vV][eE][rR]";
}
