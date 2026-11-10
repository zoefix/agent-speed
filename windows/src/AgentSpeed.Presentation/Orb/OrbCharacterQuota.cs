using AgentSpeed.Core;
using AgentSpeed.Core.Quota;
using AgentSpeed.Presentation.Colors;
using AgentSpeed.Presentation.Formatting;
using AgentSpeed.Presentation.Heat;
using AgentSpeed.Presentation.Localization;
using AgentSpeed.Presentation.Motion;
using AgentSpeed.Presentation.Quota;

namespace AgentSpeed.Presentation.Orb;

/// <summary>One window line of the 額度 card (DESIGN 2a.6): its label, percent, bar share, tone and short reset.</summary>
/// <param name="Bar">0…1.</param>
/// <param name="Reset">The short reset (<c>42m</c>, <c>3h</c>, <c>週五</c>); empty while unknown or passed.</param>
public sealed record OrbQuotaWindow(string Label, int Percent, double Bar, QuotaTone Tone, string Reset, bool Passed)
{
    public string PercentText => QuotaText.Percent(Percent);
}

/// <summary>A source of the 額度 card: its name and color and only the windows its account has.</summary>
public sealed record OrbQuotaSource(SourceKind Source, string Name, Rgba Color, IReadOnlyList<OrbQuotaWindow> Windows)
{
    public bool Equals(OrbQuotaSource? other) =>
        other is not null && Source == other.Source && Name == other.Name && Color == other.Color && Windows.SequenceEqual(other.Windows);

    public override int GetHashCode() => HashCode.Combine(Source, Name, Windows.Count);
}

/// <summary>The 額度 card's content: the orb's running, signed-in sources in source order and the card as spoken.</summary>
public sealed record OrbQuotaCard(IReadOnlyList<OrbQuotaSource> Sources, string? Spoken)
{
    public static OrbQuotaCard Empty { get; } = new([], null);

    public bool IsEmpty => Sources.Count == 0;

    public bool Equals(OrbQuotaCard? other) => other is not null && Spoken == other.Spoken && Sources.SequenceEqual(other.Sources);

    public override int GetHashCode() => HashCode.Combine(Sources.Count, Spoken);

    /// <summary>
    /// The card of an orb tracking <paramref name="sources"/> (null: every source): each active source with windows, its
    /// windows in QUOTAS 2 order; spoken as <c>a11y.orb.quota</c> with each source's name and its windows.
    /// </summary>
    public static OrbQuotaCard Of(QuotaState state, Func<SourceKind, bool> tracked, long nowMs, Localizer loc, TimeZoneInfo? zone = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(tracked);
        ArgumentNullException.ThrowIfNull(loc);
        var snapshots = state.CardSources(state.Active.Where(tracked));
        if (snapshots.Count == 0)
        {
            return Empty;
        }

        var sources = new List<OrbQuotaSource>(snapshots.Count);
        var spoken = new List<string>(snapshots.Count);
        foreach (var snapshot in snapshots)
        {
            var name = loc[DisplayText.SourceKey(snapshot.Source)];
            var lines = QuotaLines.Of(snapshot, nowMs, loc, zone);
            sources.Add(new OrbQuotaSource(snapshot.Source, name, HeatPalette.SourceColor(snapshot.Source),
                [.. lines.Select(l => new OrbQuotaWindow(l.Label, l.Percent, l.Bar, l.Tone, l.ResetShort, l.Passed))]));
            spoken.Add(name + " " + QuotaLines.Spoken(lines, loc));
        }

        return new OrbQuotaCard(sources, loc.Format("a11y.orb.quota", string.Join(loc["list.separator"], spoken)));
    }
}

/// <summary>
/// The 額度 card's lines as they move (DESIGN 2a.6, 額度 Motion): a bar eases to its new length (0.4 s, ease-out) and a
/// reset empties it over 0.6 s while its reset cell fades out; a changed percent crossfades (0.2 s); a source joining or
/// leaving fades its lines (0.25 s). Reduce Motion: lengths change at once, fades stay. Lines that come with the card
/// itself show at once (the card fades in). Times are seconds on the shared orb clock.
/// </summary>
public sealed class OrbQuotaMotion
{
    public const double BarEase = 0.4;
    public const double ResetEmpty = 0.6;
    public const double TextFade = 0.2;
    public const double SourceFade = 0.25;

    /// <summary>One window line as drawn.</summary>
    public sealed class Window
    {
        internal Window(OrbQuotaWindow data, double time)
        {
            Data = data;
            BarTo = BarFrom = data.Bar;
            BarAt = PercentAt = ResetAt = double.NegativeInfinity;
            OldPercent = data.PercentText;
            OldReset = data.Reset;
        }

        public OrbQuotaWindow Data { get; private set; }

        internal double BarFrom { get; private set; }

        internal double BarTo { get; private set; }

        internal double BarAt { get; private set; }

        internal double BarDuration { get; private set; } = BarEase;

        internal bool BarSine { get; private set; }

        /// <summary>The percent shown before the latest change (crossfading out).</summary>
        public string OldPercent { get; private set; }

        internal double PercentAt { get; private set; }

        /// <summary>The reset shown before the latest change (fading out).</summary>
        public string OldReset { get; private set; }

