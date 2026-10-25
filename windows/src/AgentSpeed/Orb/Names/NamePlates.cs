using System.Windows;
using AgentSpeed.Interop;
using AgentSpeed.Presentation.Layout;
using AgentSpeed.Presentation.Localization;
using AgentSpeed.Presentation.Menus;
using AgentSpeed.Presentation.Orb.Names;
using AgentSpeed.Presentation.Settings;
using AgentSpeed.Presentation.Settings.Orbs;
using AgentSpeed.Theme;

namespace AgentSpeed.Orb.Names;

/// <summary>An orb for the plate layout: its definition, window and (target) disc center in physical pixels.</summary>
/// <param name="Held">A new orb still flying to its slot: its plate is placed but appears only after landing.</param>
internal readonly record struct PlateOrb(OrbDefinition Definition, IOrbWindow Window, PointD Center, bool Held);

/// <summary>
/// The name plates of the floating orbs (DESIGN 2b, Name plate): which orbs show one, where each sits without
/// colliding, what it says, when the source chip folds into it, and the undo chip stacked beyond it.
/// </summary>
internal sealed class NamePlates
{
    private readonly ThemeService _theme;
    private readonly Func<double> _clock;
    private readonly Dictionary<string, OrbPlate> _plates = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Placement> _placements = new(StringComparer.Ordinal);
    private Localizer _loc;
    private PlateTypography? _type;
    private PlateInk? _ink;
    private Undo? _undo;

    public NamePlates(ThemeService theme, Localizer loc, Func<double> clock)
    {
        _theme = theme;
        _loc = loc;
        _clock = clock;
    }

    public void Relocalize(Localizer loc) => _loc = loc;

    /// <summary>The orb shows a name plate, or will once it lands: its source chip is folded into it.</summary>
    public bool Owns(string orbId) => _placements.ContainsKey(orbId);

    /// <summary>Space already occupied below the disc by its name and undo plates (DIPs).</summary>
    public double BottomOf(string orbId)
    {
        var bottom = _plates.TryGetValue(orbId, out var plate) && plate.IsShowing && !plate.RestRect.IsEmpty ? plate.RestRect.Bottom : 0;
        if (_undo is { } undo && undo.OrbId == orbId && undo.Plate.IsShowing && !undo.Plate.RestRect.IsEmpty)
        {
            bottom = Math.Max(bottom, undo.Plate.RestRect.Bottom);
        }

        return bottom;
    }

    /// <summary>Space already occupied above the disc by its name and undo plates (DIPs).</summary>
    public double TopOf(string orbId)
    {
        var top = _plates.TryGetValue(orbId, out var plate) && plate.IsShowing && !plate.RestRect.IsEmpty ? plate.RestRect.Top : 0;
        if (_undo is { } undo && undo.OrbId == orbId && undo.Plate.IsShowing && !undo.Plate.RestRect.IsEmpty)
        {
            top = Math.Min(top, undo.Plate.RestRect.Top);
        }

        return top;
    }

    /// <summary>
    /// Lays out every plate (on drag release, display changes, and changes of a name, size, visibility, sources or the
    /// plate settings): who shows one, the collision-free slots, the shortened names; plates fade, move or morph to
    /// their new state.
    /// </summary>
    public void Resolve(IReadOnlyList<PlateOrb> orbs, AppSettings settings, SourceAvailability availability)
    {
        ArgumentNullException.ThrowIfNull(orbs);
        ArgumentNullException.ThrowIfNull(settings);
        var type = Typography();
        var ink = Ink();
        var subjects = new NamePlateSubject[orbs.Count];
        for (var i = 0; i < orbs.Count; i++)
        {
            var orb = orbs[i];
            var monitor = Monitors.FromPoint(new POINT((int)Math.Round(orb.Center.X), (int)Math.Round(orb.Center.Y)));
            var scale = monitor?.Scale ?? 1;
            var work = monitor?.WorkRect ?? new RectD(0, 0, 1920, 1080);
            subjects[i] = new NamePlateSubject(
                new PointD(orb.Center.X / scale, orb.Center.Y / scale),
                orb.Definition.Size,
                new RectD(work.X / scale, work.Y / scale, work.Width / scale, work.Height / scale),
                NamePlateRules.Shows(orb.Definition, settings));
        }

        NamePlateLayout Fit(int i, double budget) =>
            NamePlateContent.Plate(orbs[i].Definition, availability, _loc, new NamePlateMetrics(orbs[i].Definition.Size), budget, type.Measure);

        var placed = NamePlatePlacement.Resolve(subjects, (i, budget) => Fit(i, budget).Width);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < orbs.Count; i++)
        {
            var orb = orbs[i];
            var id = orb.Definition.Id;
            seen.Add(id);
            if (!subjects[i].HasPlate || placed[i] is not { } spot)
            {
                _placements.Remove(id);
                if (_plates.TryGetValue(id, out var gone))
                {
                    gone.Hide();
                }

                continue;
            }

            var center = subjects[i].Center;
            var area = subjects[i].Area;
            var chip = NamePlateContent.Chip(orb.Definition, availability, _loc);
            var placement = new Placement(
                Fit(i, spot.Budget),
                NamePlateLayout.Of(chip, new NamePlateMetrics(orb.Definition.Size), type.Measure),
                spot.Slot,
                new Rect(spot.Rect.X - center.X, spot.Rect.Y - center.Y, spot.Rect.Width, spot.Rect.Height),
                new Rect(area.X - center.X, area.Y - center.Y, area.Width, area.Height));
            _placements[id] = placement;
            if (orb.Held || Plate(orb.Window, id) is not { } plate)
            {
                continue;
            }

            plate.SetStyle(ink, type, _theme.ReduceMotion, orb.Definition.Opacity);
            plate.Window.Follow(orb.Window.Options);
            plate.Show(placement.Layout, placement.Chip, placement.Slot, placement.Rect, placement.Area);
        }

