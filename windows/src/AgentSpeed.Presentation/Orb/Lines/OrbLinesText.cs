using AgentSpeed.Core;
using AgentSpeed.Core.Intelligence;
using AgentSpeed.Core.Traffic;
using AgentSpeed.Presentation.Formatting;
using AgentSpeed.Presentation.Localization;
using AgentSpeed.Presentation.Perf;

namespace AgentSpeed.Presentation.Orb.Lines;

/// <summary>The readout's samples in each mode (DESIGN 10, Floating orb readout and Intelligence readout).</summary>
public static class OrbLinesText
{
    /// <summary>Traffic counts as activity from this ↓ + ↑ rate on (bytes per second).</summary>
    public const double LoudTraffic = 1000;

    public const string Down = "↓";
    public const string Up = "↑";

    public static OrbLinesSample Traffic(TrafficRate rate, bool bits, Localizer localizer)
    {
        var text = new TrafficText(localizer, bits);
        var rx = text.Rate(rate.Rx);
        var tx = text.Rate(rate.Tx);
        var content = new OrbLinesContent(
            OrbLinesKind.Traffic, new OrbLine(Down, rx.Number, rx.Unit), new OrbLine(Up, tx.Number, tx.Unit), bits, text.SpokenRates(rate));
        return new OrbLinesSample(OrbLinesKind.Traffic, content, rate.Sum >= LoudTraffic);
    }

    /// <summary>
    /// The intelligence line — the orb's 性能 (DESIGN 12): the highest score of its agents in compact form, live before resting, <c>–</c>
    /// while none — over the thinking line (<c>liveThinking</c> tok/s, hidden after 3 s at 0). Loud while the score has a value
    /// or an agent of the orb generates (DESIGN 10, revised quiet rule); otherwise the sample carries nothing, so the readout
    /// keeps its last score while it fades — also after its agents have left the agents list.
    /// </summary>
    public static OrbLinesSample Intelligence(PerfSetReading score, IntelligenceSums sums, bool generating, Localizer localizer)
    {
        ArgumentNullException.ThrowIfNull(score);
        ArgumentNullException.ThrowIfNull(localizer);
        if (!score.HasScore && !generating)
        {
            return new OrbLinesSample(OrbLinesKind.Intelligence, null, false);
        }

        var thinking = SpeedFormat.Speed(sums.LiveThinking);
        var content = new OrbLinesContent(
            OrbLinesKind.Intelligence,
            new OrbLine(localizer["intel.glyph"], score.Compact, string.Empty),
            new OrbLine(localizer["intel.thinking.glyph"], thinking, localizer["unit.tokS"]),
            false,
            score.Sentence(thinking, localizer),
            Tooltip(score, localizer))
        {
            ThinkingZero = ThinkingZeroHold.ReadsZero(sums.LiveThinking),
            SpokenAlone = score.Sentence(null, localizer),
            Score = OrbLineScore.Of(score),
        };
        return new OrbLinesSample(OrbLinesKind.Intelligence, content, true);
    }

    /// <summary>The orb's text tooltip (DESIGN 10, Hover): <c>perf.tooltip.top</c> while there is a score, then <c>perf.tooltip.note</c>.</summary>
    public static string Tooltip(PerfSetReading score, Localizer localizer)
    {
        ArgumentNullException.ThrowIfNull(score);
        ArgumentNullException.ThrowIfNull(localizer);
        var note = localizer["perf.tooltip.note"];
        return score.Tooltip(localizer) is { } top ? top + "\n" + note : note;
    }
}
