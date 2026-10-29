using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AgentSpeed.App;
using AgentSpeed.Core;
using AgentSpeed.Boards.Gpu;
using AgentSpeed.Orb.Gpu;
using AgentSpeed.Presentation.Boards;
using AgentSpeed.Presentation.Boards.Demo;
using AgentSpeed.Presentation.Boards.Effects;
using AgentSpeed.Presentation.Boards.Scene;
using AgentSpeed.Presentation.Localization;
using AgentSpeed.Presentation.Orb;
using AgentSpeed.Presentation.Settings;

namespace AgentSpeed.Boards.Harness;

/// <summary>
/// Developer harness <c>AGENT_SPEED_BOARD_FRAMES=DIR</c>: renders each board style with the Direct3D renderer on a
/// simulated 60 fps clock fed by the demo, into PNG sheets <c>board-STYLE.png</c> and <c>board-STYLE-light.png</c> (rows S, M,
/// L; columns calm, cruise, redline, overtake, new leader, Reduce Motion, effects off, high contrast, the prototypes'
/// reference shot: 40 steps of the team story at rest, and a top-rung meter's shimmer midway) and <c>board-frames.txt</c> (instances, layers, texture, CPU and GPU
/// time per frame), then exits. Deterministic (fixed seeds and steps); no window, so it runs next to the installed app.
/// <c>AGENT_SPEED_BOARD_FRAMES_STYLES</c> limits the styles, <c>AGENT_SPEED_BOARD_FRAMES_THEMES</c> the appearances (dark,
/// light), <c>AGENT_SPEED_BOARD_FRAMES_SCALE</c> sets the pixel scale (default 2), <c>AGENT_SPEED_BOARD_FRAMES_LANG</c> the language
/// (default zh-Hant). Boards render with every live metric on, as a new board has them.
/// <para>
/// <c>AGENT_SPEED_BOARD_FRAMES_METRICS=1</c> renders the live metrics instead (DESIGN 2c, Live metrics): per style and appearance a
/// sheet <c>metrics-STYLE-THEME.png</c> (rows S, M, L; columns <see cref="MetricsStates"/>) and each cell as
/// <c>metrics-STYLE-SIZE-THEME-STATE.png</c>, after the team story's 40 ticks at rest, like the prototype's shots.
/// <c>AGENT_SPEED_BOARD_FRAMES_SCORES=1</c> renders 性能's forms instead (<see cref="BoardScoreFrames"/>).
/// </para>
/// </summary>
internal static class BoardFrameHarness
{
    private const double Step = 1 / 60.0;
    private const double Start = 1000;

    private enum Moment
    {
        AtTime,
        AfterOvertake,
        AfterNewLeader,

        /// <summary>A top-rung meter's shimmer halfway across (the GPU pulse on a cached row).</summary>
        Shimmer,
    }

    private sealed record Column(string Name, BoardSimScenario Scenario, double? Target, double Seconds, Moment Moment, BoardEffectsPolicy Policy);

    private static readonly Column[] Columns =
    [
        new("calm", BoardSimScenario.Calm, null, 6, Moment.AtTime, new BoardEffectsPolicy(EffectsIntensity.Standard)),
        new("cruise", BoardSimScenario.Team, 700, 6, Moment.AtTime, new BoardEffectsPolicy(EffectsIntensity.Standard)),
        new("redline", BoardSimScenario.Swarm, 3200, 8, Moment.AtTime, new BoardEffectsPolicy(EffectsIntensity.Standard)),
        new("overtake", BoardSimScenario.Team, 2400, 3, Moment.AfterOvertake, new BoardEffectsPolicy(EffectsIntensity.Standard)),
        new("new leader", BoardSimScenario.Team, 2400, 3, Moment.AfterNewLeader, new BoardEffectsPolicy(EffectsIntensity.Standard)),
        new("reduce motion", BoardSimScenario.Swarm, 3200, 8, Moment.AtTime, new BoardEffectsPolicy(ReduceMotion: true)),
        new("effects off", BoardSimScenario.Swarm, 3200, 8, Moment.AtTime, new BoardEffectsPolicy(EffectsIntensity.Off)),
        new("high contrast", BoardSimScenario.Swarm, 3200, 8, Moment.AtTime, new BoardEffectsPolicy(HighContrast: true)),

        // The prototypes' ?steps=40&scenario=team: 40 ticks, then rest before the 41st.
        new("reference", BoardSimScenario.Team, null, 19.95, Moment.AtTime, new BoardEffectsPolicy(ReduceMotion: true)),
        new("shimmer", BoardSimScenario.Team, 700, 4, Moment.Shimmer, new BoardEffectsPolicy(EffectsIntensity.Standard)),
    ];

