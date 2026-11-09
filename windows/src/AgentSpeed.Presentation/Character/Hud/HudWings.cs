using System.Collections.Immutable;

namespace AgentSpeed.Presentation.Character;

/// <summary>The cards of the character's floating readout (DESIGN 2a.6, <i>Readouts</i>): the speed on her right, the model above her head, and the column of facts on her left, top to bottom.</summary>
public enum HudCard
{
    Speed,
    Model,
    Performance,
    Network,
    Cost,
    Agents,
}

public static class HudCards
{
    public const int Count = 6;

    /// <summary>The left column, top to bottom.</summary>
    public static ImmutableArray<HudCard> Column { get; } = [HudCard.Performance, HudCard.Network, HudCard.Cost, HudCard.Agents];

    public static ImmutableArray<HudCard> All { get; } = [HudCard.Speed, HudCard.Model, HudCard.Performance, HudCard.Network, HudCard.Cost, HudCard.Agents];
}

/// <summary>
/// Her outline this frame as horizontal bands of <c>step</c> points from <c>top</c>: each band's leftmost and rightmost x
/// of her drawn parts. Built by the renderer from the skinned vertices, so the cards clear an arm held out, a raised staff
/// or open wings as they move.
/// </summary>
public sealed class HudSilhouette
{
    private readonly double[] _left;
    private readonly double[] _right;

    public HudSilhouette(double top, double bottom, double step)
    {
        Top = top;
        Step = Math.Max(step, 1);
        var count = Math.Max(1, (int)Math.Ceiling((bottom - top) / Step));
        _left = new double[count];
        _right = new double[count];
        Clear();
    }

    public double Top { get; }

    public double Step { get; }

    public int BandCount => _left.Length;

    /// <summary>Empties every band, keeping the storage.</summary>
    public void Clear()
    {
        Array.Fill(_left, double.PositiveInfinity);
        Array.Fill(_right, double.NegativeInfinity);
    }

    /// <summary>Copies another silhouette of the same shape.</summary>
    public void CopyFrom(HudSilhouette other)
    {
        ArgumentNullException.ThrowIfNull(other);
        Array.Copy(other._left, _left, Math.Min(_left.Length, other._left.Length));
        Array.Copy(other._right, _right, Math.Min(_right.Length, other._right.Length));
    }

    /// <summary>Adds a point of her outline (any vertex of a drawn part).</summary>
    public void Add(double x, double y)
    {
        var f = Math.Floor((y - Top) / Step);
        if (!(f >= 0) || f >= _left.Length)
        {
            return;
        }

        var i = (int)f;
        if (x < _left[i])
        {
            _left[i] = x;
        }

        if (x > _right[i])
        {
            _right[i] = x;
        }
    }

    /// <summary>Adds a segment, so a long triangle edge marks every band it crosses.</summary>
    public void Add(double x0, double y0, double x1, double y1)
    {
        var n = Math.Max(1, (int)Math.Ceiling(Math.Abs(y1 - y0) / Step));
        for (var k = 0; k <= n; k++)
        {
            var t = (double)k / n;
            Add(x0 + (x1 - x0) * t, y0 + (y1 - y0) * t);
        }
    }

    /// <summary>Her leftmost and rightmost x over the bands that <c>y0…y1</c> touches, null when she has nothing there.</summary>
    public (double Left, double Right)? Extent(double y0, double y1)
    {
        var a = Math.Max(0, Floor((Math.Min(y0, y1) - Top) / Step));
        var b = Math.Min(_left.Length - 1, Floor((Math.Max(y0, y1) - Top) / Step));
        if (a > b)
        {
            return null;
        }

        var l = double.PositiveInfinity;
        var r = double.NegativeInfinity;
        for (var i = a; i <= b; i++)
        {
            l = Math.Min(l, _left[i]);
            r = Math.Max(r, _right[i]);
        }

        return l <= r ? (l, r) : null;
    }

    private static int Floor(double v) => v < int.MinValue ? int.MinValue : v > int.MaxValue ? int.MaxValue : (int)Math.Floor(v);
}

/// <summary>Where the readout cards are anchored on her this frame, in window points (y down).</summary>
public readonly record struct HudAnchors(double CenterX, double HeadTop, double Chest, double Shoulders);

