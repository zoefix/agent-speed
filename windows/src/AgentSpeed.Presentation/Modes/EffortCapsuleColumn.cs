namespace AgentSpeed.Presentation.Modes;

/// <summary>
/// The agents' shared capsule column (DESIGN 4, Agents): as wide as the widest capsule among the visible rows and no
/// space when none shows; every word drops when the column would leave a project name under 72 DIP.
/// </summary>
public readonly record struct EffortCapsuleColumn(double Width, bool Compact)
{
    /// <summary>Between the column and the live value.</summary>
    public const double Gap = 8;

    public const double MinimumName = 72;

    public static EffortCapsuleColumn None => default;

    /// <summary>Space the column takes in a row, its gap included.</summary>
    public double Footprint => Width > 0 ? Width + Gap : 0;

    /// <param name="nameRoom">The narrowest row's name width without any column (its deepest indent taken off).</param>
    /// <param name="wordWidth">Measures a word at 11 DIP medium.</param>
    /// <param name="markWidth">The configured mark at 9.5 DIP medium.</param>
    public static EffortCapsuleColumn For(IEnumerable<EffortCapsule> capsules, double nameRoom, Func<string, double> wordWidth, double markWidth = 0)
    {
        ArgumentNullException.ThrowIfNull(capsules);
        var list = capsules.ToArray();
        var full = new EffortCapsuleColumn(ColumnWidth(list, compact: false, wordWidth, markWidth), Compact: false);
        return nameRoom - full.Footprint >= MinimumName ? full : new(ColumnWidth(list, compact: true, wordWidth, markWidth), Compact: true);
    }

    public static double ColumnWidth(IEnumerable<EffortCapsule> capsules, bool compact, Func<string, double> wordWidth, double markWidth = 0) =>
        capsules.Select(c => c.Width(compact, wordWidth, markWidth)).DefaultIfEmpty(0).Max();
}
