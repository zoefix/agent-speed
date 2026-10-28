namespace AgentSpeed.Presentation.Titles;

public enum TitleCardMove
{
    Left,
    Right,
    Up,
    Down,
}

/// <summary>
/// Arrow-key moves between the theme cards (DESIGN 5, Title themes): row-major within two grids, the presets and then
/// the custom group (custom sets and the New card). Left and right walk the whole order; up and down keep the column,
/// crossing between the two grids.
/// </summary>
public static class TitleCardNavigation
{
    /// <summary>The theme cards' column count: three, two below 560 DIP of content width.</summary>
    public static int Columns(double width) => width < 560 ? 2 : 3;

    /// <param name="current">Index of the focused card in the whole order.</param>
    /// <param name="presets">Cards of the first grid.</param>
    /// <param name="total">Every card: presets, custom sets and the New card when it can take the focus.</param>
    /// <returns>The index to focus; <paramref name="current"/> when the move leaves the grid.</returns>
    public static int Move(int current, TitleCardMove move, int presets, int total, int columns)
    {
        if (total <= 0)
        {
            return 0;
        }

        columns = Math.Max(1, columns);
        presets = Math.Clamp(presets, 0, total);
        current = Math.Clamp(current, 0, total - 1);
        var inPresets = current < presets;
        var groupStart = inPresets ? 0 : presets;
        var column = (current - groupStart) % columns;
        return move switch
        {
            TitleCardMove.Left => Math.Max(current - 1, 0),
            TitleCardMove.Right => Math.Min(current + 1, total - 1),
            TitleCardMove.Down when inPresets && current + columns >= presets =>
                presets < total ? Math.Min(presets + column, total - 1) : current,
            TitleCardMove.Down => current + columns < total ? current + columns : current,
            TitleCardMove.Up when !inPresets && current - columns < presets =>
                presets > 0 ? Math.Min((presets - 1) / columns * columns + column, presets - 1) : current,
            TitleCardMove.Up => current - columns >= 0 ? current - columns : current,
            _ => current,
        };
    }
}
