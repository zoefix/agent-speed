using System.Text.Json;
using AgentSpeed.Core.Perf;

namespace AgentSpeed.Core.Remote;

internal sealed partial class RelayLineDecoder
{
    private static RemoteHistoryDay DecodeHistoryDay(RemoteFields f)
    {
        var aggregate = Aggregate(f.Object("aggregate"));
        var text = f.RawText("aggregate");
        return new RemoteHistoryDay(f.Day("day"), f.Source("source"), aggregate, text, f.Hex("digest", 16));
    }

    private static RemoteHistoryPerf DecodeHistoryPerf(RemoteFields f)
    {
        var record = PerfRecord(f.Object("record"));
        var text = f.RawText("record");
        return new RemoteHistoryPerf(f.Day("day"), f.Source("source"), f.String("model", RemoteLimits.ModelBytes, RemoteText.Model), record, text);
    }

    /// <summary>
    /// The day aggregate encoding of the Linux core (<c>history_store.cpp</c>). Mode rows of a kind this version does not know
    /// are skipped, as the Linux decoder does.
    /// </summary>
    public static RemoteDayAggregate Aggregate(RemoteFields f)
    {
        var models = new Dictionary<string, RemoteDayAggregate.Model>(StringComparer.Ordinal);
        foreach (var member in f.ObjectMembers("models").EnumerateObject())
        {
            if (System.Text.Encoding.UTF8.GetByteCount(member.Name) > RemoteLimits.ModelBytes || !RemoteFields.AllowsText(member.Name, RemoteText.Model))
            {
                throw new RemoteLineException(RemoteLineErrorKind.BadCharacter, "aggregate.models");
            }

            var row = new Row(member.Value, 6, "aggregate.models");
            models[member.Name] = new RemoteDayAggregate.Model(
                row.Count(0), row.Count(1), row.Count(2), row.Count(3), row.OptionalInteger(4), row.OptionalCount(5));
            if (models.Count > RemoteLimits.HistoryList)
            {
                throw new RemoteLineException(RemoteLineErrorKind.TooMany, "aggregate.models");
            }
        }

        var unpriced = new List<RemoteDayAggregate.Unpriced>();
        foreach (var item in f.Array("unpriced", RemoteLimits.HistoryList).EnumerateArray())
        {
            var row = new Row(item, 5, "aggregate.unpriced");
            unpriced.Add(new RemoteDayAggregate.Unpriced(
                row.String(0, RemoteLimits.ModelBytes, RemoteText.Model), row.OptionalString(1, RemoteLimits.ProviderBytes, RemoteText.Model),
                row.Bool(2), row.Bool(3), row.Count(4)));
        }

        var modes = new List<RemoteDayAggregate.Mode>();
        foreach (var item in f.Array("modes", RemoteLimits.HistoryList).EnumerateArray())
        {
            var row = new Row(item, 9, "aggregate.modes");
            var model = row.String(0, RemoteLimits.ModelBytes, RemoteText.Model);
            var kind = row.String(1, 32, RemoteText.Identifier);
            var mode = row.String(2, 64, RemoteText.Identifier);
            var evidence = row.String(3, 64, RemoteText.Identifier);
            var provider = row.OptionalString(4, 32, RemoteText.Identifier);
            var (requests, output, measuredOutput, measuredMs) = (row.Count(5), row.Count(6), row.Count(7), row.Count(8));
            if (ParseKind(kind) is not { } modeKind)
            {
                continue;
            }

            if (!ExecutionModes.IsIdentifier(mode, 64) || !ExecutionModes.IsIdentifier(evidence, 64)
                || (provider is not null && !ExecutionModes.IsIdentifier(provider, 32)))
            {
                throw new RemoteLineException(RemoteLineErrorKind.BadCharacter, "aggregate.modes");
            }

            modes.Add(new RemoteDayAggregate.Mode(model, modeKind, mode, evidence, provider, requests, output, measuredOutput, measuredMs));
        }

        return new RemoteDayAggregate(
            f.Total("requests"), f.Total("input"), f.Total("cachedInput"), f.Total("cacheWriteInput"), f.Total("output"),
            f.Total("reasoning"), f.Total("measuredOutput"), f.Total("measuredMs"), f.Total("unfinalized"), f.Total("agents"),
            f.OptionalInteger("costMicroUsd"), f.OptionalTotal("unpricedRequests"), models, unpriced, modes);
    }

