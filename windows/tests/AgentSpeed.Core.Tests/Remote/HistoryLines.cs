using System.Globalization;
using System.Text;
using System.Text.Json;
using AgentSpeed.Core.History;
using AgentSpeed.Core.Remote;

namespace AgentSpeed.Core.Tests.Remote;

/// <summary>Relay history lines (REMOTE 5.2) decoded as the connection delivers them, and the Linux core's aggregate encoding.</summary>
internal static class RemoteHistoryLines
{
    public const string Host = "8C0D6B0A-1111-4222-8333-944455556666";
    public const string OtherHost = "1F2E3D4C-5555-4666-8777-988899990000";

    public static string Aggregate(int requests = 3, int output = 340, string model = "gpt-5.5", long? cost = 1234)
    {
        var micro = cost?.ToString(CultureInfo.InvariantCulture) ?? "null";
        var unpriced = cost is null ? "null" : "0";
        return $"{{\"requests\":{requests},\"input\":1200,\"cachedInput\":200,\"cacheWriteInput\":10,\"output\":{output}," +
            $"\"reasoning\":20,\"measuredOutput\":{output},\"measuredMs\":2000,\"unfinalized\":0,\"agents\":1," +
            $"\"costMicroUsd\":{micro},\"unpricedRequests\":{unpriced}," +
            $"\"models\":{{\"{model}\":[{requests},{output},{output},2000,{micro},{unpriced}]}},\"unpriced\":[]," +
            $"\"modes\":[[\"{model}\",\"serviceTier\",\"fast\",\"recorded\",\"openai\",1,100,90,500]]}}";
    }

    public const string EmptyAggregate =
        "{\"requests\":0,\"input\":0,\"cachedInput\":0,\"cacheWriteInput\":0,\"output\":0,\"reasoning\":0," +
        "\"measuredOutput\":0,\"measuredMs\":0,\"unfinalized\":0,\"agents\":0,\"costMicroUsd\":0,\"unpricedRequests\":0," +
        "\"models\":{},\"unpriced\":[],\"modes\":[]}";

    public static string Record(int n = 4, int toolN = 0)
    {
        var bins = "[" + string.Join(',', Enumerable.Range(0, 64).Select(i => i == 10 ? n.ToString(CultureInfo.InvariantCulture) : "0")) + "]";
        var lnSum = (toolN * 7.5).ToString("0.0###", CultureInfo.InvariantCulture);
        return $"{{\"n\":{n},\"outputSum\":{n * 100},\"durationMsSum\":{n * 1000},\"latencyBins\":{bins},\"reasonN\":0," +
            $"\"reasonOutput\":0,\"reasonDurationMsSum\":0,\"cacheN\":{n},\"cacheInput\":1000,\"cacheCached\":800," +
            $"\"toolN\":{toolN},\"toolLnSum\":{lnSum},\"toolFailed\":0,\"activeMs\":{n * 1500}}}";
    }

    public static RelayMessage Message(string line, string host = Host)
    {
        var outcome = new RelayLineDecoder(host).Decode(Encoding.UTF8.GetBytes(line));
        return outcome is { Kind: RelayDecodeKind.Message, Message: { } message }
            ? message
            : throw new InvalidOperationException($"not a relay message ({outcome.Error?.Message}): {line}");
    }

    public static RelayMessage Day(string day, SourceKind source = SourceKind.Codex, string? aggregate = null, string? digest = null)
    {
        digest ??= new string('a', 15) + day[^1];
        return Message($$"""{"t":"historyDay","day":"{{day}}","source":"{{SourceNames.Of(source)}}","aggregate":{{aggregate ?? Aggregate()}},"digest":"{{digest}}"}""");
    }

    public static RelayMessage Perf(string day, SourceKind source = SourceKind.Codex, string model = "gpt-5.5", string? record = null) =>
        Message($$"""{"t":"historyPerf","day":"{{day}}","source":"{{SourceNames.Of(source)}}","model":"{{model}}","record":{{record ?? Record()}}}""");

    public static RelayMessage Done { get; } = new RelayMessage.HistoryDone(0, 0);

    public static RelayMessage Failed { get; } = new RelayMessage.Error(new RelayError(RelayErrorCode.HistoryFailed, "historyFailed", "x"));

    public static RemoteHostEvent Event(RelayMessage message, string host = Host, int connection = 1) =>
        new(host, connection, new RemoteHostEventKind.Message(message, false));

    public static RemoteHostEvent Status(RemoteStatus status, string host = Host, int connection = 1) =>
        new(host, connection, new RemoteHostEventKind.Status(new RemoteHostStatus(status)));

