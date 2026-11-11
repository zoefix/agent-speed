using System.Collections.Immutable;

namespace AgentSpeed.Presentation.Character;

/// <summary>
/// The cards of the character's floating readout (DESIGN 2a.6, <i>Readouts</i>): the speed on her right with the running
/// models under it, the model above her head, and the column of facts on her left, top to bottom.
/// </summary>
public enum HudCard
{
    Speed,
    Model,
    Performance,
    Network,
    Cost,
    Agents,
    Models,

    /// <summary>額度: the column's last card (QUOTAS 6).</summary>
    Quota,
}

public static class HudCards
{
    public const int Count = 8;

    /// <summary>The left column, top to bottom.</summary>
    public static ImmutableArray<HudCard> Column { get; } = [HudCard.Performance, HudCard.Network, HudCard.Cost, HudCard.Agents, HudCard.Quota];

    public static ImmutableArray<HudCard> All { get; } =
        [HudCard.Speed, HudCard.Model, HudCard.Performance, HudCard.Network, HudCard.Cost, HudCard.Agents, HudCard.Models, HudCard.Quota];
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
/// Places the readout cards beside her (DESIGN 2a.6): each has its own anchor — the speed at her right at chest height
/// with the models card under it, the model above her head, the facts in a column at her left from her shoulders down —
/// kept clear of her outline, never behind her. Anchors trail her on soft springs, and every card floats on its own slow
/// figure of eight, bobs, tilts and breathes. A card that shows or hides fades and rises or sinks; the column closes up
/// around it. When her left side has no room for the whole column, its last cards continue under the speed on her right
/// (under the models card while it shows); a card changing sides fades out and back in at its new place instead of
/// crossing her. Where her right side is crowded under the speed, the models card steps right of her outline and narrows,
/// then moves down past the spilled cards (up to 0.3 D below them) before it hides. With <see cref="Balance"/> the column's
/// last cards also move under the models card until both sides weigh about the same, chosen again only when a card comes,
/// goes or changes size.
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

    /// <summary>A hidden models card comes back once it has fit with <see cref="ModelsSpare"/> to spare for this long.</summary>
    public const double ModelsReturnAfter = 1.2;

    /// <summary>The room (× D) a hidden or lowered models card needs to spare before it comes back.</summary>
    public const double ModelsSpare = 0.04;

    /// <summary>How far (× D) a side's weight may move before the balance is chosen again.</summary>
    public const double Reweigh = 0.06;

    /// <summary>Weights this close (points) are a tie, which goes to fewer moved cards.</summary>
    private const double Tie = 0.5;

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
    private readonly double[] _edges = new double[HudCards.Column.Length];
    private double _strain;
    private double _ease;
    private ModelsPlace _modelsPlace = ModelsPlace.UnderSpeed;
    private ModelsPlace? _modelsHop;
    private double _modelsStrain;
    private double _modelsEase;
    private double _modelsTop;
    private bool _snapModels;
    private double _sinceChange = double.PositiveInfinity;
    private double _lastGap = double.PositiveInfinity;
    private bool _hugging;
    private bool _waiting;
    private bool _placed;

    // The splits in order of preference (committed and pending), the cards and weights they were chosen for, and the
    // pending split's hold.
    private readonly int[] _rank = new int[HudCards.Column.Length + 1];
    private readonly int[] _next = new int[HudCards.Column.Length + 1];
    private readonly double[] _odds = new double[HudCards.Column.Length + 1];
    private readonly int[] _shownAt = new int[HudCards.Column.Length];
    private readonly bool[] _basisShown = new bool[HudCards.Count];
    private readonly double[] _basisHeight = new double[HudCards.Count];
    private bool _basisBalance;
    private int _rankCount;
    private int _nextCount;
    private bool _pending;
    private int _candidate = -1;
    private double _held;

    /// <summary>Where the models card goes: right under the speed, below the spilled cards, or nowhere (it hides).</summary>
    public enum ModelsPlace
    {
        UnderSpeed,
        BelowSpill,
        Hidden,
    }

