using System.Collections.Immutable;

namespace AgentSpeed.Presentation.Settings.Orbs;

/// <summary>The positions of the orbs in one display arrangement (orb id → record).</summary>
public sealed record OrbLayout
{
    private readonly ImmutableDictionary<string, OrbPositionRecord> _positions = Empty;

    public OrbLayout(string arrangement, ImmutableDictionary<string, OrbPositionRecord>? positions = null)
    {
        Arrangement = arrangement;
        Positions = positions ?? Empty;
    }

    private static ImmutableDictionary<string, OrbPositionRecord> Empty { get; } =
        ImmutableDictionary.Create<string, OrbPositionRecord>(StringComparer.Ordinal);

    /// <summary>Sorted display keys joined with <c>+</c> (<see cref="OrbsDocument.ArrangementKey"/>).</summary>
    public string Arrangement { get; init; }

    public ImmutableDictionary<string, OrbPositionRecord> Positions
    {
        get => _positions;
        init => _positions = value is null ? Empty : value.WithComparers(StringComparer.Ordinal);
    }

    public bool Equals(OrbLayout? other)
    {
        if (ReferenceEquals(this, other))
        {
            return true;
        }

        if (other is null || !string.Equals(Arrangement, other.Arrangement, StringComparison.Ordinal)
            || Positions.Count != other.Positions.Count)
        {
            return false;
        }

        foreach (var (id, record) in Positions)
        {
            if (!other.Positions.TryGetValue(id, out var theirs) || theirs != record)
            {
                return false;
            }
        }

        return true;
    }

    public override int GetHashCode() => HashCode.Combine(StringComparer.Ordinal.GetHashCode(Arrangement), Positions.Count);
}

/// <summary>
/// Every floating orb and where each one sits (DESIGN 2b, persisted as the settings value <c>orbs</c>). Immutable with
/// value equality; <see cref="Layouts"/> is ordered most recently used first and holds at most 16 arrangements.
/// </summary>
public sealed record OrbsDocument
{
    public const int CurrentVersion = 1;
    public const int MaxOrbs = 8;
    public const int MaxArrangements = 16;
    public const int MaxArrangementKeyLength = 1024;

    private readonly ImmutableArray<OrbDefinition> _orbs = [];
    private readonly ImmutableArray<OrbLayout> _layouts = [];

    public OrbsDocument(IEnumerable<OrbDefinition> orbs, IEnumerable<OrbLayout>? layouts = null)
    {
        Orbs = [.. orbs];
        Layouts = layouts is null ? [] : [.. layouts];
    }

    /// <summary>List order (Settings rows, overlap resolution).</summary>
    public ImmutableArray<OrbDefinition> Orbs
    {
        get => _orbs;
        init => _orbs = value.IsDefault ? [] : value;
    }

    /// <summary>Most recently used arrangement first.</summary>
    public ImmutableArray<OrbLayout> Layouts
    {
        get => _layouts;
        init => _layouts = value.IsDefault ? [] : value;
    }

    public int Count => Orbs.Length;

    /// <summary>Add, Duplicate and Split are possible (fewer than eight orbs).</summary>
    public bool CanAdd => Orbs.Length < MaxOrbs;

    /// <summary>The last orb cannot be removed (the master switch hides it).</summary>
    public bool CanRemove => Orbs.Length > 1;

    public bool CanMerge => Orbs.Length > 1;

    /// <summary>Every orb is individually hidden.</summary>
    public bool AllHidden => Orbs.Length > 0 && Orbs.All(o => !o.Visible);

    /// <summary>
    /// The document of <see cref="AppSettings.Default"/>: one fresh orb whose id is drawn once per process, so default
    /// settings compare equal.
    /// </summary>
    internal static OrbsDocument ProcessDefault { get; } = Fresh();

    /// <summary>A fresh install: one orb tracking all sources, <c>led</c>, 148 pt, at the default position.</summary>
    public static OrbsDocument Fresh(string? id = null) => new([new OrbDefinition(id ?? OrbDefinition.NewId())]);

    /// <summary>The key of a display arrangement: the connected display keys, sorted, joined with <c>+</c>.</summary>
    public static string ArrangementKey(IEnumerable<string> displayKeys)
    {
        ArgumentNullException.ThrowIfNull(displayKeys);
        return string.Join('+', displayKeys
            .Where(k => !string.IsNullOrEmpty(k))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ThenBy(k => k, StringComparer.Ordinal));
    }

    public OrbDefinition? Find(string? id) => id is null ? null : Orbs.FirstOrDefault(o => string.Equals(o.Id, id, StringComparison.Ordinal));