    /// <summary>METRICS 6.11 <i>Records</i>.</summary>
    public static PerfSums PerfRecord(RemoteFields f)
    {
        var bins = f.Array("latencyBins", LatencyBins.Count);
        if (bins.GetArrayLength() != LatencyBins.Count)
        {
            throw new RemoteLineException(RemoteLineErrorKind.OutOfRange, "record.latencyBins");
        }

        var record = new PerfSums();
        var index = 0;
        foreach (var bin in bins.EnumerateArray())
        {
            var value = RemoteFields.Integer(bin, "record.latencyBins");
            record.LatencyBins[index++] = value >= 0 ? value : throw new RemoteLineException(RemoteLineErrorKind.OutOfRange, "record.latencyBins");
        }

        record.N = f.Total("n");
        record.OutputSum = f.Total("outputSum");
        record.DurationMsSum = f.Total("durationMsSum");
        record.ReasonN = f.Total("reasonN");
        record.ReasonOutput = f.Total("reasonOutput");
        record.ReasonDurationMsSum = f.Total("reasonDurationMsSum");
        record.CacheN = f.Total("cacheN");
        record.CacheInput = f.Total("cacheInput");
        record.CacheCached = f.Total("cacheCached");
        record.ToolN = f.Total("toolN");
        record.ToolLnSum = f.Number("toolLnSum");
        record.ToolFailed = f.Total("toolFailed");
        record.ActiveMs = f.Total("activeMs");
        return record;
    }

    /// <summary>The exact JSON text of a top-level member of <paramref name="line"/> (the last one when repeated).</summary>
    public static string? RawMember(string name, ReadOnlyMemory<byte> line)
    {
        try
        {
            using var document = JsonDocument.Parse(line, ParseOptions);
            return document.RootElement.ValueKind == JsonValueKind.Object && RemoteFields.Find(document.RootElement, name) is { } value
                ? value.GetRawText()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>A stored aggregate text (<c>remote_days.data</c>), with the relay line's validation; null when it does not decode.</summary>
    public static RemoteDayAggregate? StoredAggregate(string json) => Stored(json, Aggregate);

    /// <summary>A stored performance record text (<c>remote_perf.data</c>).</summary>
    public static PerfSums? StoredRecord(string json) => Stored(json, PerfRecord);

    private static T? Stored<T>(string json, Func<RemoteFields, T> decode)
        where T : class
    {
        try
        {
            using var document = JsonDocument.Parse(json, ParseOptions);
            return document.RootElement.ValueKind == JsonValueKind.Object ? decode(new RemoteFields(document.RootElement)) : null;
        }
        catch (Exception error) when (error is JsonException or RemoteLineException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>A fixed-length JSON array row of the aggregate encoding.</summary>
    private readonly struct Row
    {
        private readonly JsonElement[] _items;
        private readonly string _name;

        public Row(JsonElement value, int count, string name)
        {
            if (value.ValueKind != JsonValueKind.Array)
            {
                throw new RemoteLineException(RemoteLineErrorKind.WrongType, name);
            }

            _items = [.. value.EnumerateArray()];
            _name = name;
            if (_items.Length != count)
            {
                throw new RemoteLineException(RemoteLineErrorKind.OutOfRange, name);
            }
        }

        public long Count(int index)
        {
            var value = RemoteFields.Integer(_items[index], _name);
            return value >= 0 ? value : throw new RemoteLineException(RemoteLineErrorKind.OutOfRange, _name);
        }

        public long? OptionalCount(int index) => IsNull(index) ? null : Count(index);

        public long? OptionalInteger(int index) => IsNull(index) ? null : RemoteFields.Integer(_items[index], _name);

        public bool Bool(int index) => _items[index].ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw new RemoteLineException(RemoteLineErrorKind.WrongType, _name),
        };

        public string String(int index, int maxBytes, RemoteText rule) => RemoteFields.Checked(_items[index], _name, maxBytes, rule);

        public string? OptionalString(int index, int maxBytes, RemoteText rule) => IsNull(index) ? null : String(index, maxBytes, rule);

        private bool IsNull(int index) => _items[index].ValueKind == JsonValueKind.Null;
    }
}