    /// <summary>
    /// The live metrics states of the metrics sheets: every readout on with 思考 above zero; 思考 hidden at zero (no source reports
    /// reasoning); quiet (nothing generating); every switch off (the plain hero, two-line rows at M and L); 性能 off; every source a
    /// subscription (the tag).
    /// </summary>
    private static readonly (string Name, BoardReadouts Readouts, bool NoReasoning, bool Quiet, bool Subscriptions)[] MetricsStates =
    [
        ("thinking", BoardReadouts.All, false, false, false),
        ("hidden", BoardReadouts.All, true, false, false),
        ("quiet", BoardReadouts.All, false, true, false),
        ("off", BoardReadouts.None, false, false, false),
        ("nointel", BoardReadouts.All with { Intelligence = false }, false, false, false),
        ("tag", BoardReadouts.All, false, false, true),
    ];

    public static int Run(string directory)
    {
        Directory.CreateDirectory(directory);
        var reportPath = Path.Combine(directory, "board-frames.txt");
        if (!GpuDevice.Initialize(composition: false) || GpuDevice.Shared is not { } device)
        {
            File.WriteAllText(reportPath, "Direct3D 11 unavailable: " + GpuDevice.Failure);
            return 2;
        }

        var scale = double.TryParse(Environment.GetEnvironmentVariable("AGENT_SPEED_BOARD_FRAMES_SCALE"), NumberStyles.Float, CultureInfo.InvariantCulture,
            out var s) && s > 0 ? s : 2;
        var styles = (Environment.GetEnvironmentVariable("AGENT_SPEED_BOARD_FRAMES_STYLES") ?? "glass,timing,neon")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(BoardStyles.Parse).Distinct().ToArray();
        var themes = (Environment.GetEnvironmentVariable("AGENT_SPEED_BOARD_FRAMES_THEMES") ?? "dark,light")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct().ToArray();
        var language = Environment.GetEnvironmentVariable("AGENT_SPEED_BOARD_FRAMES_LANG") is { } code && LanguageCodes.All.Contains(code)
            ? code
            : LanguageCodes.ZhHant;
        var loc = new Localizer(Localizer.LoadEmbeddedTable(), language);
        var report = new System.Text.StringBuilder();
        report.AppendLine(CultureInfo.InvariantCulture, $"adapter: {device.AdapterName}, scale {scale}, language {language}");
        BoardGpuRenderer.Timer = new GpuTimer();
        using var target = new OffscreenTarget();
        if (Environment.GetEnvironmentVariable("AGENT_SPEED_BOARD_FRAMES_METRICS") == "1")
        {
            return Metrics(directory, reportPath, device, target, styles, themes, scale, loc, report);
        }

        if (Environment.GetEnvironmentVariable("AGENT_SPEED_BOARD_FRAMES_SCORES") == "1")
        {
            return BoardScoreFrames.Run(directory, reportPath, device, target, styles, themes, scale, loc, report, WriteSheet, Save);
        }

        foreach (var style in styles)
        {
            foreach (var theme in themes)
            {
                var dark = !string.Equals(theme, "light", StringComparison.OrdinalIgnoreCase);
                var rows = new List<List<BitmapSource>>();
                foreach (var size in BoardSizes.All)
                {
                    var row = new List<BitmapSource>();
                    foreach (var column in Columns)
                    {
                        var (bitmap, line) = Capture(device, target, style, size, column, scale, loc, dark);
                        report.AppendLine(CultureInfo.InvariantCulture,
                            $"{BoardStyles.Key(style)} {(dark ? "dark" : "light")} {BoardSizes.Key(size)} {column.Name}: {line}");
                        if (bitmap is null)
                        {
                            continue;
                        }

                        row.Add(bitmap);
                        if (column.Moment == Moment.AtTime && column.Target is null && column.Scenario == BoardSimScenario.Team)
                        {
                            // The reference cell alone, to lay over the prototype's screenshot.
                            Save(bitmap, Path.Combine(directory,
                                $"ref-{BoardStyles.Key(style)}-{BoardSizes.Key(size)}-{(dark ? "dark" : "light")}.png"));
                        }
                    }

                    rows.Add(row);
                    File.WriteAllText(reportPath, report.ToString());
                }

                WriteSheet(rows, Path.Combine(directory, $"board-{BoardStyles.Key(style)}{(dark ? string.Empty : "-light")}.png"), dark);
            }
        }

        File.WriteAllText(reportPath, report.ToString());
        return 0;
    }