        internal double ResetAt { get; private set; }

        public double Bar(double time)
        {
            var t = Ramp(time, BarAt, BarDuration);
            var k = BarSine ? Easing.SmoothStep(0, 1, t) : Easing.OutCubic(t);
            return BarFrom + (BarTo - BarFrom) * k;
        }

        /// <summary>The new percent's opacity; the old one shows at 1 − this.</summary>
        public double PercentAlpha(double time) => Ramp(time, PercentAt, TextFade);

        /// <summary>The new reset's opacity; the old one shows at 1 − this.</summary>
        public double ResetAlpha(double time) => Ramp(time, ResetAt, Data.Passed ? ResetEmpty : TextFade);

        internal bool IsMoving(double time) =>
            (time >= BarAt && time < BarAt + BarDuration) || (time >= PercentAt && time < PercentAt + TextFade)
            || (time >= ResetAt && time < ResetAt + ResetEmpty);

        internal void Take(OrbQuotaWindow next, double time, bool reduceMotion, bool atOnce)
        {
            var previous = Data;
            Data = next;
            if (atOnce)
            {
                BarFrom = BarTo = next.Bar;
                BarAt = PercentAt = ResetAt = double.NegativeInfinity;
                OldPercent = next.PercentText;
                OldReset = next.Reset;
                return;
            }

            if (Math.Abs(next.Bar - BarTo) > 1e-6)
            {
                BarFrom = reduceMotion ? next.Bar : Bar(time);
                BarTo = next.Bar;
                BarAt = time;
                BarSine = next.Passed && !previous.Passed;
                BarDuration = BarSine ? ResetEmpty : BarEase;
            }

            if (next.PercentText != previous.PercentText)
            {
                OldPercent = previous.PercentText;
                PercentAt = time;
            }

            if (next.Reset != previous.Reset)
            {
                OldReset = previous.Reset;
                ResetAt = time;
            }
        }
    }

    /// <summary>One source's block: its header and lines, fading in or out as a whole.</summary>
    public sealed class Source
    {
        internal Source(OrbQuotaSource data, double appearAt)
        {
            Data = data;
            AppearAt = appearAt;
        }

        public OrbQuotaSource Data { get; internal set; }

        public List<Window> Windows { get; } = [];

        internal double AppearAt { get; set; }

        internal double? LeaveAt { get; set; }

        public bool Leaving => LeaveAt is not null;

        public double Alpha(double time) => Math.Min(Ramp(time, AppearAt, SourceFade), LeaveAt is { } leave ? 1 - Ramp(time, leave, SourceFade) : 1);

        internal bool Gone(double time) => LeaveAt is { } leave && time >= leave + SourceFade;
    }

    private readonly List<Source> _sources = [];

    public IReadOnlyList<Source> Sources => _sources;

    public int Count => _sources.Count;

    /// <summary>Takes the card's sources; <paramref name="atOnce"/> when the card is not shown yet (its lines come with it).</summary>
    public void Sync(IReadOnlyList<OrbQuotaSource> sources, double time, bool reduceMotion, bool atOnce)
    {
        ArgumentNullException.ThrowIfNull(sources);
        _sources.RemoveAll(s => atOnce ? !sources.Any(n => n.Source == s.Data.Source) : s.Gone(time));
        foreach (var source in _sources)
        {
            if (!source.Leaving && !sources.Any(n => n.Source == source.Data.Source))
            {
                source.LeaveAt = time;
            }
        }

        for (var i = 0; i < sources.Count; i++)
        {
            var next = sources[i];
            var source = _sources.FirstOrDefault(s => s.Data.Source == next.Source);
            if (source is null)
            {
                source = new Source(next, atOnce ? double.NegativeInfinity : time);
                var at = _sources.FindIndex(s => s.Data.Source > next.Source);
                _sources.Insert(at < 0 ? _sources.Count : at, source);
            }
            else if (source.Leaving)
            {
                source.LeaveAt = null;
                source.AppearAt = atOnce ? double.NegativeInfinity : time - SourceFade * source.Alpha(time);
            }

            source.Data = next;
            source.Windows.RemoveAll(w => !next.Windows.Any(n => n.Label == w.Data.Label));
            for (var k = 0; k < next.Windows.Count; k++)
            {
                var data = next.Windows[k];
                var at = source.Windows.FindIndex(w => w.Data.Label == data.Label);
                if (at < 0)
                {
                    source.Windows.Insert(Math.Min(k, source.Windows.Count), new Window(data, time));
                }
                else
                {
                    var window = source.Windows[at];
                    if (at != k)
                    {
                        source.Windows.RemoveAt(at);
                        source.Windows.Insert(Math.Min(k, source.Windows.Count), window);
                    }

                    window.Take(data, time, reduceMotion, atOnce);
                }
            }
        }
    }

    public bool IsMoving(double time) =>
        _sources.Any(s => (time >= s.AppearAt && time < s.AppearAt + SourceFade) || (s.LeaveAt is { } l && time < l + SourceFade)
            || s.Windows.Any(w => w.IsMoving(time)));

    private static double Ramp(double time, double start, double duration) => Math.Clamp((time - start) / duration, 0, 1);
}
