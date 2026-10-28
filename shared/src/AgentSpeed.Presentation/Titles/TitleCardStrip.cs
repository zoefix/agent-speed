using AgentSpeed.Presentation.Heat;

namespace AgentSpeed.Presentation.Titles;

/// <summary>
/// The preview strip of a theme card (DESIGN 5, Title themes): levels 1, 5, 9 and 13 joined by <c> · </c>. It shows
/// whole titles only: when all four do not fit, it drops middle titles first and keeps the first and the last, so it
/// never ends on a truncated title.
/// </summary>
public static class TitleCardStrip
{
    public const string Separator = " · ";

    /// <summary>The levels a strip can show.</summary>
    public static IReadOnlyList<SpeedTitle> Levels { get; } = [SpeedTitle.Walk, SpeedTitle.Car, SpeedTitle.Maglev, SpeedTitle.Rocket];

    /// <summary>Subsets of <see cref="Levels"/> (indexes) from most to fewest titles, keeping the first and last while they fit.</summary>
    public static IReadOnlyList<IReadOnlyList<int>> Choices { get; } = [[0, 1, 2, 3], [0, 2, 3], [0, 1, 3], [0, 3], [3], [0]];

    /// <summary>
    /// The first choice whose titles fit <paramref name="width"/> whole, as indexes into <see cref="Levels"/>; the
    /// fewest titles when none does.
    /// </summary>
    /// <param name="titles">The texts of the four <see cref="Levels"/>.</param>
    /// <param name="measure">Width of a strip text in the strip's font.</param>
    public static IReadOnlyList<int> Choose(IReadOnlyList<string> titles, double width, Func<string, double> measure)
    {
        ArgumentNullException.ThrowIfNull(titles);
        ArgumentNullException.ThrowIfNull(measure);
        if (titles.Count != Levels.Count)
        {
            throw new ArgumentException("A strip has four titles.", nameof(titles));
        }

        foreach (var choice in Choices)
        {
            if (Math.Ceiling(measure(Join(titles, choice))) <= width)
            {
                return choice;
            }
        }

        return Choices[^1];
    }

    public static string Join(IReadOnlyList<string> titles, IReadOnlyList<int> choice) =>
        string.Join(Separator, choice.Select(index => titles[index]));
}
