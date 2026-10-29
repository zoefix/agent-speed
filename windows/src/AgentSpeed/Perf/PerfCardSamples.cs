using AgentSpeed.Core;
using AgentSpeed.Core.Perf;
using AgentSpeed.Core.Work;
using AgentSpeed.Presentation.Localization;
using AgentSpeed.Presentation.Perf;

namespace AgentSpeed.Perf;

/// <summary>
/// Sample performance scores for the capture harness and the Windows tests: every card state of the prototype
/// (<c>spec/prototypes/perf-score</c>) scored by the core's formula, and daily records for the Usage History section.
/// </summary>
internal static class PerfCardSamples
{
    public static IReadOnlyList<(string Caption, PerfCardSubject Subject)> Cards(Localizer loc)
    {
        ArgumentNullException.ThrowIfNull(loc);
        string[] effort = [loc.Format("effort.tooltip", "medium")];
        string[] fast = [loc.Format("effort.tooltip", "xhigh"), loc.Format("speed.tooltip", loc["speed.value.fast"])];
        return
        [
            ("LIVE · CLAUDE CODE", new(SourceKind.ClaudeCode, "nova", "claude-opus-5-5",
                Evaluation(SourceKind.ClaudeCode, PerfForm.Live, 20, 42, "claude-opus-5-5", 1123,
                    (77, 20), (5400, 20), (3.5, 42), (null, 0), (0.99, 20), (292, 42), (0.97, 42)),
                fast, new WorkSignals(null, null, 0.78, 2, 2))),
            ("LIVE · CODEX · THINKING", new(SourceKind.Codex, "rocket", "gpt-6.1-sol",
                Evaluation(SourceKind.Codex, PerfForm.Live, 20, 31, "gpt-6.1-sol", 1410,
                    (88, 20), (2900, 20), (4.4, 31), (41, 12), (0.98, 20), (210, 31), (0.94, 31)),
                effort, new WorkSignals(4, null, 0.46, 5, 1))),
            ("PROVISIONAL", new(SourceKind.ClaudeCode, "nova", "claude-sonnet-5-5",
                Evaluation(SourceKind.ClaudeCode, PerfForm.Live, 7, 9, "claude-sonnet-5-5", 1290,
                    (58, 7), (6100, 7), (2.4, 9), (null, 0), (0.97, 7), (520, 9), (0.89, 9)),
                effort)),
            ("RESTING · TOP OF 2", new(SourceKind.ClaudeDesktop, "orbit", "claude-opus-5-5",
                Evaluation(SourceKind.ClaudeDesktop, PerfForm.Resting, 20, 40, "claude-opus-5-5", 1650,
                    (92, 20), (3600, 20), (5.1, 40), (null, 0), (0.99, 20), (160, 40), (0.98, 40)),
                effort, new WorkSignals(null, null, 0.81, 1, 1), TopCount: 2)),
            ("ABSENT COMPONENTS · PI", new(SourceKind.Pi, "vega", "gpt-6.1-terra",
                Evaluation(SourceKind.Pi, PerfForm.Live, 14, 14, "gpt-6.1-terra", null,
                    (40, 14), (null, 0), (4.2, 14), (null, 0), (null, 2), (180, 14), (1.0, 14)),
                [loc.Format("effort.tooltip", "high")])),
            ("HIGH · 12,840", new(SourceKind.OpenCode, "quasar", "qwen4-coder-flash",
                Evaluation(SourceKind.OpenCode, PerfForm.Live, 20, 0, "qwen4-coder-flash", 11_800,
                    (642, 20), (null, 0), (null, 0), (null, 0), (null, 0), (null, 0), (null, 2)),
                effort)),
            ("NONE", new(SourceKind.Gemini, "lumen", "gemini-3-pro",
                Evaluation(SourceKind.Gemini, PerfForm.None, 2, 0, "gemini-3-pro", null,
                    (null, 2), (null, 0), (null, 0), (null, 0), (null, 0), (null, 0), (null, 0)),
                effort)),
        ];
    }

    /// <summary>Daily records of three models over the 30 days ending on <paramref name="today"/>, with a few gaps.</summary>
    public static IReadOnlyList<PerfRecord> Records(DateOnly today)
    {
        var records = new List<PerfRecord>();
        (SourceKind Source, string Model, double Speed, int Requests)[] models =
        [
            (SourceKind.ClaudeCode, "claude-opus-5-5", 78, 60),
            (SourceKind.Codex, "gpt-6.1-sol", 92, 44),
            (SourceKind.Pi, "gpt-6.1-terra", 41, 8),
        ];
        for (var d = 29; d >= 0; d--)
        {
            var day = today.AddDays(-d).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
            for (var m = 0; m < models.Length; m++)
            {
                var (source, model, speed, requests) = models[m];
                if ((d + m) % 9 == 4)
                {
                    continue;
                }

                var wave = 1 + 0.18 * Math.Sin((d + 3 * m) / 3.0);
                var n = Math.Max(1, (int)Math.Round(requests * (0.6 + 0.5 * Math.Abs(Math.Cos(d / 4.0 + m)))));
                records.Add(new PerfRecord(day, source, model, Sums(source, n, speed * wave, 4200 / wave)));
            }
        }

        return records;
    }

    private static PerfSums Sums(SourceKind source, int n, double speed, double latencyMs)
    {
        var sums = new PerfSums();
        var output = (long)Math.Round(speed * 12);
        for (var i = 0; i < n; i++)
        {
            sums.AddResponse(source, output, output / 3, 24_000, 23_400, (long)Math.Round(output / speed * 1000),
                PerfSources.RecordsLatency(source) ? (long)Math.Round(latencyMs * (0.8 + 0.4 * (i % 5) / 4.0)) : null);
            sums.AddCall(250 + 40 * (i % 7), failed: i % 23 == 0);
        }

        sums.ActiveMs = n * 90_000L;
        return sums;
    }

    private static PerfEvaluation Evaluation(SourceKind source, PerfForm form, int n, int tools, string model, double? baseline,
        params (double? Measure, int Samples)[] measures)
    {
        // Measures in PerfFormula.Order: T, L, N, R, H, U, S.
        var (components, score) = PerfFormula.Combine(source, measures);
        if (n < PerfFormula.MinSamples)
        {
            score = null;
        }

        return new PerfEvaluation(source, score is null ? PerfForm.None : form, score, n, tools, 600_000, 0, model, components) { Baseline = baseline };
    }
}
