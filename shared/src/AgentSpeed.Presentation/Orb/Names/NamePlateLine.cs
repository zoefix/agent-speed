using System.Collections.Immutable;
using AgentSpeed.Core;

namespace AgentSpeed.Presentation.Orb.Names;

/// <summary>How a plate dot is drawn.</summary>
public enum NamePlateDotKind
{
    /// <summary>A filled dot in the source's colour.</summary>
    Source,

    /// <summary>An unavailable source: a hollow ring in the secondary colour.</summary>
    Hollow,

    /// <summary>All sources: a conic gradient through every source colour in source order from 12 o'clock.</summary>
    Spectrum,
}

public readonly record struct NamePlateDot(NamePlateDotKind Kind, SourceKind Source = default)
{
    public static NamePlateDot Spectrum => new(NamePlateDotKind.Spectrum);
}

/// <summary>The ink of a run: neutral text, a status in the secondary colour, or the accent (the undo action).</summary>
public enum NamePlateInk
{
    Primary,
    Secondary,
    Accent,
}

/// <summary>
/// One run of a plate: overlapping dots (may be empty), then text. <paramref name="Cjk"/> picks the tracking and the
/// vertical centering for the run (from the full name, so every shortened form keeps them).
/// </summary>
public sealed record NamePlateRun(ImmutableArray<NamePlateDot> Dots, string Text, NamePlateInk Ink, bool Cjk)
{
    public bool Equals(NamePlateRun? other) =>
        other is not null && Text == other.Text && Ink == other.Ink && Cjk == other.Cjk && Dots.SequenceEqual(other.Dots);

    public override int GetHashCode() => HashCode.Combine(Text, Ink, Cjk, Dots.Length);
}

/// <summary>What a plate shows: its runs from left to right (the name plate has one, the folded chip one per source).</summary>
public sealed record NamePlateLine(ImmutableArray<NamePlateRun> Runs)
{
    public static NamePlateLine Empty { get; } = new([]);

    public bool IsCjk => Runs.Any(r => r.Cjk);

    public bool Equals(NamePlateLine? other) => other is not null && Runs.SequenceEqual(other.Runs);

    public override int GetHashCode() => Runs.Aggregate(Runs.Length, (hash, run) => HashCode.Combine(hash, run));
}

/// <summary>A run placed on the plate: the left edge of its first dot and of its text, and the text's tracked width.</summary>
public readonly record struct NamePlateRunLayout(double DotsX, double TextX, double TextWidth);

/// <summary>A line laid out in a plate's metrics: total width and the position of every run.</summary>
public sealed record NamePlateLayout(NamePlateLine Line, NamePlateMetrics Metrics, double Width, ImmutableArray<NamePlateRunLayout> Runs)
{
    /// <summary>
    /// Lays a line out: the first dot concentric with the left cap (a line without dots starts with the trailing
    /// padding), 0.42 f from the dots to the text, 0.9 f between runs, 0.52 h trailing.
    /// </summary>
    public static NamePlateLayout Of(NamePlateLine line, NamePlateMetrics metrics, NamePlateMeasure measure)
    {
        ArgumentNullException.ThrowIfNull(line);
        ArgumentNullException.ThrowIfNull(measure);
        var runs = ImmutableArray.CreateBuilder<NamePlateRunLayout>(line.Runs.Length);
        var x = line.Runs.Length > 0 && line.Runs[0].Dots.Length > 0 ? metrics.Leading : metrics.Trailing;
        for (var i = 0; i < line.Runs.Length; i++)
        {
            var run = line.Runs[i];
            if (i > 0)
            {
                x += metrics.ItemGap;
            }

            var dotsX = x;
            if (run.Dots.Length > 0)
            {
                x += metrics.DotsWidth(run.Dots.Length) + metrics.TextGap;
            }

            var width = TextWidth(run.Text, run.Cjk, metrics, measure);
            runs.Add(new NamePlateRunLayout(dotsX, x, width));
            x += width;
        }

        return new NamePlateLayout(line, metrics, x + metrics.Trailing, runs.MoveToImmutable());
    }

    /// <summary>A text's width at the plate's size with its tracking.</summary>
    public static double TextWidth(string text, bool cjk, NamePlateMetrics metrics, NamePlateMeasure measure)
    {
        ArgumentNullException.ThrowIfNull(measure);
        return string.IsNullOrEmpty(text)
            ? 0
            : NamePlateScript.TrackedWidth(text, measure(text, metrics.FontSize), metrics.Tracking(cjk));
    }
}
