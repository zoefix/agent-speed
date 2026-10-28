using System.Globalization;

namespace AgentSpeed.Presentation.Updates;

/// <summary>
/// Command-line flags: <c>--after-update &lt;old version&gt;</c> and <c>--wait-pid &lt;pid&gt;</c> (the relaunch after an
/// install, UPDATES section 5) and the development flags <c>--check-updates-now</c>, <c>--preview-history</c>,
/// <c>--demo-speed 24,96,400</c>, <c>--hero-demo [tour|startups|idle]</c>, <c>--hero-glow blur|strokes</c>,
/// <c>--no-hero</c>, <c>--reduce-motion</c> and the orb flags <c>--orb-demo</c>, <c>--orb-style</c>, <c>--orb-count</c>,
/// <c>--orb-size</c>, <c>--orb-tour</c>, <c>--theme</c>, <c>--effects</c>.
/// Unknown arguments are ignored.
/// </summary>
public sealed record LaunchArguments(bool AfterUpdate, string? PreviousVersion, int? WaitPid, bool CheckUpdatesNow)
{
    public const string AfterUpdateFlag = "--after-update";
    public const string WaitPidFlag = "--wait-pid";
    public const string CheckUpdatesNowFlag = "--check-updates-now";
    public const string PreviewHistoryFlag = "--preview-history";
    public const string DemoSpeedFlag = "--demo-speed";
    public const string HeroDemoFlag = "--hero-demo";
    public const string HeroGlowFlag = "--hero-glow";
    public const string ReduceMotionFlag = "--reduce-motion";
    public const string NoHeroFlag = "--no-hero";
    public const string OrbDemoFlag = "--orb-demo";
    public const string OrbStyleFlag = "--orb-style";
    public const string OrbCountFlag = "--orb-count";
    public const string OrbSizeFlag = "--orb-size";
    public const string OrbTourFlag = "--orb-tour";
    public const string ThemeFlag = "--theme";
    public const string EffectsFlag = "--effects";
    public const string CaptureDirFlag = "--capture-dir";
    public const string ReportE2EFlag = "--report-e2e";
    private const int MaxDemoSpeeds = 32;

    /// <summary>Development flag: fill the Usage History window with generated days instead of the real history.</summary>
    public bool PreviewHistory { get; init; }

    /// <summary>
    /// Development flag: hold the live speed at these tok/s values in turn (normalized, comma separated), to inspect the
    /// speed titles and the zero-speed glyphs. Null when not given.
    /// </summary>
    public string? DemoSpeeds { get; init; }

    public IReadOnlyList<double> DemoSpeedValues => ParseSpeeds(DemoSpeeds);

    /// <summary>
    /// Development flag: drive the live value through the hero vehicle tour of DESIGN 4c (<c>tour</c>, the default, or
    /// <c>startups</c> or <c>idle</c>) and open the dashboard, so the vehicle can be captured on a real machine. Null when not given.
    /// </summary>
    public string? HeroDemo { get; init; }

    /// <summary>Development flag: force the hero glow technique (<c>blur</c> or <c>strokes</c>) to compare them.</summary>
    public string? HeroGlow { get; init; }

    /// <summary>Development flag: behave as if the system animations were off (Reduce Motion), without changing them.</summary>
    public bool ReduceMotion { get; init; }

    /// <summary>Development flag: leave the dashboard hero without its vehicle layer (to measure what the layer costs).</summary>
    public bool NoHero { get; init; }

    /// <summary>
    /// Development flag <c>--orb-demo styles|multi|grid|names|names-cluster|names-crowd</c>: replace the orbs for this
    /// session only (settings are not saved) by every style side by side, a multi-orb arrangement with different source
    /// sets, a grid of <see cref="OrbCount"/> orbs of <see cref="OrbStyle"/> (the benchmark layout), or the name plate
    /// placement scenes (a top row, a bottom-docked row and a right-docked column; a 2 × 2 cluster with mixed sizes; a
    /// crowd where one plate has no room).
    /// </summary>
    public string? OrbDemo { get; init; }

    /// <summary>Development flag <c>--orb-style led|needle|liquid|reactor|minimal</c> for the demo orbs.</summary>
    public string? OrbStyle { get; init; }

