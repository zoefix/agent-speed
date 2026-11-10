using AgentSpeed.Presentation.Motion;

namespace AgentSpeed.Presentation.Orb;

/// <summary>
/// The models card's lines as they move (DESIGN 2a.6, Readouts): at most <c>limit</c> model rows, then the
/// <c>character.models.more</c> line when more models run. A row that changes place glides there (0.3 s, smoothstep;
/// Reduce Motion: at once); a model that stops running fades out of its row and a new one fades in (0.25 s; fades stay
/// with Reduce Motion). Lines that arrive with the card itself show at once, since the card fades in. Times are seconds
/// on the shared orb clock.
/// </summary>
public sealed class OrbModelsMotion
{
    public const double Glide = 0.3;
    public const double Fade = 0.25;

    /// <summary>One line: a model's row, or the more line (<see cref="Row"/> null, <see cref="Hidden"/> models).</summary>
    public sealed class Line
    {
        internal Line(OrbModelRow? row, int hidden, double index, double appearAt)
        {
            Row = row;
            Hidden = hidden;
            From = index;
            To = index;
            AppearAt = appearAt;
        }

        public OrbModelRow? Row { get; internal set; }

        public int Hidden { get; internal set; }

        internal double From { get; set; }

        /// <summary>The line's place (0 the first line).</summary>
        public double To { get; internal set; }

        internal double MoveAt { get; set; } = double.NegativeInfinity;

        internal double AppearAt { get; set; }

        internal double? LeaveAt { get; set; }

        public bool Leaving => LeaveAt is not null;

        internal string Key => Row?.Model ?? string.Empty;

        /// <summary>Where the line is at <paramref name="time"/>, in lines from the first.</summary>
        public double Position(double time) => From + (To - From) * Easing.SmoothStep(0, 1, Ramp(time, MoveAt, Glide));

        public double Alpha(double time) =>
            Math.Min(Ramp(time, AppearAt, Fade), LeaveAt is { } leave ? 1 - Ramp(time, leave, Fade) : 1);

        internal bool IsMoving(double time) =>
            (time >= MoveAt && time < MoveAt + Glide) || (time >= AppearAt && time < AppearAt + Fade)
            || (LeaveAt is { } leave && time >= leave && time < leave + Fade);

        private static double Ramp(double time, double start, double duration) => Math.Clamp((time - start) / duration, 0, 1);
    }

    private readonly List<Line> _lines = new(OrbModelsText.MaxRows + 2);
    private int _version = -1;
    private int _limit = -1;

    /// <summary>Every line drawn now, leaving ones included, in no particular order.</summary>
    public List<Line>.Enumerator GetEnumerator() => _lines.GetEnumerator();

    public IReadOnlyList<Line> Lines => _lines;

    /// <summary>The lines the card makes room for: the rows that stay and the more line.</summary>
    public int Count { get; private set; }

    /// <summary>Follows the card's rows (<paramref name="version"/> changes with them) at most <paramref name="limit"/> at a time.</summary>
    public void Sync(IReadOnlyList<OrbModelRow> rows, int version, int limit, double time, bool reduceMotion)
    {
        ArgumentNullException.ThrowIfNull(rows);
        for (var i = _lines.Count - 1; i >= 0; i--)
        {
            if (_lines[i].LeaveAt is { } leave && time >= leave + Fade)
            {
                _lines.RemoveAt(i);
            }
        }

        if (version == _version && limit == _limit)
        {
            return;
        }

        _version = version;
        _limit = limit;
        var fresh = !_lines.Exists(l => !l.Leaving);
        var shown = rows.Count > limit ? limit : rows.Count;
        var hidden = rows.Count - shown;
        foreach (var line in _lines)
        {
            line.LeaveAt ??= time;
        }

        for (var k = 0; k <= shown; k++)
        {
            if (k == shown && hidden == 0)
            {
                break;
            }

            var row = k < shown ? rows[k] : null;
            var key = row?.Model ?? string.Empty;
            var line = _lines.Find(l => l.Key == key && (l.Row is null) == (row is null));
            if (line is null)
            {
                _lines.Add(new Line(row, hidden, k, fresh ? double.NegativeInfinity : time));
                continue;
            }

            if (line.LeaveAt is { } left)
            {
                var alpha = Math.Min(line.Alpha(time), 1 - Math.Clamp((time - left) / Fade, 0, 1));
                line.LeaveAt = null;
                line.AppearAt = time - alpha * Fade;
            }

            line.Row = row;
            line.Hidden = hidden;
            if (line.To != k)
            {
                line.From = reduceMotion ? k : line.Position(time);
                line.To = k;
                line.MoveAt = time;
            }
        }

        Count = shown + (hidden > 0 ? 1 : 0);
    }

    /// <summary>A line glides or fades at <paramref name="time"/> (the orb needs frames).</summary>
    public bool IsMoving(double time)
    {
        foreach (var line in _lines)
        {
            if (line.IsMoving(time))
            {
                return true;
            }
        }

        return false;
    }
}
