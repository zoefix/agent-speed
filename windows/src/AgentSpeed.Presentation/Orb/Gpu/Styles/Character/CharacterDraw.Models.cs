using System.Globalization;
using AgentSpeed.Presentation.Character;
using AgentSpeed.Presentation.Localization;
using AgentSpeed.Presentation.Orb.Styles;

namespace AgentSpeed.Presentation.Orb.Gpu.Styles;

public sealed partial class CharacterDraw
{
    /// <summary>A model id is cut in the middle beyond this many <c>t</c> (DESIGN 2a.6, Models).</summary>
    private const double NameRoom = 9;

    /// <summary>Where her right side leaves less room, ids are cut shorter, down to this many <c>t</c>, before the card moves or hides.</summary>
    private const double NameRoomMin = 5;

    private const double NameShrinkAfter = 0.3;
    private const double NameGrowAfter = 4;

    private readonly OrbModelsMotion _models = new();
    private readonly Dictionary<(string Model, double Room), string> _shortNames = [];
    private readonly Dictionary<int, string> _moreTexts = [];
    private double _shortNamesSize = double.NaN;
    private string? _moreTemplate;
    private double _nameRoom = NameRoom;
    private double _nameShrink;
    private double _nameGrow;

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

    /// <summary>The columns of the card: the widest speed, the unit, and the content's width.</summary>
    private readonly record struct ModelColumns(double Number, double Unit, double Width);

    /// <summary>
    /// How many <c>t</c> the ids may take: 9, or less (down to 5, in half steps) while her right side leaves the card less room
    /// than it needs; shorter after 0.3 s without room, longer again after 4 s with room (at once while still or hidden).
    /// </summary>
    private void FitNames(in CardMetrics m, double dt, double time, bool atOnce)
    {
        double longest = 0, more = 0;
        foreach (var line in _models)
        {
            if (line.Row is { } row)
            {
                longest = Math.Max(longest, Advance(row.Model, OrbFont.Info, m.T));
            }
            else
            {
                more = Advance(MoreText(line.Hidden), OrbFont.Info, m.T);
            }
        }

        var columns = ModelsColumns(m, time);
        var rest = columns.Width - Math.Min(longest, _nameRoom * m.T) + 2 * m.PadX;
        var floor = 2 * m.PadX + NameIndent(m) + more;
        var t = m.T;
        double Width(double room) => Math.Max(rest + Math.Min(longest, room * t), floor);
        var limit = _wings.ModelsWidth + 0.5;
        var best = NameRoom;
        while (best > NameRoomMin && Width(best) > limit)
        {
            best -= 0.5;
        }

        if (Width(_nameRoom) > limit && best < _nameRoom)
        {
            _nameGrow = 0;
            _nameShrink += dt;
            if (atOnce || _nameShrink >= NameShrinkAfter)
            {
                _nameRoom = best;
                _nameShrink = 0;
            }
        }
        else if (best > _nameRoom)
        {
            _nameShrink = 0;
            _nameGrow += dt;
            if (atOnce || _nameGrow >= NameGrowAfter)
            {
                _nameRoom = best;
                _nameGrow = 0;
            }
        }
        else
        {
            _nameShrink = 0;
            _nameGrow = 0;
        }
    }

    private ModelColumns ModelsColumns(in CardMetrics m, double time)
    {
        double name = 0, number = 0, more = 0;
        foreach (var line in _models)
        {
            if (line.Leaving && line.Alpha(time) <= 0.01)
            {
                continue;
            }

            if (line.Row is { } row)
            {
                name = Math.Max(name, Advance(ShortName(row.Model, m.T, _nameRoom), OrbFont.Info, m.T));
                number = Math.Max(number, DigitsWidth(row.Number, OrbFont.TrafficNumber, m.T));
            }
            else
            {
                more = Advance(MoreText(line.Hidden), OrbFont.Info, m.T);
            }
        }

        var unit = Advance(SpeedUnit, OrbFont.Unit, CardMetrics.Small(m.T));
        var width = Math.Max(NameIndent(m) + name + NameGap(m) + number + m.CellGap + unit, NameIndent(m) + more);
        return new ModelColumns(number, unit, width);
    }

    private (double W, double H) ModelsSize(in CardMetrics m, double time)
    {
        var count = _models.Count;
        if (count == 0 || !ModelsLive)
        {
            return (0, 0);
        }

        var columns = ModelsColumns(m, time);
        return Plate(columns.Width, count * CardMetrics.RowHeight(m.T) + (count - 1) * 0.3 * m.T, m);
    }

    /// <summary>The id as the card shows it: whole up to <paramref name="rooms"/> <c>t</c>, else cut in the middle (cached).</summary>
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

        var room = rooms * t;
        var text = Advance(model, OrbFont.Info, t) <= room ? model : OrbMiddleTruncation.Fit(model, room, e => Advance(e, OrbFont.Info, t));
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
    /// The models card: per line the vendor's dot, the id in the value token and the speed right-aligned in tabular digits
    /// (<c>–</c> in the secondary token without one), <c>tok/s</c> once after the first line's number; the more line in the
    /// secondary token. Lines glide and fade by <see cref="OrbModelsMotion"/>. With a high-contrast theme every dot takes the
    /// theme's highlight, like the model chip's.
    /// </summary>
    private void DrawModels(OrbDrawList list, in Pen pen, (double W, double H) box, in CardMetrics m, in CardInk ink, bool right, double time,
        bool highContrast)
    {
        DrawPlate(list, pen, box, ink, m, null, right ? -1 : 1);
        var columns = ModelsColumns(m, time);
        var left = -box.W / 2 + m.PadX;
        var top = -box.H / 2 + m.PadY;
        var numberRight = box.W / 2 - m.PadX - columns.Unit - m.CellGap;
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
            Put(list, ShortName(row.Model, m.T, _nameRoom), OrbFont.Info, m.T, pen, nameLeft, baseline, ink.Number.MultiplyAlpha(a));
            var width = DigitsWidth(row.Number, OrbFont.TrafficNumber, m.T);
            PutDigits(list, row.Number, OrbFont.TrafficNumber, m.T, pen, numberRight - width, baseline,
                (row.Speed is null ? ink.Secondary : ink.Number).MultiplyAlpha(a));
        }

        Put(list, SpeedUnit, OrbFont.Unit, CardMetrics.Small(m.T), pen, numberRight + m.CellGap, top + CardMetrics.Baseline(m.T), ink.Secondary);
    }
}
