using System.Globalization;
using AgentSpeed.Core;
using AgentSpeed.Core.Monitoring;
using AgentSpeed.Core.Pricing;
using AgentSpeed.Core.Tasks;

namespace AgentSpeed.Probe;

internal enum ProbeMode
{
    DumpToday,
    DumpHistory,
    DumpUnpriced,
    DumpTasks,
    Watch,
    SyncPrices,
    SyncRates,
}

/// <param name="GeminiRoots">Gemini CLI <c>tmp</c> folders; empty uses the defaults.</param>
/// <param name="OmpRoots">omp <c>sessions</c> folders; empty uses the default root and every named profile.</param>
/// <param name="AntigravityRoots">Antigravity store (or <c>conversations</c>) folders; empty uses the three default stores.</param>
/// <param name="StorePath">History store to use and keep; null analyses into a temporary store.</param>
/// <param name="SinceMs">Overrides the start of "today" for <c>--dump-today</c> (Unix milliseconds).</param>
/// <param name="PricesPath"><c>--prices PATH</c>: price with this table instead of the bundled one (METRICS 9), as <c>reference.py --prices</c>.</param>
/// <param name="SyncDirectory">Scratch app-data folder of <c>--sync-prices</c> / <c>--sync-rates</c> (NETWORK 3, 4).</param>
/// <param name="SyncCurrency">The selected currency of <c>--sync-rates</c>.</param>
/// <param name="WorkBuddyRoot">The WorkBuddy data folder or its <c>projects</c> folder; null uses <c>~/.workbuddy</c>.</param>
/// <param name="WorkBuddyAiRoot">The WorkBuddy AI data folder or its <c>projects</c> folder; null uses <c>~/.workbuddy-ai</c>.</param>
/// <param name="PiRoot">One Pi sessions folder (replaces every automatic Pi root); null uses Pi's automatic roots.</param>
/// <param name="OpenClawRoot">An OpenClaw state folder, agent folder or database (replaces every automatic state folder).</param>
/// <param name="HermesRoot">A Hermes home or one <c>logs</c> folder (replaces the automatic Hermes home).</param>
/// <param name="DroidRoot">The Factory Droid <c>.factory</c> folder or its <c>sessions</c> folder (replaces the automatic root).</param>
internal sealed record ProbeOptions(
    ProbeMode Mode,
    int WatchSeconds,
    string? CodexRoot,
    string? ClaudeRoot,
    string? DshRoot,
    string? OpenCodeRoot,
    IReadOnlyList<string> GeminiRoots,
    bool Utc,
    string? StorePath,
    long? SinceMs = null,
    IReadOnlyList<string>? OmpRoots = null,
    IReadOnlyList<string>? AntigravityRoots = null,
    string? PricesPath = null,
    string? SyncDirectory = null,
    string? SyncCurrency = null,
    string? WorkBuddyRoot = null,
    string? WorkBuddyAiRoot = null,
    string? ZCodeRoot = null,
    string? KimiRoot = null,
    string? GrokBuildRoot = null,
    string? PiRoot = null,
    string? OpenClawRoot = null,
    string? HermesRoot = null,
    string? DroidRoot = null,
    string? TraeRoot = null)
{
    public const string Usage =
        "usage: AgentSpeed.Probe (--dump-today [--since UNIXMS] [--now UNIXMS] | --dump-history [--utc] [--history-store PATH] | " +
        "--dump-unpriced [--days N | --first YYYY-MM-DD --last YYYY-MM-DD | --live] [--source ID] [--history-store PATH] | " +
        "--dump-tasks [--utc] [--first YYYY-MM-DD] [--last YYYY-MM-DD] [--source ID] [--now UNIXMS] | " +
        "--watch N | --sync-prices DIR | --sync-rates DIR CURRENCY) [--prices PATH] " +
        "[--codex PATH] [--claude PATH] [--dsh PATH] [--opencode PATH] [--gemini PATH]... [--omp PATH]... [--antigravity PATH]... " +
        "[--workbuddy PATH] [--workbuddy-ai PATH] [--zcode PATH] [--kimi PATH] [--grok-build PATH] [--pi PATH] [--openclaw PATH] [--hermes PATH] [--droid PATH] [--trae PATH]";

    /// <summary><c>--days N</c> of <c>--dump-unpriced</c>: the last N local days, today included.</summary>
    public int? Days { get; init; }

    /// <summary><c>--first</c> / <c>--last</c> of <c>--dump-unpriced</c>: a custom range of local dates.</summary>
    public string? First { get; init; }

    public string? Last { get; init; }

    /// <summary><c>--live</c> of <c>--dump-unpriced</c>: today's engine responses instead of the history.</summary>
    public bool Live { get; init; }

    /// <summary><c>--source ID</c> of <c>--dump-unpriced</c> (one source, METRICS 1) or <c>--dump-tasks</c> (one scored source).</summary>
    public SourceKind? Source { get; init; }

    /// <summary>
    /// <c>--now UNIXMS</c>: the instant <c>--dump-today</c> evaluates the task values at (default: the capture time), or the
    /// <c>analysisMs</c> of <c>--dump-tasks</c> (default: the wall clock).
    /// </summary>
    public long? NowMs { get; init; }

    public MonitorConfiguration Configuration => new(
        new HashSet<SourceKind>(Enum.GetValues<SourceKind>()),
        CodexRoot,
        ClaudeRoot,
        DshRoot,
        OpenCodeRoot,
        GeminiRoots.Count > 0 ? GeminiRoots[0] : null,
        OmpRoots is { Count: > 0 } omp ? omp[0] : null,
        AntigravityRoots is { Count: > 0 } antigravity ? antigravity[0] : null,
        WorkBuddyRoot,
        WorkBuddyAiRoot, ZCodeRoot, KimiRoot, GrokBuildRoot, PiRoot, OpenClawRoot, HermesRoot, DroidRoot, TraeRoot);

    /// <summary>Resolved roots; several <c>--gemini</c>, <c>--omp</c> or <c>--antigravity</c> folders are all read (like the reference).</summary>
    public SourceLocations Locations
    {
        get
        {
            var resolved = SourceLocations.Resolve(Configuration);
            if (GeminiRoots.Count > 1)
            {
                resolved = resolved with { GeminiRoots = [.. GeminiRoots.Select(SourceLocations.Normalize)] };
            }

            if (OmpRoots is { Count: > 1 } omp)
            {
                resolved = resolved with { OmpRoots = SourceLocations.NormalizeAll(omp) };
            }

            if (AntigravityRoots is { Count: > 1 } antigravity)
            {
                resolved = resolved with
                {
                    AntigravityStores = SourceLocations.NormalizeAll(antigravity.SelectMany(LiveMonitor.AntigravityStores)),
                };
            }

            return resolved;
        }
    }

    /// <summary>The table of <c>--prices</c>, else the bundled one; null with <paramref name="error"/> when the file does not validate.</summary>
    public PriceTable? PriceTable(out string? error)
    {
        error = null;
        if (PricesPath is null)
        {
            return PriceBook.Bundled;
        }

        byte[] data;
        try
        {
            data = File.ReadAllBytes(PricesPath);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            error = $"cannot read {PricesPath}";
            return null;
        }

        var loaded = AgentSpeed.Core.Pricing.PriceTable.Load(data);
        error = loaded.Table is null ? $"{PricesPath} is not a valid price table: {loaded.Error}" : null;
        return loaded.Table;
    }

    public static ProbeOptions? Parse(string[] args, out string? error)
    {
        error = null;
        ProbeMode? mode = null;
        var seconds = 0;
        var utc = false;
        string? codex = null, claude = null, dsh = null, opencode = null, store = null, prices = null, sync = null, currency = null;
        string? workBuddy = null, workBuddyAi = null, zcode = null, kimi = null, grokBuild = null, pi = null, openClaw = null, hermes = null, droid = null, trae = null;
        long? since = null, nowMs = null;
        int? days = null;
        string? first = null, last = null, sourceId = null;
        var live = false;
        var gemini = new List<string>();
        var omp = new List<string>();
        var antigravity = new List<string>();
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--dump-today":
                    mode = ProbeMode.DumpToday;
                    break;
                case "--dump-history":
                    mode = ProbeMode.DumpHistory;
                    break;
                case "--dump-unpriced":
                    mode = ProbeMode.DumpUnpriced;
                    break;
                case "--dump-tasks":
                    mode = ProbeMode.DumpTasks;
                    break;
                case "--now" when i + 1 < args.Length && long.TryParse(args[i + 1], NumberStyles.None, CultureInfo.InvariantCulture, out var at):
                    nowMs = at;
                    i++;
                    break;
                case "--days" when i + 1 < args.Length && int.TryParse(args[i + 1], NumberStyles.None, CultureInfo.InvariantCulture, out var count):
                    days = count;
                    i++;
                    break;
                case "--first" when i + 1 < args.Length:
                    first = args[++i];
                    break;
                case "--last" when i + 1 < args.Length:
                    last = args[++i];
                    break;
                case "--live":
                    live = true;
                    break;
                case "--source" when i + 1 < args.Length:
                    sourceId = args[++i];
                    break;
                case "--utc":
                    utc = true;
                    break;
                case "--watch" when i + 1 < args.Length && int.TryParse(args[i + 1], out var n) && n > 0:
                    mode = ProbeMode.Watch;
                    seconds = n;
                    i++;
                    break;
                case "--codex" when i + 1 < args.Length:
                    codex = args[++i];
                    break;
                case "--claude" when i + 1 < args.Length:
                    claude = args[++i];
                    break;
                case "--dsh" when i + 1 < args.Length:
                    dsh = args[++i];
                    break;
                case "--opencode" when i + 1 < args.Length:
                    opencode = args[++i];
                    break;
                case "--gemini" when i + 1 < args.Length:
                    gemini.Add(args[++i]);
                    break;
                case "--omp" when i + 1 < args.Length:
                    omp.Add(args[++i]);
                    break;
                case "--antigravity" when i + 1 < args.Length:
                    antigravity.Add(args[++i]);
                    break;
                case "--workbuddy" when i + 1 < args.Length:
                    workBuddy = args[++i];
                    break;
                case "--workbuddy-ai" when i + 1 < args.Length:
                    workBuddyAi = args[++i];
                    break;
                case "--zcode" when i + 1 < args.Length:
                    zcode = args[++i];
                    break;
                case "--kimi" when i + 1 < args.Length:
                    kimi = args[++i];
                    break;
                case "--grok-build" when i + 1 < args.Length:
                    grokBuild = args[++i];
                    break;
                case "--trae" when i + 1 < args.Length:
                    trae = args[++i];
                    break;
                case "--pi" when i + 1 < args.Length:
                    pi = args[++i];
                    break;
                case "--openclaw" when i + 1 < args.Length:
                    openClaw = args[++i];
                    break;
                case "--hermes" when i + 1 < args.Length:
                    hermes = args[++i];
                    break;
                case "--droid" when i + 1 < args.Length:
                    droid = args[++i];
                    break;
                case "--store" or "--history-store" when i + 1 < args.Length:
                    store = args[++i];
                    break;
                case "--prices" when i + 1 < args.Length:
                    prices = args[++i];
                    break;
                case "--sync-prices" when i + 1 < args.Length:
                    mode = ProbeMode.SyncPrices;
                    sync = args[++i];
                    break;
                case "--sync-rates" when i + 2 < args.Length:
                    mode = ProbeMode.SyncRates;
                    sync = args[++i];
                    currency = args[++i];
                    break;
                case "--since" when i + 1 < args.Length && long.TryParse(args[i + 1], out var ms):
                    since = ms;
                    i++;
                    break;
                default:
                    error = $"unknown or incomplete argument: {args[i]}";
                    return null;
            }
        }

        if (mode is null)
        {
            error = "missing --dump-today, --dump-history, --dump-unpriced, --dump-tasks, --watch N, --sync-prices DIR or --sync-rates DIR CURRENCY";
            return null;
        }

        SourceKind? source = null;
        if (mode == ProbeMode.DumpUnpriced && UnpricedRangeError(days, first, last, live, sourceId, out source) is { } rangeError)
        {
            error = rangeError;
            return null;
        }

        if (mode == ProbeMode.DumpTasks && TasksRangeError(first, last, sourceId, out source) is { } tasksError)
        {
            error = tasksError;
            return null;
        }

        return new ProbeOptions(
            mode.Value, seconds, codex, claude, dsh, opencode, gemini, utc, store, since, omp, antigravity, prices, sync, currency,
            workBuddy, workBuddyAi, zcode, kimi, grokBuild, pi, openClaw, hermes, droid, trae)
        {
            Days = days,
            First = first,
            Last = last,
            Live = live,
            Source = source,
            NowMs = nowMs,
        };
    }

    /// <summary>The checks of <c>reference.py --dump-unpriced</c>.</summary>
    private static string? UnpricedRangeError(int? days, string? first, string? last, bool live, string? sourceId, out SourceKind? source)
    {
        source = null;
        if (days is not null && (days < 1 || first is not null || last is not null))
        {
            return "--days takes a positive number and excludes --first/--last";
        }

        if ((first is not null && !IsDate(first)) || (last is not null && !IsDate(last)))
        {
            return "dates are YYYY-MM-DD";
        }

        if ((first is null) != (last is null) || (first is not null && string.CompareOrdinal(first, last) > 0))
        {
            return "--first and --last go together, first <= last";
        }

        if (live && (days is not null || first is not null))
        {
            return "--live takes no range: it is today's engine data";
        }

        if (sourceId is not null)
        {
            source = Enum.GetValues<SourceKind>().Cast<SourceKind?>().FirstOrDefault(k => JsonLine.SourceName(k!.Value) == sourceId);
            if (source is null)
            {
                return "--source takes a source id: " + string.Join(", ", Enum.GetValues<SourceKind>().Select(JsonLine.SourceName));
            }
        }

        return null;
    }

    /// <summary>The checks of <c>reference.py --dump-tasks</c>, with dates checked as in <c>--dump-unpriced</c>.</summary>
    private static string? TasksRangeError(string? first, string? last, string? sourceId, out SourceKind? source)
    {
        source = null;
        if ((first is not null && !IsDate(first)) || (last is not null && !IsDate(last)))
        {
            return "dates are YYYY-MM-DD";
        }

        if (sourceId is not null)
        {
            source = TaskSetView.ScoredSources.Cast<SourceKind?>().FirstOrDefault(k => JsonLine.SourceName(k!.Value) == sourceId);
            if (source is null)
            {
                return "--source takes a scored source id: " + string.Join(", ", TaskSetView.ScoredSources.Select(JsonLine.SourceName));
            }
        }

        return null;
    }

    private static bool IsDate(string text) =>
        text.Length == 10 && DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _);
}
