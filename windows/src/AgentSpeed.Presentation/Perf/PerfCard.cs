using AgentSpeed.Core;
using AgentSpeed.Core.Perf;
using AgentSpeed.Core.Work;
using AgentSpeed.Presentation.Formatting;
using AgentSpeed.Presentation.History;
using AgentSpeed.Presentation.Localization;

namespace AgentSpeed.Presentation.Perf;

/// <summary>The card's big number: the label color while live, secondary while resting, a tertiary <c>–</c> while none.</summary>
public enum PerfNumberTone
{
    Live,
    Resting,
    None,
}

/// <summary>A glyph of the 行為 block.</summary>
public enum PerfSignalGlyph
{
    /// <summary>The amber loop glyph of the dashboard's looping mark (Sync U+E895).</summary>
    Loop,

    /// <summary>RepeatAll U+E8EE, secondary.</summary>
    Repeat,

    /// <summary>BulletedList U+E8FD.</summary>
    Summary,
}

public sealed record PerfSignalLine(PerfSignalGlyph Glyph, string Text, bool IsWarning);

/// <summary>
/// One component row of the card (DESIGN 12, <i>Score card</i>, 3): label, value (or the absence word), the mini meter's fill
/// on the log scale, the sub-score, and the meaning line (or why it is absent).
/// </summary>
/// <param name="Fill">The mini meter's fill, 0–1 of its width; 0 for an absent component (a dotted track).</param>
public sealed record PerfComponentRow(
    PerfComponentId Id,
    string Label,
    bool Present,
    string Value,
    double Fill,
    string SubScore,
    string Meaning,
    string Spoken);

/// <summary>A titled group of component rows: 速度 or 效率.</summary>
public sealed record PerfComponentGroup(string Title, IReadOnlyList<PerfComponentRow> Rows);

/// <summary>Everything a score card is built from.</summary>
/// <param name="Project">The row's project (the card's title).</param>
/// <param name="Model">The agent's current model, used when the evaluation names none.</param>
/// <param name="Evaluation">The supplying agent's evaluation (METRICS 6.9 <i>Facts for the card</i>); null before the engine evaluated it.</param>
/// <param name="FactLines">The row's former tooltip lines, in their order (DESIGN 12, <i>Score card</i>, 5).</param>
/// <param name="SubAgents">A board group row's sub-agents (<c>boards.subCount</c> on line 2); 0 elsewhere.</param>
/// <param name="TopCount">The hero card's N: agents of the set with a score in the shown form (eyebrow from 2).</param>
public sealed record PerfCardSubject(
    SourceKind Source,
    string Project,
    string? Model,
    PerfEvaluation? Evaluation,
    IReadOnlyList<string>? FactLines = null,
    WorkSignals? Work = null,
    int SubAgents = 0,
    int TopCount = 0)
{
    /// <summary>A card for <paramref name="agent"/>, with the row's former tooltip lines as its facts.</summary>
    /// <param name="supplier">The agent whose score the row shows (a board group: the supplying member; null: the agent itself).</param>
    public static PerfCardSubject Of(AgentStatus agent, IReadOnlyList<string> facts, AgentStatus? supplier = null, int subAgents = 0,
        int topCount = 0)
    {
        ArgumentNullException.ThrowIfNull(agent);
        var shown = supplier ?? agent;
        return new PerfCardSubject(shown.Source, shown.Project, shown.Model, shown.Perf ?? shown.Intelligence.Evaluation, facts, shown.Work,
            subAgents, topCount);
    }
}

