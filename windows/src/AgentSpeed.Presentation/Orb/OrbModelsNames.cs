namespace AgentSpeed.Presentation.Orb;

/// <summary>The models card's widths in one frame (points): its columns and the spacing around them.</summary>
/// <param name="Longest">The longest display name.</param>
/// <param name="Unit">The unit after the first row's number; 0 when the first row has no speed.</param>
/// <param name="More">The more line; 0 without one.</param>
/// <param name="Indent">The dot and the gap after it.</param>
/// <param name="Plate">Both paddings of the plate.</param>
public readonly record struct OrbModelsColumns(double Longest, double Number, double Unit, double More, double Indent, double NameGap, double CellGap,
    double Plate)
{
    /// <summary>The card's width with names <paramref name="names"/> points wide; 0 drops the names (the dot and the speed).</summary>
    public double Width(double names)
    {
        var row = Indent + (names > 0 ? Math.Min(Longest, names) + NameGap : 0) + Number + (Unit > 0 ? CellGap + Unit : 0);
        return Plate + Math.Max(row, More > 0 ? Indent + More : 0);
    }
}

/// <summary>
/// The models card's name width (DESIGN 2a.6, Models: place and width): <c>9 t</c>, narrowed in half steps as far as
/// <c>4 t</c> to fit the room to the orb's edge at the card's place, then none (the rows keep the dot and the speed).
/// Narrower at once; wider only after the wider card has fit with <c>0.04 D</c> to spare for 1.2 s; at once while the
/// card is still or not drawn.
/// </summary>
public sealed class OrbModelsNames
{
    public const double Widest = 9;
    public const double Narrowest = 4;
    public const double Step = 0.5;
    public const double GrowAfter = 1.2;

    private double _grow;

    /// <summary>The name width in <c>t</c>; 0 when the rows show only the dot and the speed.</summary>
    public double Room { get; private set; } = Widest;

    /// <summary>Follows the room (points) at the card's place; <paramref name="spare"/> is 0.04 D in points.</summary>
    public void Update(in OrbModelsColumns columns, double t, double room, double spare, double dt, bool atOnce)
    {
        var current = columns.Width(Room * t);
        if (atOnce || current > room + 0.5)
        {
            Room = Fitting(columns, t, room);
            _grow = 0;
            return;
        }

        var wider = Fitting(columns, t, room - spare);
        if (wider > Room && columns.Width(wider * t) > current)
        {
            _grow += dt;
            if (_grow >= GrowAfter)
            {
                Room = wider;
                _grow = 0;
            }
        }
        else
        {
            _grow = 0;
        }
    }

    /// <summary>The widest name width (in <c>t</c>) whose card fits <paramref name="room"/>; 0 when only the names' absence does.</summary>
    public static double Fitting(in OrbModelsColumns columns, double t, double room)
    {
        for (var names = Widest; names >= Narrowest; names -= Step)
        {
            if (columns.Width(names * t) <= room + 0.5)
            {
                return names;
            }
        }

        return 0;
    }
}