    public static RemoteHostEvent Due(string host = Host, int connection = 1) => new(host, connection, new RemoteHostEventKind.HistoryDue());

    public static RemoteHistoryDay DayLine(string day) => ((RelayMessage.HistoryDay)Day(day)).Value;

    public static RemoteHistoryPerf PerfLine(string day, string model = "gpt-5.5") => ((RelayMessage.HistoryPerf)Perf(day, model: model)).Value;

    /// <summary>Applies events in order and returns the last effect.</summary>
    public static RemoteHistoryEffect Apply(this RemoteHistoryLedger ledger, HistoryStore store, params RemoteHostEvent[] events)
    {
        var last = RemoteHistoryEffect.None;
        foreach (var hostEvent in events)
        {
            last = ledger.Receive(hostEvent, store);
        }

        return last;
    }

    public static Dictionary<string, string> Digests(params (string Key, string Digest)[] digests) =>
        digests.ToDictionary(d => d.Key, d => d.Digest, StringComparer.Ordinal);

    public static long Count(HistoryStore store, string table) =>
        store.Connection.ScalarInt64($"SELECT count(*) FROM {table}") ?? 0;

    /// <summary><c>history_store.cpp</c> <c>encode</c> of a merged day aggregate.</summary>
    public static string LinuxJson(DayAggregate d)
    {
        static string Text(string value) => JsonSerializer.Serialize(value);
        static string Optional(long? value) => value?.ToString(CultureInfo.InvariantCulture) ?? "null";
        var models = string.Join(',', d.Models.OrderBy(m => m.Key, StringComparer.Ordinal).Select(m =>
            $"{Text(m.Key)}:[{m.Value.Requests},{m.Value.Output},{m.Value.MeasuredOutput},{m.Value.MeasuredMs}," +
            $"{Optional(m.Value.Cost?.CostMicroUsd)},{Optional(m.Value.Cost?.UnpricedRequests)}]"));
        var unpriced = string.Join(',', AgentSpeed.Core.Unpriced.UnpricedFacts.Sorted(d.Unpriced).Select(f =>
            $"[{Text(f.Fact.Model)},{(f.Fact.Provider is { } p ? Text(p) : "null")},{Bool(f.Fact.Fast)},{Bool(f.Fact.NoDate)},{f.Requests}]"));
        var modes = string.Join(',', d.Modes.OrderBy(m => m.Key.Model, StringComparer.Ordinal).ThenBy(m => m.Key.Kind)
            .ThenBy(m => m.Key.Mode, StringComparer.Ordinal).ThenBy(m => m.Key.Evidence, StringComparer.Ordinal)
            .ThenBy(m => m.Key.Provider, StringComparer.Ordinal).Select(m =>
                $"[{Text(m.Key.Model)},{Text(ExecutionModes.Name(m.Key.Kind))},{Text(m.Key.Mode)},{Text(m.Key.Evidence)}," +
                $"{(m.Key.Provider.Length > 0 ? Text(m.Key.Provider) : "null")},{m.Value.Requests},{m.Value.Output}," +
                $"{m.Value.MeasuredOutput},{m.Value.MeasuredMs}]"));
        return $"{{\"requests\":{d.Requests},\"input\":{d.Input},\"cachedInput\":{d.CachedInput},\"cacheWriteInput\":{d.CacheWriteInput}," +
            $"\"output\":{d.Output},\"reasoning\":{d.Reasoning},\"measuredOutput\":{d.MeasuredOutput},\"measuredMs\":{d.MeasuredMs}," +
            $"\"unfinalized\":{d.Unfinalized},\"agents\":{d.Agents},\"costMicroUsd\":{Optional(d.Cost?.CostMicroUsd)}," +
            $"\"unpricedRequests\":{Optional(d.Cost?.UnpricedRequests)},\"models\":{{{models}}},\"unpriced\":[{unpriced}],\"modes\":[{modes}]}}";
    }

    /// <summary>Every value of a row, for comparing rows whose lists are not equal by reference.</summary>
    public static string Describe(DailyUsage d) => string.Join(" | ",
        $"{d.Id} {d.Host} {d.Totals} mo={d.MeasuredOutput} ms={d.MeasuredMs} unf={d.Unfinalized} agents={d.Agents}",
        $"peak={d.Peak?.ToString("R", CultureInfo.InvariantCulture)} cost={d.Cost} pv={d.PriceVersion}",
        string.Join(";", d.Models.Select(m => $"{m} {m.Cost}")),
        string.Join(";", d.Modes),
        $"noCost={d.Unpriced.NoCostData} derived={d.Unpriced.Derived} {string.Join(";", d.Unpriced.Facts)}");

    private static string Bool(bool value) => value ? "true" : "false";
}
