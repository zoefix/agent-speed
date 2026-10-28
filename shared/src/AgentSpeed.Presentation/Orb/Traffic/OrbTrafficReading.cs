using AgentSpeed.Core.Traffic;

namespace AgentSpeed.Presentation.Orb.Traffic;

/// <summary>
/// What an orb's traffic readout shows (DESIGN 10, Floating orb readout): download over upload in fixed cells, with the
/// quiet rule's two moments. Times are seconds on the shared orb clock.
/// </summary>
/// <param name="Bits">Rates in bits per second (the unit cell is <c>Mb/s</c> wide instead of <c>MB/s</c>).</param>
/// <param name="ShownAt">When the readout began fading in; fully shown <see cref="FadeIn"/> later.</param>
/// <param name="QuietAt">When it begins fading out: 10 s after the last sample at or above 1 KB/s; gone <see cref="FadeOut"/> later.</param>
public readonly record struct OrbTrafficReading(
    TrafficFormat.Parts Rx,
    TrafficFormat.Parts Tx,
    bool Bits,
    double ShownAt,
    double QuietAt,
    TrafficRate Rate)
{
    public const double FadeIn = 0.25;
    public const double FadeOut = 0.6;

    /// <summary>Opacity at <paramref name="time"/>; Reduce Motion makes both fades instant.</summary>
    public double Alpha(double time, bool reduceMotion)
    {
        var fadeIn = reduceMotion ? 0 : FadeIn;
        var fadeOut = reduceMotion ? 0 : FadeOut;
        var incoming = fadeIn > 0 ? (time - ShownAt) / fadeIn : time >= ShownAt ? 1 : 0;
        var outgoing = fadeOut > 0 ? 1 - (time - QuietAt) / fadeOut : time < QuietAt ? 1 : 0;
        return Math.Min(Math.Clamp(incoming, 0, 1), Math.Max(outgoing, 0));
    }

    /// <summary>A fade runs at <paramref name="time"/> (frames at 30 fps).</summary>
    public bool IsFading(double time, bool reduceMotion) =>
        !reduceMotion && ((time >= ShownAt && time < ShownAt + FadeIn) || (time >= QuietAt && time < QuietAt + FadeOut));

    /// <summary>Fully faded out at <paramref name="time"/>: nothing is drawn and new quiet samples change nothing.</summary>
    public bool IsGone(double time) => time >= QuietAt + FadeOut;
}

/// <summary>
/// The quiet rule of the orb traffic readout (DESIGN 10): visible while the shown <c>↓ + ↑</c> rate is at least 1 KB/s;
/// after 10 s below it fades out (0.6 s) and returns (0.25 s) with the next loud sample. Quiet samples of a gone readout
/// change nothing, so a quiet orb renders no frames.
/// </summary>
public sealed class OrbTrafficQuietRule
{
    public const double Threshold = 1000;
    public const double QuietAfter = 10;

    public OrbTrafficReading? Reading { get; private set; }

    /// <summary>Applies one sample at <paramref name="time"/>; returns whether the reading changed. A null rate clears the readout.</summary>
    public bool Apply(TrafficRate? rate, bool bits, double time)
    {
        if (rate is not { } value)
        {
            if (Reading is null)
            {
                return false;
            }

            Reading = null;
            return true;
        }

        var rx = TrafficFormat.RateParts(value.Rx, bits);
        var tx = TrafficFormat.RateParts(value.Tx, bits);
        var loud = value.Sum >= Threshold;
        OrbTrafficReading next;
        if (Reading is { } current && !current.IsGone(time))
        {
            next = current with { Rx = rx, Tx = tx, Bits = bits, Rate = value };
            if (loud)
            {
                if (time >= current.QuietAt)
                {
                    // Fading out: come back from the current opacity.
                    var alpha = current.Alpha(time, reduceMotion: false);
                    next = next with { ShownAt = time - alpha * OrbTrafficReading.FadeIn };
                }

                next = next with { QuietAt = time + QuietAfter };
            }
        }
        else
        {
            if (!loud)
            {
                return false;
            }

            next = new OrbTrafficReading(rx, tx, bits, time, time + QuietAfter, value);
        }

        if (Reading is { } previous && previous == next)
        {
            return false;
        }

        Reading = next;
        return true;
    }

    public void Clear() => Reading = null;
}
