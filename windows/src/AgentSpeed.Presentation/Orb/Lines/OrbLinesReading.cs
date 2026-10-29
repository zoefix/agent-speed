namespace AgentSpeed.Presentation.Orb.Lines;

/// <summary>What the two-line readout shows (DESIGN 10): network speed, or the live intelligence of the orb's sources.</summary>
public enum OrbLinesKind : byte
{
    /// <summary>Arrows: ↓ over ↑ in every style.</summary>
    Traffic,

    /// <summary>Text glyphs: a larger intelligence line over a smaller thinking line, each centered on its own.</summary>
    Intelligence,
}

/// <summary>One line: a glyph (an arrow's character, or text), a number and a unit (empty: none).</summary>
public readonly record struct OrbLine(string Glyph, string Number, string Unit);

/// <summary>
/// The readout's text at one sample, with the spoken form the orb's label adds while it is visible. The first line is the
/// top one in every style.
/// </summary>
/// <param name="First">Traffic: the ↓ line; intelligence: the intelligence line.</param>
/// <param name="Second">Traffic: the ↑ line; intelligence: the thinking line.</param>
/// <param name="Bits">Traffic in bits: the unit cell is as wide as <c>Mb/s</c> instead of <c>MB/s</c>.</param>
/// <param name="Detail">More than the lines say (the orb's tooltip); null when there is nothing more.</param>
public sealed record OrbLinesContent(OrbLinesKind Kind, OrbLine First, OrbLine Second, bool Bits, string Spoken, string? Detail = null)
{
    /// <summary>Intelligence: the thinking line's value reads 0, so it hides after its hold (<see cref="OrbThinkingFade"/>).</summary>
    public bool ThinkingZero { get; init; }

    /// <summary>Intelligence: what the label adds while the thinking line is hidden (<c>a11y.orb.intel.only</c>).</summary>
    public string? SpokenAlone { get; init; }

    /// <summary>Intelligence: the score on the first line, its form and its provisional mark (DESIGN 12).</summary>
    public OrbLineScore Score { get; init; } = OrbLineScore.None;
}

/// <summary>
/// One sample for the quiet rule: its mode, what to show (null: no value now, keep the last of the same mode) and whether
/// it counts as activity.
/// </summary>
public readonly record struct OrbLinesSample(OrbLinesKind Kind, OrbLinesContent? Content, bool Loud);

/// <summary>
/// What the orb's two-line readout shows with the quiet rule's two moments (DESIGN 10). Times are seconds on the shared
/// orb clock.
/// </summary>
/// <param name="ShownAt">When the readout began fading in; fully shown <see cref="FadeIn"/> later.</param>
/// <param name="QuietAt">When it begins fading out: 10 s after the last loud sample; gone <see cref="FadeOut"/> later.</param>
public readonly record struct OrbLinesReading(OrbLinesContent Content, double ShownAt, double QuietAt)
{
    public const double FadeIn = 0.25;
    public const double FadeOut = 0.6;

    /// <summary>Intelligence: the thinking line's hold, fade and glide while its value reads 0.</summary>
    public OrbThinkingFade Thinking { get; init; }

    /// <summary>Intelligence: how the score moves when it changes (DESIGN 12, <i>Motion</i>).</summary>
    public OrbScoreMotion Score { get; init; }

    /// <summary>What the orb's label adds at <paramref name="time"/>: the intelligence alone once the thinking line hides.</summary>
    public string Spoken(double time) => Content.SpokenAlone is { } alone && Thinking.Hides(time) ? alone : Content.Spoken;

    /// <summary>The next scheduled change still ahead of <paramref name="time"/> (the quiet fade, the thinking line's hold end).</summary>
    public double? NextChange(double time)
    {
        var quiet = QuietAt > time ? QuietAt : (double?)null;
        return Thinking.NextChange(time) is { } hide && (quiet is null || hide < quiet) ? hide : quiet;
    }

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
/// The quiet rule of the two-line readout (DESIGN 10): visible while samples are loud (traffic of at least 1 KB/s; for
/// intelligence a score, live or resting, or an agent generating); 10 s after the last loud one it fades out (0.6 s) and
/// returns (0.25 s) with the next. Quiet samples of a gone readout change nothing, so a quiet orb renders no frames.
/// </summary>
public sealed class OrbLinesQuietRule
{
    public const double QuietAfter = 10;

    public OrbLinesReading? Reading { get; private set; }

    /// <summary>
    /// Applies one sample at <paramref name="time"/>; returns whether the reading changed. A null sample clears the readout,
    /// and so does a sample of another mode before it shows.
    /// </summary>
    public bool Apply(OrbLinesSample? sample, double time)
    {
        var previous = Reading;
        Reading = sample is { } value ? Next(previous is { } current && current.Content.Kind == value.Kind ? current : null, value, time) : null;
        return Reading != previous;
    }

    private static OrbLinesReading? Next(OrbLinesReading? current, OrbLinesSample sample, double time)
    {
        if (current is not { } shown || shown.IsGone(time))
        {
            return sample.Loud && sample.Content is { } content
                ? new OrbLinesReading(content, time, time + QuietAfter)
                {
                    Thinking = default(OrbThinkingFade).Next(content.ThinkingZero, time),
                    Score = OrbScoreMotion.Start(content.Score),
                }
                : current;
        }

        var next = sample.Content is { } update
            ? shown with
            {
                Content = update,
                Thinking = shown.Thinking.Next(update.ThinkingZero, time),
                Score = shown.Score.Next(update.Score, time),
            }
            : shown;
        if (!sample.Loud)
        {
            return next;
        }

        // Fading out: come back from the current opacity.
        var shownAt = time >= shown.QuietAt ? time - shown.Alpha(time, reduceMotion: false) * OrbLinesReading.FadeIn : shown.ShownAt;
        return next with { ShownAt = shownAt, QuietAt = time + QuietAfter };
    }

    public void Clear() => Reading = null;
}
