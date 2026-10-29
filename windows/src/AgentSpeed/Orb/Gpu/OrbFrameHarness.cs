using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AgentSpeed.App;
using AgentSpeed.Core;
using AgentSpeed.Presentation.Colors;
using AgentSpeed.Presentation.Formatting;
using AgentSpeed.Presentation.Heat;
using AgentSpeed.Presentation.Localization;
using AgentSpeed.Presentation.Motion;
using AgentSpeed.Presentation.Orb;
using AgentSpeed.Presentation.Orb.Gpu;
using AgentSpeed.Presentation.Orb.Lines;
using AgentSpeed.Presentation.Settings;
using AgentSpeed.Core.Perf;
using AgentSpeed.Presentation.Perf;

namespace AgentSpeed.Orb.Gpu;

/// <summary>
/// Developer harness <c>AGENT_SPEED_ORB_FRAMES=DIR</c>: renders every orb style on a simulated clock with the Direct3D
/// renderer into PNG sheets laid out like the macOS previews (<c>orb-style-STYLE-dark|light.png</c>: columns idle, cruise,
/// boost, overdrive, redline, beyond, new peak, hover, pulse; rows 96 (64 for minimal), 148 and 240 pt at 2×, R = 2000,
/// three agents, zh-Hant), plus per-level sheets, the two-line readout's network and intelligence modes (<c>orb-lines.png</c>),
/// the intelligence readout at 112, 148 and 196 pt (<c>orb-intel-dark|light.png</c>, its fit in the report), its thinking
/// line hidden at zero (<c>orb-intel-zero-dark|light.png</c>, its single-line baselines in the report), 性能's forms and
/// provisional mark (<c>orb-score-dark|light|contrast.png</c>) and the GPU/CPU
/// cost of each frame, then exits. Deterministic: fixed seeds, fixed time steps; no window, so it also runs without a
/// desktop. <c>AGENT_SPEED_ORB_FRAMES_STYLES</c> limits the styles, <c>AGENT_SPEED_ORB_FRAMES_SCALE</c> sets the pixel
/// scale, <c>AGENT_SPEED_ORB_FRAMES_LANG</c> the language (zh-Hant, zh-Hans, en, ja), and
/// <c>AGENT_SPEED_ORB_FRAMES_LINES=1</c> renders only the two-line readout's sheets.
/// </summary>
internal static class OrbFrameHarness
{
    private const double Redline = 2000;
    private const double Step = 1 / 60.0;

    private static readonly (string Name, double Fraction)[] Tiers =
    [
        ("idle", 0), ("cruise", 0.07), ("boost", 0.24), ("overdrive", 0.52), ("redline", 0.9), ("beyond", 1.45),
    ];

    private enum MoneyState
    {
        None,
        Priced,
        Partial,
        Unknown,
        Switching,
        Hover,
    }

    private enum Extra
    {
        None,
        Peak,
        Hover,
        Pulse,
    }

