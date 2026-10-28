using AgentSpeed.Presentation.Hero;

namespace AgentSpeed.Presentation.Titles;

/// <summary>One title's place in a transition: opacity and travel (0 = at rest, 1 = a full line away).</summary>
public readonly record struct TitleFrame(double Alpha, double Travel);

/// <summary>
/// Title label transitions shared by every surface that shows a title as text (DESIGN 1 and 2a): the orb readout, the
/// dashboard hero and the Settings previews.
/// </summary>
public static class TitleTransitions
{
    /// <summary>A level change (the label slide).</summary>
    public const double SlideDuration = 0.35;

    /// <summary>A rename of the shown level (theme or language change): a plain crossfade, no level-up motion.</summary>
    public const double RenameDuration = 0.25;

    /// <summary>
    /// The outgoing title at progress <paramref name="p"/> (0–1 of <see cref="SlideDuration"/>): it fades out over the
    /// first 40 % while sliding up. Without motion (Off, Reduce Motion) the travel is ignored and the stagger stays.
    /// </summary>
    public static TitleFrame Outgoing(double p) =>
        new(1 - Smoothstep(0, 0.4, p), HeroMath.EaseOutCubic(Math.Min(1, p / 0.4)));

    /// <summary>The incoming title: it fades in only from 35 % while sliding into place, so two titles never overlap legibly.</summary>
    public static TitleFrame Incoming(double p) =>
        new(Smoothstep(0.35, 0.8, p), HeroMath.EaseOutCubic(Math.Max(0, (p - 0.35) / 0.65)));

    /// <summary>The new name's share of a rename crossfade, <paramref name="elapsed"/> seconds after it started.</summary>
    public static double Rename(double elapsed) => Smoothstep(0, 1, elapsed / RenameDuration);

    private static double Smoothstep(double from, double to, double x) => HeroMath.Smooth((x - from) / (to - from));
}

/// <summary>
/// Notices when the label of the shown level changes without a level change (a theme or language switch) so the label
/// crossfades in <see cref="TitleTransitions.RenameDuration"/> instead of sliding.
/// </summary>
public sealed class TitleRenameTracker
{
    private string? _shown;
    private int _level = -1;

    /// <summary>The label being faded out; null when no rename runs.</summary>
    public string? From { get; private set; }

    public double Start { get; private set; } = double.NegativeInfinity;

    /// <summary>Notes the label drawn for <paramref name="level"/> at <paramref name="now"/>; returns true when a rename starts.</summary>
    public bool Note(int level, string label, double now)
    {
        ArgumentNullException.ThrowIfNull(label);
        var renamed = _shown is not null && level == _level && !string.Equals(_shown, label, StringComparison.Ordinal);
        if (renamed)
        {
            From = _shown;
            Start = now;
        }
        else if (level != _level)
        {
            From = null;
        }

        _shown = label;
        _level = level;
        return renamed;
    }

    /// <summary>The new label's share (0–1) at <paramref name="now"/>; 1 when no rename runs.</summary>
    public double Mix(double now) => From is null ? 1 : TitleTransitions.Rename(now - Start);

    public bool IsRunning(double now) => From is not null && now - Start < TitleTransitions.RenameDuration;
}
