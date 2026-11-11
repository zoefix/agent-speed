using System.Globalization;
using System.IO;
using System.Text;
using System.Windows.Media.Imaging;
using AgentSpeed.Core;
using AgentSpeed.Presentation.Character;
using AgentSpeed.Presentation.Colors;
using AgentSpeed.Presentation.Heat;
using AgentSpeed.Presentation.Localization;
using AgentSpeed.Presentation.Motion;
using AgentSpeed.Presentation.Orb;
using AgentSpeed.Presentation.Orb.Gpu;
using AgentSpeed.Presentation.Orb.Gpu.Styles;
using AgentSpeed.Presentation.Settings;

namespace AgentSpeed.Orb.Gpu;

/// <summary>The character cards split between her two sides (DESIGN 2a.6, <i>Balance</i>) in the frame harness.</summary>
internal static partial class OrbFrameHarness
{
    private static readonly double[] BalanceSizes = [148, 120];

    /// <summary><c>AGENT_SPEED_ORB_FRAMES_BALANCE</c>: how many packs get the balance sheets and dances (default none).</summary>
    private static int BalanceSheetPacks() =>
        int.TryParse(Environment.GetEnvironmentVariable("AGENT_SPEED_ORB_FRAMES_BALANCE"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)
            ? Math.Max(0, n)
            : 0;

    /// <summary>
    /// Every card of the column showing (性能 with 思考 and 首字, network, 費用, today, 額度 with two or three sources)
    /// against one to four running models, at 148 and 120 pt, dark and light (<c>character-balance-ID-dark|light.png</c>: a
    /// row per size and quota, a column per model count); then a dance at each size (<see cref="CharacterBalanceDance"/>).
    /// </summary>
    private static void CharacterBalanceSheets(GpuDevice device, OffscreenTarget target, CharacterPack pack, double scale, OrbReadoutStrings strings,
        string info, Localizer loc, string directory, StringBuilder report)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        OrbQuotaCard[] quotas =
        [
            OrbQuotaCard.Of(global::AgentSpeed.Quota.QuotaDemo.State(now, two: true), _ => true, now, loc),
            OrbQuotaCard.Of(global::AgentSpeed.Quota.QuotaDemo.State(now, two: false), _ => true, now, loc),
        ];
        foreach (var dark in new[] { true, false })
        {
            var rows = new List<List<BitmapSource>>();
            foreach (var size in BalanceSizes)
            {
                foreach (var quota in quotas)
                {
                    var row = new List<BitmapSource>();
                    for (var models = 1; models <= 4; models++)
                    {
                        var (bitmap, _) = CharacterStill(device, target, pack, size, scale, dark, Redline * 0.05, strings, info, loc, 4.0,
                            EffectsIntensity.Standard, quota: quota, models: models);
                        if (bitmap is not null)
                        {
                            row.Add(bitmap);
                        }
                    }

                    rows.Add(row);
                }
            }

            WriteSheet(rows, dark, scale, Path.Combine(directory, $"character-balance-{pack.Id}-{(dark ? "dark" : "light")}.png"));
        }

        foreach (var size in BalanceSizes)
        {
            CharacterBalanceDance(device, target, pack, size, scale, strings, info, loc, quotas[1], directory, report);
        }
    }

    /// <summary>
    /// 120 s through the bands (a new one every 10 s) with three quota sources while the models card grows and shrinks
    /// (every 15 s), dark: a frame every 10 s (<c>character-balance-dance-ID-SIZE.png</c>) and each card's changes of side
    /// in the report.
    /// </summary>
    private static void CharacterBalanceDance(GpuDevice device, OffscreenTarget target, CharacterPack pack, double size, double scale,
        OrbReadoutStrings strings, string info, Localizer loc, OrbQuotaCard quota, string directory, StringBuilder report)
    {
        const double Length = 120;
        double[] levels = [0.05, 0.24, 0.52, 0.95, 0.52, 0.24];
        int[] models = [1, 2, 3, 4, 3, 2, 1, 4];
        var effects = new EffectsPolicy(EffectsIntensity.Standard);
        var d = OrbStyles.FootprintDiameter(OrbStyle.Character, size);
        var orb = new OrbGpuOrb(0x9E3779B9u, device.Atlas.ForScale(scale));
        var context = new OrbGpuContext(d, scale, OrbTokens.For(OrbAppearance.Dark), effects);
        orb.SetCharacter(CharacterContent(pack, loc, quota));
        orb.Configure(new OrbGpuConfig(OrbStyle.Character, d, effects, true, true, Redline, strings, null, SourceSet.Empty), context);
        if (orb.Style is not CharacterDraw draw)
        {
            return;
        }

        var time = 300.0;
        var value = Redline * levels[0];
        orb.SetData(time, value, HeatMath.RawTier(value, Redline), SpeedTitles.Raw(value, Redline), [SourceKind.ClaudeCode], info, null);
        FeedCharacter(orb, loc, time, value, models[0]);
        orb.SnapNext();
        device.Atlas.BeginFrame();
        orb.Render(time);
        var warm = time;
        _ = CaptureCharacter(device, target, orb, ref warm, d * 1.5 * scale, 2);
        time = warm + Step;
        var start = time;
        var nextTick = start;
        var nextShot = start + 5;
        var pixels = (int)Math.Round(d * 1.5 * scale);
        var sides = new bool?[HudCards.Count];
        var changes = new int[HudCards.Count];
        var log = new StringBuilder();
        var shots = new List<BitmapSource>();
        while (time < start + Length)
        {
            var t = time - start;
            if (time >= nextTick)
            {
                nextTick = time + 0.5;
                value = Redline * levels[(int)(t / 10) % levels.Length];
                orb.SetData(time, value, HeatMath.RawTier(value, Redline), SpeedTitles.Raw(value, Redline), [SourceKind.ClaudeCode], info, null);
                FeedCharacter(orb, loc, time, value, models[(int)(t / 15) % models.Length]);
            }

            device.Atlas.BeginFrame();
            orb.Render(time);
            foreach (var card in HudCards.Column)
            {
                var i = (int)card;
                var right = draw.Wings.IsAtHerRight(card);
                if (draw.Wings.Presence[i] > 0 && sides[i] is { } was && was != right)
                {
                    changes[i]++;
                    log.Append(CultureInfo.InvariantCulture, $" {t:0.0}s {card}→{(right ? "R" : "L")}");
                }

                sides[i] = draw.Wings.Presence[i] > 0 ? right : sides[i];
            }

            if (time >= nextShot)
            {
                nextShot += 10;
                if (target.Render(device, orb.List, pixels, pixels) is { } bytes)
                {
                    shots.Add(OffscreenTarget.ToBitmap(bytes, pixels, pixels));
                }
            }

            time += Step;
        }

        WriteSheet([.. shots.Chunk(6).Select(c => c.ToList())], true, scale,
            Path.Combine(directory, $"character-balance-dance-{pack.Id}-{size:0}.png"));
        var counts = string.Join(", ", HudCards.Column.Select(c => $"{c} {changes[(int)c]}"));
        report.AppendLine(CultureInfo.InvariantCulture, $"balance dance {pack.Id} {size:0} pt, {Length:0} s: {changes.Sum()} side changes ({counts});{log}");
    }
}