    public static int Run(string directory)
    {
        Directory.CreateDirectory(directory);
        var report = new System.Text.StringBuilder();
        if (!GpuDevice.Initialize(composition: false) || GpuDevice.Shared is not { } device)
        {
            File.WriteAllText(Path.Combine(directory, "orb-frames.txt"), "Direct3D 11 unavailable: " + GpuDevice.Failure);
            return 2;
        }

        report.AppendLine(CultureInfo.InvariantCulture, $"adapter: {device.AdapterName}");
        var scale = double.TryParse(Environment.GetEnvironmentVariable("AGENT_SPEED_ORB_FRAMES_SCALE"), NumberStyles.Float, CultureInfo.InvariantCulture,
            out var s) && s > 0 ? s : 2;
        var language = Environment.GetEnvironmentVariable("AGENT_SPEED_ORB_FRAMES_LANG") is { } code && LanguageCodes.All.Contains(code)
            ? code
            : LanguageCodes.ZhHant;
        var loc = new Localizer(Localizer.LoadEmbeddedTable(), language);
        device.Atlas.Culture = loc.Culture;
        var titles = Enum.GetValues<SpeedTitle>().Select(t => DisplayText.TitleLabel(t, loc)).ToArray();
        var strings = new OrbReadoutStrings(loc["unit.tokS"], loc["peak.new"], titles, loc.UsesLatinScript);
        var info = loc.Format("today.summary", SpeedFormat.Tokens(1_240_000), 3);
        var styles = (Environment.GetEnvironmentVariable("AGENT_SPEED_ORB_FRAMES_STYLES") ?? "led,needle,liquid,reactor,minimal")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(OrbStyles.Parse).Distinct().ToArray();
        GpuDevice.Shared.Current.Context.Flush();
        OrbGpuRenderer.Timer = new GpuTimer();
        using var target = new OffscreenTarget();
        if (Environment.GetEnvironmentVariable("AGENT_SPEED_ORB_FRAMES_LINES") is { Length: > 0 })
        {
            LineSheets(device, target, styles, scale, strings, info, loc, directory, report);
            File.WriteAllText(Path.Combine(directory, "orb-frames.txt"), report.ToString());
            return 0;
        }

        foreach (var dark in new[] { true, false })
        {
            foreach (var style in styles)
            {
                var sizes = new[] { style == OrbStyle.Minimal ? 64.0 : 96, 148, 240 };
                var rows = new List<List<BitmapSource>>();
                var cpu = 0.0;
                var frames = 0;
                foreach (var size in sizes)
                {
                    var row = new List<BitmapSource>();
                    var columns = Tiers.Select(t => (t.Fraction, Extra.None)).Concat([(0.95, Extra.Peak), (0.6, Extra.Hover), (0.6, Extra.Pulse)]);
                    foreach (var (fraction, extra) in columns)
                    {
                        var (bitmap, perFrame) = Still(device, target, style, size, scale, dark, Redline * fraction, extra, strings, info,
                            EffectsIntensity.Standard);
                        cpu += perFrame;
                        frames++;
                        if (bitmap is not null)
                        {
                            row.Add(bitmap);
                        }
                    }

                    rows.Add(row);
                }

                var name = $"orb-style-{OrbStyles.Key(style)}-{(dark ? "dark" : "light")}";
                WriteSheet(rows, dark, scale, Path.Combine(directory, name + ".png"));
                report.AppendLine(CultureInfo.InvariantCulture, $"{name}: build {cpu / frames * 1000:0.000} ms/frame");
            }

            // Effects levels at the redline, 148 pt, every style in a row.
            var levels = new List<List<BitmapSource>>();
            foreach (var level in new[] { EffectsIntensity.Off, EffectsIntensity.Soft, EffectsIntensity.Standard, EffectsIntensity.Max })
            {
                var row = new List<BitmapSource>();
                foreach (var style in styles)
                {
                    var (bitmap, _) = Still(device, target, style, 148, scale, dark, Redline * 1.2, Extra.None, strings, info, level);
                    if (bitmap is not null)
                    {
                        row.Add(bitmap);
                    }
                }

                levels.Add(row);
            }

            WriteSheet(levels, dark, scale, Path.Combine(directory, $"orb-levels-{(dark ? "dark" : "light")}.png"));
        }

        // Windows high contrast (the default dark theme colors): idle, cruise and redline of every style.
        var contrast = new List<List<BitmapSource>>();
        foreach (var fraction in new[] { 0.0, 0.07, 1.2 })
        {
            var row = new List<BitmapSource>();
            foreach (var style in styles)
            {
                var (bitmap, _) = Still(device, target, style, 148, scale, true, Redline * fraction, Extra.None, strings, info, EffectsIntensity.Standard,
                    highContrast: true);
                if (bitmap is not null)
                {
                    row.Add(bitmap);
                }
            }

            contrast.Add(row);
        }

        WriteSheet(contrast, true, scale, Path.Combine(directory, "orb-contrast.png"));

        // The money unit (DESIGN 9): priced, partly priced (≥), nothing priced (–), the unit switch half way, unit hover.
        var money = new List<List<BitmapSource>>();
        foreach (var dark in new[] { true, false })
        {
            foreach (var style in styles)
            {
                var row = new List<BitmapSource>();
                foreach (var state in new[] { MoneyState.Priced, MoneyState.Partial, MoneyState.Unknown, MoneyState.Switching, MoneyState.Hover })
                {
                    var (bitmap, _) = Still(device, target, style, 148, scale, dark, Redline * 0.6, Extra.None, strings, info, EffectsIntensity.Standard,
                        money: state);
                    if (bitmap is not null)
                    {
                        row.Add(bitmap);
                    }
                }

                money.Add(row);
            }
        }

        WriteSheet(money, true, scale, Path.Combine(directory, "orb-money.png"));

        LineSheets(device, target, styles, scale, strings, info, loc, directory, report);

        // GPU time per frame at S = 240 (the budget's size) at the redline, Max effects.
        foreach (var style in styles)
        {
            OrbGpuRenderer.Timer?.Drain();
            _ = Still(device, target, style, 240, scale, true, Redline * 1.45, Extra.None, strings, info, EffectsIntensity.Max, warmFrames: 120);
            device.Current.Context.Flush();
            Thread.Sleep(50);
            _ = Still(device, target, style, 240, scale, true, Redline * 1.45, Extra.None, strings, info, EffectsIntensity.Max, warmFrames: 2);
            var gpu = OrbGpuRenderer.Timer?.Drain() ?? (0.0, 0);
            report.AppendLine(CultureInfo.InvariantCulture, $"gpu {OrbStyles.Key(style)} S240 max: {gpu.Milliseconds:0.000} ms over {gpu.Frames} frames");
        }

        report.AppendLine(CultureInfo.InvariantCulture, $"atlas: {device.Atlas.Rasterized} strings rasterized");
        File.WriteAllText(Path.Combine(directory, "orb-frames.txt"), report.ToString());
        return 0;
    }

