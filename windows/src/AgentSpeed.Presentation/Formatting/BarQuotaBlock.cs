using AgentSpeed.Core;
using AgentSpeed.Core.Quota.Network;
using AgentSpeed.Core.Quota.Routes;
using AgentSpeed.Presentation.Localization;
using AgentSpeed.Presentation.Quota;
using AgentSpeed.Presentation.Settings;

namespace AgentSpeed.Presentation.Formatting;

/// <summary>The taskbar quota block's cells (DIP), left to right: the label column and the right-aligned percent.</summary>
public readonly record struct BarQuotaCells(double Label, double Number)
{
    public const double LabelGap = 3;

    /// <summary>The block's width without the gap before it.</summary>
    public double Width => Label + LabelGap + Number;

    /// <summary>Each cell as wide as the wider of the two.</summary>
    public BarQuotaCells Max(BarQuotaCells other) => new(Math.Max(Label, other.Label), Math.Max(Number, other.Number));
}

/// <summary>The color a part of the quota block takes (DESIGN 10, Menu bar and taskbar, <i>Quota</i>: Colors).</summary>
public enum BarQuotaInk
{
    /// <summary>A current number: the label color at <see cref="BarIntelligenceBlock.LiveOpacity"/>.</summary>
    Number,

    /// <summary>Labels, <c>≈</c>, a stale or signed-out number and the unavailable form: the secondary color.</summary>
    Secondary,

    /// <summary>A current number from 95 % used: the Fluent critical color.</summary>
    Critical,

    /// <summary>High contrast, a number that is not pale: the theme's text color (no red).</summary>
    Text,

    /// <summary>High contrast, labels, <c>≈</c> and a pale number: the theme's gray-text color.</summary>
    GrayText,
}

/// <summary>What the quota block shows: the source's windows, or <c>額度 –</c> while its quota is off or it has no window.</summary>
public enum BarQuotaForm
{
    Windows,

    /// <summary>The source is disabled or its quota switch is off.</summary>
    Off,

    /// <summary>No snapshot with a window yet (nothing read, signed out, a plan without a quota).</summary>
    None,
}

/// <summary>A drawn line of the quota block.</summary>
/// <param name="Number">The percent without its <c>≈</c> (<c>68%</c>), or <c>–</c> while unavailable.</param>
/// <param name="Estimated">The percent is extrapolated (QUOTAS 10.7): a leading <c>≈</c> in the secondary color.</param>
/// <param name="Pale">The number takes the secondary color: its window is over an hour old, the route's sign-in no longer works, or unavailable.</param>
/// <param name="Critical">From 95 % used and not pale: red.</param>
public sealed record BarQuotaLine(string Label, string Number, bool Estimated = false, bool Pale = false, bool Critical = false)
{
    /// <summary>The number as drawn, with its <c>≈</c> while extrapolated.</summary>
    public string Text => Estimated ? QuotaEstimateText.Approx + Number : Number;
}

/// <summary>
/// The taskbar 額度 block (DESIGN 10, Menu bar and taskbar, <i>Quota</i>; issue #81): after the value, the money block, the network
/// block and the 性能 block, one source's first two windows in QUOTAS 2 order (<c>5h 68%</c> over <c>7d 42%</c>), the same windows
/// the dashboard's row shows with their estimates, used or remaining. A window over an hour old, or every window while the
/// source's route reports a sign-in that no longer works, is pale; from 95 % used a number is red. A disabled source, a switch
/// that is off or no window shows <c>額度 –</c> centred alone. Every window, its reset, estimate mark and age, are in the tooltip
/// and the spoken words; the source's name only there.
/// </summary>
/// <param name="Lines">One or two drawn lines; one is centred alone.</param>
/// <param name="Spoken">
/// <c>a11y.bar.quota</c> with the source's name and every window's words; unavailable, the tooltip's sentence. The route's
/// status sentence follows after <c>list.separator</c>, so a number pale because of a sign-in is said why.
/// </param>
public sealed record BarQuotaBlock(SourceKind Source, BarQuotaForm Form, IReadOnlyList<BarQuotaLine> Lines, string Spoken)
{
    public const string Dash = "–";

