using System.Collections.Immutable;
using AgentSpeed.Core;

namespace AgentSpeed.Presentation.Settings.Orbs;

/// <summary>
/// The sources an orb tracks (DESIGN 2b <c>sources</c>): every enabled source, including ones detected or added later,
/// or an explicit list. Keys this version does not know are kept for the round trip and otherwise ignored; a list
/// without a known key behaves like all sources. <c>default</c> is all sources.
/// </summary>
public readonly record struct OrbSources
{
    public const int MaxUnknownKeys = 16;
    public const int MaxKeyLength = 64;
    public const int MaxKeysRead = 64;

    private static readonly string[] Keys =
        ["codex", "claudeCode", "claudeDesktop", "deepseek", "opencode", "gemini", "omp", "antigravity", "workbuddy", "workbuddyAi", "zcode", "kimi", "grokBuild", "pi", "openclaw", "hermes", "droid", "trae"];

    private readonly bool _explicit;
    private readonly SourceSet _known;
    private readonly ImmutableArray<string> _unknown;

    private OrbSources(SourceSet known, ImmutableArray<string> unknown)
    {
        _explicit = true;
        _known = known;
        _unknown = unknown.IsDefault ? [] : unknown;
    }

    public static OrbSources All => default;

    /// <summary>True for the persisted <c>"all"</c>.</summary>
    public bool IsAll => !_explicit;

    /// <summary>The known sources of an explicit list, in source order; empty for all sources.</summary>
    public SourceSet Known => _known;

    /// <summary>Keys from a newer version, in their original order (kept, ignored).</summary>
    public ImmutableArray<string> UnknownKeys => _unknown.IsDefault ? [] : _unknown;

    /// <summary>All sources, or an explicit list that names no source this version knows.</summary>
    public bool TracksAll => !_explicit || _known.IsEmpty;

    /// <summary>The engine's source set: null tracks every enabled source.</summary>
    public SourceSet? Set => TracksAll ? null : _known;

    /// <summary>An explicit set in source order; an empty one means all sources.</summary>
    public static OrbSources Of(SourceSet sources) => sources.IsEmpty ? All : new OrbSources(sources, []);

    public static OrbSources Of(SourceKind source) => Of(SourceSet.Of(source));

    /// <summary>From a nullable engine set (null = all).</summary>
    public static OrbSources From(SourceSet? sources) => sources is { } set ? Of(set) : All;

    /// <summary>
    /// Known keys in source order, then unknown ones (bounded, without duplicates); no usable key at all means all
    /// sources.
    /// </summary>
    public static OrbSources FromKeys(IEnumerable<string?> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        var known = SourceSet.Empty;
        var unknown = ImmutableArray.CreateBuilder<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var key in keys.Take(MaxKeysRead))
        {
            if (TryParseKey(key, out var kind))
            {
                known = known.With(kind);
            }
            else if (IsKeepableKey(key) && unknown.Count < MaxUnknownKeys && seen.Add(key!))
            {
                unknown.Add(key!);
            }
        }

        return known.IsEmpty && unknown.Count == 0 ? All : new OrbSources(known, unknown.ToImmutable());
    }

    /// <summary>The persisted key of a source (METRICS 1).</summary>
    public static string KeyOf(SourceKind kind) => (uint)kind < (uint)Keys.Length
        ? Keys[(int)kind]
        : throw new ArgumentOutOfRangeException(nameof(kind), kind, null);

    public static bool TryParseKey(string? key, out SourceKind kind)
    {
        var index = key is null ? -1 : Array.IndexOf(Keys, key);
        kind = index >= 0 ? (SourceKind)index : default;
        return index >= 0;
    }

    /// <summary>Known keys in source order, then the unknown ones; null for <c>"all"</c>.</summary>
    public IReadOnlyList<string>? ToKeys() =>
        IsAll ? null : [.. _known.Select(KeyOf), .. UnknownKeys];

    public bool Tracks(SourceKind kind) => TracksAll || _known.Contains(kind);

    /// <summary>The sources the orb reads, given every enabled (or available) source.</summary>
    public SourceSet Resolve(SourceSet enabled) => TracksAll ? enabled : _known.Intersect(enabled);

    /// <summary>Checking a source (DESIGN 2b menu item 3): on an all-sources orb the set becomes that one source.</summary>
    public OrbSources Checking(SourceKind kind) =>
        TracksAll ? new OrbSources(SourceSet.Of(kind), UnknownKeys) : new OrbSources(_known.With(kind), UnknownKeys);

    /// <summary>Unchecking a source; null when that is refused (the last tracked source, or an all-sources orb).</summary>
    public OrbSources? Unchecking(SourceKind kind)
    {
        if (TracksAll)
        {
            return null;
        }

        if (!_known.Contains(kind))
        {
            return this;
        }

        var remaining = _known.Without(kind);
        return remaining.IsEmpty ? null : new OrbSources(remaining, UnknownKeys);
    }

    /// <summary>A click on a source row: checks it, or unchecks a tracked one (null when refused).</summary>
    public OrbSources? Toggling(SourceKind kind) => !TracksAll && _known.Contains(kind) ? Unchecking(kind) : Checking(kind);

    public bool Equals(OrbSources other) =>
        _explicit == other._explicit && _known == other._known && UnknownKeys.SequenceEqual(other.UnknownKeys, StringComparer.Ordinal);

    public override int GetHashCode()
    {
        var hash = HashCode.Combine(_explicit, _known);
        foreach (var key in UnknownKeys)
        {
            hash = HashCode.Combine(hash, StringComparer.Ordinal.GetHashCode(key));
        }

        return hash;
    }

    public override string ToString() => IsAll ? "all" : "[" + string.Join(", ", ToKeys()!) + "]";

    private static bool IsKeepableKey(string? key) =>
        !string.IsNullOrWhiteSpace(key) && key.Length <= MaxKeyLength && key != "all" && !key.Any(char.IsControl);
}