    /// <summary>
    /// An intelligence sample: window output and reasoning tokens, and the orb's 性能 in <paramref name="form"/> (none:
    /// <c>–</c>), <paramref name="provisional"/> or not, with N = <paramref name="count"/>.
    /// </summary>
    private static OrbLinesSample HarnessIntelligence(Localizer loc, double output, double thinking, double score,
        PerfForm form = PerfForm.Live, bool provisional = false, int count = 1)
    {
        var value = form == PerfForm.None ? PerfSetValue.None : new PerfSetValue(form, score, "harness");
        var sums = new Core.Intelligence.IntelligenceSums(output, thinking, output) { Score = value };
        var reading = form == PerfForm.None ? PerfSetReading.None : new PerfSetReading(form, value.Shown, provisional, count, null, null);
        return OrbLinesText.Intelligence(reading, sums, generating: true, loc);
    }

    /// <summary>
    /// The two-line readout (DESIGN 10): network speed against live intelligence at 148 and 196 pt in dark, light and high
    /// contrast (<c>orb-lines.png</c>); and the intelligence readout alone, a short and a long reading at 112, 148 and 196 pt
    /// (<c>orb-intel-dark|light.png</c>, a row per style) with each size's fit in the report.
    /// </summary>
    private static void LineSheets(GpuDevice device, OffscreenTarget target, OrbStyle[] styles, double scale, OrbReadoutStrings strings,
        string info, Localizer loc, string directory, System.Text.StringBuilder report)
    {
        var traffic = OrbLinesText.Traffic(new Core.Traffic.TrafficRate(1_280_000, 64_000), false, loc);
        var intelligence = HarnessIntelligence(loc, 1200, 528, 1286.25, count: 4);
        var lines = new List<List<BitmapSource>>();
        foreach (var (dark, highContrast) in new[] { (true, false), (false, false), (true, true) })
        {
            foreach (var style in styles)
            {
                var row = new List<BitmapSource>();
                foreach (var size in new[] { 148.0, 196 })
                {
                    foreach (var sample in new[] { traffic, intelligence })
                    {
                        var (bitmap, _) = Still(device, target, style, size, scale, dark, Redline * 0.3, Extra.None, strings, info,
                            EffectsIntensity.Standard, highContrast: highContrast, lines: sample);
                        if (bitmap is not null)
                        {
                            row.Add(bitmap);
                        }
                    }
                }

                lines.Add(row);
            }
        }

        WriteSheet(lines, true, scale, Path.Combine(directory, "orb-lines.png"));

        // 940 (provisional) over 5.0 tok/s, and 1,286 over 176 tok/s.
        var readings = new[]
        {
            HarnessIntelligence(loc, 400, 15, 940, provisional: true),
            intelligence,
        };
        var sizes = new[] { 112.0, 148, 196 };
        var measure = new OrbLinesDraw(device.Atlas.ForScale(scale));
        foreach (var style in styles)
        {
            var placement = OrbLinesPlacement.For(style);
            foreach (var size in sizes)
            {
                var (top, bottom) = measure.WidestLines(placement, size, intelligence.Content!);
                var drawn = measure.Fits(placement, size, intelligence.Content) ? "drawn" : "not drawn";
                var hub = string.Empty;
                if (placement.HubReach is { } reach)
                {
                    var (topBaseline, bottomBaseline) = placement.Baselines(0, size, OrbLinesKind.Intelligence);
                    hub = string.Create(CultureInfo.InvariantCulture,
                        $", hub corners {Math.Sqrt(top * top / 4 + topBaseline * topBaseline):0.0} / {Math.Sqrt(bottom * bottom / 4 + bottomBaseline * bottomBaseline):0.0} pt, reach {reach * size:0.0} pt");
                }

                report.AppendLine(CultureInfo.InvariantCulture,
                    $"intel {OrbStyles.Key(style)} {size:0}: {drawn} (widest lines {top:0.0} / {bottom:0.0} pt, room {placement.Fit * size:0.0} pt{hub})");
            }
        }

        foreach (var dark in new[] { true, false })
        {
            var rows = new List<List<BitmapSource>>();
            foreach (var style in styles)
            {
                var row = new List<BitmapSource>();
                foreach (var size in sizes)
                {
                    foreach (var reading in readings)
                    {
                        var (bitmap, _) = Still(device, target, style, size, scale, dark, Redline * 0.3, Extra.None, strings, info,
                            EffectsIntensity.Standard, lines: reading);
                        if (bitmap is not null)
                        {
                            row.Add(bitmap);
                        }
                    }
                }

                rows.Add(row);
            }

            WriteSheet(rows, dark, scale, Path.Combine(directory, $"orb-intel-{(dark ? "dark" : "light")}.png"));
        }

        ThinkingZeroSheets(device, target, styles, scale, strings, info, loc, directory, report, intelligence);
        ScoreSheets(device, target, styles, scale, strings, info, loc, directory);
    }

