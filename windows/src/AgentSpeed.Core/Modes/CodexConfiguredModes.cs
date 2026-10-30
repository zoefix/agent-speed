using System.Text.RegularExpressions;

namespace AgentSpeed.Core;

/// <summary>The declared root <c>service_tier</c> of the Codex home's <c>config.toml</c>: a configured value, never a running task's.</summary>
public static partial class CodexConfiguredModes
{
    /// <summary><c>config.toml</c> of the Codex home, for the home or its <c>sessions</c> folder.</summary>
    public static string ConfigPath(string root)
    {
        root = Path.TrimEndingDirectorySeparator(root);
        return Path.Combine(Path.GetFileName(root) == "sessions" ? Path.GetDirectoryName(root) ?? string.Empty : root, "config.toml");
    }

    public static ExecutionModes Read(string? root) => string.IsNullOrEmpty(root) ? ExecutionModes.Empty : Parse(ConfigFile.Text(ConfigPath(root)));

    internal static ExecutionModes Parse(string? config)
    {
        if (config is null) return ExecutionModes.Empty;
        var modes = ExecutionModes.Empty;
        var found = false;
        using var lines = new StringReader(config);
        while (lines.ReadLine() is { } line)
        {
            var text = line.Trim();
            if (text.StartsWith('#')) continue;
            if (text.Contains("\"\"\"", StringComparison.Ordinal) || text.Contains("'''", StringComparison.Ordinal)) return ExecutionModes.Empty;
            if (text.StartsWith('[')) break;
            var match = TierLine().Match(text);
            if (match.Success)
            {
                if (found) return ExecutionModes.Empty;
                modes = ExecutionModeNative.OpenAiTier(match.Groups[2].Value,
                    ExecutionModeEvidence.Configured, ExecutionModeScope.Configuration);
                found = true;
            }
            else if (text.StartsWith("service_tier", StringComparison.Ordinal)
                && (text.Length == 12 || text[12] is '=' or ' ' or '\t')) return ExecutionModes.Empty;
        }
        return modes;
    }

    [GeneratedRegex("^service_tier\\s*=\\s*([\"'])([A-Za-z0-9][A-Za-z0-9._:-]{0,63})\\1\\s*(?:#.*)?$")]
    private static partial Regex TierLine();
}
