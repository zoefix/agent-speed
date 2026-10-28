using AgentSpeed.Core.Traffic;
using AgentSpeed.Presentation.Localization;

namespace AgentSpeed.Presentation.Formatting;

/// <summary>
/// Traffic wording shared by the dashboard, the taskbar, history and accessibility (DESIGN 10, Formatting): rates in the
/// chosen unit, totals always in bytes, pairs as <c>↓ 48.2 MB · ↑ 3.10 MB</c>, and the spoken forms without arrows.
/// </summary>
public sealed class TrafficText
{
    private readonly Localizer _loc;

    public TrafficText(Localizer localizer, bool bits)
    {
        _loc = localizer ?? throw new ArgumentNullException(nameof(localizer));
        Bits = bits;
    }

    public bool Bits { get; }

    public TrafficFormat.Parts Rate(double bytesPerSecond) => TrafficFormat.RateParts(bytesPerSecond, Bits);

    /// <summary><c>↓ 48.2 MB · ↑ 3.10 MB</c> (<c>traffic.pair</c>).</summary>
    public string Pair(TrafficBytes bytes) => _loc.Format("traffic.pair", TrafficFormat.Total(bytes.Rx), TrafficFormat.Total(bytes.Tx));

    /// <summary><c>↓ 1.28 MB/s · ↑ 64.0 KB/s</c>.</summary>
    public string RatePair(TrafficRate rate) => _loc.Format("traffic.pair", Rate(rate.Rx).Text, Rate(rate.Tx).Text);

    /// <summary><c>今天下載 48.2 MB，上傳 3.10 MB</c> (<c>traffic.a11y.totals</c>).</summary>
    public string SpokenTotals(TrafficBytes bytes) => _loc.Format("traffic.a11y.totals", TrafficFormat.Total(bytes.Rx), TrafficFormat.Total(bytes.Tx));

    /// <summary><c>下載每秒 12.4 MB，上傳每秒 186 KB</c> (<c>a11y.orb.traffic</c>): rates read as words, units as written.</summary>
    public string SpokenRates(TrafficRate rate) => _loc.Format("a11y.orb.traffic", Spoken(Rate(rate.Rx)), Spoken(Rate(rate.Tx)));

    /// <summary>A rate's number and unit without <c>/s</c>, which the sentence says.</summary>
    public static string Spoken(TrafficFormat.Parts parts) =>
        parts.Number + " " + (parts.Unit.EndsWith("/s", StringComparison.Ordinal) ? parts.Unit[..^2] : parts.Unit);

    /// <summary>The reason per-agent traffic is unavailable, for captions.</summary>
    public string Reason(TrafficUnavailableReason reason) => _loc[ReasonKey(reason)];

    public static string ReasonKey(TrafficUnavailableReason reason) => reason switch
    {
        TrafficUnavailableReason.HelperMissing => "traffic.unavailable.helperMissing",
        TrafficUnavailableReason.HelperStopped => "traffic.unavailable.helperStopped",
        TrafficUnavailableReason.HelperOutdated => "traffic.unavailable.helperOutdated",
        TrafficUnavailableReason.Denied => "traffic.unavailable.denied",
        _ => "traffic.unavailable.sampler",
    };
}
