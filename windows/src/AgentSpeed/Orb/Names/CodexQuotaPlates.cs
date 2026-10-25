using System.Windows;
using System.Windows.Controls;
using AgentSpeed.Core;
using AgentSpeed.Interop;
using AgentSpeed.Presentation.Layout;
using AgentSpeed.Presentation.Localization;
using AgentSpeed.Presentation.Orb.Names;
using AgentSpeed.Presentation.Quota;
using AgentSpeed.Presentation.Settings;
using AgentSpeed.Theme;

namespace AgentSpeed.Orb.Names;

/// <summary>The quota capsule uses the orb's existing material, typography, motion and batched attachment placement.</summary>
internal sealed class CodexQuotaPlates(ThemeService theme, Func<double> clock) : IDisposable
{
    private readonly Dictionary<string, OrbPlate> _plates = new(StringComparer.Ordinal);
    private readonly Dictionary<(PlateLook Look, CodexQuotaSeverity Severity), PlateInk> _inks = new();
    private PlateTypography? _type;

    public event EventHandler? DetailsRequested;

    public void Resolve(IReadOnlyList<PlateOrb> orbs, AppSettings settings, NamePlates names, CodexQuotaDisplay display, Localizer loc)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var orb in orbs)
        {
            if (!settings.CodexQuotaEnabled || !settings.CodexEnabled || orb.Definition.TrackedSources is { } sources && !sources.Contains(SourceKind.Codex)
                || orb.Held || orb.Window.Handle == 0 || !orb.Window.IsVisible)
            {
                continue;
            }

            var id = orb.Definition.Id;
            seen.Add(id);
            if (!_plates.TryGetValue(id, out var plate) || plate.Window.IsClosed || !ReferenceEquals(plate.Window.Orb, orb.Window))
            {
                plate?.Close();
                var window = new NamePlateWindow(orb.Window) { Title = "Agent Speed Codex Quota" };
                ToolTipService.SetInitialShowDelay(window, 400);
                ToolTipService.SetShowDuration(window, 60_000);
                window.ToolTipOpening += (_, _) => DetailsRequested?.Invoke(this, EventArgs.Empty);
                plate = new OrbPlate(orb.Window, window, clock);
                orb.Window.Attach(plate);
                _plates[id] = plate;
            }

            if (_type is null || _type.LanguageCode != loc.LanguageCode) { _type = new PlateTypography(loc.LanguageCode); }
            var look = new PlateLook(theme.IsDark, theme.HighContrast);
            if (!_inks.TryGetValue((look, display.Severity), out var ink))
            {
                ink = new PlateInk(look, CodexQuotaDisplay.Color(display.Severity, look.Dark));
                _inks[(look, display.Severity)] = ink;
            }

            var line = new NamePlateLine([
                new NamePlateRun([], display.Prefix, NamePlateInk.Primary, NamePlateScript.HasCjk(display.Prefix)),
                new NamePlateRun([], display.Value, NamePlateInk.Accent, NamePlateScript.HasCjk(display.Value)),
            ]);
            var metrics = new NamePlateMetrics(orb.Definition.Size);
            var layout = NamePlateLayout.Of(line, metrics, _type.Measure);
            var monitor = Monitors.FromPoint(new POINT((int)orb.Center.X, (int)orb.Center.Y));
            var scale = monitor?.Scale ?? 1;
            var work = monitor?.WorkRect ?? new RectD(0, 0, 1920, 1080);
            var area = new Rect((work.X - orb.Center.X) / scale, (work.Y - orb.Center.Y) / scale, work.Width / scale, work.Height / scale);
            var x = Math.Clamp(-layout.Width / 2, area.Left + 4, Math.Max(area.Left + 4, area.Right - layout.Width - 4));
            var y = Math.Max(orb.Definition.Size / 2 + metrics.Gap, names.BottomOf(id) + 4);
            var slot = NamePlateSlot.Below;
            // At the bottom screen edge, keep the label readable rather than clipping it behind the taskbar.
            if (y + metrics.Height > area.Bottom - 4)
            {
                y = Math.Min(-orb.Definition.Size / 2 - metrics.Gap - metrics.Height, names.TopOf(id) - 4 - metrics.Height);
                slot = NamePlateSlot.Above;
            }

            plate.SetStyle(ink, _type, theme.ReduceMotion, orb.Definition.Opacity);
            plate.Window.Follow(orb.Window.Options);
            if (plate.Window.ToolTip is ToolTip tip) { tip.Content = display.Tooltip; }
            else { plate.Window.ToolTip = new ToolTip { Content = display.Tooltip }; }
            plate.Show(layout, null, slot, new Rect(x, y, layout.Width, metrics.Height), area);
        }

        foreach (var id in _plates.Keys.Where(id => !seen.Contains(id)).ToArray()) { Remove(id); }
    }

    public bool Owns(string id) => _plates.TryGetValue(id, out var plate) && plate.IsShowing;

    public void FollowHover(string id)
    {
        if (_plates.TryGetValue(id, out var plate)) { plate.FollowHover(); }
    }

    public void Vanish(string id)
    {
        if (_plates.TryGetValue(id, out var plate)) { plate.Hide(); }
    }

    public void Remove(string id)
    {
        if (_plates.Remove(id, out var plate)) { plate.Close(); }
    }

    public IEnumerable<(FrameworkElement Visual, Rect ScreenRect, double Opacity)> CaptureTargets()
    {
        foreach (var plate in _plates.Values)
        {
            if (plate.IsVisible && plate.Handle != 0 && User32.GetWindowRect(plate.Handle, out var rect))
            {
                yield return (plate.Window.View, new Rect(rect.Left, rect.Top, rect.Width, rect.Height), plate.Window.Opacity);
            }
        }
    }

    public void Dispose()
    {
        foreach (var id in _plates.Keys.ToArray()) { Remove(id); }
    }
}
