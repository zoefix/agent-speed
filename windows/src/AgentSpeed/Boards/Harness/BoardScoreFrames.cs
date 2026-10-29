using System.Globalization;
using System.IO;
using System.Windows.Media.Imaging;
using AgentSpeed.Boards.Gpu;
using AgentSpeed.Orb.Gpu;
using AgentSpeed.Presentation.Boards;
using AgentSpeed.Presentation.Boards.Demo;
using AgentSpeed.Presentation.Boards.Effects;
using AgentSpeed.Presentation.Localization;
using AgentSpeed.Presentation.Settings;
using AgentSpeed.Core.Perf;
using AgentSpeed.Presentation.Perf;

namespace AgentSpeed.Boards.Harness;

/// <summary>
/// <c>AGENT_SPEED_BOARD_FRAMES_SCORES=1</c> with the frame harness: 性能 on the boards (DESIGN 12; 2c, Live metrics) — per
/// style and appearance a sheet <c>scores-STYLE-THEME.png</c> (rows S, M, L; columns <see cref="States"/>) and each cell as
/// <c>scores-STYLE-SIZE-THEME-STATE.png</c>, after the team story's 40 ticks with the hero's 性能 set to the state (the rows keep
/// the demo's own scores: one in five is provisional and carries the dotted mark). The report gives each size's measured
/// two-line row figures (DESIGN 2c, The second line): widths, cap heights, the widened columns and each figure's cap centre from
/// the row's centre line (within 0.4 pt).
/// </summary>
internal static class BoardScoreFrames
{
    private const double Step = 1 / 60.0;
    private const double Start = 1000;

    /// <summary>The hero states; <c>climb</c> is caught 0.3 s into a count from 600 to 1,286.</summary>
    private static readonly (string Name, PerfSetReading Score, bool HighContrast)[] States =
    [
        ("live", new(PerfForm.Live, 1286, false, 3, null, null), false),
        ("provisional", new(PerfForm.Live, 940, true, 1, null, null), false),
        ("resting", new(PerfForm.Resting, 1604, false, 2, null, null), false),
        ("resting-provisional", new(PerfForm.Resting, 420, true, 1, null, null), false),
        ("high", new(PerfForm.Live, 12_840, false, 2, null, null), false),
        ("none", PerfSetReading.None, false),
        ("climb", new(PerfForm.Live, 1286, false, 1, null, null), false),
        ("hc-resting", new(PerfForm.Resting, 1604, false, 2, null, null), true),
        ("hc-provisional", new(PerfForm.Live, 940, true, 2, null, null), true),
    ];

    public static int Run(string directory, string reportPath, GpuDevice device, OffscreenTarget target, BoardStyle[] styles, string[] themes,
        double scale, Localizer loc, System.Text.StringBuilder report, Action<List<List<BitmapSource>>, string, bool> sheet,
        Action<BitmapSource, string> save)
    {
        foreach (var style in styles)
        {
            foreach (var theme in themes)
            {
                var dark = !string.Equals(theme, "light", StringComparison.OrdinalIgnoreCase);
                var rows = new List<List<BitmapSource>>();
                foreach (var size in BoardSizes.All)
                {
                    var row = new List<BitmapSource>();
                    var reported = false;
                    foreach (var state in States)
                    {
                        var name = $"{BoardStyles.Key(style)} {(dark ? "dark" : "light")} {BoardSizes.Key(size)} {state.Name}";
                        var policy = new BoardEffectsPolicy(EffectsIntensity.Off, HighContrast: state.HighContrast);
                        using var rig = new BoardRig(style, size, BoardSimScenario.Team, policy, loc, dark: dark);
                        var pixels = (int)Math.Round(rig.Scene.Canvas.Extent * scale);
                        var now = Start;
                        while (rig.Ticks < 40)
                        {
                            rig.Advance(now);
                            now += Step;
                        }

                        var last = rig.LastTick! with { Events = [], NewPeak = false };
                        var climb = state.Name == "climb";
                        var from = climb ? state.Score with { Shown = 600 } : state.Score;
                        rig.Scene.Apply(last with { Gauge = last.Gauge with { Score = from } }, now);
                        rig.Scene.Settle();
                        if (climb)
                        {
                            rig.Scene.Apply(last with { Gauge = last.Gauge with { Score = state.Score } }, now);
                            now += 0.3;
                        }

                        rig.Frame.Invalidate();
                        if (!rig.Frame.Build(device, now, scale))
                        {
                            report.AppendLine(CultureInfo.InvariantCulture, $"{name}: the device failed");
                            continue;
                        }

                        if (!reported)
                        {
                            reported = true;
                            report.AppendLine(Figures(rig.Scene));
                        }

                        byte[]? bytes = null;
                        for (var i = 0; i < 2; i++)
                        {
                            bytes = target.Render(device, view => rig.Frame.Draw(device, view, pixels), pixels, pixels);
                            device.Current.Context.Flush();
                        }

                        var gauge = rig.Scene.Gauge;
                        report.AppendLine(CultureInfo.InvariantCulture,
                            $"{name}: shown {gauge.Score.Text(now, compact: false)} ({state.Score.Form}, provisional {state.Score.Provisional}, N {state.Score.Count}), mark {gauge.Score.Proof(now):0.00}, demand {rig.Scene.Demand(now)}, {rig.Frame.List.Count} instances, {rig.Frame.List.LayerCount} layers");
                        if (bytes is null)
                        {
                            continue;
                        }

                        var bitmap = OffscreenTarget.ToBitmap(bytes, pixels, pixels);
                        row.Add(bitmap);
                        save(bitmap, Path.Combine(directory,
                            $"scores-{BoardStyles.Key(style)}-{BoardSizes.Key(size)}-{(dark ? "dark" : "light")}-{state.Name}.png"));
                    }

                    rows.Add(row);
                    File.WriteAllText(reportPath, report.ToString());
                }

                sheet(rows, Path.Combine(directory, $"scores-{BoardStyles.Key(style)}-{(dark ? "dark" : "light")}.png"), dark);
            }
        }

        File.WriteAllText(reportPath, report.ToString());
        return 0;
    }

    // A size's two-line row figures as the painter measured them, or that S has none.
    private static string Figures(Presentation.Boards.Scene.BoardScene scene)
    {
        var head = $"{BoardStyles.Key(scene.Style)} {BoardSizes.Key(scene.Size)} figures:";
        if (!scene.Metrics.TwoLineRows)
        {
            return head + " one-line rows";
        }

        var f = scene.Figures;
        var c = scene.Columns;
        var height = scene.Metrics.GroupHeight;
        return string.Create(CultureInfo.InvariantCulture,
            $"{head} 8,888 {f.Intelligence:0.00} wide, widest value {f.Value:0.00}; caps 性能 {f.IntelligenceCap:0.00}, value {f.ValueCap:0.00}, interval {f.IntervalCap:0.00}; columns 性能 {c.Intelligence:0.#}, value {c.Value:0.#}; baseline {f.Baseline(height, true):0.00} under the top, cap centres from the centre line {BoardRowFigures.Centre(height):0.#}: 性能 {f.Offset(height, true, f.IntelligenceCap):+0.000;-0.000}, value {f.Offset(height, true, f.ValueCap):+0.000;-0.000} (alone {f.Offset(height, false, f.ValueCap):+0.000;-0.000}), interval {f.IntervalBaseline(height) - f.IntervalCap / 2 - BoardRowFigures.Centre(height):+0.000;-0.000}");
    }
}
