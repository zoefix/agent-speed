using System.Globalization;
using AgentSpeed.Presentation.Localization;
using AgentSpeed.Presentation.Orb.Lines;

namespace AgentSpeed.Presentation.Orb;

/// <summary>The 性能 card's 首字 row (DESIGN 2a.6): its line (label, value, unit) and what the orb's label reads for it.</summary>
public sealed record OrbLatency(OrbLine Line, string Spoken)
{
    /// <summary>
    /// A first-content latency in ms as the row shows it, each rounding taken once, half away from zero, the unit following
    /// the rounded value: below 1 s whole milliseconds to the nearest 10 (<c>850 ms</c>), from 1 s one decimal (<c>1.2 s</c>),
    /// from 10 s whole seconds (<c>14 s</c>). Null for none.
    /// </summary>
    public static OrbLatency? Of(double? ms, Localizer localizer)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        if (ms is not { } value || !double.IsFinite(value))
        {
            return null;
        }

        var (number, key) = Number(Math.Max(0, value));
        var spoken = localizer.Format(key, number);
        return new OrbLatency(new OrbLine(localizer["character.latency.glyph"], number, localizer.Format(key, string.Empty).Trim()),
            localizer.Format("a11y.orb.latency", spoken));
    }

    /// <summary>The value and the unit's format key (<c>perf.unit.ms</c> or <c>perf.unit.s</c>).</summary>
    public static (string Number, string UnitKey) Number(double ms)
    {
        var tens = Math.Round(ms / 10, MidpointRounding.AwayFromZero) * 10;
        if (tens < 1000)
        {
            return (tens.ToString("0", CultureInfo.InvariantCulture), "perf.unit.ms");
        }

        var tenth = Math.Round(ms / 1000, 1, MidpointRounding.AwayFromZero);
        if (tenth < 10)
        {
            return (tenth.ToString("0.0", CultureInfo.InvariantCulture), "perf.unit.s");
        }

        return (Math.Round(ms / 1000, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.InvariantCulture), "perf.unit.s");
    }
}

/// <summary>
/// The 首字 row's presence (DESIGN 2a.6): shown while the value exists, fading in and out over 0.25 s; it keeps its last line
/// while it fades out and comes back from the opacity it still had. Times are seconds on the shared orb clock.
/// </summary>
/// <param name="ShownAt">When it began fading in, moved earlier by the opacity it already had.</param>
/// <param name="HiddenAt">When it began fading out; null while it shows.</param>
public readonly record struct OrbLatencyFade(OrbLatency? Latency, double ShownAt, double? HiddenAt)
{
    public const double Fade = 0.25;

    public static OrbLatencyFade None { get; } = new(null, double.NegativeInfinity, double.NegativeInfinity);

    /// <summary>Applies one engine tick at <paramref name="time"/>; <paramref name="next"/> null when the set has no value.</summary>
    public OrbLatencyFade Next(OrbLatency? next, double time)
    {
        if (next is null)
        {
            return Latency is null || HiddenAt is not null ? this : this with { HiddenAt = time };
        }

        if (Latency is null || IsGone(time))
        {
            return new OrbLatencyFade(next, time, null);
        }

        return HiddenAt is null
            ? this with { Latency = next }
            : new OrbLatencyFade(next, time - Alpha(time) * Fade, null);
    }

    /// <summary>The row's opacity at <paramref name="time"/> (fades stay with Reduce Motion, DESIGN 2a.6).</summary>
    public double Alpha(double time)
    {
        if (Latency is null)
        {
            return 0;
        }

        var incoming = Math.Clamp((time - ShownAt) / Fade, 0, 1);
        var outgoing = HiddenAt is { } hidden ? 1 - Math.Clamp((time - hidden) / Fade, 0, 1) : 1;
        return Math.Min(incoming, outgoing);
    }

    /// <summary>The row's share of its room in the card: eased with its fade, or whole until it is gone with Reduce Motion.</summary>
    public double Share(double time, bool reduceMotion)
    {
        var alpha = Alpha(time);
        return reduceMotion ? (alpha > 0 ? 1 : 0) : Motion.Easing.SmoothStep(0, 1, alpha);
    }

    public bool IsFading(double time) =>
        Latency is not null && ((time >= ShownAt && time < ShownAt + Fade) || (HiddenAt is { } hidden && time >= hidden && time < hidden + Fade));

    public bool IsGone(double time) => Latency is null || (HiddenAt is { } hidden && time >= hidden + Fade);
}