    // The live metrics sheets: each state after 40 ticks of the team story at rest (Reduce Motion: nothing moves), the thinking
    // fold settled.
    private static int Metrics(string directory, string reportPath, GpuDevice device, OffscreenTarget target, BoardStyle[] styles, string[] themes,
        double scale, Localizer loc, System.Text.StringBuilder report)
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
                    foreach (var state in MetricsStates)
                    {
                        using var rig = new BoardRig(style, size, BoardSimScenario.Team, new BoardEffectsPolicy(ReduceMotion: true), loc, dark: dark,
                            readouts: state.Readouts);
                        rig.Feed.Sim.NoReasoning = state.NoReasoning;
                        if (state.Subscriptions)
                        {
                            rig.Feed.Billable = SourceSet.Empty;
                        }

                        var pixels = (int)Math.Round(rig.Scene.Canvas.Extent * scale);
                        var now = Start;
                        var frames = 0;
                        while (rig.Ticks < 40 || now < Start + 19.95)
                        {
                            // The quiet state: every agent stops for the last 5 s (the 3 s window drains).
                            rig.Feed.Sim.Quiet = state.Quiet && rig.Ticks >= 30;
                            if (rig.Ticks < 40)
                            {
                                rig.Advance(now);
                            }

                            if (!rig.Frame.Build(device, now, scale))
                            {
                                report.AppendLine(CultureInfo.InvariantCulture, $"{BoardStyles.Key(style)} {theme} {BoardSizes.Key(size)} {state.Name}: the device failed");
                                break;
                            }

                            frames++;
                            now += Step;
                        }

                        byte[]? bytes = null;
                        for (var i = 0; i < 2; i++)
                        {
                            bytes = target.Render(device, view => rig.Frame.Draw(device, view, pixels), pixels, pixels);
                            device.Current.Context.Flush();
                        }

                        var tick = rig.LastTick;
                        var gauge = tick?.Gauge;
                        report.AppendLine(CultureInfo.InvariantCulture,
                            $"{BoardStyles.Key(style)} {(dark ? "dark" : "light")} {BoardSizes.Key(size)} {state.Name}: readouts {state.Readouts.Bits}, intel {gauge?.Intelligence}, thinking {gauge?.Thinking} (hidden {tick?.ThinkingHidden}), cost {gauge?.Cost.Number} {gauge?.Cost.Unit} ({gauge?.Cost.State}), groups {tick?.Entries.Count(e => e.Kind == BoardEntryKind.Group)}, two-line {tick?.Entries.Any(e => e.TwoLine)}, {rig.Frame.List.Count} instances, {rig.Frame.List.LayerCount} layers, {frames} frames");
                        if (bytes is null)
                        {
                            continue;
                        }

                        var bitmap = OffscreenTarget.ToBitmap(bytes, pixels, pixels);
                        row.Add(bitmap);
                        Save(bitmap, Path.Combine(directory,
                            $"metrics-{BoardStyles.Key(style)}-{BoardSizes.Key(size)}-{(dark ? "dark" : "light")}-{state.Name}.png"));
                    }

