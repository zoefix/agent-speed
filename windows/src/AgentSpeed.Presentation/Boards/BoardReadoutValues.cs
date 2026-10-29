using AgentSpeed.Core.Perf;
using AgentSpeed.Presentation.Perf;

namespace AgentSpeed.Presentation.Boards;

/// <summary>How a board's cost reads (DESIGN 2c, Live metrics: Values).</summary>
public enum BoardMoneyState
{
    /// <summary>A rate in the display currency (<c>≥</c> before it when partly priced).</summary>
    Value,

    /// <summary>A real 0 (quiet, or a free model): drawn in the quiet ink, with its unit.</summary>
    Zero,

    /// <summary>Nothing priced: <c>–</c> in the quiet ink, no unit.</summary>
    Unknown,

    /// <summary>Every source (the group's source) is excluded as a subscription: <c>cost.subscription.tag</c>, no unit.</summary>
    Tag,
}

/// <summary>A cost per second as a board shows it (section 9 rate form, METRICS 9.8 currency).</summary>
/// <param name="Number">The digits, <c>0</c>, <c>–</c> or the subscription tag.</param>
/// <param name="Unit"><c>cost.unitPerSecond</c> with the current symbol (<c>US$/s</c>); <c>–</c> and the tag do not draw it (<see cref="HasUnit"/>).</param>
/// <param name="AtLeast">Part of the value has no known price: <c>≥</c> precedes it.</param>
/// <param name="SpokenNumber">The number as accessibility reads it, wrapped in <c>a11y.orb.costAtLeast</c> when partial.</param>
/// <param name="Currency">The currency's name (<c>currency.&lt;code&gt;</c>).</param>
public sealed record BoardMoney(string Number, string Unit, bool AtLeast, BoardMoneyState State, string SpokenNumber, string Currency)
{
    public bool Quiet => State != BoardMoneyState.Value;

    public bool HasUnit => State is BoardMoneyState.Value or BoardMoneyState.Zero;
}

/// <summary>
/// The hero's live metrics of one tick (DESIGN 2c, Live metrics: Values): 性能 over the board's agents, its thinking rate and
/// its cost per second, as shown.
/// </summary>
/// <param name="Score">The board's 性能 (METRICS 6.9 over <c>A</c>): the highest score, live before resting, with its
/// supplying agent's provisional mark and N for the words.</param>
/// <param name="Thinking">METRICS 7 of <c>liveThinking</c>; <c>–</c> while quiet.</param>
/// <param name="Quiet">Nothing generated in the window over the board's agents (<c>Σ w = 0</c>): 思考 reads <c>–</c>; 性能 keeps
/// its own form (DESIGN 2c, Updates, motion and quiet).</param>
/// <param name="ThinkingZero">The shown thinking reads 0 under a live value (it hides after 3 s).</param>
public sealed record BoardHeroReadouts(PerfSetReading Score, string Thinking, bool Quiet, bool ThinkingZero, BoardMoney Cost)
{
    public const string Dash = PerfNumber.Dash;

    /// <summary>A quiet hero in US dollars (before the first tick).</summary>
    public static BoardHeroReadouts Empty { get; } = new(PerfSetReading.None, Dash, true, false,
        new BoardMoney("0", "$/s", false, BoardMoneyState.Zero, "0", string.Empty));

    /// <summary>The 性能 number in full form (neon, timing), or <c>–</c> while none.</summary>
    public string Intelligence => Score.Full;

    /// <summary>The score as shown; null while none.</summary>
    public int? Shown => Score.Shown;

    /// <summary>思考 as accessibility reads it: <c>0</c> while quiet, where the gauge draws <c>–</c>.</summary>
    public string SpokenThinking => Quiet ? "0" : Thinking;
}

/// <summary>
/// A ranking row's 性能 of one tick (DESIGN 2c, Ranking rows; DESIGN 12, <i>Values and forms</i>): a group row's highest member
/// score, a sub row's own value, with the words its label adds.
/// </summary>
/// <param name="Score">The value with its supplying agent's provisional mark and N (the words).</param>
/// <param name="Spoken"><c>a11y.agent.intel</c> with the score words while the column shows; empty for none or with 性能 off.</param>
public sealed record BoardRowReadout(PerfSetReading Score, string Spoken)
{
    /// <summary>The cell's text: the score in compact form, or <c>–</c>.</summary>
    public string Intelligence => Score.Compact;

    /// <summary>Drawn in the style's live row color; resting and none take its resting color.</summary>
    public bool Live => Score.HasScore && Score.Form == PerfForm.Live;

    /// <summary>The dotted underline goes under the digits (the supplying agent's score is provisional).</summary>
    public bool IsProvisional => Score.IsProvisional;
}
