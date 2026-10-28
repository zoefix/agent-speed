using AgentSpeed.Core;
using AgentSpeed.Core.Traffic;
using AgentSpeed.Presentation.Localization;

namespace AgentSpeed.Presentation.Formatting;

/// <summary>One source row of the dashboard's Network section (DESIGN 10, Dashboard 3).</summary>
/// <param name="Rx">The live download rate, or null for a tertiary <c>–</c> (below 0.5 KB/s, or no sample yet).</param>
/// <param name="TodayBytes">Today's ↓ + ↑ (the sort key).</param>
public sealed record DashboardTrafficRow(
    SourceKind Source,
    string Name,
    string TodayText,
    TrafficFormat.Parts? Rx,
    TrafficFormat.Parts? Tx,
    long TodayBytes,
    string AccessibleName);

/// <summary>
/// The dashboard's Network section (DESIGN 10, Dashboard): this computer's live rates in the header, the source rows
/// (up to three, the rest behind <c>traffic.expand</c>), and the caption — or, when per-agent traffic cannot be measured,
/// the reason and (Windows) the helper's install or update button.
/// </summary>
/// <param name="Machine">This computer's live rate; null before two samples exist.</param>
/// <param name="Rows">Every source with traffic today or now, most bytes first.</param>
/// <param name="Unavailable">Why the rows are replaced (<c>traffic.unavailable.*</c>); null when they show.</param>
/// <param name="HelperButton">The borderless helper button's text (install or update), or null.</param>
/// <param name="Caption"><c>traffic.includesTools</c> / <c>traffic.excludesTools</c>, or <c>traffic.empty</c>.</param>
public sealed record DashboardTraffic(
    TrafficFormat.Parts? MachineRx,
    TrafficFormat.Parts? MachineTx,
    string MachineSpoken,
    IReadOnlyList<DashboardTrafficRow> Rows,
    string? Unavailable,
    string? HelperButton,
    string Caption,
    bool Bits)
{
    /// <summary>Rows shown before <c>traffic.expand</c>.</summary>
    public const int CollapsedRows = 3;

    /// <summary>A direction below this rate (bytes/s) reads <c>–</c> in a row.</summary>
    public const double QuietRate = 500;

    public static DashboardTraffic From(TrafficSnapshot snapshot, bool includeTools, bool bits, Localizer localizer)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(localizer);
        var text = new TrafficText(localizer, bits);
        var machine = snapshot.MachineLive;
        var spoken = machine is { } m ? text.SpokenRates(m) : string.Empty;
        var reason = snapshot.ProcessState.UnavailableReason;
        if (reason is { } why)
        {
            var button = why switch
            {
                TrafficUnavailableReason.HelperMissing => localizer["traffic.helper.install"],
                TrafficUnavailableReason.HelperOutdated => localizer["traffic.helper.update"],
                _ => null,
            };
            return new DashboardTraffic(machine is { } r ? text.Rate(r.Rx) : null, machine is { } t ? text.Rate(t.Tx) : null, spoken, [],
                text.Reason(why), button, string.Empty, bits);
        }

        var rows = new List<DashboardTrafficRow>();
        foreach (var (source, traffic) in snapshot.Sources)
        {
            var today = traffic.Today.Total(includeTools);
            var live = traffic.Live(includeTools);
            if (today.IsZero && (live?.Sum ?? 0) <= 0)
            {
                continue;
            }

            var name = localizer[DisplayText.SourceKey(source)];
            var todayText = text.Pair(today);
            TrafficFormat.Parts? rx = live is { Rx: >= QuietRate } l1 ? text.Rate(l1.Rx) : null;
            TrafficFormat.Parts? tx = live is { Tx: >= QuietRate } l2 ? text.Rate(l2.Tx) : null;
            var spokenRow = live is { } l ? $"{name}, {text.SpokenTotals(today)}, {text.SpokenRates(l)}" : $"{name}, {text.SpokenTotals(today)}";
            rows.Add(new DashboardTrafficRow(source, name, todayText, rx, tx, today.Sum, spokenRow));
        }

        rows.Sort((a, b) => b.TodayBytes != a.TodayBytes ? b.TodayBytes.CompareTo(a.TodayBytes) : a.Source.CompareTo(b.Source));
        var caption = rows.Count == 0 ? localizer["traffic.empty"] : localizer[includeTools ? "traffic.includesTools" : "traffic.excludesTools"];
        return new DashboardTraffic(machine is { } rr ? text.Rate(rr.Rx) : null, machine is { } tt ? text.Rate(tt.Tx) : null, spoken, rows,
            null, null, caption, bits);
    }

    /// <summary>The sparkline's scale top (bytes/s): the window's largest value rounded up to a 1-2-5 step, at least 10 KB/s.</summary>
    public static double ScaleTop(double peak)
    {
        var value = Math.Max(10_000, double.IsFinite(peak) ? peak : 0);
        var magnitude = Math.Pow(10, Math.Floor(Math.Log10(value)));
        foreach (var step in (double[])[1, 2, 5, 10])
        {
            if (value <= step * magnitude * (1 + 1e-9))
            {
                return step * magnitude;
            }
        }

        return 10 * magnitude;
    }
}