    /// <summary>Development flag <c>--orb-count 1..8</c>: number of orbs in the grid demo.</summary>
    public int? OrbCount { get; init; }

    /// <summary>Development flag <c>--orb-size 64..240</c>: diameter of the demo orbs.</summary>
    public double? OrbSize { get; init; }

    /// <summary>Development flag <c>--orb-tour</c>: loop the orbs through idle → climb → redline → overdrive → back.</summary>
    public bool OrbTour { get; init; }

    /// <summary>Development flag <c>--theme light|dark</c>: force the appearance for this session.</summary>
    public string? Theme { get; init; }

    /// <summary>Development flag <c>--effects off|soft|standard|max</c>: force the effects level for this session.</summary>
    public string? Effects { get; init; }

    /// <summary>
    /// Development flag <c>--capture-dir &lt;folder&gt;</c>: the app renders its orbs or menus into PNG files in that folder
    /// when a <c>capture.request</c> file appears there (used to verify the look on a test machine without capturing
    /// the screen).
    /// </summary>
    public string? CaptureDir { get; init; }

    /// <summary>
    /// Development flag <c>--report-e2e &lt;url&gt;</c>: drives live reporting end to end against a local reference server
    /// (<c>spec/tools/report_server.py</c>) with a synthetic engine feed, prints one line per check and quits. Only a loopback
    /// URL is accepted; null when not given.
    /// </summary>
    public string? ReportE2E { get; init; }

    /// <summary>Any orb development flag: the session's settings are kept in memory only.</summary>
    public bool EphemeralSettings => OrbDemo is not null || OrbStyle is not null || OrbCount is not null || OrbSize is not null
        || Theme is not null || Effects is not null;

    /// <summary>Comma-separated non-negative finite numbers; anything invalid yields an empty list.</summary>
    public static IReadOnlyList<double> ParseSpeeds(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        var values = new List<double>();
        foreach (var part in text.Split(',', StringSplitOptions.TrimEntries))
        {
            if (values.Count >= MaxDemoSpeeds
                || !double.TryParse(part, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value)
                || !double.IsFinite(value) || value > 1e9)
            {
                return [];
            }

            values.Add(value);
        }

        return values;
    }

    public static LaunchArguments None { get; } = new(false, null, null, false);

