using AgentSpeed.Core.Intelligence;
using AgentSpeed.Core.Perf;

namespace AgentSpeed.Presentation.Tests;

/// <summary>Readout sums for presentation tests: thinking window sums and a 性能 value, live or resting.</summary>
internal static class TestSums
{
    /// <summary>
    /// <paramref name="output"/> tokens in the window, <paramref name="thinking"/> of them reasoning out of
    /// <paramref name="basis"/>, and a live score of <paramref name="score"/> (none when the window is empty).
    /// </summary>
    public static IntelligenceSums Of(double output, double score, double thinking = 0, double basis = 0) =>
        new(output, thinking, basis) { Score = output > 0 ? Live(score) : PerfSetValue.None };

    public static PerfSetValue Live(double score, bool provisional = false) => Value(PerfForm.Live, score, provisional);

    public static PerfSetValue Resting(double score, bool provisional = false) => Value(PerfForm.Resting, score, provisional);

    private static PerfSetValue Value(PerfForm form, double score, bool provisional) => new(form, score, "a")
    {
        Evaluation = PerfEvaluation.Empty(Core.SourceKind.ClaudeCode) with { Form = form, Score = score, N = provisional ? 6 : 20 },
    };
}
