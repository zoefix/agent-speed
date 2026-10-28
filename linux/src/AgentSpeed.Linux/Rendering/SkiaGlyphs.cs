using AgentSpeed.Presentation.Orb.Gpu;
using SkiaSharp;
using Avalonia.Platform;

namespace AgentSpeed.Linux.Rendering;

internal sealed class SkiaGlyphs : IOrbGlyphs, IDisposable
{
    internal sealed record Entry(OrbTextKey Key, SKTypeface Face, float Width, float Ascent, float Pad);
    private readonly Dictionary<OrbTextKey, int> _indices = [];
    private readonly List<Entry> _entries = [];
    // Inter is bundled as an Avalonia asset, not installed as a system font.
    // Resolving it by family name gives different/missing glyphs in the live app.
    private readonly SKTypeface _normal = LoadBundledFont("Inter-Medium.ttf");
    private readonly SKTypeface _bold = LoadBundledFont("Inter-Bold.ttf");
    private readonly SKTypeface _cjk = SKTypeface.FromFamilyName("Noto Sans CJK SC", SKFontStyle.Bold);
    private readonly SKFont _measure = new() { Edging = SKFontEdging.Antialias };

    internal static SKTypeface LoadBundledFont(string file)
    {
        using var stream = new StandardAssetLoader().Open(new Uri("avares://Avalonia.Fonts.Inter/Assets/" + file));
        using var data = SKData.Create(stream);
        return SKTypeface.FromData(data) ?? throw new InvalidDataException($"Cannot load bundled orb font {file}.");
    }

    public bool TryGet(in OrbTextKey key, out OrbGlyph glyph)
    {
        if (!_indices.TryGetValue(key, out var index))
        {
            var face = key.Text.Any(c => c > 255) ? _cjk : key.Font is OrbFont.Unit or OrbFont.Info ? _normal : _bold;
            _measure.Typeface = face; _measure.Size = key.Size;
            var width = Measure(key.Text, key.Tracking);
            var pad = Math.Max(1, key.GlowSigma * 3);
            index = _entries.Count;
            _indices[key] = index;
            _entries.Add(new(key, face, width, _measure.Metrics.Ascent, pad));
        }
        var e = _entries[index];
        glyph = new(e.Width, -e.Ascent, -e.Pad, e.Ascent - e.Pad, e.Width + 2 * e.Pad,
            key.Size * 1.4f + 2 * e.Pad, index, 0, index + 1, 1);
        return true;
    }
    internal Entry At(float index) => _entries[(int)Math.Round(index)];
    internal void BeginFrame() { if (_entries.Count > 8192) { _indices.Clear(); _entries.Clear(); } }
    private float Measure(string text, float tracking)
    {
        var width = 0f;
        foreach (var rune in text.EnumerateRunes()) width += _measure.MeasureText(rune.ToString()) + tracking;
        return Math.Max(0, width - tracking);
    }
    public double DigitCell(OrbFont font, double size)
    {
        _measure.Typeface = _bold; _measure.Size = (float)size;
        return "0123456789".Max(c => _measure.MeasureText(c.ToString()));
    }
    public double CapHeight(OrbFont font, double size) => size * .73;
    public void Dispose() { _measure.Dispose(); _normal.Dispose(); _bold.Dispose(); _cjk.Dispose(); }
}