    public static LaunchArguments Parse(IReadOnlyList<string>? args)
    {
        if (args is null || args.Count == 0)
        {
            return None;
        }

        var afterUpdate = false;
        string? previous = null;
        int? waitPid = null;
        var checkNow = false;
        var previewHistory = false;
        string? demoSpeeds = null;
        string? heroDemo = null;
        string? heroGlow = null;
        var reduceMotion = false;
        var noHero = false;
        string? orbDemo = null;
        string? orbStyle = null;
        int? orbCount = null;
        double? orbSize = null;
        var orbTour = false;
        string? theme = null;
        string? effects = null;
        string? captureDir = null;
        string? reportE2E = null;
        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            var value = i + 1 < args.Count ? args[i + 1] : null;
            if (string.Equals(arg, AfterUpdateFlag, StringComparison.OrdinalIgnoreCase))
            {
                afterUpdate = true;
                if (SemanticVersion.TryParse(value, out var version))
                {
                    previous = version.ToString();
                    i++;
                }
            }
            else if (string.Equals(arg, WaitPidFlag, StringComparison.OrdinalIgnoreCase))
            {
                if (value is not null && int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var pid) && pid > 0)
                {
                    waitPid = pid;
                    i++;
                }
            }
            else if (string.Equals(arg, CheckUpdatesNowFlag, StringComparison.OrdinalIgnoreCase))
            {
                checkNow = true;
            }
            else if (string.Equals(arg, PreviewHistoryFlag, StringComparison.OrdinalIgnoreCase))
            {
                previewHistory = true;
            }
            else if (string.Equals(arg, HeroDemoFlag, StringComparison.OrdinalIgnoreCase))
            {
                heroDemo = "tour";
                if (value is "tour" or "startups" or "idle")
                {
                    heroDemo = value;
                    i++;
                }
            }
            else if (string.Equals(arg, HeroGlowFlag, StringComparison.OrdinalIgnoreCase))
            {
                if (value is "blur" or "strokes")
                {
                    heroGlow = value;
                    i++;
                }
            }
            else if (string.Equals(arg, ReduceMotionFlag, StringComparison.OrdinalIgnoreCase))
            {
                reduceMotion = true;
            }
            else if (string.Equals(arg, NoHeroFlag, StringComparison.OrdinalIgnoreCase))
            {
                noHero = true;
            }
            else if (string.Equals(arg, OrbDemoFlag, StringComparison.OrdinalIgnoreCase))
            {
                orbDemo = "styles";
                if (value is "styles" or "multi" or "grid" or "names" or "names-cluster" or "names-crowd")
                {
                    orbDemo = value;
                    i++;
                }
            }
            else if (string.Equals(arg, OrbStyleFlag, StringComparison.OrdinalIgnoreCase))
            {
                if (value is "led" or "needle" or "liquid" or "reactor" or "minimal")
                {
                    orbStyle = value;
                    i++;
                }
            }
            else if (string.Equals(arg, OrbCountFlag, StringComparison.OrdinalIgnoreCase))
            {
                if (value is not null && int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var count) && count is >= 1 and <= 8)
                {
                    orbCount = count;
                    i++;
                }
            }
            else if (string.Equals(arg, OrbSizeFlag, StringComparison.OrdinalIgnoreCase))
            {
                if (value is not null && double.TryParse(value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var size)
                    && size is >= 32 and <= 240)
                {
                    orbSize = size;
                    i++;
                }
            }
            else if (string.Equals(arg, OrbTourFlag, StringComparison.OrdinalIgnoreCase))
            {
                orbTour = true;
            }
            else if (string.Equals(arg, ThemeFlag, StringComparison.OrdinalIgnoreCase))
            {
                if (value is "light" or "dark")
                {
                    theme = value;
                    i++;
                }
            }
            else if (string.Equals(arg, CaptureDirFlag, StringComparison.OrdinalIgnoreCase))
            {
                if (value is not null && Path.IsPathFullyQualified(value) && value.Length < 260 && value.IndexOf('\0') < 0)
                {
                    captureDir = value;
                    i++;
                }
            }
            else if (string.Equals(arg, ReportE2EFlag, StringComparison.OrdinalIgnoreCase))
            {
                if (value is not null && Uri.TryCreate(value, UriKind.Absolute, out var url) && url.Scheme is "http" or "https"
                    && url.Host is "127.0.0.1" or "localhost" or "[::1]" or "::1" && url.UserInfo.Length == 0)
                {
                    reportE2E = value;
                    i++;
                }
            }
            else if (string.Equals(arg, EffectsFlag, StringComparison.OrdinalIgnoreCase))
            {
                if (value is "off" or "soft" or "standard" or "max")
                {
                    effects = value;
                    i++;
                }
            }
            else if (string.Equals(arg, DemoSpeedFlag, StringComparison.OrdinalIgnoreCase))
            {
                var speeds = ParseSpeeds(value);
                if (speeds.Count > 0)
                {
                    demoSpeeds = string.Join(',', speeds.Select(v => v.ToString("R", CultureInfo.InvariantCulture)));
                    i++;
                }
            }
        }

        return new LaunchArguments(afterUpdate, previous, waitPid, checkNow)
        {
            PreviewHistory = previewHistory,
            DemoSpeeds = demoSpeeds,
            HeroDemo = heroDemo,
            HeroGlow = heroGlow,
            ReduceMotion = reduceMotion,
            NoHero = noHero,
            OrbDemo = orbDemo,
            OrbStyle = orbStyle,
            OrbCount = orbCount,
            OrbSize = orbSize,
            OrbTour = orbTour,
            Theme = theme,
            Effects = effects,
            CaptureDir = captureDir,
            ReportE2E = reportE2E,
        };
    }

    /// <summary>The arguments the installer passes to the new executable.</summary>
    public static IReadOnlyList<string> ForRelaunch(string previousVersion, int processId) =>
        [AfterUpdateFlag, previousVersion, WaitPidFlag, processId.ToString(CultureInfo.InvariantCulture)];
}