/// <summary>What the cards are laid out around this frame, in window points (y down).</summary>
/// <param name="Footprint">The footprint D (2 S): every distance of the layout is a share of it.</param>
public sealed record HudFrame((double MinX, double MinY, double MaxX, double MaxY) Bounds, HudSilhouette Silhouette, double Footprint,
    double CenterX, double HeadTop, double Chest, double Shoulders)
{
    public HudFrame((double MinX, double MinY, double MaxX, double MaxY) bounds, HudSilhouette silhouette, double footprint, HudAnchors anchors)
        : this(bounds, silhouette, footprint, anchors.CenterX, anchors.HeadTop, anchors.Chest, anchors.Shoulders)
    {
    }
}

/// <summary>
/// Places the readout cards beside her (DESIGN 2a.6): each has its own anchor — the speed at her right at chest height,
/// the model above her head, the facts in a column at her left from her shoulders down — kept clear of her outline, never
/// behind her. Anchors trail her on soft springs, and every card floats on its own slow figure of eight, bobs, tilts and
/// breathes. A card that shows or hides fades and rises or sinks; the column closes up around it. When her left side has
/// no room for the whole column, its last cards continue under the speed on her right; a card changing sides fades out
/// and back in at its new place instead of crossing her.
/// </summary>
public sealed class HudWings
{
    /// <summary>Where a card is drawn this frame: its center, scale, tilt (radians, clockwise on screen) and opacity.</summary>
    public readonly record struct Place(double X, double Y, double Scale, double Spin, double Alpha);

    public const double Gap = 0.035;
    public const double Margin = 0.025;
    public const double ColumnGap = 0.022;
    public const double HeadGap = 0.03;
    public const double FadeIn = 0.35;
    public const double FadeOut = 0.5;
    public const double HopOut = 0.18;
    public const double HopIn = 0.3;
    public const double SpillAfter = 0.6;
    public const double ReturnAfter = 4.0;
    public const double SettleWindow = 30.0;
    public const double SettledReturn = 12.0;
    private const double Dodge = 0.06;
    private const double NearReach = 0.12;
    private const double Reach = 0.3;

    /// <summary>Float amplitudes (x, y) of the speed and of the other cards.</summary>
    public static readonly (double X, double Y) SpeedFloat = (0.008, 0.011);
    public static readonly (double X, double Y) CardFloat = (0.013, 0.017);

    /// <summary>Vertical offsets tried, in shares of D, when a card or the column does not fit beside her at its row.</summary>
    private static readonly double[] Reaches = [NearReach, Reach];

    private static readonly double[] Shifts = [0, -0.04, 0.04, -0.08, 0.08, -0.12, 0.12, 0.17, -0.17, 0.23, 0.3];

    private readonly Place[] _places;
    private readonly double[] _presence;
    private readonly (double W, double H)[] _sizes;
    private readonly SecondOrder[] _anchorX;
    private readonly SecondOrder[] _anchorY;
    private readonly SecondOrder[] _growW;
    private readonly SecondOrder[] _growH;
    private readonly (double X, double Y)[] _targets;
    private readonly bool[] _right;
    private readonly double[] _hop;
    private readonly double[] _edges = new double[4];
    private double _strain;
    private double _ease;
    private double _sinceChange = double.PositiveInfinity;
    private bool _hugging;
    private bool _placed;

    public HudWings()
    {
        const int count = HudCards.Count;
        _places = new Place[count];
        for (var i = 0; i < count; i++)
        {
            _places[i] = new Place(0, 0, 1, 0, 0);
        }

        _presence = new double[count];
        _sizes = new (double, double)[count];
        _anchorX = Enumerable.Range(0, count).Select(_ => new SecondOrder(0.9, 0.78, 0)).ToArray();
        _anchorY = Enumerable.Range(0, count).Select(_ => new SecondOrder(0.9, 0.78, 0)).ToArray();
        _growW = Enumerable.Range(0, count).Select(_ => new SecondOrder(3.2, 1, 0)).ToArray();
        _growH = Enumerable.Range(0, count).Select(_ => new SecondOrder(3.2, 1, 0)).ToArray();
        _targets = new (double, double)[count];
        _right = new bool[count];
        _hop = Enumerable.Repeat(1.0, count).ToArray();
    }

