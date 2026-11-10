using System.Globalization;
using AgentSpeed.Core;
using AgentSpeed.Core.Locations;
using AgentSpeed.Core.Monitoring;
using AgentSpeed.Core.Pricing;

namespace AgentSpeed.Probe;

internal enum ProbeMode
{
    DumpToday,
    DumpHistory,
    DumpUnpriced,
    Watch,
    SyncPrices,
    SyncRates,
}

/// <param name="StorePath">History store to use and keep; null analyses into a temporary store.</param>
/// <param name="SinceMs">Overrides the start of "today" for <c>--dump-today</c> (Unix milliseconds).</param>
/// <param name="PricesPath"><c>--prices PATH</c>: price with this table instead of the bundled one (METRICS 9), as <c>reference.py --prices</c>.</param>
/// <param name="SyncDirectory">Scratch app-data folder of <c>--sync-prices</c> / <c>--sync-rates</c> (NETWORK 3, 4).</param>
/// <param name="SyncCurrency">The selected currency of <c>--sync-rates</c>.</param>
internal sealed record ProbeOptions(
    ProbeMode Mode,
    int WatchSeconds,
    bool Utc,
    string? StorePath,
    long? SinceMs = null,
    string? PricesPath = null,
    string? SyncDirectory = null,
    string? SyncCurrency = null)
{
    public const string Usage =
        "usage: AgentSpeed.Probe (--dump-today [--since UNIXMS] [--now UNIXMS] | --dump-history [--utc] [--history-store PATH] | " +
        "--dump-unpriced [--days N | --first YYYY-MM-DD --last YYYY-MM-DD | --live] [--source ID] [--history-store PATH] | " +
        "--watch N [--remote DESTINATION... [--history] [--history-store PATH]] | --sync-prices DIR | --sync-rates DIR CURRENCY) [--prices PATH] " +
        "[--codex PATH]... [--claude PATH]... [--dsh PATH]... [--opencode PATH]... [--gemini PATH]... [--omp PATH]... [--antigravity PATH]... " +
        "[--workbuddy PATH]... [--workbuddy-ai PATH]... [--zcode PATH]... [--kimi PATH]... [--grok-build PATH]... [--pi PATH]... " +
        "[--openclaw PATH]... [--hermes PATH]... [--droid PATH]... [--devin PATH]... [--qoder PATH]... [--qoder-cli PATH]...  (each source flag is repeatable; @auto is the automatic entry, METRICS 2.1)";

    /// <summary>The source flags and the location list each one adds to.</summary>
    private static readonly Dictionary<string, LocationFolder> SourceFlags = new(StringComparer.Ordinal)
    {
        ["--codex"] = LocationFolder.Codex,
        ["--claude"] = LocationFolder.Claude,
        ["--dsh"] = LocationFolder.Dsh,
        ["--opencode"] = LocationFolder.OpenCode,
        ["--gemini"] = LocationFolder.Gemini,
        ["--omp"] = LocationFolder.Omp,
        ["--antigravity"] = LocationFolder.Antigravity,
        ["--workbuddy"] = LocationFolder.WorkBuddy,
        ["--workbuddy-ai"] = LocationFolder.WorkBuddyAi,
        ["--zcode"] = LocationFolder.ZCode,
        ["--kimi"] = LocationFolder.Kimi,
        ["--grok-build"] = LocationFolder.GrokBuild,
        ["--pi"] = LocationFolder.Pi,
        ["--openclaw"] = LocationFolder.OpenClaw,
        ["--hermes"] = LocationFolder.Hermes,
        ["--droid"] = LocationFolder.Droid,
        ["--devin"] = LocationFolder.Devin,
        ["--qoder"] = LocationFolder.Qoder,
        ["--qoder-cli"] = LocationFolder.QoderCli,
    };

    /// <summary>
    /// The location lists of the source flags (METRICS 2.1): every flag adds one entry to its source's list, <c>@auto</c> the
    /// automatic entry; a source without a flag reads its automatic roots.
    /// </summary>
    public LocationLists Lists { get; init; } = LocationLists.Automatic;

    /// <summary><c>--days N</c> of <c>--dump-unpriced</c>: the last N local days, today included.</summary>
    public int? Days { get; init; }

    /// <summary><c>--remote DESTINATION</c> (repeatable): <c>--watch</c> also watches these SSH hosts (REMOTE).</summary>
    public IReadOnlyList<string> Remotes { get; init; } = [];

    /// <summary><c>--history</c> (with <c>--remote</c>): also syncs the hosts' daily history (REMOTE 8) into <c>--history-store</c> or a scratch store.</summary>
    public bool RemoteHistory { get; init; }

    /// <summary><c>--first</c> / <c>--last</c> of <c>--dump-unpriced</c>: a custom range of local dates.</summary>
    public string? First { get; init; }

    public string? Last { get; init; }

    /// <summary><c>--live</c> of <c>--dump-unpriced</c>: today's engine responses instead of the history.</summary>
    public bool Live { get; init; }

    /// <summary><c>--source ID</c> of <c>--dump-unpriced</c> (one source, METRICS 1).</summary>
    public SourceKind? Source { get; init; }

    /// <summary><c>--now UNIXMS</c>: the instant <c>--dump-today</c> evaluates the performance scores at (default: the capture time).</summary>
    public long? NowMs { get; init; }

    public MonitorConfiguration Configuration => new(new HashSet<SourceKind>(Enum.GetValues<SourceKind>())) { Locations = Lists };

    /// <summary>Resolved roots: the union of every list's entries.</summary>
    public SourceLocations Locations => SourceLocations.Resolve(Configuration);

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
        string? store = null, prices = null, sync = null, currency = null;
        var lists = new Dictionary<LocationFolder, List<DataLocation>>();
        long? since = null, nowMs = null;
        int? days = null;
        string? first = null, last = null, sourceId = null;
        var live = false;
        var remotes = new List<string>();
        var remoteHistory = false;
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
                case { } flag when SourceFlags.TryGetValue(flag, out var folder) && i + 1 < args.Length:
                    if (Entry(args[++i]) is not { } entry)
                    {
                        error = $"{flag} needs a path or @auto: {args[i]}";
                        return null;
                    }

                    if (!lists.TryGetValue(folder, out var list))
                    {
                        lists[folder] = list = [];
                    }

                    list.Add(entry);
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
                case "--remote" when i + 1 < args.Length && Core.Remote.RemoteHostValidation.DestinationIssue(args[i + 1]) is null
                    && remotes.Count < Core.Remote.RemoteHostsDocument.MaxHosts:
                    remotes.Add(args[++i]);
                    break;
                case "--history":
                    remoteHistory = true;
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
            error = "missing --dump-today, --dump-history, --dump-unpriced, --watch N, --sync-prices DIR or --sync-rates DIR CURRENCY";
            return null;
        }

        if ((remotes.Count > 0 && mode != ProbeMode.Watch) || (remoteHistory && remotes.Count == 0))
        {
            error = "--remote goes with --watch N, --history with --remote";
            return null;
        }

        SourceKind? source = null;
        if (mode == ProbeMode.DumpUnpriced && UnpricedRangeError(days, first, last, live, sourceId, out source) is { } rangeError)
        {
            error = rangeError;
            return null;
        }

        return new ProbeOptions(mode.Value, seconds, utc, store, since, prices, sync, currency)
        {
            Lists = lists.Aggregate(LocationLists.Automatic, (all, pair) => all.With(pair.Key, pair.Value)),
            Days = days,
            First = first,
            Last = last,
            Live = live,
            Source = source,
            NowMs = nowMs,
            Remotes = remotes,
            RemoteHistory = remoteHistory,
        };
    }

    /// <summary>One entry of a source flag: <c>@auto</c>, else the path made absolute; null for a path that cannot be one.</summary>
    private static DataLocation? Entry(string value)
    {
        if (value == "@auto")
        {
            return DataLocation.Automatic;
        }

        try
        {
            return value.Length > 0 ? DataLocation.At(Path.GetFullPath(value)) : null;
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
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

    private static bool IsDate(string text) =>
        text.Length == 10 && DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _);
}
