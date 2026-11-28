using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AgentSpeed.Controls;
using AgentSpeed.Core;
using AgentSpeed.Presentation.Heat;
using AgentSpeed.Presentation.Cost;
using AgentSpeed.Presentation.Formatting;
using AgentSpeed.Presentation.Orb;
using AgentSpeed.Presentation.Settings;

namespace AgentSpeed.Taskbar;

/// <summary>
/// Draws the taskbar widget (DESIGN section 3) with WPF into a premultiplied BGRA buffer. Runs on the taskbar
/// thread; every WPF object here belongs to that thread.
/// </summary>
internal sealed class TaskbarRenderer
{
    private const double ValueSize = 15;
    private const double UnitSize = 11;
    private const double GlyphStroke = 1.75;
    private const double Padding = 8;
    private const double GlyphGap = 4.5;
    private const double UnitGap = 3;

    private readonly Typeface _valueFace = new(new FontFamily("Segoe UI Variable Display, Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
    private readonly Typeface _unitFace = new(new FontFamily("Segoe UI Variable Text, Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    private readonly Dictionary<(char, int), FormattedText> _glyphCache = new();
    private readonly DrawingVisual _visual = new();
    private readonly TaskbarSecondaryText _secondary = new();
    private readonly TaskbarTrafficText _traffic = new();
    private readonly TaskbarIntelligenceText _intelligence = new();
    private readonly TaskbarQuotaText _quota = new();
    private RenderTargetBitmap? _bitmap;
    private double _cacheScale = -1;
    private Color _cachePrimary;
    private Color _cacheSecondary;
    private double _digitWidth;
    private FormattedText? _unit;
    private string _unitText = string.Empty;

    public TaskbarRenderer()
    {
        TextOptions.SetTextRenderingMode(_visual, TextRenderingMode.Grayscale);
        TextOptions.SetTextHintingMode(_visual, TextHintingMode.Fixed);
    }

    /// <summary>
    /// The second value's number cell (DIP) the host's width governor holds (grows at once, shrinks after its delay);
    /// the number is right-aligned in it (DESIGN 3).
    /// </summary>
    public double SecondaryCell { get; set; }

    /// <summary>Width (DIP) the second value's number needs now, for the host's governor; 0 without one.</summary>
    public double SecondaryNumberWidth(TaskbarState state, double scale)
    {
        if (!ShowsSecondary(state))
        {
            return 0;
        }

        var colors = Palette(state);
        return _secondary.NumberWidth(state.Secondary!, scale, colors.Primary, colors.Secondary);
    }

    /// <summary>
    /// The quota block's label and number cells (DIP) the host's governor holds (each grows at once, narrows after its delay;
    /// DESIGN 10, Menu bar and taskbar, <i>Quota</i>).
    /// </summary>
    public BarQuotaCells QuotaCells { get; set; }

    /// <summary>The quota block's cells (DIP) its texts need now, for the host's governor; zero without the block.</summary>
    public BarQuotaCells QuotaCellsNeeded(TaskbarState state, double scale)
    {
        if (!ShowsQuota(state))
        {
            return default;
        }

        var colors = Palette(state);
        return _quota.Needed(state.Quota!, scale, colors.Primary, colors.Secondary, state.HighContrast, state.TaskbarDark);
    }

    private static bool ShowsSecondary(TaskbarState state) => state.Secondary is not null && state.Mode != BarDisplayMode.GlyphOnly;

    private static bool ShowsTraffic(TaskbarState state) => state.Traffic is not null && state.Mode != BarDisplayMode.GlyphOnly;

    private static bool ShowsIntelligence(TaskbarState state) => state.Intelligence is not null && state.Mode != BarDisplayMode.GlyphOnly;

    private static bool ShowsQuota(TaskbarState state) => state.Quota is not null && state.Mode != BarDisplayMode.GlyphOnly;

    /// <summary>Widget length along the taskbar in physical pixels: the content width.</summary>
    public int MeasureLength(TaskbarState state, double scale, bool vertical)
    {
        var colors = Palette(state);
        EnsureCache(scale, colors.Primary, colors.Secondary, state.UnitText);
        if (vertical)
        {
            var lines = state.Mode == BarDisplayMode.GlyphAndValue ? 2 : 1;
            var height = Padding + lines * 20 + (state.ShowUnit && state.Mode != BarDisplayMode.GlyphOnly ? 14 : 0)
                + (ShowsSecondary(state) ? TaskbarSecondaryText.Height : 0) + (ShowsTraffic(state) ? TaskbarTrafficText.Height : 0)
                + (ShowsIntelligence(state) ? TaskbarIntelligenceText.Height : 0) + (ShowsQuota(state) ? TaskbarQuotaText.Height : 0) + Padding;
            return (int)Math.Ceiling(height * scale);
        }

        return (int)Math.Ceiling(ContentWidth(state) * scale);
    }

    /// <summary>
    /// Width (DIP) of glyph + value + unit with padding. Digits are tabular, so this only changes when the number of
    /// characters changes; the host delays shrinking so the widget never flickers.
    /// </summary>
    private double ContentWidth(TaskbarState state)
    {
        var width = Padding * 2;
        if (state.Mode != BarDisplayMode.ValueOnly)
        {
            width += GlyphSize;
        }

        if (state.Mode != BarDisplayMode.GlyphOnly)
        {
            if (state.Mode == BarDisplayMode.GlyphAndValue)
            {
                width += GlyphGap;
            }

            width += TabularWidth(ValueText(state), 0);
            if (state.ShowUnit && _unit is not null)
            {
                width += UnitGap + _unit.WidthIncludingTrailingWhitespace;
            }

            if (ShowsSecondary(state))
            {
                var colors = Palette(state);
                width += _secondary.Width(state.Secondary!, SecondaryCell, _cacheScale, colors.Primary, colors.Secondary);
            }

            if (ShowsTraffic(state))
            {
                var colors = Palette(state);
                width += _traffic.Width(state.Traffic!, _cacheScale, colors.Primary, colors.Secondary);
            }

            if (ShowsIntelligence(state))
            {
                var colors = Palette(state);
                width += _intelligence.Width(state.Intelligence!, _cacheScale, colors.Primary, colors.Secondary, state.HighContrast);
            }

            if (ShowsQuota(state))
            {
                var colors = Palette(state);
                width += _quota.Width(state.Quota!, QuotaCells, _cacheScale, colors.Primary, colors.Secondary, state.HighContrast, state.TaskbarDark);
            }
        }

        return width;
    }

    /// <summary>Renders into <paramref name="destination"/> (top-down, premultiplied BGRA, stride = width * 4).</summary>
    public void Render(
        TaskbarState state, BarScene scene, int widthPx, int heightPx, double scale, bool vertical,
        bool hovered, bool pressed, nint destination, int destinationBytes)
    {
        var colors = Palette(state);
        EnsureCache(scale, colors.Primary, colors.Secondary, state.UnitText);

        var width = widthPx / scale;
        var height = heightPx / scale;
        using (var dc = _visual.RenderOpen())
        {
            // Alpha 1/255 everywhere keeps the whole rectangle hit-testable without being visible.
            dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)), null, new Rect(0, 0, width, height));

            // While a shorter value waits out the shrink delay, the content stays anchored to the notification area.
            var contentLeft = vertical ? 0 : Math.Max(0, Snap(width - ContentWidth(state), scale));
            if (hovered || pressed)
            {
                var fill = Color.FromArgb((byte)(pressed ? 0x0A : 0x12), colors.Primary.R, colors.Primary.G, colors.Primary.B);
                var inset = vertical
                    ? new Rect(3, 2, width - 6, height - 4)
                    : new Rect(contentLeft + 2, Math.Max(2, (height - 40) / 2), width - contentLeft - 4, Math.Min(height - 4, 40));
                dc.DrawRoundedRectangle(new SolidColorBrush(fill), null, inset, 4, 4);
            }

            if (vertical)
            {
                DrawVertical(dc, state, scene, colors, width, height);
            }
            else
            {
                DrawHorizontal(dc, state, scene, colors, contentLeft, height, scale);
            }
        }

        if (_bitmap is null || _bitmap.PixelWidth != widthPx || _bitmap.PixelHeight != heightPx || Math.Abs(_bitmap.DpiX - 96 * scale) > 0.01)
        {
            _bitmap = new RenderTargetBitmap(widthPx, heightPx, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        }
        else
        {
            _bitmap.Clear();
        }

        _bitmap.Render(_visual);
        _bitmap.CopyPixels(new Int32Rect(0, 0, widthPx, heightPx), destination, destinationBytes, widthPx * 4);
    }

    private void DrawHorizontal(DrawingContext dc, TaskbarState state, BarScene scene, (Color Primary, Color Secondary) colors,
        double left, double height, double scale)
    {
        var x = left + Padding;
        var centerY = height / 2;

        if (state.Mode != BarDisplayMode.ValueOnly)
        {
            // Optically centered on the digits' cap height, not on the line box.
            var glyphRect = new Rect(Snap(x, scale), centerY - GlyphSize / 2, GlyphSize, GlyphSize);
            DrawGlyph(dc, state, scene, colors.Primary, glyphRect, scale);
            x += GlyphSize + (state.Mode == BarDisplayMode.GlyphAndValue ? GlyphGap : 0);
        }

        if (state.Mode == BarDisplayMode.GlyphOnly)
        {
            return;
        }

        var valueText = ValueText(state);
        var idle = state.Tier == SpeedTier.Idle || state.Value <= 0 || state.ValueMuted;
        var role = idle ? 1 : 0;
        var baseline = Snap(centerY + CapHeight / 2, scale);
        var valueWidth = TabularWidth(valueText, role);
        DrawTabular(dc, valueText, x + valueWidth, baseline, role, scale);
        x += valueWidth;

        if (state.ShowUnit && _unit is not null)
        {
            x += UnitGap;
            dc.DrawText(_unit, new Point(Snap(x, scale), baseline - _unit.Baseline));
            x += _unit.WidthIncludingTrailingWhitespace;
        }

        if (ShowsSecondary(state))
        {
            _secondary.Draw(dc, state.Secondary!, x, centerY, SecondaryCell, scale, colors.Primary, colors.Secondary);
            x += _secondary.Width(state.Secondary!, SecondaryCell, scale, colors.Primary, colors.Secondary);
        }

        // DESIGN 10: the network block, the intelligence block, then the quota block, each with its own 8 px gap and width.
        if (ShowsTraffic(state))
        {
            _traffic.Draw(dc, state.Traffic!, x, centerY, scale, colors.Primary, colors.Secondary);
            x += _traffic.Width(state.Traffic!, scale, colors.Primary, colors.Secondary);
        }

        if (ShowsIntelligence(state))
        {
            _intelligence.Draw(dc, state.Intelligence!, x, centerY, scale, colors.Primary, colors.Secondary, state.HighContrast);
            x += _intelligence.Width(state.Intelligence!, scale, colors.Primary, colors.Secondary, state.HighContrast);
        }

        if (ShowsQuota(state))
        {
            _quota.Draw(dc, state.Quota!, x, centerY, QuotaCells, scale, colors.Primary, colors.Secondary, state.HighContrast, state.TaskbarDark);
        }
    }

    private void DrawVertical(DrawingContext dc, TaskbarState state, BarScene scene, (Color Primary, Color Secondary) colors,
        double width, double height)
    {
        var y = Padding;
        if (state.Mode != BarDisplayMode.ValueOnly)
        {
            var rect = new Rect((width - GlyphSize) / 2, y, GlyphSize, GlyphSize);
            DrawGlyph(dc, state, scene, colors.Primary, rect, 1);
            y += 20;
        }

        if (state.Mode == BarDisplayMode.GlyphOnly)
        {
            return;
        }

        var text = ValueText(state);
        var role = state.Tier == SpeedTier.Idle || state.Value <= 0 || state.ValueMuted ? 1 : 0;
        var total = TabularWidth(text, role);
        var sample = Glyph('8', role);
        DrawTabular(dc, text, (width + total) / 2, y + sample.Baseline, role, 1);
        y += sample.Height;
        if (state.ShowUnit && _unit is not null)
        {
            dc.DrawText(_unit, new Point((width - _unit.WidthIncludingTrailingWhitespace) / 2, y - 2));
            y += 14;
        }

        if (ShowsSecondary(state))
        {
            // Stacked under the value, centered across the bar (the gap is part of the block's width).
            var blockWidth = _secondary.Width(state.Secondary!, SecondaryCell, 1, colors.Primary, colors.Secondary) - TaskbarSecondaryText.Gap;
            _secondary.Draw(dc, state.Secondary!, (width - blockWidth) / 2 - TaskbarSecondaryText.Gap, y + TaskbarSecondaryText.Height / 2,
                SecondaryCell, 1, colors.Primary, colors.Secondary);
            y += TaskbarSecondaryText.Height;
        }

        if (ShowsTraffic(state))
        {
            var blockWidth = _traffic.Width(state.Traffic!, 1, colors.Primary, colors.Secondary) - TaskbarTrafficText.Gap;
            _traffic.Draw(dc, state.Traffic!, (width - blockWidth) / 2 - TaskbarTrafficText.Gap, y + TaskbarTrafficText.Height / 2, 1,
                colors.Primary, colors.Secondary);
            y += TaskbarTrafficText.Height;
        }

        if (ShowsIntelligence(state))
        {
            var blockWidth = _intelligence.Width(state.Intelligence!, 1, colors.Primary, colors.Secondary, state.HighContrast) - TaskbarIntelligenceText.Gap;
            _intelligence.Draw(dc, state.Intelligence!, (width - blockWidth) / 2 - TaskbarIntelligenceText.Gap,
                y + TaskbarIntelligenceText.Height / 2, 1, colors.Primary, colors.Secondary, state.HighContrast);
            y += TaskbarIntelligenceText.Height;
        }

        if (ShowsQuota(state))
        {
            var blockWidth = _quota.Width(state.Quota!, QuotaCells, 1, colors.Primary, colors.Secondary, state.HighContrast, state.TaskbarDark)
                - TaskbarQuotaText.Gap;
            _quota.Draw(dc, state.Quota!, (width - blockWidth) / 2 - TaskbarQuotaText.Gap, y + TaskbarQuotaText.Height / 2, QuotaCells, 1,
                colors.Primary, colors.Secondary, state.HighContrast, state.TaskbarDark);
        }
    }

    private static void DrawGlyph(DrawingContext dc, TaskbarState state, BarScene scene, Color label, Rect rect, double scale)
    {
        var monochrome = !state.ColoredGlyph || state.HighContrast;
        var stroke = Math.Max(1.5, GlyphStroke * scale) / scale;
        GaugeGlyphPainter.Draw(dc, rect, scene.Heat, label, monochrome, scene.FillOpacity,
            state.HighContrast ? 0 : scene.Glow, stroke);
    }

    /// <summary>tok/s, or the money text of DESIGN 9 when the live unit is money.</summary>
    private static string ValueText(TaskbarState state) => state.ValueText ?? SpeedFormat.Speed(state.Value);

    private void DrawTabular(DrawingContext dc, string text, double right, double baseline, int role, double scale)
    {
        var x = right - TabularWidth(text, role);
        foreach (var c in text)
        {
            var glyph = Glyph(c, role);
            var cell = char.IsDigit(c) ? _digitWidth : glyph.WidthIncludingTrailingWhitespace;
            var offset = (cell - glyph.WidthIncludingTrailingWhitespace) / 2;
            dc.DrawText(glyph, new Point(Snap(x + offset, scale), baseline - glyph.Baseline));
            x += cell;
        }
    }

    private double TabularWidth(string text, int role)
    {
        var width = 0.0;
        foreach (var c in text)
        {
            width += char.IsDigit(c) ? _digitWidth : Glyph(c, role).WidthIncludingTrailingWhitespace;
        }

        return width;
    }

    private FormattedText Glyph(char c, int role)
    {
        if (!_glyphCache.TryGetValue((c, role), out var text))
        {
            text = new FormattedText(c.ToString(), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, _valueFace,
                ValueSize, new SolidColorBrush(role == 0 ? _cachePrimary : _cacheSecondary), null,
                TextFormattingMode.Ideal, _cacheScale);
            _glyphCache[(c, role)] = text;
        }

        return text;
    }

    private void EnsureCache(double scale, Color primary, Color secondary, string unitText)
    {
        if (Math.Abs(scale - _cacheScale) < 0.001 && primary == _cachePrimary && secondary == _cacheSecondary && unitText == _unitText)
        {
            return;
        }

        _cacheScale = scale;
        _cachePrimary = primary;
        _cacheSecondary = secondary;
        _unitText = unitText;
        _glyphCache.Clear();

        _digitWidth = 0;
        for (var c = '0'; c <= '9'; c++)
        {
            _digitWidth = Math.Max(_digitWidth, Glyph(c, 0).WidthIncludingTrailingWhitespace);
        }

        _unit = string.IsNullOrEmpty(unitText)
            ? null
            : new FormattedText(unitText, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, _unitFace, UnitSize,
                new SolidColorBrush(secondary), null, TextFormattingMode.Ideal, scale);
    }

    private double CapHeight => (_valueFace.CapsHeight > 0 ? _valueFace.CapsHeight : 0.7) * ValueSize;

    /// <summary>Gauge diameter ≈ cap height × 1.2 of the 15 px value: 14 DIP (DESIGN section 3).</summary>
    private const double GlyphSize = 14;

    private static (Color Primary, Color Secondary) Palette(TaskbarState state)
    {
        if (state.HighContrast)
        {
            var text = SystemColors.WindowTextColor;
            return (text, text);
        }

        return state.TaskbarDark
            ? (Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF), Color.FromArgb(0x9E, 0xFF, 0xFF, 0xFF))
            : (Color.FromArgb(0xE6, 0x00, 0x00, 0x00), Color.FromArgb(0x99, 0x00, 0x00, 0x00));
    }

    private static double Snap(double value, double scale) => Math.Round(value * scale) / scale;
}