    /// <summary>Where the models card is placed now (it changes only after the new place has been wanted for a while).</summary>
    public ModelsPlace ModelsAt => _modelsPlace;

    /// <summary>
    /// The room from the models card's left edge to the orb's edge at its place this frame (points): the card narrows its
    /// names, then drops them, to fit it.
    /// </summary>
    public double ModelsWidth { get; private set; } = double.PositiveInfinity;

    /// <summary>The models card's width in its narrowest form, dots and speeds only (points); the renderer sets it each frame.</summary>
    public double ModelsNarrowest { get; set; }

    /// <summary>
    /// The 額度 card's narrowest form (no resets, short bars, no headers; points); the renderer sets it each frame. Only
    /// when even that does not fit does the card spill over like the column's other cards.
    /// </summary>
    public double QuotaNarrowest { get; set; }

    /// <summary>The room from the 額度 card's column edge to the orb's edge at its place this frame (points).</summary>
    public double QuotaRoom { get; private set; } = double.PositiveInfinity;

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

    /// <summary>
    /// Splits the column between her two sides so they weigh about the same (DESIGN 2a.6, Balance). Off below S = 96, where
    /// the column stays at her left and only spills over.
    /// </summary>
    public bool Balance { get; set; } = true;

    /// <summary>The card is drawn at her right (the speed, the models card, and column cards moved there by the balance or the spill-over).</summary>
    public bool IsAtHerRight(HudCard card) => card is HudCard.Speed or HudCard.Models || _right[(int)card];