    /// <summary>The number cell is never narrower than this.</summary>
    public const string WidestNumber = "100%";

    /// <summary>The language whose UI face draws the labels.</summary>
    public string LanguageCode { get; init; } = LanguageCodes.ZhHant;

    /// <summary>
    /// The tooltip's quota header: <c>bar.quota.title</c> with the source's name, <c> · </c> and <c>quota.updated</c>; unavailable, the
    /// <c>bar.quota.off</c> or <c>bar.quota.none</c> sentence.
    /// </summary>
    public string TooltipHeader { get; init; } = string.Empty;

    /// <summary>The tooltip's lines under the header: every window's words, then the route's status other than ok.</summary>
    public IReadOnlyList<string> TooltipLines { get; init; } = [];

    /// <summary>Line 1 alone, centred in the block.</summary>
    public bool Alone => Lines.Count < 2;

    /// <summary>The drawn content, for the widget's redraw check.</summary>
    public string DrawKey => LanguageCode + "|" + string.Join("|", Lines.Select(l => $"{l.Label}:{l.Text}:{l.Pale}:{l.Critical}"));

    /// <summary>The source the block shows: chosen and the widget shows a value (not glyph only); null otherwise.</summary>
    public static SourceKind? ShownSource(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return settings.BarMode == BarDisplayMode.GlyphOnly ? null : BarQuotaSettings.Valid(settings.BarQuota);
    }

    /// <summary>A sign-in that no longer works (QUOTAS 9.1): its numbers turn pale.</summary>
    public static bool SignedOut(QuotaStatusKind kind) =>
        kind is QuotaStatusKind.NoCredential or QuotaStatusKind.NoBrowser or QuotaStatusKind.Expired or QuotaStatusKind.Denied;

    /// <summary>Null unless a quota source is chosen and the widget shows a value (not glyph only).</summary>
    public static BarQuotaBlock? From(AppSettings settings, QuotaState state, long nowMs, Localizer loc, TimeZoneInfo? zone = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(loc);
        if (ShownSource(settings) is not { } source)
        {
            return null;
        }

        var name = loc[DisplayText.SourceKey(source)];
        if (!settings.ShowsQuota(source))
        {
            return Unavailable(source, BarQuotaForm.Off, loc.Format("bar.quota.off", name), null, loc);
        }

        var route = RouteStatus(settings, state, source);
        var status = route is { } r ? QuotaStatusText.Text(r.Status, r.Route, nowMs, loc, settings.Quota.ZcodeRegion, zone) : null;
        if (state.Of(source) is not { HasWindows: true } snapshot)
        {
            return Unavailable(source, BarQuotaForm.None, loc.Format("bar.quota.none", name), status, loc);
        }

        var remaining = settings.BarQuotaPercent == BarQuotaPercent.Remaining;
        var signedOut = route is { } signIn && SignedOut(signIn.Status.Kind);
        var windows = QuotaLines.Of(snapshot, nowMs, loc, zone, new QuotaEstimateSource(state, null));
        var separator = loc["list.separator"];
        var words = windows.Select(w => Words(w, remaining, loc)).ToList();
        var drawn = windows.Take(2).Select(w =>
        {
            var pale = signedOut || w.Age.Length > 0;
            return new BarQuotaLine(w.Label, QuotaText.Percent(remaining ? 100 - w.Percent : w.Percent), w.Estimated, pale,
                !pale && w.Tone == QuotaTone.Critical);
        }).ToList();
        var spoken = loc.Format("a11y.bar.quota", name, string.Join(separator, words));
        return new BarQuotaBlock(source, BarQuotaForm.Windows, drawn, status is null ? spoken : spoken + separator + status)
        {
            LanguageCode = loc.LanguageCode,
            TooltipHeader = loc.Format("bar.quota.title", name) + " · " + QuotaText.Updated(snapshot.ObservedMs, nowMs, loc, zone),
            TooltipLines = status is null ? words : [.. words, status],
        };
    }