    /// <summary>
    /// 性能's forms on the performance readout (DESIGN 12, <i>Values and forms</i>, <i>Provisional mark</i>) at 148 and 196 pt,
    /// a row per style (<c>orb-score-dark|light|contrast.png</c>): live 1,286 (the highest of 4), provisional 940 on the
    /// dotted underline, 12.8k in compact form, resting 1,604 pale on one line once 思考 has hidden at zero, and <c>–</c>
    /// while none.
    /// </summary>
    private static void ScoreSheets(GpuDevice device, OffscreenTarget target, OrbStyle[] styles, double scale, OrbReadoutStrings strings,
        string info, Localizer loc, string directory)
    {
        (OrbLinesSample Sample, double Seconds)[] forms =
        [
            (HarnessIntelligence(loc, 1200, 384, 1286.2, count: 4), 2.4),
            (HarnessIntelligence(loc, 600, 192, 940, provisional: true, count: 2), 2.4),
            (HarnessIntelligence(loc, 600, 123, 12_840, count: 2), 2.4),
            (HarnessIntelligence(loc, 0, 0, 1604, PerfForm.Resting, count: 2), ThinkingZeroHold.Seconds + 1),
            (HarnessIntelligence(loc, 300, 66, 0, PerfForm.None), 2.4),
        ];
        foreach (var (dark, highContrast, name) in new[] { (true, false, "dark"), (false, false, "light"), (true, true, "contrast") })
        {
            var rows = new List<List<BitmapSource>>();
            foreach (var style in styles)
            {
                var row = new List<BitmapSource>();
                foreach (var size in new[] { 148.0, 196 })
                {
                    foreach (var (sample, seconds) in forms)
                    {
                        var (bitmap, _) = Still(device, target, style, size, scale, dark, Redline * 0.3, Extra.None, strings, info,
                            EffectsIntensity.Standard, highContrast: highContrast, lines: sample, seconds: seconds);
                        if (bitmap is not null)
                        {
                            row.Add(bitmap);
                        }
                    }
                }

                rows.Add(row);
            }

            WriteSheet(rows, dark, scale, Path.Combine(directory, $"orb-score-{name}.png"));
        }
    }

