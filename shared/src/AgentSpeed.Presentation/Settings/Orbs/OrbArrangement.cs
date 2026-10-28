using System.Collections.Immutable;
using AgentSpeed.Presentation.Layout;

namespace AgentSpeed.Presentation.Settings.Orbs;

/// <summary>A connected display: its key (Windows <c>DeviceName|WxH</c>), work area in physical pixels and scale (dpi / 96).</summary>
public readonly record struct OrbDisplay(string Key, RectD Work, double Scale, bool IsMain);

/// <summary>Where an orb's disc is: the display key, the center and the radius in physical pixels.</summary>
public readonly record struct OrbLocation(string Display, PointD Center, double Radius);

/// <summary>The updated document and the locations of the orbs an operation placed or moved.</summary>
public sealed record OrbLayoutResult(OrbsDocument Document, string Arrangement, ImmutableDictionary<string, OrbLocation> Locations);

/// <summary>
/// Positions of the orbs across display arrangements (DESIGN 2b, Arrangement changes) and the document side of the
/// placement rules. Pure; <c>centers</c> are the orbs' live disc centers in physical pixels (null or missing entries
/// fall back to <see cref="Resolve"/>). Hidden orbs are never obstacles.
/// </summary>
public static class OrbArrangement
{
    /// <summary>The arrangement key of the connected displays: sorted keys joined with <c>+</c>.</summary>
    public static string Key(IEnumerable<OrbDisplay> displays)
    {
        ArgumentNullException.ThrowIfNull(displays);
        return OrbsDocument.ArrangementKey(displays.Select(d => d.Key));
    }

    /// <summary>
    /// Every orb's location for the connected displays: its record in this arrangement; else its record from the most
    /// recently used arrangement whose display is still connected; else its latest record moved to the main display
    /// (anchors kept); else the default position. Overlaps are then resolved in list order (visible orbs first): a
    /// later orb slides to the nearest free touching position. The returned document has this arrangement's layout
    /// written for every orb with a record and marked most recently used.
    /// </summary>
    public static OrbLayoutResult Resolve(OrbsDocument document, IReadOnlyList<OrbDisplay> displays)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(displays);
        var connected = Connected(displays);
        if (connected.Count == 0)
        {
            return new OrbLayoutResult(document, string.Empty, ImmutableDictionary<string, OrbLocation>.Empty);
        }

        var arrangement = Key(connected.Values);
        var main = Main(displays, connected);
        var current = document.LayoutFor(arrangement);
        var placed = new Dictionary<string, List<OrbDisc>>(StringComparer.OrdinalIgnoreCase);
        var locations = ImmutableDictionary.CreateBuilder<string, OrbLocation>(StringComparer.Ordinal);
        var written = new List<KeyValuePair<string, OrbPositionRecord>>(document.Count);

        foreach (var orb in document.Orbs.OrderBy(o => o.Visible ? 0 : 1))
        {
            var (record, display) = Pick(document, current, orb.Id, connected, main);
            var radius = orb.RadiusPx(display.Scale);
            if (!placed.TryGetValue(display.Key, out var others))
            {
                placed[display.Key] = others = [];
            }

            PointD center;
            if (record is { } r)
            {
                var wanted = r.ToCenter(display.Work, display.Scale, radius);
                center = OrbPlacement.ResolveOverlap(wanted, radius, others, display.Work, display.Scale);
                written.Add(new(orb.Id, center == wanted
                    ? r.OnDisplay(display.Key)
                    : OrbPositionRecord.FromCenter(display.Key, center, radius, display.Work, display.Scale)));
            }
            else
            {
                center = OrbPlacement.ResetPosition(radius, others, display.Work, display.Scale);
            }

            if (orb.Visible)
            {
                others.Add(new OrbDisc(center, radius));
            }

            locations[orb.Id] = new OrbLocation(display.Key, center, radius);
        }