    public IReadOnlyList<Place> Places => _places;

    /// <summary>Shown share of each card, 0…1 (eased into the opacity and the column's spacing).</summary>
    public IReadOnlyList<double> Presence => _presence;

    /// <summary>Each card's size this frame, eased toward the size it was given (points).</summary>
    public IReadOnlyList<(double W, double H)> Sizes => _sizes;

    /// <summary>No float, tilt or breathing (Reduce Motion, effects Off): the cards still keep clear of her.</summary>
    public bool Still { get; set; }

    /// <summary>A card is fading in or out or changing sides (frames are needed even when nothing else moves).</summary>
    public bool IsFading
    {
        get
        {
            for (var i = 0; i < _presence.Length; i++)
            {
                if ((_presence[i] > 0 && _presence[i] < 1) || _hop[i] < 1)
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>How many cards of the column (in its order) stay at her left; the rest continue under the speed.</summary>
    public int Split { get; private set; } = HudCards.Column.Length;

    /// <summary>The card is drawn at her right (the speed, and column cards that spilled over).</summary>
    public bool IsAtHerRight(HudCard card) => card == HudCard.Speed || _right[(int)card];

    /// <summary>
    /// Advances the layout one frame. <paramref name="given"/> are the cards' current sizes in points and
    /// <paramref name="shown"/> which cards should show (switched on and not quiet), both indexed by <see cref="HudCard"/>.
    /// </summary>
    public void Update(double dt, double time, HudFrame f, IReadOnlyList<(double W, double H)> given, IReadOnlyList<bool> shown)
    {
        ArgumentNullException.ThrowIfNull(f);
        for (var i = 0; i < _presence.Length; i++)
        {
            var target = shown[i] ? 1.0 : 0.0;
            var rate = dt / (target > _presence[i] ? FadeIn : FadeOut);
            _presence[i] = target > _presence[i] ? Math.Min(target, _presence[i] + rate) : Math.Max(target, _presence[i] - rate);
            if (!_placed)
            {
                _presence[i] = target;
            }

            if (!_placed || _presence[i] == 0 || Still)
            {
                _growW[i].Snap(given[i].W);
                _growH[i].Snap(given[i].H);
            }

            _sizes[i] = (_growW[i].Update(dt, given[i].W), _growH[i].Update(dt, given[i].H));
        }

        _targets[(int)HudCard.Speed] = SpeedAnchor(f, _sizes[(int)HudCard.Speed]);
        _targets[(int)HudCard.Model] = ModelAnchor(f, _sizes[(int)HudCard.Model]);
        ColumnAnchors(dt, f, _sizes);

        var d = f.Footprint;
        foreach (var card in HudCards.All)
        {
            var i = (int)card;
            if (!_placed || (_presence[i] == 0 && !shown[i]))
            {
                _anchorX[i].Snap(_targets[i].X);
                _anchorY[i].Snap(_targets[i].Y);
            }

            var ax = _anchorX[i].Update(dt, _targets[i].X);
            var ay = _anchorY[i].Update(dt, _targets[i].Y);
            if (card != HudCard.Model && _placed && Intrudes(card, ax, ay, f))
            {
                var k = 1 - Math.Exp(-dt / Dodge);
                ax += (_targets[i].X - ax) * k;
                ay += (_targets[i].Y - ay) * k;
                _anchorX[i].Snap(ax);
                _anchorY[i].Snap(ay);
            }

            var shownShare = _presence[i] * _presence[i] * (3 - 2 * _presence[i]);
            var hopShare = _hop[i] * _hop[i] * (3 - 2 * _hop[i]);
            var ease = shownShare * hopShare;
            var place = new Place(ax, ay + (1 - shownShare) * 0.02 * d, 0.96 + 0.04 * ease, 0, ease);
            if (!Still)
            {
                var (dx, dy, spin, scale) = Float(card, time);
                place = new Place(place.X + d * dx, place.Y + d * dy, place.Scale * scale, spin, place.Alpha);
            }

            _places[i] = place;
        }

        _placed = true;
    }

    /// <summary>Whether the card at x, y reaches into her outline (closer than half the gap on the side facing her).</summary>
    private bool Intrudes(HudCard card, double x, double y, HudFrame f)
    {
        var (w, h) = _sizes[(int)card];
        if (!(w > 0) || f.Silhouette.Extent(y - h / 2, y + h / 2) is not { } e)
        {
            return false;
        }

        var room = 0.5 * Gap * f.Footprint;
        return IsAtHerRight(card) ? x - w / 2 < e.Right + room : x + w / 2 > e.Left - room;
    }

    private static double Wave(double t, double period, double phase) => Math.Sin(2 * Math.PI * t / period + phase);

    /// <summary>
    /// A card's float at time t: offset in shares of D, tilt in radians and breathing scale. Each card has its own periods
    /// and phase, so no two move in step; two sines per axis trace a slow figure of eight.
    /// </summary>
    public static (double Dx, double Dy, double Spin, double Scale) Float(HudCard card, double t)
    {
        double k = (int)card;
        var phase = 1.7 * k + 0.4;
        var (ax, ay) = card == HudCard.Speed ? SpeedFloat : CardFloat;
        var x1 = 0.65 * Wave(t, 7.3 + 0.9 * k, phase);
        var x2 = 0.35 * Wave(t, 3.1 + 0.4 * k, 2.1 * phase);
        var y1 = 0.7 * Wave(t, 5.9 + 0.7 * k, 1.3 * phase);
        var y2 = 0.3 * Wave(t, 2.6 + 0.3 * k, 0.6 * phase);
        var tilt = card == HudCard.Speed ? 0.6 : 1.0;
        var s1 = 0.032 * Wave(t, 11.0 + k, phase);
        var s2 = 0.014 * Wave(t, 4.7 + 0.5 * k, 2 * phase);
        var breath = 0.012 * Wave(t, 6.1 + 0.6 * k, 0.8 * phase);
        return (ax * (x1 + x2), ay * (y1 + y2), tilt * (s1 + s2), 1 + breath);
    }

    /// <summary>The speed: its left edge clear of her right side at chest height (or the nearest row where it fits), its right edge inside the bounds.</summary>
    private static (double X, double Y) SpeedAnchor(HudFrame f, (double W, double H) size)
    {
        var d = f.Footprint;
        (double X, double Y, double Over) At(double shift)
        {
            var y = f.Chest + shift * d;
            var side = f.Silhouette.Extent(y - size.H / 2, y + size.H / 2)?.Right ?? f.CenterX;
            var x = Math.Max(side + Gap * d, f.CenterX + 0.04 * d) + size.W / 2;
            return (x, y, Math.Max(0, x + size.W / 2 - (f.Bounds.MaxX - Margin * d)));
        }

        var best = At(0);
        for (var n = 1; n < Shifts.Length; n++)
        {
            if (!(best.Over > 0.5))
            {
                break;
            }

            var candidate = At(Shifts[n]);
            if (candidate.Over < best.Over - 0.5)
            {
                best = candidate;
            }
        }

        var bx = Math.Min(best.X, f.Bounds.MaxX - Margin * d - size.W / 2);
        var by = Math.Min(Math.Max(best.Y, f.Bounds.MinY + Margin * d + size.H / 2), f.Bounds.MaxY - Margin * d - size.H / 2);
        return (bx, by);
    }

    /// <summary>The model: centered over her head, clear of its top (and of a raised hand or prop above it).</summary>
    private static (double X, double Y) ModelAnchor(HudFrame f, (double W, double H) size)
    {
        var d = f.Footprint;
        var bottom = f.HeadTop - HeadGap * d;
        var x = Math.Min(Math.Max(f.CenterX, f.Bounds.MinX + Margin * d + size.W / 2), f.Bounds.MaxX - Margin * d - size.W / 2);
        if (f.Silhouette.Extent(bottom - size.H, bottom) is { } e && e.Right > x - size.W / 2 && e.Left < x + size.W / 2)
        {
            var y = bottom - size.H;
            while (y > f.Bounds.MinY && f.Silhouette.Extent(y - size.H, y) is { } r && r.Right > x - size.W / 2 && r.Left < x + size.W / 2)
            {
                y -= f.Silhouette.Step;
            }

            bottom = y - Gap * d * 0.5;
        }

        var cy = Math.Max(bottom - size.H / 2, f.Bounds.MinY + Margin * d + size.H / 2);
        return (x, cy);
    }

    /// <summary>
    /// The column: cards stacked from her shoulders down at her left, their right edges on one line clear of her outline;
    /// cards from <see cref="Split"/> on stack under the speed at her right. The split follows the room with hysteresis
    /// (<see cref="SpillAfter"/>, <see cref="ReturnAfter"/>); a card whose side changed fades out where it was, jumps, and
    /// fades back in. Writes the column's targets.
    /// </summary>
    private void ColumnAnchors(double dt, HudFrame f, (double W, double H)[] sizes)
    {
        var before = Split;
        _sinceChange += dt;
        if (!_placed || Still)
        {
            (Split, _hugging) = Best(f, sizes);
            _strain = 0;
            _ease = 0;
        }
        else if (Fit(f, sizes, Split, 0, Reach, aligned: true).Over > 0.5)
        {
            _ease = 0;
            _strain += dt;
            if (_strain >= SpillAfter)
            {
                (Split, _hugging) = Best(f, sizes);
                _strain = 0;
            }
        }
        else if (Roomier(f, sizes) is { } back)
        {
            _strain = 0;
            _ease += dt;
            if (_ease >= (_sinceChange < SettleWindow ? SettledReturn : ReturnAfter))
            {
                Split = back;
                _hugging = false;
                _ease = 0;
            }
        }
        else
        {
            _strain = 0;
            _ease = 0;
            _hugging = false;
        }

        var chosen = Fit(f, sizes, Split, 0, Reach, aligned: !_hugging);
        Layout(f, sizes, Split, chosen.Shift, aligned: !_hugging, room: 0, write: true);
        for (var k = 0; k < HudCards.Column.Length; k++)
        {
            var i = (int)HudCards.Column[k];
            var wanted = k >= Split;
            if (wanted == _right[i])
            {
                _hop[i] = Still ? 1 : Math.Min(1, _hop[i] + dt / HopIn);
            }
            else if (_presence[i] == 0 || Still || !_placed)
            {
                _right[i] = wanted;
                _hop[i] = 1;
                _anchorX[i].Snap(_targets[i].X);
                _anchorY[i].Snap(_targets[i].Y);
            }
            else
            {
                _hop[i] = Math.Max(0, _hop[i] - dt / HopOut);
                if (_hop[i] == 0)
                {
                    _right[i] = wanted;
                    _anchorX[i].Snap(_targets[i].X);
                    _anchorY[i].Snap(_targets[i].Y);
                }
            }
        }

        if (_placed && Split != before)
        {
            _sinceChange = 0;
        }
    }

    /// <summary>The most cards at her left, more than now, whose column fits near her shoulders with room to spare; null when none does.</summary>
    private int? Roomier(HudFrame f, (double W, double H)[] sizes)
    {
        if (Split >= HudCards.Column.Length)
        {
            return null;
        }

        for (var k = HudCards.Column.Length; k > Split; k--)
        {
            if (Fit(f, sizes, k, 0.04 * f.Footprint, NearReach, aligned: true).Over == 0)
            {
                return k;
            }
        }

        return null;
    }

    /// <summary>
    /// The most cards at her left whose column fits with its right edges on one line, near her shoulders first, then
    /// anywhere in reach; when none fits, each card hugging her outline where it overflows least.
    /// </summary>
    private (int Split, bool Hugging) Best(HudFrame f, (double W, double H)[] sizes)
    {
        var count = HudCards.Column.Length;
        foreach (var reach in Reaches)
        {
            for (var split = count; split >= 0; split--)
            {
                if (Fit(f, sizes, split, 0, reach, aligned: true).Over <= 0.5)
                {
                    return (split, false);
                }
            }
        }

        var best = (Split: count, Over: double.PositiveInfinity);
        for (var split = count; split >= 0; split--)
        {
            var over = Fit(f, sizes, split, 0, Reach, aligned: false).Over;
            if (over < best.Over - 0.5)
            {
                best = (split, over);
            }
        }

        return (best.Split, true);
    }

    /// <summary>The shift (within reach × D of her shoulders) where the split's column overflows least, and that overflow.</summary>
    private (double Shift, double Over) Fit(HudFrame f, (double W, double H)[] sizes, int split, double room, double reach, bool aligned)
    {
        var best = (Shift: 0.0, Over: double.PositiveInfinity);
        foreach (var shift in Shifts)
        {
            if (Math.Abs(shift) > reach + 1e-9)
            {
                continue;
            }

            var over = Layout(f, sizes, split, shift, aligned, room, write: false);
            if (over < best.Over - 0.5)
            {
                best = (shift, over);
            }

            if (best.Over <= 0.5)
            {
                break;
            }
        }

        return best;
    }

    /// <summary>
    /// Lays the column out and returns how far its shown cards overflow the bounds (points, presence weighted); writes the
    /// targets when <paramref name="write"/>. The left column starts at her shoulders moved by <paramref name="shift"/> (× D);
    /// the right one under the speed.
    /// </summary>
    private double Layout(HudFrame f, (double W, double H)[] sizes, int split, double shift, bool aligned, double room, bool write)
    {
        var d = f.Footprint;
        var gap = ColumnGap * d;
        var cards = HudCards.Column;
        var minX = f.Bounds.MinX + Margin * d;
        var maxX = f.Bounds.MaxX - Margin * d;
        var maxY = f.Bounds.MaxY - Margin * d;
        var over = 0.0;

        var total = -gap;
        for (var k = 0; k < split; k++)
        {
            total += _presence[(int)cards[k]] * (sizes[(int)cards[k]].H + gap);
        }

        var top = f.Shoulders + shift * d;
        top = Math.Min(Math.Max(top, f.Bounds.MinY + Margin * d), maxY - Math.Max(0, total));
        var line = double.PositiveInfinity;
        var y = top;
        for (var k = 0; k < split; k++)
        {
            var i = (int)cards[k];
            var h = sizes[i].H;
            var side = f.Silhouette.Extent(y, y + h)?.Left ?? f.CenterX;
            var edge = Math.Min(side - Gap * d, f.CenterX - 0.04 * d);
            _edges[k] = edge;
            if (write)
            {
                _targets[i].Y = y + h / 2;
            }

            if (_presence[i] > 0.01)
            {
                line = Math.Min(line, edge);
            }

            y += _presence[i] * (h + gap);
        }

        for (var k = 0; k < split; k++)
        {
            var i = (int)cards[k];
            var w = sizes[i].W + room;
            var x = (aligned && double.IsFinite(line) ? line : _edges[k]) - w / 2;
            over += _presence[i] * Math.Max(0, minX - (x - w / 2));
            if (write)
            {
                _targets[i].X = Math.Max(x, minX + w / 2);
            }
        }

        if (split >= cards.Length)
        {
            return over;
        }

        var speed = (int)HudCard.Speed;
        y = _targets[speed].Y + sizes[speed].H / 2 + gap;
        var left = double.NegativeInfinity;
        for (var k = split; k < cards.Length; k++)
        {
            var i = (int)cards[k];
            var h = sizes[i].H;
            var side = f.Silhouette.Extent(y, y + h)?.Right ?? f.CenterX;
            if (_presence[i] > 0.01)
            {
                left = Math.Max(left, Math.Max(side + Gap * d, f.CenterX + 0.04 * d));
            }

            if (write)
            {
                _targets[i].Y = y + h / 2;
            }

            over += _presence[i] * Math.Max(0, y + h - maxY);
            y += _presence[i] * (h + gap);
        }

        if (!double.IsFinite(left))
        {
            left = f.CenterX + 0.04 * d;
        }

        for (var k = split; k < cards.Length; k++)
        {
            var i = (int)cards[k];
            var w = sizes[i].W + room;
            over += _presence[i] * Math.Max(0, left + w - maxX);
            if (write)
            {
                _targets[i].X = Math.Min(left + w / 2, maxX - w / 2);
            }
        }

        return over;
    }
}
