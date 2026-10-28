namespace AgentSpeed.Presentation.Menus;

/// <summary>A dot's center and diameter inside the icon box.</summary>
public readonly record struct DotPlacement(double X, double Y, double Diameter);

/// <summary>
/// Packs colour dots into a menu row's 16 × 16 icon box, so an orb's name stays in the text column: one 8 px dot, two
/// to four 6 px dots in rows of two, five to nine 4 px dots in rows of three, 2 px apart, every row centered. Edges
/// fall on whole pixels at 100 %.
/// </summary>
public static class MenuDotLayout
{
    public const double Box = 16;
    public const int MaxDots = 9;
    private const double Gap = 2;

    public static IReadOnlyList<DotPlacement> Layout(int count)
    {
        var n = Math.Clamp(count, 0, MaxDots);
        if (n == 0)
        {
            return [];
        }

        var (diameter, columns) = n switch
        {
            1 => (8.0, 1),
            <= 4 => (6.0, 2),
            _ => (4.0, 3),
        };
        var pitch = diameter + Gap;
        var rows = (n + columns - 1) / columns;
        var top = (Box - (rows * pitch - Gap)) / 2;
        var dots = new List<DotPlacement>(n);
        for (var row = 0; row < rows; row++)
        {
            var inRow = Math.Min(columns, n - row * columns);
            var left = (Box - (inRow * pitch - Gap)) / 2;
            for (var i = 0; i < inRow; i++)
            {
                dots.Add(new DotPlacement(left + i * pitch + diameter / 2, top + row * pitch + diameter / 2, diameter));
            }
        }

        return dots;
    }
}