    /// <summary>
    /// The intelligence readout's thinking line hidden at zero (DESIGN 10) at 148 and 196 pt, a row per style
    /// (<c>orb-intel-zero-dark|light.png</c>): 133 over 176 tok/s, 125 over 0 inside its 3 s hold, half way through the
    /// fade and glide, and the intelligence line alone by the value; the report gives each style's baselines and stack shift.
    /// </summary>
    private static void ThinkingZeroSheets(GpuDevice device, OffscreenTarget target, OrbStyle[] styles, double scale, OrbReadoutStrings strings,
        string info, Localizer loc, string directory, System.Text.StringBuilder report, OrbLinesSample live)
    {
        var zero = HarnessIntelligence(loc, 400, 0, 1225.5);
        var hold = ThinkingZeroHold.Seconds;
        (OrbLinesSample Sample, double Seconds)[] moments =
            [(live, 2.4), (zero, 2.4), (zero, hold + OrbThinkingFade.Glide / 2), (zero, hold + 2 * OrbThinkingFade.Glide)];
        var sizes = new[] { 148.0, 196 };
        foreach (var style in styles)
        {
            var placement = OrbLinesPlacement.For(style);
            var layout = OrbReadoutLayout.For(style);
            foreach (var size in sizes)
            {
                var (top, bottom) = placement.Baselines(0, size, OrbLinesKind.Intelligence);
                var two = placement.StackShift(layout, size, OrbLinesKind.Intelligence);
                var alone = placement.StackShift(layout, size, OrbLinesKind.Intelligence, single: true);
                var single = placement.SingleBaseline(0, size);
                report.AppendLine(CultureInfo.InvariantCulture,
                    $"intel-zero {OrbStyles.Key(style)} {size:0}: baselines {top:0.00} / {bottom:0.00} pt, alone {single:0.00} pt; stack shift {two:0.00} -> {alone:0.00} pt");
            }
        }

        foreach (var dark in new[] { true, false })
        {
            var rows = new List<List<BitmapSource>>();
            foreach (var style in styles)
            {
                var row = new List<BitmapSource>();
                foreach (var size in sizes)
                {
                    foreach (var (sample, seconds) in moments)
                    {
                        var (bitmap, _) = Still(device, target, style, size, scale, dark, Redline * 0.3, Extra.None, strings, info,
                            EffectsIntensity.Standard, lines: sample, seconds: seconds);
                        if (bitmap is not null)
                        {
                            row.Add(bitmap);
                        }
                    }
                }

                rows.Add(row);
            }

            WriteSheet(rows, dark, scale, Path.Combine(directory, $"orb-intel-zero-{(dark ? "dark" : "light")}.png"));
        }
    }