    /// <summary>
    /// Advances the layout one frame. <paramref name="given"/> are the cards' current sizes in points and
    /// <paramref name="shown"/> which cards should show (switched on and not quiet), both indexed by <see cref="HudCard"/>.
    /// </summary>
    public void Update(double dt, double time, HudFrame f, IReadOnlyList<(double W, double H)> given, IReadOnlyList<bool> shown)
    {
        ArgumentNullException.ThrowIfNull(f);
        ArgumentNullException.ThrowIfNull(given);
        ArgumentNullException.ThrowIfNull(shown);
        if (given.Count < HudCards.Count || shown.Count < HudCards.Count)
        {
            // Cards past the lists' ends are hidden with no size.
            given = [.. Enumerable.Range(0, HudCards.Count).Select(i => i < given.Count ? given[i] : (0.0, 0.0))];
            shown = [.. Enumerable.Range(0, HudCards.Count).Select(i => i < shown.Count && shown[i])];
        }

        for (var i = 0; i < _presence.Length; i++)
        {
            var target = shown[i] && (i != (int)HudCard.Models || _modelsPlace != ModelsPlace.Hidden) ? 1.0 : 0.0;
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
        ChooseModelsPlace(dt, f, shown[(int)HudCard.Models]);
        if (!_placed && _modelsPlace == ModelsPlace.Hidden)
        {
            _presence[(int)HudCard.Models] = 0;
        }

        ColumnAnchors(dt, f, _sizes, given, shown);
        ModelsAnchor(f);

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
    /// cards from <see cref="Split"/> on stack under the speed at her right. The split is the best one of the preferred
    /// order (<see cref="Rank"/>) that fits, chosen again when the cards or their weights change (<see cref="Steady"/>) and
    /// following the room with hysteresis (<see cref="SpillAfter"/>, <see cref="ReturnAfter"/>), never twice within
    /// <see cref="ChangeWait"/> (the cards hug her outline meanwhile); a card whose side changed fades out where it was,
    /// jumps, and fades back in. Writes the column's targets.
    /// </summary>
    private void ColumnAnchors(double dt, HudFrame f, (double W, double H)[] sizes, IReadOnlyList<(double W, double H)> given,
        IReadOnlyList<bool> shown)
    {
        var before = Split;
        _sinceChange += dt;
        _waiting = false;
        if (Reweighed(given, shown, f.Footprint) || !_placed)
        {
            _pending = true;
        }

        if (_pending)
        {
            _nextCount = Rank(given, shown, ColumnGap * f.Footprint, _next);
            if (!_placed)
            {
                Adopt();
            }
        }

        if (!_placed || Still)
        {
            if (_pending)
            {
                Steady(dt, f, sizes);
            }

            (Split, _hugging) = Best(f, sizes, _rank, _rankCount);
            _strain = 0;
            _ease = 0;
        }
        else if (Fit(f, sizes, Split, 0, Reach, aligned: true).Over > 0.5)
        {
            _ease = 0;
            _strain += dt;
            if (_strain >= SpillAfter && _sinceChange < ChangeWait)
            {
                // A card changes sides at most once per ChangeWait; until then the cards hug her outline where they are.
                _waiting = true;
            }
            else if (_strain >= SpillAfter)
            {
                if (_pending)
                {
                    Adopt();
                }

                (Split, _hugging) = Best(f, sizes, _rank, _rankCount);
                _strain = 0;
            }
        }
        else if (_pending)
        {
            _strain = 0;
            _ease = 0;
            Steady(dt, f, sizes);
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

        var aligned = !_hugging && !_waiting;
        var chosen = Fit(f, sizes, Split, 0, Reach, aligned);
        Layout(f, sizes, Split, chosen.Shift, aligned, room: 0, write: true);
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
            _lastGap = _sinceChange;
            _sinceChange = 0;
        }
    }

    /// <summary>
    /// The pending order's best split that fits: kept once it has held for <see cref="SpillAfter"/>, at most once in
    /// <see cref="ReturnAfter"/> (<see cref="SettledReturn"/> while the last two changes came within
    /// <see cref="SettleWindow"/> of each other). Taken at once when it is the split in place.
    /// </summary>
    private void Steady(double dt, HudFrame f, (double W, double H)[] sizes)
    {
        var (candidate, hugging) = Best(f, sizes, _next, _nextCount);
        if (candidate == Split)
        {
            Adopt();
            return;
        }

        if (candidate != _candidate)
        {
            _candidate = candidate;
            _held = 0;
        }

        _held += dt;
        if (_held >= SpillAfter && _sinceChange >= ChangeWait)
        {
            Adopt();
            Split = candidate;
            _hugging = hugging;
        }
    }

    /// <summary>
    /// How long after the last change the split may change again: <see cref="ReturnAfter"/>, or <see cref="SettledReturn"/>
    /// when the last two changes came within <see cref="SettleWindow"/>, whether balance or spill-over moves the cards.
    /// </summary>
    private double ChangeWait => _lastGap < SettleWindow ? SettledReturn : ReturnAfter;

    /// <summary>The pending order becomes the one the split follows.</summary>
    private void Adopt()
    {
        Array.Copy(_next, _rank, _nextCount);
        _rankCount = _nextCount;
        _pending = false;
        _candidate = -1;
        _held = 0;
    }

    /// <summary>Whether the card weighs on a side: a shown card of the column, or the models card while it is placed.</summary>
    private bool Weighs(HudCard card, IReadOnlyList<bool> shown) =>
        card == HudCard.Models ? shown[(int)card] && _modelsPlace != ModelsPlace.Hidden : HudCards.Column.Contains(card) && shown[(int)card];

    /// <summary>
    /// Whether the split must be chosen again since the last time: a card that weighs came or went, the balance was switched,
    /// or a side's weight moved by more than <see cref="Reweigh"/> × D. Remembers what it compared against when it did.
    /// </summary>
    private bool Reweighed(IReadOnlyList<(double W, double H)> given, IReadOnlyList<bool> shown, double d)
    {
        var changed = Balance != _basisBalance;
        double left = 0, right = 0;
        for (var i = 0; i < HudCards.Count && !changed; i++)
        {
            var card = (HudCard)i;
            var on = Weighs(card, shown);
            changed = on != _basisShown[i];
            if (on && !changed)
            {
                var moved = given[i].H - _basisHeight[i];
                if (card != HudCard.Models && HudCards.Column.IndexOf(card) < Split)
                {
                    left += moved;
                }
                else
                {
                    right += moved;
                }
            }
        }

        if (!changed && Math.Abs(left) <= Reweigh * d && Math.Abs(right) <= Reweigh * d)
        {
            return false;
        }

        for (var i = 0; i < HudCards.Count; i++)
        {
            _basisShown[i] = Weighs((HudCard)i, shown);
            _basisHeight[i] = given[i].H;
        }

        _basisBalance = Balance;
        return true;
    }

    /// <summary>
    /// The splits in order of preference, into <paramref name="into"/>: with <see cref="Balance"/>, by how evenly they weigh
    /// her two sides (a side's cards' heights and the gaps between them; her right is the models card and the moved cards),
    /// a tie to fewer moved cards; without, the most cards at her left first. A split is the column index of the first moved
    /// shown card (the column's length for none). Returns how many there are.
    /// </summary>
    private int Rank(IReadOnlyList<(double W, double H)> given, IReadOnlyList<bool> shown, double gap, int[] into)
    {
        var cards = HudCards.Column;
        var n = 0;
        for (var k = 0; k < cards.Length; k++)
        {
            if (Weighs(cards[k], shown))
            {
                _shownAt[n++] = k;
            }
        }

        var models = Weighs(HudCard.Models, shown);
        for (var moved = 0; moved <= n; moved++)
        {
            double left = 0, right = models ? given[(int)HudCard.Models].H : 0;
            var rightCount = models ? 1 : 0;
            for (var j = 0; j < n; j++)
            {
                var h = given[(int)cards[_shownAt[j]]].H;
                if (j < n - moved)
                {
                    left += h + (j > 0 ? gap : 0);
                }
                else
                {
                    right += h + (rightCount++ > 0 ? gap : 0);
                }
            }

            var split = moved == 0 ? cards.Length : _shownAt[n - moved];
            var odds = Balance ? Math.Abs(left - right) : moved;
            var at = moved;
            while (at > 0 && odds < _odds[at - 1] - Tie)
            {
                into[at] = into[at - 1];
                _odds[at] = _odds[at - 1];
                at--;
            }

            into[at] = split;
            _odds[at] = odds;
        }

        return n + 1;
    }

    /// <summary>The best-ranked split, ahead of the one in place, whose column fits near her shoulders with room to spare; null when none does.</summary>
    private int? Roomier(HudFrame f, (double W, double H)[] sizes)
    {
        for (var r = 0; r < _rankCount && _rank[r] != Split; r++)
        {
            if (Fit(f, sizes, _rank[r], 0.04 * f.Footprint, NearReach, aligned: true).Over == 0)
            {
                return _rank[r];
            }
        }

        return null;
    }

    /// <summary>
    /// The best-ranked split whose column fits with its right edges on one line, near her shoulders first, then anywhere in
    /// reach; when none fits, each card hugging her outline where it overflows least.
    /// </summary>
    private (int Split, bool Hugging) Best(HudFrame f, (double W, double H)[] sizes, int[] rank, int count)
    {
        foreach (var reach in Reaches)
        {
            for (var r = 0; r < count; r++)
            {
                if (Fit(f, sizes, rank[r], 0, reach, aligned: true).Over <= 0.5)
                {
                    return (rank[r], false);
                }
            }
        }

        var best = (Split: rank[0], Over: double.PositiveInfinity);
        for (var r = 0; r < count; r++)
        {
            var over = Fit(f, sizes, rank[r], 0, Reach, aligned: false).Over;
            if (over < best.Over - 0.5)
            {
                best = (rank[r], over);
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
            var edge = aligned && double.IsFinite(line) ? line : _edges[k];
            var x = edge - w / 2;
            var fit = OverflowWidth(i, sizes[i].W) + room;
            over += _presence[i] * Math.Max(0, minX - (edge - fit));
            if (write)
            {
                _targets[i].X = Math.Max(x, minX + w / 2);
                if (i == (int)HudCard.Quota)
                {
                    QuotaRoom = Math.Max(0, edge - minX);
                }
            }
        }

        if (split >= cards.Length)
        {
            return over;
        }

        y = SpeedBottom(sizes) + gap;
        if (_modelsPlace == ModelsPlace.UnderSpeed)
        {
            var models = (int)HudCard.Models;
            y += _presence[models] * (sizes[models].H + gap);
        }

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

        // Under the models card, never left of its edge: the right side reads as one column.
        var modelsCard = (int)HudCard.Models;
        if (_modelsPlace == ModelsPlace.UnderSpeed && sizes[modelsCard].H > 0 && _presence[modelsCard] > 0.01)
        {
            left = Math.Max(left, Math.Min(ModelsLeft(f, ModelsUnder(f)), maxX - sizes[modelsCard].W));
        }

        for (var k = split; k < cards.Length; k++)
        {
            var i = (int)cards[k];
            var w = sizes[i].W + room;
            over += _presence[i] * Math.Max(0, left + OverflowWidth(i, sizes[i].W) + room - maxX);
            if (write)
            {
                _targets[i].X = Math.Min(left + w / 2, maxX - w / 2);
                if (i == (int)HudCard.Quota)
                {
                    QuotaRoom = Math.Max(0, maxX - left);
                }
            }
        }

        return over;
    }

    /// <summary>The width a card needs to fit: the 額度 card narrows itself to the room, so only its narrowest form counts.</summary>
    private double OverflowWidth(int card, double width) =>
        card == (int)HudCard.Quota && QuotaNarrowest > 0 ? Math.Min(width, QuotaNarrowest) : width;

    private double SpeedBottom((double W, double H)[] sizes)
    {
        var speed = (int)HudCard.Speed;
        return _targets[speed].Y + sizes[speed].H / 2;
    }

    /// <summary>
    /// The models card's left edge with its top at <paramref name="top"/>: on the speed's line, or further right where her
    /// outline under the speed is wider, <see cref="Gap"/> clear of it over the rows the card spans.
    /// </summary>
    private double ModelsLeft(HudFrame f, double top)
    {
        var speed = (int)HudCard.Speed;
        var line = _targets[speed].X - _sizes[speed].W / 2;
        return f.Silhouette.Extent(top, top + _sizes[(int)HudCard.Models].H) is { } e ? Math.Max(line, e.Right + Gap * f.Footprint) : line;
    }

    /// <summary>The room from the card's left edge to the orb's edge with its top at <paramref name="top"/>; 0 past the bottom.</summary>
    private double ModelsRoomAt(HudFrame f, double top)
    {
        var d = f.Footprint;
        return top + _sizes[(int)HudCard.Models].H > f.Bounds.MaxY - Margin * d + 0.5
            ? 0
            : Math.Max(0, f.Bounds.MaxX - Margin * d - ModelsLeft(f, top));
    }

    /// <summary>The card in its narrowest form (dots and speeds) fits with its top at <paramref name="top"/>, with <paramref name="spare"/> to spare.</summary>
    private bool ModelsFit(HudFrame f, double top, double spare) => ModelsRoomAt(f, top) + 0.5 >= ModelsNarrowest + spare;

    /// <summary>The top right under the speed title chip.</summary>
    private double ModelsUnder(HudFrame f) => SpeedBottom(_sizes) + ColumnGap * f.Footprint;

    /// <summary>The first top from the spilled cards' bottom down to 0.3 D below them where the card fits; null for none.</summary>
    private double? ModelsBelowSpill(HudFrame f, double spare)
    {
        var d = f.Footprint;
        var gap = ColumnGap * d;
        var y = ModelsUnder(f);
        var cards = HudCards.Column;
        for (var k = Split; k < cards.Length; k++)
        {
            var i = (int)cards[k];
            y += _presence[i] * (_sizes[i].H + gap);
        }

        var step = Math.Max(1, f.Silhouette.Step);
        for (var top = y; top <= y + Reach * d + 1e-9; top += step)
        {
            if (ModelsFit(f, top, spare))
            {
                return top;
            }
        }

        return null;
    }

    /// <summary>The best place the card fits now, with <paramref name="spare"/> to spare: under the speed, below the spilled cards, or hidden.</summary>
    private (ModelsPlace Place, double Top) ModelsWanted(HudFrame f, double spare)
    {
        var under = ModelsUnder(f);
        if (ModelsFit(f, under, spare))
        {
            return (ModelsPlace.UnderSpeed, under);
        }

        return ModelsBelowSpill(f, spare) is { } top ? (ModelsPlace.BelowSpill, top) : (ModelsPlace.Hidden, under);
    }

    /// <summary>
    /// Picks the models card's place (DESIGN 2a.6, Models: place and width): where it stands, as long as it fits there; once
    /// it has not fit for <see cref="SpillAfter"/>, the best place that fits (lower, or hidden); from hidden back once it has
    /// fit with 0.04 D to spare for <see cref="ModelsReturnAfter"/>, from the lower place back up after
    /// <see cref="ReturnAfter"/>. At once while still, before the first frame or while the card is switched off or quiet.
    /// Moving between its two places it fades out where it was and back in at the new one, like a card changing sides.
    /// </summary>
    private void ChooseModelsPlace(double dt, HudFrame f, bool shown)
    {
        var i = (int)HudCard.Models;
        var fits = ModelsWanted(f, 0);
        if (_modelsHop is { } next)
        {
            _hop[i] = Math.Max(0, _hop[i] - dt / HopOut);
            if (_hop[i] == 0)
            {
                _modelsPlace = next;
                _modelsHop = null;
                _snapModels = true;
            }

            ModelsWidth = ModelsRoomAt(f, ModelsTopOf(f, _modelsPlace));
            return;
        }

        _hop[i] = Math.Min(1, _hop[i] + dt / HopIn);
        if (!_placed || Still || !shown)
        {
            _snapModels |= fits.Place != _modelsPlace && _presence[i] == 0;
            _modelsPlace = fits.Place;
            _modelsTop = fits.Top;
            _modelsStrain = 0;
            _modelsEase = 0;
        }
        else if (_modelsPlace != ModelsPlace.Hidden && !ModelsFit(f, ModelsTopOf(f, _modelsPlace), 0))
        {
            _modelsEase = 0;
            _modelsStrain += dt;
            if (_modelsStrain >= SpillAfter)
            {
                Move(fits.Place);
            }
        }
        else if (ModelsWanted(f, ModelsSpare * f.Footprint) is var roomy && roomy.Place < _modelsPlace)
        {
            _modelsStrain = 0;
            _modelsEase += dt;
            if (_modelsEase >= (_modelsPlace == ModelsPlace.Hidden ? ModelsReturnAfter : ReturnAfter))
            {
                Move(roomy.Place);
            }
        }
        else
        {
            _modelsStrain = 0;
            _modelsEase = 0;
        }

        ModelsWidth = ModelsRoomAt(f, ModelsTopOf(f, _modelsPlace));

        void Move(ModelsPlace place)
        {
            _modelsStrain = 0;
            _modelsEase = 0;
            if (place != ModelsPlace.Hidden && _modelsPlace != ModelsPlace.Hidden && _presence[i] > 0)
            {
                _modelsHop = place;
                return;
            }

            _snapModels |= _presence[i] == 0;
            _modelsPlace = place;
        }
    }

    /// <summary>The card's top in a place: under the speed, or the first fitting row below the spilled cards (the last one found when none fits now).</summary>
    private double ModelsTopOf(HudFrame f, ModelsPlace place)
    {
        if (place != ModelsPlace.BelowSpill)
        {
            return ModelsUnder(f);
        }

        if (ModelsBelowSpill(f, 0) is { } top)
        {
            _modelsTop = top;
        }

        return _modelsTop;
    }

    /// <summary>The models card's target: its left edge on the speed's line or right of her, right under the speed or below the spilled cards.</summary>
    private void ModelsAnchor(HudFrame f)
    {
        var d = f.Footprint;
        var i = (int)HudCard.Models;
        var (w, h) = _sizes[i];
        var top = _modelsPlace == ModelsPlace.Hidden && _presence[i] > 0 ? _targets[i].Y - h / 2 : ModelsTopOf(f, _modelsPlace);
        var left = Math.Min(ModelsLeft(f, top), f.Bounds.MaxX - Margin * d - w);
        _targets[i] = (left + w / 2, top + h / 2);
        if (_snapModels)
        {
            _anchorX[i].Snap(_targets[i].X);
            _anchorY[i].Snap(_targets[i].Y);
            _snapModels = false;
        }
    }
}