    /// <summary>
    /// The color of a part: labels and <c>≈</c> (<paramref name="number"/> false) in the secondary color; a number pale, red or in
    /// the number color. High contrast as the intelligence block: numbers in the text color (no red), the rest in gray text.
    /// </summary>
    public static BarQuotaInk InkOf(bool number, bool pale, bool critical, bool highContrast) => (number && !pale, highContrast) switch
    {
        (true, true) => BarQuotaInk.Text,
        (false, true) => BarQuotaInk.GrayText,
        (true, false) => critical ? BarQuotaInk.Critical : BarQuotaInk.Number,
        _ => BarQuotaInk.Secondary,
    };

    /// <summary>Line 1's baseline in a block centred on <paramref name="centerY"/>, as the intelligence block's (alone: halfway between).</summary>
    public double FirstBaseline(double centerY, double capHeight, double pitch) =>
        Alone ? centerY + capHeight / 2 : centerY - pitch / 2 + capHeight / 2;

    /// <summary>The cells the drawn texts need: the wider label, and a number cell at least as wide as <see cref="WidestNumber"/>.</summary>
    public BarQuotaCells Cells(Func<string, double> text, Func<string, double> number)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(number);
        var label = 0.0;
        var widest = number(WidestNumber);
        foreach (var line in Lines)
        {
            label = Math.Max(label, text(line.Label));
            widest = Math.Max(widest, number(line.Text));
        }

        return new BarQuotaCells(label, widest);
    }

    /// <summary>
    /// The first route of the source (Qoder CLI: Qoder's) whose status line is not ok, as Settings shows it
    /// (<see cref="QuotaStatusText.Shown"/>: the network switch off reads so, ZCode says nothing before a key is saved).
    /// </summary>
    private static (QuotaRoute Route, QuotaRouteStatus Status)? RouteStatus(AppSettings settings, QuotaState state, SourceKind source)
    {
        var key = source == SourceKind.QoderCli ? SourceKind.Qoder : source;
        foreach (var route in QuotaRoutes.All)
        {
            if (route.Source() == key
                && QuotaStatusText.Shown(route, state.StatusOf(route), settings.RouteIsOn(route), settings.NetworkEnabled, state.ZcodeKeySaved)
                    is { Kind: not QuotaStatusKind.Ok } status)
            {
                return (route, status);
            }
        }

        return null;
    }

    /// <summary>
    /// A window as the tooltip and the spoken words read it: <c>a11y.quota.window</c> (remaining: <c>a11y.quota.windowLeft</c>), each
    /// <c>…NoReset</c> without a reset; then <c>a11y.quota.estimated</c> while extrapolated and the age while over an hour old.
    /// </summary>
    private static string Words(QuotaLine line, bool remaining, Localizer loc)
    {
        var key = remaining ? "a11y.quota.windowLeft" : "a11y.quota.window";
        var percent = remaining ? 100 - line.Percent : line.Percent;
        var words = line.ResetLong.Length > 0
            ? loc.Format(key, line.Label, percent, line.ResetLong)
            : loc.Format(key + "NoReset", line.Label, percent);
        if (line.Estimated)
        {
            words += loc["list.separator"] + loc["a11y.quota.estimated"];
        }

        return words + line.Age;
    }

    private static BarQuotaBlock Unavailable(SourceKind source, BarQuotaForm form, string sentence, string? status, Localizer loc) =>
        new(source, form, [new BarQuotaLine(loc["quota.title"], Dash, Pale: true)], status is null ? sentence : sentence + loc["list.separator"] + status)
        {
            LanguageCode = loc.LanguageCode,
            TooltipHeader = sentence,
            TooltipLines = status is null ? [] : [status],
        };
}