    public int IndexOf(string? id)
    {
        for (var i = 0; i < Orbs.Length; i++)
        {
            if (string.Equals(Orbs[i].Id, id, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }

    public OrbLayout? LayoutFor(string arrangement) =>
        Layouts.FirstOrDefault(l => string.Equals(l.Arrangement, arrangement, StringComparison.OrdinalIgnoreCase));

    public OrbPositionRecord? RecordFor(string arrangement, string orbId) =>
        LayoutFor(arrangement)?.Positions.TryGetValue(orbId, out var record) == true ? record : null;

    /// <summary>Replaces the orb with the same id; unknown ids leave the document unchanged.</summary>
    public OrbsDocument Replace(OrbDefinition orb)
    {
        ArgumentNullException.ThrowIfNull(orb);
        var index = IndexOf(orb.Id);
        return index < 0 ? this : this with { Orbs = Orbs.SetItem(index, orb) };
    }

    /// <summary>New list of orbs; positions of orbs that are gone are dropped.</summary>
    public OrbsDocument WithOrbs(IEnumerable<OrbDefinition> orbs)
    {
        ImmutableArray<OrbDefinition> list = [.. orbs];
        var ids = list.Select(o => o.Id).ToHashSet(StringComparer.Ordinal);
        var layouts = Layouts
            .Select(l => l with { Positions = l.Positions.RemoveRange(l.Positions.Keys.Where(k => !ids.Contains(k)).ToArray()) })
            .Where(l => l.Positions.Count > 0);
        return this with { Orbs = list, Layouts = [.. layouts] };
    }

    /// <summary>Stores a position in an arrangement and marks it most recently used (dropping beyond 16).</summary>
    public OrbsDocument WithPosition(string arrangement, string orbId, OrbPositionRecord record) =>
        WithPositions(arrangement, [new KeyValuePair<string, OrbPositionRecord>(orbId, record)]);

    public OrbsDocument WithPositions(string arrangement, IEnumerable<KeyValuePair<string, OrbPositionRecord>> records)
    {
        ArgumentException.ThrowIfNullOrEmpty(arrangement);
        ArgumentNullException.ThrowIfNull(records);
        var existing = LayoutFor(arrangement);
        var positions = existing?.Positions ?? ImmutableDictionary.Create<string, OrbPositionRecord>(StringComparer.Ordinal);
        foreach (var (id, record) in records)
        {
            if (Find(id) is not null)
            {
                positions = positions.SetItem(id, record);
            }
        }

        if (positions.Count == 0)
        {
            return this;
        }

        var layout = new OrbLayout(existing?.Arrangement ?? arrangement, positions);
        return this with { Layouts = Promote(layout) };
    }

    /// <summary>Forgets an orb's position in one arrangement.</summary>
    public OrbsDocument WithoutPosition(string arrangement, string orbId)
    {
        var existing = LayoutFor(arrangement);
        if (existing is null || !existing.Positions.ContainsKey(orbId))
        {
            return this;
        }

        var positions = existing.Positions.Remove(orbId);
        var layouts = positions.Count == 0
            ? Layouts.Remove(existing)
            : Layouts.Replace(existing, existing with { Positions = positions });
        return this with { Layouts = layouts };
    }

    /// <summary>Marks an arrangement as the most recently used one.</summary>
    public OrbsDocument Touch(string arrangement) =>
        LayoutFor(arrangement) is { } layout && !ReferenceEquals(Layouts[0], layout) ? this with { Layouts = Promote(layout) } : this;

    /// <summary>
    /// Every value in range: at most eight orbs, invalid or duplicate ids replaced by fresh UUIDs, at most 16
    /// arrangements of valid records for existing orbs.
    /// </summary>
    public OrbsDocument Validated()
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var orbs = ImmutableArray.CreateBuilder<OrbDefinition>();
        foreach (var orb in Orbs.Take(MaxOrbs))
        {
            if (orb is null)
            {
                continue;
            }

            var clean = orb.Validated();
            while (!ids.Add(clean.Id))
            {
                clean = clean with { Id = OrbDefinition.NewId() };
            }

            orbs.Add(clean);
        }

        var layouts = ImmutableArray.CreateBuilder<OrbLayout>();
        var arrangements = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var layout in Layouts)
        {
            if (layouts.Count >= MaxArrangements)
            {
                break;
            }

            if (layout is null || !IsValidArrangement(layout.Arrangement) || !arrangements.Add(layout.Arrangement))
            {
                continue;
            }

            var positions = ImmutableDictionary.CreateBuilder<string, OrbPositionRecord>(StringComparer.Ordinal);
            foreach (var (id, record) in layout.Positions)
            {
                if (positions.Count < MaxOrbs && ids.Contains(id) && record.Validated() is { } valid)
                {
                    positions[id] = valid;
                }
            }

            if (positions.Count > 0)
            {
                layouts.Add(new OrbLayout(layout.Arrangement, positions.ToImmutable()));
            }
        }

        if (orbs.Count == 0)
        {
            orbs.Add(new OrbDefinition(OrbDefinition.NewId()));
        }

        return new OrbsDocument(orbs.ToImmutable(), layouts.ToImmutable());
    }

    public bool Equals(OrbsDocument? other) =>
        ReferenceEquals(this, other)
        || (other is not null && Orbs.SequenceEqual(other.Orbs) && Layouts.SequenceEqual(other.Layouts));

    public override int GetHashCode() => HashCode.Combine(Orbs.Length, Layouts.Length, Orbs.Length > 0 ? Orbs[0] : null);

    internal static bool IsValidArrangement(string? key) =>
        !string.IsNullOrWhiteSpace(key) && key.Length <= MaxArrangementKeyLength && !key.Any(char.IsControl);

    private ImmutableArray<OrbLayout> Promote(OrbLayout layout)
    {
        var rest = Layouts.Where(l => !string.Equals(l.Arrangement, layout.Arrangement, StringComparison.OrdinalIgnoreCase));
        return [.. new[] { layout }.Concat(rest).Take(MaxArrangements)];
    }
}
