using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows.Media.Imaging;
using AgentSpeed.Core;
using AgentSpeed.Core.Traffic;
using AgentSpeed.Presentation.Character;
using AgentSpeed.Presentation.Characters;
using AgentSpeed.Presentation.Colors;
using AgentSpeed.Presentation.Cost;
using AgentSpeed.Presentation.Heat;
using AgentSpeed.Presentation.Localization;
using AgentSpeed.Presentation.Motion;
using AgentSpeed.Presentation.Orb;
using AgentSpeed.Presentation.Orb.Gpu;
using AgentSpeed.Presentation.Orb.Gpu.Styles;
using AgentSpeed.Presentation.Orb.Lines;
using AgentSpeed.Presentation.Perf;
using AgentSpeed.Presentation.Settings;
using AgentSpeed.Presentation.Settings.Orbs;

namespace AgentSpeed.Orb.Gpu;

/// <summary>
/// The <c>character</c> sheets of the frame harness (DESIGN 2a.6): with <c>character</c> among
/// <c>AGENT_SPEED_ORB_FRAMES_STYLES</c>, every pack of the <c>AGENT_SPEED_CHARACTERS</c> folder (or
/// <c>AGENT_SPEED_ORB_FRAMES_CHARACTERS</c>, a comma list of ids) at idle, cruise, boost, overdrive and redline, at 148 pt
/// and 72 pt (or the sizes of <c>AGENT_SPEED_ORB_FRAMES_SIZES</c>, a comma list), dark and light (<c>character-ID-dark|light.png</c>); a 6 s tour of the first one at 148 pt dark as numbered
/// frames every 0.25 s with a pose swap and the cards floating (<c>character-tour/</c>); the empty state; and the build and
/// GPU cost of a frame. The cards follow a demo reading through the app's own quiet rules.
/// </summary>
internal static partial class OrbFrameHarness
{
    private static readonly double[] CharacterLevels = [0, 0.05, 0.24, 0.52, 0.95];

    private static void CharacterSheets(GpuDevice device, OffscreenTarget target, double scale, OrbReadoutStrings strings, string info, Localizer loc,
        string directory, System.Text.StringBuilder report)
    {
        var folder = Environment.GetEnvironmentVariable(CharacterStore.DevelopmentVariable);
        var packs = new List<CharacterPack>();
        if (folder is { Length: > 0 } && Directory.Exists(folder))
        {
            var wanted = Environment.GetEnvironmentVariable("AGENT_SPEED_ORB_FRAMES_CHARACTERS")?.Split(',', StringSplitOptions.RemoveEmptyEntries);
            foreach (var dir in Directory.GetDirectories(folder).Order(StringComparer.Ordinal))
            {
                if (wanted is not null && !wanted.Contains(Path.GetFileName(dir)))
                {
                    continue;
                }

                try
                {
                    packs.Add(CharacterPack.Load(dir));
                }
                catch (PackLoadException ex)
                {
                    report.AppendLine(CultureInfo.InvariantCulture, $"character {Path.GetFileName(dir)}: {ex.Message}");
                }
            }
        }

        report.AppendLine(CultureInfo.InvariantCulture, $"characters: {packs.Count} packs from {folder}");
        foreach (var pack in packs)
        {
            foreach (var dark in new[] { true, false })
            {
                var rows = new List<List<BitmapSource>>();
                foreach (var size in CharacterSizes())
                {
                    var row = new List<BitmapSource>();
                    foreach (var fraction in CharacterLevels)
                    {
                        var (bitmap, _) = CharacterStill(device, target, pack, size, scale, dark, Redline * fraction, strings, info, loc, 4.0,
                            EffectsIntensity.Standard);
                        if (bitmap is not null)
                        {
                            row.Add(bitmap);
                        }
                    }

                    rows.Add(row);
                }

                WriteSheet(rows, dark, scale, Path.Combine(directory, $"character-{pack.Id}-{(dark ? "dark" : "light")}.png"));
            }
        }

        foreach (var pack in packs.Take(QuotaSheetPacks()))
        {
            CharacterQuotaSheets(device, target, pack, scale, strings, info, loc, directory);
        }

        foreach (var pack in packs.Take(BalanceSheetPacks()))
        {
            CharacterBalanceSheets(device, target, pack, scale, strings, info, loc, directory, report);
        }

        var empty = new List<List<BitmapSource>>();
        foreach (var dark in new[] { true, false })
        {
            var (bitmap, _) = CharacterStill(device, target, null, 148, scale, dark, Redline * 0.3, strings, info, loc, 1.0, EffectsIntensity.Standard);
            if (bitmap is not null)
            {
                empty.Add([bitmap]);
            }
        }

        WriteSheet(empty, true, scale, Path.Combine(directory, "character-empty.png"));
        if (packs.Count == 0)
        {
            return;
        }

        CharacterTour(device, target, packs[0], scale, strings, info, loc, Path.Combine(directory, "character-tour"), report);
        foreach (var level in new[] { EffectsIntensity.Standard, EffectsIntensity.Max })
        {
            OrbGpuRenderer.Timer?.Drain();
            var (_, build) = CharacterStill(device, target, packs[0], 240, scale, true, Redline * 1.2, strings, info, loc, 3.0, level, warmFrames: 120);
            device.Current.Context.Flush();
            var gpu = OrbGpuRenderer.Timer?.Drain() ?? (0.0, 0);
            report.AppendLine(CultureInfo.InvariantCulture,
                $"character {packs[0].Id} S240 {level}: build {build * 1000:0.000} ms/frame, gpu {gpu.Milliseconds:0.000} ms over {gpu.Frames} frames, "
                + $"atlases {(device.Current.Character?.ResidentBytes ?? 0) / 1048576.0:0.0} MB");
        }
    }