/// <summary>
/// The score card (性能分析卡, DESIGN 12): the breakdown behind an agent's number, as text, values and fractions the window
/// draws, plus the row's extra content for screen readers (<see cref="Description"/>).
/// </summary>
/// <param name="Meta">Line 2: model · source (· sub-agents).</param>
/// <param name="Eyebrow">Above the number: <c>phase.idle</c> while resting, <c>perf.card.top</c> on the hero's card; null when neither.</param>
/// <param name="Shown">The number as an integer, for motion; null while none.</param>
/// <param name="Usual">The usual chip; null while none.</param>
/// <param name="Summary">The summary line, or <c>perf.status.none</c> while none.</param>
/// <param name="ProvisionalHint">Under the summary while provisional; empty otherwise.</param>
/// <param name="Groups">速度 and 效率; empty while none.</param>
/// <param name="Description">The row's <c>AutomationProperties.HelpText</c>, in DESIGN 12's order.</param>
public sealed record PerfCard(
    SourceKind Source,
    string Project,
    string Meta,
    bool Provisional,
    string ProvisionalChip,
    string? Eyebrow,
    string Number,
    int? Shown,
    PerfNumberTone NumberTone,
    PerfChip? Usual,
    string Summary,
    string ProvisionalHint,
    IReadOnlyList<PerfComponentGroup> Groups,
    string SignalsTitle,
    IReadOnlyList<PerfSignalLine> Signals,
    IReadOnlyList<string> Facts,
    string Footnote,
    string Description)
{
    /// <summary>The scale's end label; the number keeps rising past it.</summary>
    public const int ScaleEnd = 4000;

    public bool HasEyebrow => !string.IsNullOrEmpty(Eyebrow);

    public bool HasSignals => Signals.Count > 0;

    /// <summary>The scale's fill: the log scale of the shown value; 0 draws the track alone.</summary>
    public double Fill => Shown is { } v ? PerfScale.Fraction(v) : 0;

    public static PerfCard Build(PerfCardSubject subject, Localizer loc)
    {
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentNullException.ThrowIfNull(loc);
        var evaluation = subject.Evaluation ?? PerfEvaluation.Empty(subject.Source);
        var sourceName = loc[DisplayText.SourceKey(subject.Source)];
        var meta = MetaLine(evaluation.Model ?? subject.Model, sourceName, subject.SubAgents, loc);
        var facts = new List<string> { loc.Format("perf.card.sample", evaluation.N) };
        facts.AddRange(subject.FactLines?.Where(l => !string.IsNullOrWhiteSpace(l)) ?? []);

        // The configured note is on the card; the row's words already say "configured" (DESIGN 4, Tooltip).
        var spokenFacts = facts.Where(l => l != loc["modes.configured.note"]);
        var footnote = loc["perf.card.footnote"];
        var signals = SignalLines(subject.Work ?? WorkSignals.None, loc);
        var shown = evaluation.Shown;
        var provisional = evaluation.Provisional;
        var description = new List<string>();
        if (shown is not { } value)
        {
            var none = loc.Format("perf.status.none", evaluation.N, PerfFormula.MinSamples);
            description.Add(loc["intel.none"]);
            description.Add(none);
            description.AddRange(signals.Select(s => s.Text));
            description.AddRange(spokenFacts);
            description.Add(footnote);
            return new PerfCard(subject.Source, subject.Project, meta, false, loc["perf.provisional"], null, PerfNumber.Dash, null,
                PerfNumberTone.None, null, none, string.Empty, [], loc["perf.signals.title"], signals, facts, footnote,
                HistoryText.JoinParts(description, loc));
        }

        var groups = new PerfComponentGroup[]
        {
            new(loc["perf.group.speed"], [.. PerfText.SpeedRows.Select(id => Row(evaluation, id, sourceName, loc))]),
            new(loc["perf.group.efficiency"], [.. PerfText.EfficiencyRows.Select(id => Row(evaluation, id, sourceName, loc))]),
        };
        var usual = PerfText.Usual(evaluation.VsUsualPercent, loc);
        var summary = SummaryLine(evaluation, value, loc);
        var hint = provisional ? loc.Format("perf.provisional.hint", evaluation.N, PerfFormula.ProvisionalBelow) : string.Empty;
        var tier = loc[PerfText.TierKey(value)];
        var words = PerfText.Words(value, provisional, evaluation.Form, subject.TopCount, loc);
        description.Add(loc.Format("a11y.perf.score", words, tier));
        description.Add(UsualWords(evaluation.VsUsualPercent, loc));
        description.Add(summary);
        if (hint.Length > 0)
        {
            description.Add(hint);
        }

        foreach (var row in groups.SelectMany(g => g.Rows))
        {
            description.Add(row.Spoken);
            description.Add(row.Meaning);
        }

        description.AddRange(signals.Select(s => s.Text));
        description.AddRange(spokenFacts);
        description.Add(footnote);
        return new PerfCard(
            subject.Source,
            subject.Project,
            meta,
            provisional,
            loc["perf.provisional"],
            EyebrowLine(evaluation.Form, subject.TopCount, loc),
            PerfNumber.Full(value),
            value,
            evaluation.Form == PerfForm.Resting ? PerfNumberTone.Resting : PerfNumberTone.Live,
            usual,
            summary,
            hint,
            groups,
            loc["perf.signals.title"],
            signals,
            facts,
            footnote,
            HistoryText.JoinParts(description, loc));
    }

    /// <summary>
    /// <c>perf.summary.slowest</c> with the present component of the lowest sub-score (ties in the rows' order) when at least two
    /// are present and it is below 1000; <c>perf.summary.allAbove</c> when none is; <c>perf.summary</c> otherwise.
    /// </summary>
    public static string SummaryLine(PerfEvaluation evaluation, int shown, Localizer loc)
    {
        ArgumentNullException.ThrowIfNull(evaluation);
        ArgumentNullException.ThrowIfNull(loc);
        var tier = loc[PerfText.TierKey(shown)];
        var ratio = PerfText.Ratio(shown);
        var present = PerfText.CardOrder.Select(evaluation.Component).Where(c => c.Present).ToList();
        if (present.Count >= 2)
        {
            var slowest = present.Aggregate((low, c) => c.SubScore < low.SubScore ? c : low);
            return slowest.SubScore < PerfScale.Reference
                ? loc.Format("perf.summary.slowest", tier, ratio, loc[PerfText.ComponentKey(slowest.Id)])
                : loc.Format("perf.summary.allAbove", tier, ratio);
        }

        return loc.Format("perf.summary", tier, ratio);
    }

    /// <summary>The usual words: above, below, the same, or no usual yet.</summary>
    public static string UsualWords(int? vsUsualPercent, Localizer loc)
    {
        ArgumentNullException.ThrowIfNull(loc);
        return vsUsualPercent switch
        {
            null => loc["perf.usual.none"],
            > 0 and var p => loc.Format("a11y.perf.usual.up", p.ToString(loc.Culture) + "%"),
            < 0 and var p => loc.Format("a11y.perf.usual.down", (-(long)p).ToString(loc.Culture) + "%"),
            _ => loc["a11y.perf.usual.same"],
        };
    }

    private static PerfComponentRow Row(PerfEvaluation evaluation, PerfComponentId id, string sourceName, Localizer loc)
    {
        var component = evaluation.Component(id);
        var key = PerfText.ComponentKey(id);
        var label = loc[key];
        if (component.Measure is not { } measure || component.Ratio is not { } ratio)
        {
            var notRecorded = component.Reason == PerfAbsence.NotRecorded;
            var reason = loc[notRecorded ? "perf.reason.notRecorded" : "perf.reason.fewSamples"];
            var why = notRecorded
                ? loc.Format("perf.reason.notRecorded.hint", sourceName)
                : loc.Format("perf.reason.fewSamples.hint", PerfFormula.MinSamples);
            return new PerfComponentRow(id, label, false, reason, 0, PerfNumber.Dash, why, loc.Format("a11y.perf.comp.absent", label, reason));
        }

        var value = PerfText.Measure(id, measure, loc);
        var meaning = loc[key + ".desc"];
        if (id == PerfComponentId.N)
        {
            meaning += " · " + loc.Format("perf.comp.pace.count", evaluation.Tools);
        }

        var subScore = PerfText.SubScore(component.SubScore);
        return new PerfComponentRow(id, label, true, value, PerfScale.Fraction(1000 * ratio), subScore, meaning,
            loc.Format("a11y.perf.comp", label, value, subScore));
    }

    private static string MetaLine(string? model, string sourceName, int subAgents, Localizer loc)
    {
        var parts = new List<string>(3);
        if (!string.IsNullOrWhiteSpace(model))
        {
            parts.Add(model);
        }

        parts.Add(sourceName);
        if (subAgents > 0)
        {
            parts.Add(loc.Format("boards.subCount", subAgents));
        }

        return string.Join(" · ", parts);
    }

    private static string? EyebrowLine(PerfForm form, int topCount, Localizer loc)
    {
        var parts = new List<string>(2);
        if (form == PerfForm.Resting)
        {
            parts.Add(loc["phase.idle"]);
        }

        if (topCount >= 2)
        {
            parts.Add(loc.Format("perf.card.top", topCount));
        }

        return parts.Count == 0 ? null : string.Join(" · ", parts);
    }

    private static IReadOnlyList<PerfSignalLine> SignalLines(WorkSignals work, Localizer loc)
    {
        var lines = new List<PerfSignalLine>(3);
        if (work.Looping is { } looping)
        {
            lines.Add(new PerfSignalLine(PerfSignalGlyph.Loop, loc.Format("work.looping", looping), IsWarning: true));
        }

        if (work.Repeating is { } repeating)
        {
            lines.Add(new PerfSignalLine(PerfSignalGlyph.Repeat, loc.Format("work.repeating", repeating), IsWarning: false));
        }

        if (work.Novelty is { } novelty)
        {
            lines.Add(new PerfSignalLine(PerfSignalGlyph.Summary,
                loc.Format("work.summary", HistoryText.Percent(novelty), work.Failed, work.Recovered), IsWarning: false));
        }

        return lines;
    }
}
