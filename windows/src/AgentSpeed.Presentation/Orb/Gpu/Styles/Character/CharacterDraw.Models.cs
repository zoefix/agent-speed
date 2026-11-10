using System.Globalization;
using AgentSpeed.Presentation.Character;
using AgentSpeed.Presentation.Localization;
using AgentSpeed.Presentation.Orb.Styles;

namespace AgentSpeed.Presentation.Orb.Gpu.Styles;

public sealed partial class CharacterDraw
{
    private readonly OrbModelsMotion _models = new();
    private readonly Dictionary<(string Model, double Room), string> _shortNames = [];
    private readonly Dictionary<string, string> _displayNames = new(StringComparer.Ordinal);
    private readonly Dictionary<int, string> _moreTexts = [];
    private readonly OrbModelsNames _names = new();
    private double _shortNamesSize = double.NaN;
    private string? _moreTemplate;

    /// <summary>The name width in <c>t</c> this frame; 0 when the rows show only the dot and the speed.</summary>
    public double ModelNameRoom => _names.Room;

    /// <summary>The models card's lines this frame: the held rows, at most four (two below S = 96).</summary>
    public OrbModelsMotion ModelLines => _models;

    /// <summary>The card is switched on, or still fading out after it was switched off.</summary>
    private bool ModelsLive => (Content?.Readouts ?? Settings.Orbs.CharacterReadouts.Default).Models || _wings.Presence[(int)HudCard.Models] > 0;

    private void SyncModels(OrbGpuContext ctx, in OrbFrame frame)
    {
        var models = ModelsLive ? Readings?.Models : null;
        _models.Sync(models?.Rows ?? [], models?.Version ?? -1, IsSmall(ctx) ? OrbModelsText.SmallRows : OrbModelsText.MaxRows, frame.Time,
            frame.Effects.ReduceMotion);
    }

    /// <summary>Lines are <c>0.3 t</c> apart.</summary>
    private static double LinePitch(in CardMetrics m) => CardMetrics.RowHeight(m.T) + 0.3 * m.T;

    private static double Dot(in CardMetrics m) => 0.62 * m.T;

    private static double NameIndent(in CardMetrics m) => Dot(m) + 0.42 * m.T;

    private static double NameGap(in CardMetrics m) => 0.8 * m.T;

    private string SpeedUnit => Content?.TokensUnit ?? "tok/s";

    private OrbModelsColumns ModelsColumns(in CardMetrics m, double time)
    {
        double longest = 0, number = 0, more = 0;
        var unit = false;
        foreach (var line in _models)
        {
            if (line.Leaving && line.Alpha(time) <= 0.01)
            {
                continue;
            }

            if (line.Row is { } row)
            {
                longest = Math.Max(longest, Advance(DisplayName(row.Model), OrbFont.Info, m.T));
                number = Math.Max(number, DigitsWidth(row.Number, OrbFont.TrafficNumber, m.T));
                unit |= !line.Leaving && line.To == 0 && row.Speed is not null;
            }
            else
            {
                more = Advance(MoreText(line.Hidden), OrbFont.Info, m.T);
            }
        }

        return new OrbModelsColumns(longest, number, unit ? Advance(SpeedUnit, OrbFont.Unit, CardMetrics.Small(m.T)) : 0, more, NameIndent(m), NameGap(m),
            m.CellGap, 2 * m.PadX);
    }

    /// <summary>The name width follows the room at the card's place; the layout learns the narrowest form (dots and speeds).</summary>
    private void FitNames(in CardMetrics m, double dt, double time, bool atOnce)
    {
        var columns = ModelsColumns(m, time);
        _wings.ModelsNarrowest = columns.Width(0);
        _names.Update(columns, m.T, _wings.ModelsWidth, HudWings.ModelsSpare * (_ctx?.Size ?? 0), dt, atOnce);
    }

    private (double W, double H) ModelsSize(in CardMetrics m, double time)
    {
        var count = _models.Count;
        if (count == 0 || !ModelsLive)
        {
            return (0, 0);
        }

        var columns = ModelsColumns(m, time);
        return (columns.Width(_names.Room * m.T), count * CardMetrics.RowHeight(m.T) + (count - 1) * 0.3 * m.T + 2 * m.PadY);
    }

