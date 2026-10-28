using System.Globalization;
using AgentSpeed.Presentation.Layout;

namespace AgentSpeed.Presentation.Hero;

public enum PathVerb : byte
{
    Move,
    Line,
    Quad,
    Cubic,
    Close,
}

/// <summary>One path command. Move/Line use <see cref="P1"/>; Quad uses P1 (control) and P2; Cubic uses P1, P2, P3.</summary>
public readonly record struct PathSegment(PathVerb Verb, PointD P1, PointD P2 = default, PointD P3 = default)
{
    public PointD End => Verb switch
    {
        PathVerb.Quad => P2,
        PathVerb.Cubic => P3,
        _ => P1,
    };
}

/// <summary>
/// Path geometry of the vehicle art (DESIGN 4c "Art format"): absolute <c>M L C Q Z</c> commands, tokens separated by
/// single spaces, numbers with at most two decimals. Parsed once; platforms build their native paths from it.
/// </summary>
public sealed class PathData
{
    private const int CurveSamples = 12;

    private PathData(PathSegment[] segments)
    {
        Segments = segments;
        var first = segments[0].P1;
        double x0 = first.X, y0 = first.Y, x1 = first.X, y1 = first.Y;
        foreach (var point in ControlPoints())
        {
            x0 = Math.Min(x0, point.X);
            y0 = Math.Min(y0, point.Y);
            x1 = Math.Max(x1, point.X);
            y1 = Math.Max(y1, point.Y);
        }

        ControlBounds = RectD.FromEdges(x0, y0, x1, y1);
    }

    public IReadOnlyList<PathSegment> Segments { get; }

    /// <summary>The first point of the path.</summary>
    public PointD Start => Segments[0].P1;

    /// <summary>Bounds of every end and control point.</summary>
    public RectD ControlBounds { get; }

    public static PathData Parse(string d)
    {
        if (string.IsNullOrEmpty(d) || d != d.Trim() || d.Contains("  ", StringComparison.Ordinal))
        {
            throw new FormatException("A path must be a trimmed, single-space separated string.");
        }

        var tokens = d.Split(' ');
        var segments = new List<PathSegment>(tokens.Length / 3 + 1);
        Span<double> values = stackalloc double[6];
        var i = 0;
        while (i < tokens.Length)
        {
            var command = tokens[i];
            var arity = command switch
            {
                "M" or "L" => 2,
                "Q" => 4,
                "C" => 6,
                "Z" => 0,
                _ => throw new FormatException($"Unsupported path token '{command}' (only M L C Q Z)."),
            };

            if (segments.Count == 0 && command != "M")
            {
                throw new FormatException("A path must start with M.");
            }

            if (i + arity > tokens.Length - 1)
            {
                throw new FormatException($"Missing arguments for {command}.");
            }

            for (var k = 0; k < arity; k++)
            {
                values[k] = Number(tokens[i + 1 + k]);
            }

            segments.Add(command switch
            {
                "M" => new PathSegment(PathVerb.Move, new PointD(values[0], values[1])),
                "L" => new PathSegment(PathVerb.Line, new PointD(values[0], values[1])),
                "Q" => new PathSegment(PathVerb.Quad, new PointD(values[0], values[1]), new PointD(values[2], values[3])),
                "C" => new PathSegment(PathVerb.Cubic, new PointD(values[0], values[1]), new PointD(values[2], values[3]),
                    new PointD(values[4], values[5])),
                _ => new PathSegment(PathVerb.Close, default),
            });
            i += 1 + arity;
        }

        return new PathData(segments.ToArray());
    }

    /// <summary>Every end and control point, in order (close commands excluded).</summary>
    public IEnumerable<PointD> ControlPoints()
    {
        foreach (var segment in Segments)
        {
            switch (segment.Verb)
            {
                case PathVerb.Move or PathVerb.Line:
                    yield return segment.P1;
                    break;
                case PathVerb.Quad:
                    yield return segment.P1;
                    yield return segment.P2;
                    break;
                case PathVerb.Cubic:
                    yield return segment.P1;
                    yield return segment.P2;
                    yield return segment.P3;
                    break;
            }
        }
    }

    /// <summary>Points along the path (curves sampled at 12 steps), the same sampling as the spec validator.</summary>
    public IEnumerable<PointD> Sample(int steps = CurveSamples)
    {
        var current = default(PointD);
        var start = default(PointD);
        foreach (var segment in Segments)
        {
            switch (segment.Verb)
            {
                case PathVerb.Move:
                    current = start = segment.P1;
                    yield return current;
                    break;
                case PathVerb.Line:
                    current = segment.P1;
                    yield return current;
                    break;
                case PathVerb.Quad:
                    for (var k = 1; k <= steps; k++)
                    {
                        var t = k / (double)steps;
                        var u = 1 - t;
                        yield return new PointD(
                            u * u * current.X + 2 * u * t * segment.P1.X + t * t * segment.P2.X,
                            u * u * current.Y + 2 * u * t * segment.P1.Y + t * t * segment.P2.Y);
                    }

                    current = segment.P2;
                    break;
                case PathVerb.Cubic:
                    for (var k = 1; k <= steps; k++)
                    {
                        var t = k / (double)steps;
                        var u = 1 - t;
                        yield return new PointD(
                            u * u * u * current.X + 3 * u * u * t * segment.P1.X + 3 * u * t * t * segment.P2.X + t * t * t * segment.P3.X,
                            u * u * u * current.Y + 3 * u * u * t * segment.P1.Y + 3 * u * t * t * segment.P2.Y + t * t * t * segment.P3.Y);
                    }

                    current = segment.P3;
                    break;
                case PathVerb.Close:
                    current = start;
                    yield return current;
                    break;
            }
        }
    }

    private static double Number(string token)
    {
        var span = token.AsSpan();
        var body = span.Length > 0 && span[0] == '-' ? span[1..] : span;
        var dot = body.IndexOf('.');
        var digitsOk = body.Length > 0 && (dot < 0
            ? IsDigits(body)
            : dot > 0 && IsDigits(body[..dot]) && body.Length - dot - 1 is >= 1 and <= 2 && IsDigits(body[(dot + 1)..]));
        if (!digitsOk || !double.TryParse(span, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture, out var value))
        {
            throw new FormatException($"Bad path number '{token}'.");
        }

        return value;
    }

    private static bool IsDigits(ReadOnlySpan<char> span)
    {
        if (span.IsEmpty)
        {
            return false;
        }

        foreach (var c in span)
        {
            if (c is < '0' or > '9')
            {
                return false;
            }
        }

        return true;
    }
}