                    rows.Add(row);
                    File.WriteAllText(reportPath, report.ToString());
                }

                WriteSheet(rows, Path.Combine(directory, $"metrics-{BoardStyles.Key(style)}-{(dark ? "dark" : "light")}.png"), dark);
            }
        }

        File.WriteAllText(reportPath, report.ToString());
        return 0;
    }

    private static (BitmapSource? Bitmap, string Line) Capture(GpuDevice device, OffscreenTarget target, BoardStyle style, BoardSize size, Column column,
        double scale, Localizer loc, bool dark)
    {
        using var rig = new BoardRig(style, size, column.Scenario, column.Policy, loc, dark: dark);
        rig.Feed.Sim.TargetTotal = column.Target;
        var pixels = (int)Math.Round(rig.Scene.Canvas.Extent * scale);
        var now = Start;
        var end = Start + column.Seconds;
        var limit = Start + 40;
        var frames = 0;
        var buildTicks = 0L;
        double? captureAt = column.Moment == Moment.AtTime ? end : null;
        while (now < limit)
        {
            if (rig.Advance(now) && captureAt is null && now >= end && column.Moment is Moment.AfterOvertake or Moment.AfterNewLeader &&
                rig.LastTick is { } tick)
            {
                var wanted = column.Moment == Moment.AfterNewLeader ? BoardRankEventKind.NewLeader : BoardRankEventKind.Overtake;
                if (tick.Events.Any(e => e.Kind == wanted))
                {
                    captureAt = now + (column.Moment == Moment.AfterNewLeader ? 0.3 : 0.15);
                }
            }

            var start = Stopwatch.GetTimestamp();
            if (!rig.Frame.Build(device, now, scale))
            {
                return (null, "the device failed");
            }

            buildTicks += Stopwatch.GetTimestamp() - start;
            frames++;
            if (captureAt is null && column.Moment == Moment.Shimmer && now >= end && ShimmerMidway(rig, now))
            {
                captureAt = now;
            }

            if (captureAt is { } at && now >= at)
            {
                break;
            }

            now += Step;
        }

        if (captureAt is null)
        {
            return (null, "no such moment within 40 s");
        }

        BoardGpuRenderer.Timer?.Drain();
        byte[]? bytes = null;
        for (var i = 0; i < 4; i++)
        {
            // A few draws so the timestamp queries of the first ones are read back.
            bytes = target.Render(device, view => rig.Frame.Draw(device, view, pixels), pixels, pixels);
            device.Current.Context.Flush();
            Thread.Sleep(5);
        }

        var gpu = BoardGpuRenderer.Timer?.Drain() ?? (0.0, 0);
        var list = rig.Frame.List;
        var atlas = rig.Frame.Layers.Atlas;
        var build = frames > 0 ? buildTicks * 1000.0 / Stopwatch.Frequency / frames : 0;
        var line = string.Create(CultureInfo.InvariantCulture,
            $"t {now - Start:0.00} s, {list.Count} instances, {list.LayerCount} layers, {list.BatchCount} batches, demand {rig.Scene.Demand(now)}, layer texture {atlas.Width}×{atlas.Height} ({atlas.Count} layers, {atlas.Rasterized} rasterized over {rig.Ticks} ticks), build {build:0.000} ms/frame, gpu {gpu.Milliseconds:0.000} ms/frame ({gpu.Frames} timed), {pixels}×{pixels} px");
        return (bytes is null ? null : OffscreenTarget.ToBitmap(bytes, pixels, pixels), line);
    }

    // A row with a top-rung meter whose shimmer is about halfway across it (the scene's animating frames only).
    private static bool ShimmerMidway(BoardRig rig, double now)
    {
        var (seconds, _) = BoardPulses.Shimmer(rig.Style);
        if (seconds <= 0 || rig.Scene.Demand(now) == FrameClass.None)
        {
            return false;
        }

        foreach (var row in rig.Scene.Rows)
        {
            if (rig.Frame.Layers.TryGet(row.Key, out var body) && double.IsFinite(body.MeterX))
            {
                var s = (now + BoardPulses.PhaseOf(row.Key)) / seconds;
                s -= Math.Floor(s);
                if (s is > 0.16 and < 0.19)
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>The cells on a desktop-like backdrop (the prototypes' stage colors), each cell as wide as the widest.</summary>
    private static void WriteSheet(List<List<BitmapSource>> rows, string path, bool dark)
    {
        var cell = rows.SelectMany(r => r).Select(b => b.PixelWidth).DefaultIfEmpty(0).Max();
        var columns = rows.Select(r => r.Count).DefaultIfEmpty(0).Max();
        var heights = rows.Select(r => r.Select(b => b.PixelHeight).DefaultIfEmpty(0).Max()).ToArray();
        var width = cell * columns;
        var height = heights.Sum();
        if (width <= 0 || height <= 0)
        {
            return;
        }

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            var backdrop = dark ? new LinearGradientBrush(Color.FromRgb(46, 58, 89), Color.FromRgb(16, 18, 28), 90)
                : new LinearGradientBrush(Color.FromRgb(233, 236, 242), Color.FromRgb(214, 219, 230), 90);
            dc.DrawRectangle(backdrop, null, new Rect(0, 0, width, height));
            var y = 0.0;
            for (var r = 0; r < rows.Count; r++)
            {
                for (var c = 0; c < rows[r].Count; c++)
                {
                    var bitmap = rows[r][c];
                    dc.DrawImage(bitmap, new Rect(c * cell + (cell - bitmap.PixelWidth) / 2.0, y + (heights[r] - bitmap.PixelHeight) / 2.0,
                        bitmap.PixelWidth, bitmap.PixelHeight));
                }

                y += heights[r];
            }
        }

        var render = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        render.Render(visual);
        Save(render, path);
        AppLog.Info($"Board frames: wrote {Path.GetFileName(path)} ({width} × {height}).");
    }

    private static void Save(BitmapSource bitmap, string path)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }
}
