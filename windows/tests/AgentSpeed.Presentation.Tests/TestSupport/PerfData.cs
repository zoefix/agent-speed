using AgentSpeed.Core;
using AgentSpeed.Core.Perf;
using AgentSpeed.Presentation.Localization;
using AgentSpeed.Presentation.Perf;

namespace AgentSpeed.Presentation.Tests.TestSupport;

/// <summary>Evaluations shaped like the prototype's cards (<c>spec/prototypes/perf-score</c>), scored by the core's formula.</summary>
internal static class PerfData
{
    private static readonly StringTable Table = Localizer.LoadEmbeddedTable();

    public static Localizer Loc(string language = LanguageCodes.ZhHant) => new(Table, language);

    /// <summary>An evaluation from its measures in the order T, L, N, R, H, U, S (null: absent) and their samples.</summary>
    public static PerfEvaluation Evaluation(SourceKind source, PerfForm form, int n, int tools, string? model,
        params (double? Measure, int Samples)[] measures)
    {
        var (components, score) = PerfFormula.Combine(source, measures);
        return new PerfEvaluation(source, score is null ? PerfForm.None : form, score, n, tools, 600_000, 0, model, components);
    }

    /// <summary>nova (Claude Code): 77 tok/s, 5.4 s, 3.5 calls a minute, thinking not recorded, 99 % cache, 292 ms, 97 %: 1,213.</summary>
    public static PerfEvaluation Nova(PerfForm form = PerfForm.Live, int n = 20, double? baseline = 1123) => Evaluation(
        SourceKind.ClaudeCode, form, n, 42, "claude-opus-5-5",
        (77, n), (5400, n), (3.5, 42), (null, 0), (0.99, n), (292, 42), (0.97, 42)) with { Baseline = baseline };

    /// <summary>quasar (OpenCode): only throughput and too few cache samples: 12,840.</summary>
    public static PerfEvaluation Quasar { get; } = Evaluation(SourceKind.OpenCode, PerfForm.Live, 20, 0, "qwen4-coder-flash",
        (642, 20), (null, 0), (null, 0), (null, 0), (null, 2), (null, 0), (null, 0));

    /// <summary>lumen (Gemini CLI): two timed requests, no score.</summary>
    public static PerfEvaluation Lumen { get; } = Evaluation(SourceKind.Gemini, PerfForm.Live, 2, 0, "gemini-3-pro",
        (null, 2), (null, 0), (null, 0), (null, 0), (null, 0), (null, 0), (null, 0));

    public static PerfCardSubject Subject(PerfEvaluation evaluation, string project = "nova") =>
        new(evaluation.Source, project, evaluation.Model, evaluation);
}
