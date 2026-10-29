using AgentSpeed.Core;
using AgentSpeed.Core.Perf;
using AgentSpeed.Presentation.Localization;
using AgentSpeed.Presentation.Perf;

namespace AgentSpeed.Presentation.Formatting;

/// <summary>
/// The 性能 column of the dashboard's agent rows (DESIGN 4, 性能 column): a fixed cell between the effort capsule column and
/// the live value, as wide as the wider of <c>8,888</c> (the widest compact score) in its number font and the header
/// caption; the number is right-aligned in it.
/// </summary>
public static class AgentIntelligenceColumn
{
    /// <summary>Between the cell and the live value.</summary>
    public const double Gap = 6;

    /// <summary>The widest compact score below a million (DESIGN 12, <i>Number formats</i>).</summary>
    public const string WidestNumber = PerfNumber.WidestCompact;

    /// <summary>The number: 12 DIP semibold, tabular, one under the live value so tok/s stays the row's main number.</summary>
    public const double NumberSize = 12;

    /// <summary>The header captions: 11 DIP medium.</summary>
    public const double CaptionSize = 11;

    /// <summary>The cell's width from the measured <c>8,888</c> and caption, rounded up to a whole DIP.</summary>
    public static double Width(double numberWidth, double captionWidth) => Math.Ceiling(Math.Max(numberWidth, captionWidth));

    /// <summary>Space the cell takes in a row, its gap included.</summary>
    public static double Footprint(double width) => width + Gap;
}

/// <summary>
/// One agent row's 性能 (DESIGN 4, 性能 column; DESIGN 12; METRICS 6.9 <i>Values</i>): the agent's own performance score in
/// compact form — live in the live value's number color, resting in the tertiary color, the tertiary <c>–</c> without a
/// score — with the provisional mark under a score from fewer than 10 timed requests. The row's tooltip is the score card.
/// </summary>
/// <param name="Shown">The integer shown, for the cell's motion; null for none.</param>
/// <param name="Provisional">A score from 3 to 9 timed requests: the dotted underline.</param>
/// <param name="Spoken">Added to the row's label after the live value: <c>a11y.agent.intel</c> with the score words; empty for none.</param>
public sealed record AgentIntelligenceCell(PerfForm Form, string Text, int? Shown, bool Provisional, string Spoken)
{
    public const string Dash = PerfNumber.Dash;

    public static AgentIntelligenceCell Of(AgentStatus agent, Localizer localizer)
    {
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentNullException.ThrowIfNull(localizer);
        var reading = PerfSetReading.Of(agent);
        if (reading.Shown is not { } shown)
        {
            return new AgentIntelligenceCell(PerfForm.None, Dash, null, false, string.Empty);
        }

        return new AgentIntelligenceCell(reading.Form, PerfNumber.Compact(shown), shown, reading.IsProvisional,
            PerfText.AgentLabel(reading, localizer));
    }
}