    /// <summary>
    /// A still of <paramref name="style"/> at <paramref name="value"/> after <paramref name="seconds"/> at that speed (and
    /// with that readout sample), with the extra event in flight.
    /// </summary>
    private static (BitmapSource? Bitmap, double BuildSeconds) Still(GpuDevice device, OffscreenTarget target, OrbStyle style, double size,
        double scale, bool dark, double value, Extra extra, OrbReadoutStrings strings, string info, EffectsIntensity level, int warmFrames = 0,
        bool highContrast = false, MoneyState money = MoneyState.None, OrbLinesSample? lines = null, double seconds = 2.4)
    {
        var effects = new EffectsPolicy(level, false, highContrast);
        var orb = new OrbGpuOrb(0x9E3779B9u, device.Atlas.ForScale(scale));
        var context = new OrbGpuContext(size, scale, OrbTokens.For(dark ? OrbAppearance.Dark : OrbAppearance.Light), effects,
            highContrast ? OrbContrastColors.Default : null);
        orb.Configure(new OrbGpuConfig(style, size, effects, true, true, Redline, strings, null, SourceSet.Empty), context);
        var tier = HeatMath.RawTier(value, Redline);
        var title = SpeedTitles.Raw(value, Redline);
        SourceKind[] agents = value > 0 ? [SourceKind.Codex, SourceKind.ClaudeCode, SourceKind.ClaudeDesktop] : [];
        var time = 100.0;
        orb.SetData(time, value, tier, title, agents, info, null);
        if (lines is { } sample)
        {
            orb.SetLines(time, sample, enabled: true);
        }

        orb.SnapNext();
        device.Atlas.BeginFrame();
        orb.Render(time);
        var end = time + seconds;
        if (extra == Extra.Hover)
        {
            end -= 0.6;
        }

        if (money != MoneyState.None)
        {
            var live = money switch
            {
                MoneyState.Partial => new Presentation.Cost.LiveMoney("0.0042", "US$/s", "$0.0042", true, false, 4200, "", ""),
                MoneyState.Unknown => new Presentation.Cost.LiveMoney("–", "US$/s", "–", false, true, 0, "", ""),
                _ => new Presentation.Cost.LiveMoney("0.0042", "US$/s", "$0.0042", false, false, 4200, "", ""),
            };
            if (money == MoneyState.Switching)
            {
                while (time < end - 0.15)
                {
                    time += Step;
                    device.Atlas.BeginFrame();
                    orb.Render(time);
                }
            }

            orb.Scene.SetMoney(live);
            if (money == MoneyState.Hover)
            {
                orb.Scene.SetUnitHover(true);
            }
        }

        var watch = Stopwatch.StartNew();
        var built = 0;
        while (time < end)
        {
            time += Step;
            device.Atlas.BeginFrame();
            orb.Render(time);
            built++;
        }

        switch (extra)
        {
            case Extra.Peak:
                orb.Peak(time);
                for (var t = 0.0; t < 0.62; t += Step)
                {
                    time += Step;
                    device.Atlas.BeginFrame();
                    orb.Render(time);
                }

                break;
            case Extra.Pulse:
                orb.Pulse(time, value, 4000);
                for (var t = 0.0; t < 0.2; t += Step)
                {
                    time += Step;
                    device.Atlas.BeginFrame();
                    orb.Render(time);
                }

                break;
            case Extra.Hover:
                orb.SetHover(time, true);
                for (var t = 0.0; t < 0.6; t += Step)
                {
                    time += Step;
                    device.Atlas.BeginFrame();
                    orb.Render(time);
                }

                break;
        }

        var buildSeconds = watch.Elapsed.TotalSeconds / Math.Max(1, built);
        var pixels = (int)Math.Round(size * 1.5 * scale);
        byte[]? bytes = null;
        for (var i = 0; i <= warmFrames || device.Atlas.NeedsRebuild; i++)
        {
            if (i > 0)
            {
                time += Step;
                device.Atlas.BeginFrame();
                orb.Render(time);
            }

            bytes = target.Render(device, orb.List, pixels, pixels);
            if (i > warmFrames + 2)
            {
                break;
            }
        }

        return (bytes is null ? null : OffscreenTarget.ToBitmap(bytes, pixels, pixels), buildSeconds);
    }

    /// <summary>The cells on the macOS sheet backdrop (a vertical gradient), each cell as wide as the widest.</summary>
    private static void WriteSheet(List<List<BitmapSource>> rows, bool dark, double scale, string path)
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
            var top = dark ? Color.FromRgb(31, 36, 51) : Color.FromRgb(219, 227, 240);
            var bottom = dark ? Color.FromRgb(13, 13, 20) : Color.FromRgb(184, 199, 219);
            dc.DrawRectangle(new LinearGradientBrush(top, bottom, 90), null, new Rect(0, 0, width, height));
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
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(render));
        using var stream = File.Create(path);
        encoder.Save(stream);
        AppLog.Info($"Orb frames: wrote {Path.GetFileName(path)} ({width} × {height}, scale {scale}).");
    }
}