        var next = written.Count > 0 ? document.WithPositions(arrangement, written) : document.Touch(arrangement);
        return new OrbLayoutResult(next, arrangement, locations.ToImmutable());
    }

    /// <summary>Stores a moved orb's center (a finished drag, after the release spring) in the current arrangement.</summary>
    public static OrbsDocument Commit(OrbsDocument document, IReadOnlyList<OrbDisplay> displays, string orbId, PointD center)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(displays);
        var connected = Connected(displays);
        if (connected.Count == 0 || document.Find(orbId) is not { } orb || DisplayAt(displays, center) is not { } display)
        {
            return document;
        }

        var record = OrbPositionRecord.FromCenter(display.Key, center, orb.RadiusPx(display.Scale), display.Work, display.Scale);
        return document.WithPosition(Key(connected.Values), orbId, record);
    }

    /// <summary>
    /// Places a new orb (Add, Duplicate) next to <paramref name="fromId"/> on its display (else on the main display)
    /// and stores the position.
    /// </summary>
    public static OrbLayoutResult PlaceNew(
        OrbsDocument document, IReadOnlyList<OrbDisplay> displays, IReadOnlyDictionary<string, PointD>? centers, string newId, string? fromId)
    {
        ArgumentNullException.ThrowIfNull(document);
        var scene = BuildScene(document, displays, centers);
        if (scene is null || document.Find(newId) is not { } orb)
        {
            return Unchanged(document, displays);
        }

        OrbDisplay display;
        OrbDisc? anchor = null;
        if (fromId is not null && fromId != newId && scene.Locations.TryGetValue(fromId, out var from))
        {
            display = scene.Display(from.Display);
            anchor = new OrbDisc(from.Center, from.Radius);
        }
        else
        {
            display = scene.Main;
        }

        var radius = orb.RadiusPx(display.Scale);
        var center = OrbPlacement.PlaceNew(radius, anchor, scene.Others(display.Key, newId), display.Work, display.Scale);
        return Placed(document, scene, [(newId, display, center)]);
    }

    /// <summary>
    /// Lays out the children of a split (or of Split by source) from <paramref name="originId"/>'s position and stores
    /// their positions, pushing orbs in the way; the locations cover the children and every pushed orb.
    /// </summary>
    public static OrbLayoutResult PlaceSplit(
        OrbsDocument document, IReadOnlyList<OrbDisplay> displays, IReadOnlyDictionary<string, PointD>? centers, string originId,
        IReadOnlyList<string> childIds)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(childIds);
        var scene = BuildScene(document, displays, centers);
        if (scene is null || !scene.Locations.TryGetValue(originId, out var origin))
        {
            return Unchanged(document, displays);
        }

        var display = scene.Display(origin.Display);
        var children = childIds.Where(id => id != originId && document.Find(id) is not null).Distinct(StringComparer.Ordinal).ToArray();
        var radii = children.Select(id => document.Find(id)!.RadiusPx(display.Scale)).ToArray();
        var exclude = children.Append(originId).ToHashSet(StringComparer.Ordinal);
        var otherIds = scene.OtherIds(display.Key, exclude);
        var others = otherIds.Select(id => new OrbDisc(scene.Locations[id].Center, scene.Locations[id].Radius)).ToArray();
        var layout = OrbPlacement.SplitLayout(new OrbDisc(origin.Center, origin.Radius), radii, display.Work, display.Scale, others);

        var moves = new List<(string, OrbDisplay, PointD)>(children.Length + layout.Pushed.Count);
        for (var i = 0; i < children.Length; i++)
        {
            moves.Add((children[i], display, layout.Children[i]));
        }

        foreach (var (index, center) in layout.Pushed)
        {
            moves.Add((otherIds[index], display, center));
        }

        return Placed(document, scene, moves);
    }

    /// <summary><c>a11y.orb.moveCorner</c>: the next free corner of the orb's display, stored as its position.</summary>
    public static OrbLayoutResult MoveToNextCorner(
        OrbsDocument document, IReadOnlyList<OrbDisplay> displays, IReadOnlyDictionary<string, PointD>? centers, string orbId)
    {
        ArgumentNullException.ThrowIfNull(document);
        var scene = BuildScene(document, displays, centers);
        if (scene is null || !scene.Locations.TryGetValue(orbId, out var location))
        {
            return Unchanged(document, displays);
        }

        var display = scene.Display(location.Display);
        var others = scene.Others(display.Key, orbId);
        var center = OrbPlacement.NextCorner(new OrbDisc(location.Center, location.Radius), others, display.Work, display.Scale);
        return Placed(document, scene, [(orbId, display, center)]);
    }

    /// <summary>
    /// Reset Position: the default corner of the main display, or the new-orb placement next to the orb that sits
    /// there; stored as the orb's position.
    /// </summary>
    public static OrbLayoutResult ResetPosition(
        OrbsDocument document, IReadOnlyList<OrbDisplay> displays, IReadOnlyDictionary<string, PointD>? centers, string orbId)
    {
        ArgumentNullException.ThrowIfNull(document);
        var scene = BuildScene(document, displays, centers);
        if (scene is null || document.Find(orbId) is not { } orb)
        {
            return Unchanged(document, displays);
        }

        var display = scene.Main;
        var radius = orb.RadiusPx(display.Scale);
        var center = OrbPlacement.ResetPosition(radius, scene.Others(display.Key, orbId), display.Work, display.Scale);
        return Placed(document, scene, [(orbId, display, center)]);
    }

    /// <summary>
    /// The drag target for the pointer position (orb-to-orb pull and alignment guides against the visible orbs on the
    /// display under it). Null for an unknown orb or no display.
    /// </summary>
    public static OrbDragResult? Drag(
        OrbsDocument document, IReadOnlyList<OrbDisplay> displays, IReadOnlyDictionary<string, PointD>? centers, string orbId, PointD raw)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (BuildScene(document, displays, centers) is not { } scene || document.Find(orbId) is not { } orb
            || DisplayAt(displays, raw) is not { } display)
        {
            return null;
        }

        return OrbMagnet.Drag(raw, orb.RadiusPx(display.Scale), scene.Others(display.Key, orbId), display.Work, display.Scale);
    }

    /// <summary>
    /// Where a released orb springs to (edge snap, touching, alignment, overlap sliding). Not stored: call
    /// <see cref="Commit"/> when the spring settles.
    /// </summary>
    public static OrbLocation? Release(
        OrbsDocument document, IReadOnlyList<OrbDisplay> displays, IReadOnlyDictionary<string, PointD>? centers, string orbId, PointD raw)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (BuildScene(document, displays, centers) is not { } scene || document.Find(orbId) is not { } orb
            || DisplayAt(displays, raw) is not { } display)
        {
            return null;
        }

        var radius = orb.RadiusPx(display.Scale);
        var center = OrbMagnet.Release(raw, radius, scene.Others(display.Key, orbId), display.Work, display.Scale);
        return new OrbLocation(display.Key, center, radius);
    }

    /// <summary>The display whose work area contains the point, else the nearest one.</summary>
    public static OrbDisplay? DisplayAt(IReadOnlyList<OrbDisplay> displays, PointD point)
    {
        ArgumentNullException.ThrowIfNull(displays);
        var usable = displays.Where(d => !string.IsNullOrEmpty(d.Key)).ToArray();
        var index = PlacementMath.NearestIndex(point, usable.Select(d => d.Work).ToArray());
        return index < 0 ? null : usable[index] with { Scale = OrbGeometry.ValidScale(usable[index].Scale) };
    }

    private static (OrbPositionRecord? Record, OrbDisplay Display) Pick(
        OrbsDocument document, OrbLayout? current, string id, Dictionary<string, OrbDisplay> connected, OrbDisplay main)
    {
        if (current is not null && current.Positions.TryGetValue(id, out var own) && connected.TryGetValue(own.Display, out var ownDisplay))
        {
            return (own, ownDisplay);
        }

        OrbPositionRecord? latest = null;
        foreach (var layout in document.Layouts)
        {
            if (ReferenceEquals(layout, current) || !layout.Positions.TryGetValue(id, out var record))
            {
                continue;
            }

            if (connected.TryGetValue(record.Display, out var display))
            {
                return (record, display);
            }

            latest ??= record;
        }

        if (latest is null && current is not null && current.Positions.TryGetValue(id, out var stale))
        {
            latest = stale;
        }

        return latest is { } moved ? (moved.OnDisplay(main.Key), main) : (null, main);
    }

    private static Dictionary<string, OrbDisplay> Connected(IReadOnlyList<OrbDisplay> displays)
    {
        var map = new Dictionary<string, OrbDisplay>(StringComparer.OrdinalIgnoreCase);
        foreach (var display in displays)
        {
            if (!string.IsNullOrEmpty(display.Key) && display.Key.Length <= OrbPositionRecord.MaxDisplayKeyLength)
            {
                map.TryAdd(display.Key, display with { Scale = OrbGeometry.ValidScale(display.Scale) });
            }
        }

        return map;
    }

    private static OrbDisplay Main(IReadOnlyList<OrbDisplay> displays, Dictionary<string, OrbDisplay> connected)
    {
        foreach (var display in displays)
        {
            if (display.IsMain && connected.TryGetValue(display.Key, out var main))
            {
                return main;
            }
        }

        return connected[displays.First(d => connected.ContainsKey(d.Key ?? string.Empty)).Key];
    }

    private static OrbLayoutResult Unchanged(OrbsDocument document, IReadOnlyList<OrbDisplay>? displays) =>
        new(document, displays is null ? string.Empty : Key(Connected(displays).Values), ImmutableDictionary<string, OrbLocation>.Empty);

    private static OrbLayoutResult Placed(OrbsDocument document, Scene scene, IReadOnlyList<(string Id, OrbDisplay Display, PointD Center)> moves)
    {
        var records = new List<KeyValuePair<string, OrbPositionRecord>>(moves.Count);
        var locations = ImmutableDictionary.CreateBuilder<string, OrbLocation>(StringComparer.Ordinal);
        foreach (var (id, display, center) in moves)
        {
            var radius = document.Find(id)!.RadiusPx(display.Scale);
            records.Add(new(id, OrbPositionRecord.FromCenter(display.Key, center, radius, display.Work, display.Scale)));
            locations[id] = new OrbLocation(display.Key, center, radius);
        }

        return new OrbLayoutResult(document.WithPositions(scene.Arrangement, records), scene.Arrangement, locations.ToImmutable());
    }

    private static Scene? BuildScene(OrbsDocument document, IReadOnlyList<OrbDisplay> displays, IReadOnlyDictionary<string, PointD>? centers)
    {
        ArgumentNullException.ThrowIfNull(displays);
        var connected = Connected(displays);
        if (connected.Count == 0)
        {
            return null;
        }

        var complete = centers is not null && document.Orbs.All(o => centers.ContainsKey(o.Id));
        var locations = complete
            ? ImmutableDictionary.CreateBuilder<string, OrbLocation>(StringComparer.Ordinal)
            : Resolve(document, displays).Locations.ToBuilder();
        if (centers is not null)
        {
            foreach (var (id, center) in centers)
            {
                if (document.Find(id) is { } orb && double.IsFinite(center.X) && double.IsFinite(center.Y)
                    && DisplayAt(displays, center) is { } display && connected.TryGetValue(display.Key, out var known))
                {
                    locations[id] = new OrbLocation(known.Key, center, orb.RadiusPx(known.Scale));
                }
            }
        }

        return new Scene(document, Key(connected.Values), connected, Main(displays, connected), locations.ToImmutable());
    }

    private sealed record Scene(
        OrbsDocument Document,
        string Arrangement,
        Dictionary<string, OrbDisplay> Connected,
        OrbDisplay Main,
        ImmutableDictionary<string, OrbLocation> Locations)
    {
        public OrbDisplay Display(string key) => Connected.TryGetValue(key, out var display) ? display : Main;

        /// <summary>Visible orbs on the display other than the excluded ones, in list order.</summary>
        public IReadOnlyList<string> OtherIds(string displayKey, IReadOnlySet<string> exclude) =>
            Document.Orbs
                .Where(o => o.Visible && !exclude.Contains(o.Id)
                    && Locations.TryGetValue(o.Id, out var l) && string.Equals(l.Display, displayKey, StringComparison.OrdinalIgnoreCase))
                .Select(o => o.Id)
                .ToArray();

        public IReadOnlyList<OrbDisc> Others(string displayKey, string excludeId) =>
            OtherIds(displayKey, new HashSet<string>(StringComparer.Ordinal) { excludeId })
                .Select(id => new OrbDisc(Locations[id].Center, Locations[id].Radius))
                .ToArray();
    }
}
