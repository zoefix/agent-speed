using System.Text.Json;
using AgentSpeed.Core.Antigravity;
using AgentSpeed.Core.Harnesses;
using AgentSpeed.Core.WorkBuddy;

namespace AgentSpeed.Core;

/// <summary>
/// What each source's config file says (EXECUTION-MODES, Current values, Configured values): shown as configured and nothing
/// else. Files are checked when first asked for and at every rediscovery, and read again only when their size or time
/// changed; only the named key is kept. Owned by the monitor thread.
/// </summary>
internal sealed class ConfiguredValues
{
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private readonly Dictionary<string, Entry> _files = new(PathComparer);

    /// <summary>Changes whenever a value changed.</summary>
    public int Version { get; private set; }

    /// <param name="root">The folder the agent's transcript was found under.</param>
    public ExecutionModes Of(SourceKind source, string root, string agentId, string? model) => source switch
    {
        SourceKind.Codex => Get(CodexConfiguredModes.ConfigPath(root), Codex).Modes,
        SourceKind.Gemini => Get(GeminiSettingsPath(root), Gemini).ByModel.GetValueOrDefault(model ?? string.Empty) ?? ExecutionModes.Empty,
        SourceKind.Antigravity when AntigravityConfigPath(agentId) is { } path => Get(path, Antigravity).Modes,
        SourceKind.WorkBuddy or SourceKind.WorkBuddyAi => Get(WorkBuddySettingsPath(root), WorkBuddy).Modes,
        _ => ExecutionModes.Empty,
    };

    /// <summary>Checks every file asked for so far; true when a value changed.</summary>
    public bool Refresh()
    {
        var changed = false;
        foreach (var (path, entry) in _files)
        {
            changed |= entry.Check(path);
        }

        if (changed)
        {
            Version++;
        }

        return changed;
    }

    /// <summary><c>&lt;home&gt;/.gemini/settings.json</c> for a <c>tmp</c> root; the sandbox's <c>.cache/.gemini/tmp</c> shares it.</summary>
    public static string GeminiSettingsPath(string root)
    {
        var folder = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(root)) ?? string.Empty;
        var parent = Path.GetDirectoryName(folder);
        return Path.Combine(Path.GetFileName(parent) == ".cache" ? Path.Combine(Path.GetDirectoryName(parent) ?? string.Empty, ".gemini") : folder, "settings.json");
    }

    /// <summary><c>config/config.json</c> in the folder that holds the store of a conversation database.</summary>
    public static string? AntigravityConfigPath(string databasePath)
    {
        var conversations = Path.GetDirectoryName(databasePath);
        var store = string.IsNullOrEmpty(conversations) ? null : Path.GetDirectoryName(conversations);
        var folder = string.IsNullOrEmpty(store) ? null : Path.GetDirectoryName(store);
        return string.IsNullOrEmpty(folder) ? null : Path.Combine(folder, "config", "config.json");
    }

    public static string WorkBuddySettingsPath(string root) => Path.Combine(WorkBuddyPaths.DataFolderOf(root), "settings.json");

    /// <summary>Each model's value: of <c>overrides</c> and then <c>customOverrides</c>, the last entry for exactly that model that names a level.</summary>
    internal static ConfigValue Gemini(string path)
    {
        using var document = ConfigFile.Json(path);
        var chosen = new Dictionary<string, string>(StringComparer.Ordinal);
        var configs = document is null ? default : HarnessJson.Get(document.RootElement, "modelConfigs");
        foreach (var name in (ReadOnlySpan<string>)["overrides", "customOverrides"])
        {
            if (HarnessJson.Get(configs, name) is not { ValueKind: JsonValueKind.Array } entries)
            {
                continue;
            }

            foreach (var entry in entries.EnumerateArray())
            {
                if (OnlyModel(HarnessJson.Get(entry, "match")) is not { } model)
                {
                    continue;
                }

                var thinking = HarnessJson.Get(HarnessJson.Get(HarnessJson.Get(entry, "modelConfig"), "generateContentConfig"), "thinkingConfig");
                var budget = HarnessJson.Get(thinking, "thinkingBudget");
                if (HarnessJson.Get(thinking, "thinkingLevel") is { ValueKind: JsonValueKind.String } level)
                {
                    chosen[model] = level.GetString()!;
                }
                else if (budget.ValueKind == JsonValueKind.Number && budget.TryGetDouble(out var value) && value == 0)
                {
                    chosen[model] = "off";
                }
            }
        }

        return new ConfigValue(ExecutionModes.Empty, chosen.ToDictionary(p => p.Key,
            p => ExecutionModes.Of(ExecutionModeNative.Effort(p.Value, ExecutionModeEvidence.Configured, ExecutionModeScope.Configuration)), StringComparer.Ordinal));
    }

    internal static ConfigValue Antigravity(string path)
    {
        using var document = ConfigFile.Json(path);
        var tier = document is null ? null : HarnessJson.String(HarnessJson.Get(document.RootElement, "userSettings"), "vertexServiceTier");
        return new ConfigValue(ExecutionModeNative.GoogleTier(tier, ExecutionModeEvidence.Configured, ExecutionModeScope.Configuration));
    }

    internal static ConfigValue WorkBuddy(string path)
    {
        using var document = ConfigFile.Json(path);
        var effort = document is null ? null : HarnessJson.String(document.RootElement, "reasoningEffort");
        return new ConfigValue(ExecutionModes.Of(ExecutionModeNative.Effort(effort, ExecutionModeEvidence.Configured, ExecutionModeScope.Configuration)));
    }

    private static ConfigValue Codex(string path) => new(CodexConfiguredModes.Parse(ConfigFile.Text(path)));

    // The one member `model` of an object, as a string: a match with any other condition does not count.
    private static string? OnlyModel(JsonElement match)
    {
        if (match.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        string? model = null;
        var members = 0;
        foreach (var member in match.EnumerateObject())
        {
            members++;
            model = member.Name == "model" && member.Value.ValueKind == JsonValueKind.String ? member.Value.GetString() : null;
        }

        return members == 1 ? model : null;
    }

    private ConfigValue Get(string path, Func<string, ConfigValue> read)
    {
        if (!_files.TryGetValue(path, out var entry))
        {
            entry = new Entry(read);
            _files[path] = entry;
            entry.Check(path);
        }

        return entry.Value;
    }

    /// <summary>What one file holds: its value, or one per model for Gemini.</summary>
    internal sealed record ConfigValue(ExecutionModes Modes, IReadOnlyDictionary<string, ExecutionModes> ByModel)
    {
        public static ConfigValue None { get; } = new(ExecutionModes.Empty);

        public ConfigValue(ExecutionModes modes)
            : this(modes, new Dictionary<string, ExecutionModes>())
        {
        }

        public bool SameAs(ConfigValue other) => Modes.Equals(other.Modes) && ByModel.Count == other.ByModel.Count
            && ByModel.All(p => other.ByModel.TryGetValue(p.Key, out var modes) && modes.Equals(p.Value));
    }

    private sealed class Entry(Func<string, ConfigValue> read)
    {
        private (long Length, DateTime WriteUtc)? _stamp;

        public ConfigValue Value { get; private set; } = ConfigValue.None;

        public bool Check(string path)
        {
            var stamp = ConfigFile.Stamp(path);
            if (stamp == _stamp)
            {
                return false;
            }

            _stamp = stamp;
            var value = stamp is null ? ConfigValue.None : read(path);
            var changed = !value.SameAs(Value);
            Value = value;
            return changed;
        }
    }
}
