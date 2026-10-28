using AgentSpeed.Core.Traffic;
using AgentSpeed.Presentation.Localization;
using AgentSpeed.Presentation.Settings;

namespace AgentSpeed.Presentation.Formatting;

/// <summary>
/// The taskbar traffic block (DESIGN 10, Menu bar and taskbar): after the value, <c>↓ 1.28 MB/s</c> over <c>↑ 64.0 KB/s</c>
/// in fixed cells, so the widget's width never changes with the values. It stays visible at <c>0 B/s</c>; a direction
/// without data reads <c>–</c>.
/// </summary>
/// <param name="Rx">Null reads <c>–</c> (no sample yet, or per-agent traffic unavailable).</param>
/// <param name="Spoken"><c>a11y.orb.traffic</c> for UI Automation and the tooltip; null when there is nothing to read.</param>
public sealed record BarTrafficBlock(TrafficFormat.Parts? Rx, TrafficFormat.Parts? Tx, bool Bits, string? Spoken)
{
    public const string Dash = "–";

    /// <summary>Null unless measuring is on, the block is chosen and the widget shows a value (not glyph only).</summary>
    public static BarTrafficBlock? From(AppSettings settings, TrafficSnapshot snapshot, Localizer localizer)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(localizer);
        var traffic = settings.Traffic;
        if (!traffic.Enabled || traffic.Bar == BarTraffic.Off || settings.BarMode == BarDisplayMode.GlyphOnly)
        {
            return null;
        }

        var rate = !snapshot.Enabled ? null
            : traffic.Bar == BarTraffic.Machine ? snapshot.MachineLive
            : snapshot.Live(null, traffic.IncludeTools);
        var text = new TrafficText(localizer, traffic.Bits);
        return rate is { } r
            ? new BarTrafficBlock(text.Rate(r.Rx), text.Rate(r.Tx), traffic.Bits, text.SpokenRates(r))
            : new BarTrafficBlock(null, null, traffic.Bits, null);
    }
}