    /// <summary>The id without a trailing date (cached).</summary>
    private string DisplayName(string model)
    {
        if (!_displayNames.TryGetValue(model, out var name))
        {
            if (_displayNames.Count > 64)
            {
                _displayNames.Clear();
            }

            name = OrbModelName.Display(model);
            _displayNames[model] = name;
        }

        return name;
    }

    /// <summary>The display name cut at its end to <paramref name="rooms"/> <c>t</c> (cached per name, size and width).</summary>
    private string ShortName(string model, double t, double rooms)
    {
        if (_shortNamesSize != t || _shortNames.Count > 64)
        {
            _shortNames.Clear();
            _shortNamesSize = t;
        }

        if (_shortNames.TryGetValue((model, rooms), out var cached))
        {
            return cached;
        }

        var name = DisplayName(model);
        var room = rooms * t;
        var text = Advance(name, OrbFont.Info, t) <= room ? name : OrbModelName.Truncate(name, room, e => Advance(e, OrbFont.Info, t));
        _shortNames[(model, rooms)] = text;
        return text;
    }

    /// <summary><c>character.models.more</c> for <paramref name="hidden"/> models (cached per count).</summary>
    private string MoreText(int hidden)
    {
        var template = Content?.ModelsMore ?? "+%@";
        if (!ReferenceEquals(template, _moreTemplate))
        {
            _moreTexts.Clear();
            _moreTemplate = template;
        }

        if (!_moreTexts.TryGetValue(hidden, out var text))
        {
            text = Localizer.FormatTemplate(template, CultureInfo.InvariantCulture, hidden.ToString(CultureInfo.InvariantCulture));
            _moreTexts[hidden] = text;
        }

        return text;
    }

    /// <summary>
    /// The models card: per line the vendor's dot, the display name in the value token (none in the narrowest form) and the
    /// speed right-aligned in tabular digits (<c>–</c> in the secondary token without one), <c>tok/s</c> once after the first
    /// row's number when it has one; the more line in the secondary token. Lines glide and fade by
    /// <see cref="OrbModelsMotion"/>. With a high-contrast theme every dot takes the theme's highlight, like the model chip's.
    /// </summary>
    private void DrawModels(OrbDrawList list, in Pen pen, (double W, double H) box, in CardMetrics m, in CardInk ink, bool right, double time,
        bool highContrast)
    {
        DrawPlate(list, pen, box, ink, m, null, right ? -1 : 1);
        var columns = ModelsColumns(m, time);
        var left = -box.W / 2 + m.PadX;
        var top = -box.H / 2 + m.PadY;
        var numberRight = box.W / 2 - m.PadX - (columns.Unit > 0 ? columns.Unit + m.CellGap : 0);
        var pitch = LinePitch(m);
        var dot = Dot(m);
        var nameLeft = left + NameIndent(m);
        foreach (var line in _models)
        {
            var a = line.Alpha(time);
            if (a <= 0.01)
            {
                continue;
            }

            var baseline = top + line.Position(time) * pitch + CardMetrics.Baseline(m.T);
            if (line.Row is not { } row)
            {
                Put(list, MoreText(line.Hidden), OrbFont.Info, m.T, pen, nameLeft, baseline, ink.Secondary.MultiplyAlpha(a));
                continue;
            }

            var (dx, dy) = pen.Point(left + dot / 2, baseline - 0.36 * m.T);
            var color = highContrast ? ink.Accent : row.Accent ?? ink.Secondary;
            list.Add(OrbGpuInstance.Circle(dx, dy, dot / 2 * pen.Scale, color.MultiplyAlpha(a * pen.Alpha)));
            if (_names.Room > 0)
            {
                Put(list, ShortName(row.Model, m.T, _names.Room), OrbFont.Info, m.T, pen, nameLeft, baseline, ink.Number.MultiplyAlpha(a));
            }

            var width = DigitsWidth(row.Number, OrbFont.TrafficNumber, m.T);
            PutDigits(list, row.Number, OrbFont.TrafficNumber, m.T, pen, numberRight - width, baseline,
                (row.Speed is null ? ink.Secondary : ink.Number).MultiplyAlpha(a));
        }

        if (columns.Unit > 0)
        {
            Put(list, SpeedUnit, OrbFont.Unit, CardMetrics.Small(m.T), pen, numberRight + m.CellGap, top + CardMetrics.Baseline(m.T), ink.Secondary);
        }
    }
}