    /// <summary><c>AGENT_SPEED_ORB_FRAMES_SIZES</c> (sizes in pt, 64–240), else 148 and 72.</summary>
    private static double[] CharacterSizes()
    {
        var sizes = (Environment.GetEnvironmentVariable("AGENT_SPEED_ORB_FRAMES_SIZES") ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(t => double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : double.NaN)
            .Where(v => v is >= 64 and <= 240)
            .ToArray();
        return sizes.Length > 0 ? sizes : [148, 72];
    }

    /// <summary><c>AGENT_SPEED_ORB_FRAMES_QUOTA</c>: how many packs get 額度 sheets (default 1).</summary>
    private static int QuotaSheetPacks() =>
        int.TryParse(Environment.GetEnvironmentVariable("AGENT_SPEED_ORB_FRAMES_QUOTA"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)
            ? Math.Max(0, n)
            : 1;

    /// <summary>
    /// The 額度 card (DESIGN 2a.6) at 148 and 92 pt with two sources (Claude Desktop 7d + Fable 7d, Codex 7d), three
    /// (Claude Code 5h + 7d too) and hosts' sources after this computer's (DESIGN 13 <i>Host quotas</i>: Codex, then Codex
    /// and Claude Code on <c>devbox</c> and Gemini CLI on <c>gpu-01.lab</c>), at rest so the live cards are quiet (below
    /// S = 96 the column keeps two cards): <c>character-quota-ID-dark|light.png</c>.
    /// </summary>
    private static void CharacterQuotaSheets(GpuDevice device, OffscreenTarget target, CharacterPack pack, double scale, OrbReadoutStrings strings,
        string info, Localizer loc, string directory)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var two = OrbQuotaCard.Of(global::AgentSpeed.Quota.QuotaDemo.State(now, two: true), _ => true, now, loc);
        var three = OrbQuotaCard.Of(global::AgentSpeed.Quota.QuotaDemo.State(now, two: false), _ => true, now, loc);
        var hosts = new[] { ("A0000000-0000-4000-8000-000000000001", "devbox"), ("A0000000-0000-4000-8000-000000000002", "gpu-01.lab") };
        var remote = global::AgentSpeed.Quota.QuotaDemo.State(now, two: true) with
        {
            Hosts = global::AgentSpeed.Quota.QuotaDemo.Hosts(now, [.. hosts.Select(h => h.Item1)]),
        };
        var withHosts = OrbQuotaCard.Of(remote.CardItems(s => s is SourceKind.Codex or SourceKind.ClaudeCode or SourceKind.Gemini, _ => true, hosts,
            hosts.Select(h => h.Item1).ToHashSet()), now, loc);
        foreach (var dark in new[] { true, false })
        {
            var rows = new List<List<BitmapSource>>();
            foreach (var size in new[] { 148.0, 92.0 })
            {
                var row = new List<BitmapSource>();
                foreach (var card in new[] { two, three, withHosts })
                {
                    var (bitmap, _) = CharacterStill(device, target, pack, size, scale, dark, 0, strings, info, loc, 4.0,
                        EffectsIntensity.Standard, quota: card);
                    if (bitmap is not null)
                    {
                        row.Add(bitmap);
                    }
                }

                rows.Add(row);
            }

            WriteSheet(rows, dark, scale, Path.Combine(directory, $"character-quota-{pack.Id}-{(dark ? "dark" : "light")}.png"));
        }
    }

    /// <summary>The demo content of a harness character orb: every card on, the model chip, today's line.</summary>
    private static OrbCharacterContent CharacterContent(CharacterPack? pack, Localizer loc, OrbQuotaCard? quota = null) => new()
    {
        Quota = quota ?? OrbQuotaCard.Empty,
        Pack = pack,
        NoneInstalled = pack is null,
        EmptyLabel = loc["character.empty"],
        Readouts = CharacterReadouts.Default with { Model = true },
        ModelName = "Claude Opus 4.5",
        CharacterName = pack?.Manifest.Name.GetValueOrDefault(loc.LanguageCode) ?? pack?.Id,
        Accent = pack is null ? Rgba.FromHex("#D97757") : Rgba.FromHex(pack.Manifest.Accent),
        CostGlyph = loc["cost.glyph"],
        TodayLine = loc.Format("boards.today", SpeedFormat.Tokens(1_240_000)),
        AgentsLine = loc.Format("agents.count", 3),
        IntelligenceGlyph = loc["intel.glyph"],
        ThinkingGlyph = loc["intel.thinking.glyph"],
        ModelsMore = loc["character.models.more"],
        TokensUnit = loc["unit.tokS"],
    };

    /// <summary>The demo's running models (the first <paramref name="count"/>): timed ones, one without a timed response yet, a long dated id.</summary>
    private static IReadOnlyList<OrbModelRow> HarnessModels(double value, int count = 3) => value > 1
        ?
        [
            .. new[]
            {
                new OrbModelRow("claude-opus-5-5", (int)Math.Round(value * 0.62), Rgba.FromHex("#D97757")),
                new OrbModelRow("gpt-6.1-sol", (int)Math.Round(value * 0.41), Rgba.FromHex("#10A37F")),
                new OrbModelRow("gemini-4-argon-preview-2026-11-01", null, Rgba.FromHex("#4C8DF6")),
                new OrbModelRow("kimi-k3", (int)Math.Round(value * 0.18), Rgba.FromHex("#6E56CF")),
            }.Take(count),
        ]
        : [];

    private static void FeedCharacter(OrbGpuOrb orb, Localizer loc, double time, double value, int models = 3)
    {
        var generating = value > 1;
        var rx = value * 52 * (1 + 0.35 * Math.Sin(time * 1.7));
        var network = OrbLinesText.Traffic(new TrafficRate(rx, 0.22 * rx + 120 * Math.Max(0, Math.Sin(time * 0.9))), false, loc);
        var performance = HarnessIntelligence(loc, value, generating ? value * 0.3 : 0, generating ? 760 + 3.2 * value : 0);
        var money = generating ? new LiveMoney((value * 0.000004).ToString("0.0000", CultureInfo.InvariantCulture), "US$/s", "$", false, false, value * 4, "", "")
            : null;
        orb.SetCharacterNetwork(time, network);
        orb.SetCharacterReadings(time, performance, money, generating, generating ? OrbLatency.Of(1180 + 40 * Math.Sin(time), loc) : null,
            HarnessModels(value, models));
    }

    private static (BitmapSource? Bitmap, double BuildSeconds) CharacterStill(GpuDevice device, OffscreenTarget target, CharacterPack? pack, double size,
        double scale, bool dark, double value, OrbReadoutStrings strings, string info, Localizer loc, double seconds, EffectsIntensity level,
        int warmFrames = 0, OrbQuotaCard? quota = null, int models = 3)
    {
        var effects = new EffectsPolicy(level);
        var d = OrbStyles.FootprintDiameter(OrbStyle.Character, size);
        var orb = new OrbGpuOrb(0x9E3779B9u, device.Atlas.ForScale(scale));
        var context = new OrbGpuContext(d, scale, OrbTokens.For(dark ? OrbAppearance.Dark : OrbAppearance.Light), effects);
        orb.SetCharacter(CharacterContent(pack, loc, quota));
        orb.Configure(new OrbGpuConfig(OrbStyle.Character, d, effects, true, true, Redline, strings, null, SourceSet.Empty), context);
        var tier = HeatMath.RawTier(value, Redline);
        var title = SpeedTitles.Raw(value, Redline);
        SourceKind[] agents = value > 0 ? [SourceKind.Codex, SourceKind.ClaudeCode] : [];
        var time = 100.0;
        orb.SetData(time, value, tier, title, agents, info, null);
        FeedCharacter(orb, loc, time, value, models);
        orb.SnapNext();
        device.Atlas.BeginFrame();
        orb.Render(time);
        var watch = Stopwatch.StartNew();
        var built = 0;
        var end = time + seconds;
        var nextTick = time;
        while (time < end)
        {
            time += Step;
            if (time >= nextTick)
            {
                nextTick = time + 0.5;
                FeedCharacter(orb, loc, time, value, models);
            }

            device.Atlas.BeginFrame();
            orb.Render(time);
            built++;
        }

        var buildSeconds = watch.Elapsed.TotalSeconds / Math.Max(1, built);
        return (CaptureCharacter(device, target, orb, ref time, d * 1.5 * scale, warmFrames), buildSeconds);
    }

    /// <summary>Renders until the character's atlases have decoded (they load off the render thread), then captures.</summary>
    private static BitmapSource? CaptureCharacter(GpuDevice device, OffscreenTarget target, OrbGpuOrb orb, ref double time, double side, int warmFrames)
    {
        var pixels = (int)Math.Round(side);
        byte[]? bytes = null;
        var deadline = Stopwatch.StartNew();
        for (var i = 0; ; i++)
        {
            bytes = target.Render(device, orb.List, pixels, pixels);
            var pending = device.Current.Character?.PendingDecodes ?? 0;
            if ((pending == 0 && i >= warmFrames && !device.Atlas.NeedsRebuild && i > 0) || deadline.Elapsed.TotalSeconds > 20)
            {
                bytes = target.Render(device, orb.List, pixels, pixels);
                break;
            }

            if (pending > 0)
            {
                Thread.Sleep(30);
            }

            time += Step;
            device.Atlas.BeginFrame();
            orb.Render(time);
        }

        return bytes is null ? null : OffscreenTarget.ToBitmap(bytes, pixels, pixels);
    }

    /// <summary>A 6 s tour at 148 pt dark: cruise, a climb to boost (a pose swap), redline, back; a frame every 0.25 s.</summary>
    private static void CharacterTour(GpuDevice device, OffscreenTarget target, CharacterPack pack, double scale, OrbReadoutStrings strings, string info,
        Localizer loc, string directory, System.Text.StringBuilder report)
    {
        Directory.CreateDirectory(directory);
        var effects = new EffectsPolicy(EffectsIntensity.Standard);
        var d = OrbStyles.FootprintDiameter(OrbStyle.Character, 148);
        var orb = new OrbGpuOrb(0x9E3779B9u, device.Atlas.ForScale(scale));
        var context = new OrbGpuContext(d, scale, OrbTokens.For(OrbAppearance.Dark), effects);
        orb.SetCharacter(CharacterContent(pack, loc));
        orb.Configure(new OrbGpuConfig(OrbStyle.Character, d, effects, true, true, Redline, strings, null, SourceSet.Empty), context);
        var time = 200.0;
        var start = time;
        orb.SetData(time, Redline * 0.05, HeatMath.RawTier(Redline * 0.05, Redline), SpeedTitles.Raw(Redline * 0.05, Redline), [SourceKind.ClaudeCode], info,
            null);
        FeedCharacter(orb, loc, time, Redline * 0.05);
        orb.SnapNext();
        device.Atlas.BeginFrame();
        orb.Render(time);
        var warm = time;
        _ = CaptureCharacter(device, target, orb, ref warm, d * 1.5 * scale, 2);
        time = warm + Step;
        start = time;
        var frame = 0;
        var nextShot = start;
        var nextTick = start;
        var pixels = (int)Math.Round(d * 1.5 * scale);
        while (time < start + 6)
        {
            var t = time - start;
            var value = t < 1.5 ? Redline * 0.05 : t < 3 ? Redline * 0.3 : t < 4.5 ? Redline * 0.95 : Redline * 0.06;
            if (time >= nextTick)
            {
                nextTick = time + 0.5;
                orb.SetData(time, value, HeatMath.RawTier(value, Redline), SpeedTitles.Raw(value, Redline), [SourceKind.ClaudeCode], info, null);
                FeedCharacter(orb, loc, time, value);
            }

            device.Atlas.BeginFrame();
            orb.Render(time);
            if (time >= nextShot)
            {
                nextShot += 0.25;
                if (target.Render(device, orb.List, pixels, pixels) is { } bytes)
                {
                    WriteSheet([[OffscreenTarget.ToBitmap(bytes, pixels, pixels)]], true, scale, Path.Combine(directory, $"frame-{frame:00}.png"));
                }

                frame++;
            }

            time += Step;
        }

        report.AppendLine(CultureInfo.InvariantCulture, $"character tour: {frame} frames of {pack.Id}, {pixels} px");
    }
}