        foreach (var id in _plates.Keys.Where(k => !seen.Contains(k)).ToArray())
        {
            Remove(id);
        }

        foreach (var id in _placements.Keys.Where(k => !seen.Contains(k)).ToArray())
        {
            _placements.Remove(id);
        }

        Restack();
    }

    /// <summary>A new orb landed: its plate fades in 0.28 s later and stays (in place of the 2 s chip).</summary>
    public void Land(IOrbWindow window, OrbDefinition orb)
    {
        ArgumentNullException.ThrowIfNull(orb);
        if (_placements.TryGetValue(orb.Id, out var placement) && Plate(window, orb.Id) is { } plate)
        {
            plate.SetStyle(Ink(), Typography(), _theme.ReduceMotion, orb.Opacity);
            plate.Window.Follow(window.Options);
            plate.Show(placement.Layout, placement.Chip, placement.Slot, placement.Rect, placement.Area, NamePlateMotion.LandingDelay);
        }
    }

    /// <summary>
    /// Folds the source chip into one orb's plate (hover, drag, after a sources change); every other plate shows its
    /// name.
    /// </summary>
    public void Fold(string? orbId)
    {
        foreach (var (id, plate) in _plates)
        {
            plate.Expand(id == orbId);
        }
    }

    public void FollowHover(string orbId)
    {
        if (_plates.TryGetValue(orbId, out var plate))
        {
            plate.FollowHover();
        }

        if (_undo is { } undo && undo.OrbId == orbId)
        {
            undo.Plate.FollowHover();
        }
    }

    /// <summary>The orb vanishes (removed, merged): its plate fades out with it.</summary>
    public void Vanish(string orbId)
    {
        _placements.Remove(orbId);
        if (_plates.TryGetValue(orbId, out var plate))
        {
            plate.Hide();
        }

        if (_undo is { } undo && undo.OrbId == orbId)
        {
            undo.Plate.Hide();
        }
    }

    /// <summary>The orb's window closes (its owned plate windows close with it).</summary>
    public void Remove(string orbId)
    {
        _placements.Remove(orbId);
        if (_plates.Remove(orbId, out var plate))
        {
            plate.Close();
        }

        if (_undo is { } undo && undo.OrbId == orbId)
        {
            undo.Plate.Close();
            _undo = null;
        }
    }

    /// <summary>
    /// The undo chip in the plate's metrics and material, stacked 4 pt beyond the orb's name plate (above it when the
    /// plate is above). False when the orb shows no plate (the caller shows the plain undo chip).
    /// </summary>
    public bool ShowUndo(IOrbWindow window, OrbDefinition orb, string message, Action undo)
    {
        ArgumentNullException.ThrowIfNull(orb);
        if (!_plates.TryGetValue(orb.Id, out var plate) || !plate.IsShowing || plate.RestRect.IsEmpty)
        {
            HideUndo();
            return false;
        }

        if (_undo is { } previous && (previous.OrbId != orb.Id || !ReferenceEquals(previous.Plate.Window.Orb, window)))
        {
            previous.Plate.Close();
            _undo = null;
        }

        if (_undo is null)
        {
            var action = UndoPlateWindow.CreateAction(_loc["orbs.undo"], message, undo);
            var undoWindow = new UndoPlateWindow(window, action);
            var undoPlate = new OrbPlate(window, undoWindow, _clock);
            undoPlate.Arranged += (_, _) => PlaceAction(undoPlate, undoWindow);
            window.Attach(undoPlate);
            _undo = new Undo(orb.Id, undoPlate, undoWindow, orb.Size, message);
        }

        _undo = _undo with { Size = orb.Size, Message = message };
        System.Windows.Automation.AutomationProperties.SetHelpText(_undo.Window.Action, message);
        _undo.Plate.SetStyle(Ink(), Typography(), _theme.ReduceMotion, orb.Opacity);
        _undo.Window.Follow(window.Options with { ClickThrough = false });
        Stack(_undo, plate);
        return true;
    }

    public void HideUndo() => _undo?.Plate.Hide();

    /// <summary>The plate-styled undo chip is shown (false: the caller shows the plain one).</summary>
    public bool ShowsUndo => _undo?.Plate.IsShowing == true;

    /// <summary>The visible plates with their screen rectangles and window opacity, for the developer capture.</summary>
    public IEnumerable<(FrameworkElement Visual, Rect ScreenRect, double Opacity)> CaptureTargets()
    {
        var plates = new List<OrbPlate>(_plates.Values);
        if (_undo is { } undo)
        {
            plates.Add(undo.Plate);
        }

        foreach (var plate in plates)
        {
            if (plate.IsVisible && plate.Handle != 0 && plate.Window.Content is FrameworkElement content
                && User32.GetWindowRect(plate.Handle, out var rect))
            {
                yield return (content, new Rect(rect.Left, rect.Top, rect.Width, rect.Height), plate.Window.Opacity);
            }
        }
    }

    public void Dispose()
    {
        foreach (var id in _plates.Keys.ToArray())
        {
            Remove(id);
        }

        _undo?.Plate.Close();
        _undo = null;
    }

    /// <summary>The look of the plates; rebuilt when the appearance or high contrast changes.</summary>
    private PlateInk Ink()
    {
        var look = new PlateLook(_theme.IsDark, _theme.HighContrast);
        if (_ink is null || _ink.Look != look)
        {
            _ink = new PlateInk(look);
        }

        return _ink;
    }

    private PlateTypography Typography()
    {
        if (_type is null || _type.LanguageCode != _loc.LanguageCode)
        {
            _type = new PlateTypography(_loc.LanguageCode);
        }

        return _type;
    }

    private OrbPlate? Plate(IOrbWindow window, string id)
    {
        if (_plates.TryGetValue(id, out var plate))
        {
            if (ReferenceEquals(plate.Window.Orb, window) && !plate.Window.IsClosed)
            {
                return plate;
            }

            _plates.Remove(id);
            plate.Close();
        }

        if (window.Handle == 0)
        {
            return null;
        }

        plate = new OrbPlate(window, new NamePlateWindow(window), _clock);
        plate.SlotChanged += (_, _) => Restack();
        window.Attach(plate);
        _plates[id] = plate;
        return plate;
    }

    /// <summary>Keeps the undo chip beyond its plate after the plate moved to another slot.</summary>
    private void Restack()
    {
        if (_undo is { } undo && undo.Plate.IsShowing)
        {
            if (_plates.TryGetValue(undo.OrbId, out var plate) && plate.IsShowing)
            {
                Stack(undo, plate);
            }
            else
            {
                undo.Plate.Hide();
            }
        }
    }

    private void Stack(Undo undo, OrbPlate plate)
    {
        var metrics = new NamePlateMetrics(undo.Size);
        var layout = NamePlateLayout.Of(NamePlateContent.Undo(undo.Message, _loc), metrics, Typography().Measure);
        var anchor = plate.RestRect;
        var area = _placements.TryGetValue(undo.OrbId, out var placement) ? placement.Area : Rect.Empty;
        var w = layout.Width;
        var h = metrics.Height;
        var x = anchor.X + (anchor.Width - w) / 2;
        if (!area.IsEmpty && w <= area.Width - 2 * NamePlatePlacement.AreaInset)
        {
            x = Math.Clamp(x, area.X + NamePlatePlacement.AreaInset, area.Right - NamePlatePlacement.AreaInset - w);
        }

        var above = plate.Slot == NamePlateSlot.Above;
        var y = above ? anchor.Y - NamePlateMotion.UndoGap - h : anchor.Bottom + NamePlateMotion.UndoGap;
        if (!area.IsEmpty && (y < area.Y + NamePlatePlacement.AreaInset || y + h > area.Bottom - NamePlatePlacement.AreaInset))
        {
            above = !above;
            y = above ? anchor.Y - NamePlateMotion.UndoGap - h : anchor.Bottom + NamePlateMotion.UndoGap;
        }

        undo.Plate.Show(layout, null, above ? NamePlateSlot.Above : NamePlateSlot.Below, new Rect(x, y, w, h), area);
    }

    /// <summary>Puts the invisible action button over the accent text (window DIP, motion included).</summary>
    private static void PlaceAction(OrbPlate plate, UndoPlateWindow window)
    {
        if (plate.Content is not { } layout || layout.Runs.Length < 2)
        {
            return;
        }

        var capsule = plate.CapsuleInWindow;
        var run = layout.Runs[^1];
        var offset = window.View.MotionOffset;
        var pad = layout.Metrics.ItemGap / 2;
        window.PlaceAction(new Rect(capsule.X + run.TextX - pad + offset.X, capsule.Y + offset.Y, run.TextWidth + pad + layout.Metrics.Trailing,
            capsule.Height));
    }

    private sealed record Placement(NamePlateLayout Layout, NamePlateLayout Chip, NamePlateSlot Slot, Rect Rect, Rect Area);

    private sealed record Undo(string OrbId, OrbPlate Plate, UndoPlateWindow Window, double Size, string Message);
}
