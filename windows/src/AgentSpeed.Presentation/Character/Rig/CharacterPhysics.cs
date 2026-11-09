namespace AgentSpeed.Presentation.Character;

/// <summary>
/// Pendulum chains for hair and skirt (physics.py): anchors follow body parameters, wind pushes, outputs are angles.
/// Verlet in fixed 1/240 s substeps, a spring toward the hanging rest shape, damping, two passes of length
/// constraints; each output is tanh(segment angle relative to the previous segment / degrees).
/// </summary>
public sealed class CharacterPhysics
{
    internal const double Gravity = 1400.0;
    internal const double Substep = 1.0 / 240;

    private readonly Chain[] _chains;
    private readonly int? _windSlot;

    /// <param name="slot">Maps a parameter name to its index in the value array the chains read and write (null: absent, reads 0).</param>
    public CharacterPhysics(IReadOnlyList<RigDocument.Chain> specs, Func<string, int?> slot, double windSign = 1)
    {
        ArgumentNullException.ThrowIfNull(specs);
        ArgumentNullException.ThrowIfNull(slot);
        _chains = specs.Select(s => new Chain(s, slot)).ToArray();
        _windSlot = slot("Wind");
        WindSign = windSign;
    }

    public double WindSign { get; }

    public void Reset(ReadOnlySpan<double> values)
    {
        foreach (var c in _chains)
        {
            c.Reset(values);
        }
    }

    /// <summary>Steps every chain with the current values (read only).</summary>
    public void Step(double dt, ReadOnlySpan<double> values)
    {
        var wind = _windSlot is { } w ? values[w] : 0;
        foreach (var c in _chains)
        {
            c.Step(dt, values, wind, WindSign);
        }
    }

    /// <summary>Writes the chains' output parameters.</summary>
    public void Write(Span<double> values)
    {
        foreach (var c in _chains)
        {
            c.Write(values);
        }
    }

    private sealed class Chain
    {
        private readonly RigDocument.Chain _spec;
        private readonly (int? Slot, double X, double Y)[] _inputs;
        private readonly (int? Slot, int Segment, double Degrees)[] _outputs;
        private readonly double[] _px;
        private readonly double[] _py;
        private readonly double[] _qx;
        private readonly double[] _qy;
        private double _time;

        public Chain(RigDocument.Chain spec, Func<string, int?> slot)
        {
            _spec = spec;
            _inputs = spec.Inputs.Select(i => (slot(i.Param), i.X ?? 0, i.Y ?? 0)).ToArray();
            _outputs = spec.Outputs.Select(o => (slot(o.Param), o.Segment, o.Degrees)).ToArray();
            var n = spec.Segments.Length + 1;
            _px = new double[n];
            _py = new double[n];
            _qx = new double[n];
            _qy = new double[n];
        }

        private (double X, double Y) Anchor(ReadOnlySpan<double> values)
        {
            double sx = 0, sy = 0;
            foreach (var i in _inputs)
            {
                var v = i.Slot is { } s ? values[s] : 0;
                sx += v * i.X;
            }

            foreach (var i in _inputs)
            {
                var v = i.Slot is { } s ? values[s] : 0;
                sy += v * i.Y;
            }

            return (_spec.Anchor[0] + sx, _spec.Anchor[1] + sy);
        }

        public void Reset(ReadOnlySpan<double> values)
        {
            var (ax, ay) = Anchor(values);
            _px[0] = ax;
            _py[0] = ay;
            for (var i = 1; i < _px.Length; i++)
            {
                _px[i] = _px[i - 1];
                _py[i] = _py[i - 1] + _spec.Segments[i - 1];
            }

            Array.Copy(_px, _qx, _px.Length);
            Array.Copy(_py, _qy, _py.Length);
            _time = 0;
        }

        public void Step(double dt, ReadOnlySpan<double> values, double wind, double windSign)
        {
            var steps = Math.Max(1, (int)Math.Round(dt / Substep, MidpointRounding.ToEven));
            var h = dt / steps;
            var k = _spec.Stiffness;
            var keep = Math.Max(0.0, 1 - _spec.Damping * h);
            for (var step = 0; step < steps; step++)
            {
                _time += h;
                var gust = 1 + 0.35 * Math.Sin(_time * 7.3) + 0.2 * Math.Sin(_time * 13.1 + 1.7);
                var forceX = windSign * wind * _spec.Wind * Gravity * gust;
                var (ax, ay) = Anchor(values);
                _px[0] = ax;
                _py[0] = ay;
                var rx = ax;
                var ry = ay;
                for (var i = 1; i < _px.Length; i++)
                {
                    ry = ry + _spec.Segments[i - 1];
                    rx = rx + 0;
                    var vx = (_px[i] - _qx[i]) * keep;
                    var vy = (_py[i] - _qy[i]) * keep;
                    var accX = forceX + k * (rx - _px[i]);
                    var accY = Gravity + k * (ry - _py[i]);
                    _qx[i] = _px[i];
                    _qy[i] = _py[i];
                    _px[i] = _px[i] + vx + accX * h * h;
                    _py[i] = _py[i] + vy + accY * h * h;
                }

                for (var pass = 0; pass < 2; pass++)
                {
                    for (var i = 1; i < _px.Length; i++)
                    {
                        var dx = _px[i] - _px[i - 1];
                        var dy = _py[i] - _py[i - 1];
                        var dist = Math.Max(Hypot(dx, dy), 1e-6);
                        var r = _spec.Segments[i - 1] / dist;
                        _px[i] = _px[i - 1] + dx * r;
                        _py[i] = _py[i - 1] + dy * r;
                    }
                }
            }
        }

        public void Write(Span<double> values)
        {
            var parent = 0.0;
            foreach (var o in _outputs)
            {
                var i = o.Segment;
                var angle = Math.Atan2(_px[i + 1] - _px[i], _py[i + 1] - _py[i]) * (180.0 / Math.PI);
                if (o.Slot is { } slot)
                {
                    values[slot] = Math.Tanh((angle - parent) / o.Degrees);
                }

                parent = angle;
            }
        }
    }

    /// <summary>C's <c>hypot</c>, which Swift and Python call: no intermediate overflow.</summary>
    internal static double Hypot(double x, double y)
    {
        x = Math.Abs(x);
        y = Math.Abs(y);
        if (x < y)
        {
            (x, y) = (y, x);
        }

        if (x == 0)
        {
            return 0;
        }

        var r = y / x;
        return x * Math.Sqrt(1 + r * r);
    }
}
