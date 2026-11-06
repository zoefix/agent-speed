using System.Security.Cryptography;
using System.Text;
using AgentSpeed.Core.History;
using AgentSpeed.Core.Perf;
using AgentSpeed.Core.Unpriced;

namespace AgentSpeed.Core.Remote;

/// <summary>
/// The Linux core's day aggregate encoding (REMOTE 5.4, <c>history_store.cpp</c> <c>encode</c>), field for field. Costs are
/// null when the relay wrote <c>null</c> (contributions stored before costs existed).
/// </summary>
/// <param name="Models">By raw model (<c>""</c> = unknown).</param>
internal sealed record RemoteDayAggregate(
    long Requests,
    long Input,
    long CachedInput,
    long CacheWriteInput,
    long Output,
    long Reasoning,
    long MeasuredOutput,
    long MeasuredMs,
    long Unfinalized,
    long Agents,
    long? CostMicroUsd,
    long? UnpricedRequests,
    IReadOnlyDictionary<string, RemoteDayAggregate.Model> Models,
    IReadOnlyList<RemoteDayAggregate.Unpriced> UnpricedFacts,
    IReadOnlyList<RemoteDayAggregate.Mode> Modes)
{
    public sealed record Model(long Requests, long Output, long MeasuredOutput, long MeasuredMs, long? CostMicroUsd, long? UnpricedRequests);

    /// <summary><c>[model, provider, fast, noDate, count]</c> (METRICS 9.10).</summary>
    public sealed record Unpriced(string Model, string? Provider, bool Fast, bool NoDate, long Count);

    /// <summary><c>[model, kind, mode, evidence, provider, requests, output, measuredOutput, measuredMs]</c>.</summary>
    public sealed record Mode(
        string Model, ExecutionModeKind Kind, string Value, string Evidence, string? Provider, long Requests, long Output, long MeasuredOutput,
        long MeasuredMs);

    public bool Equals(RemoteDayAggregate? other) => other is not null && Requests == other.Requests && Input == other.Input
        && CachedInput == other.CachedInput && CacheWriteInput == other.CacheWriteInput && Output == other.Output
        && Reasoning == other.Reasoning && MeasuredOutput == other.MeasuredOutput && MeasuredMs == other.MeasuredMs
        && Unfinalized == other.Unfinalized && Agents == other.Agents && CostMicroUsd == other.CostMicroUsd
        && UnpricedRequests == other.UnpricedRequests && Models.Count == other.Models.Count
        && Models.All(m => other.Models.TryGetValue(m.Key, out var value) && value == m.Value)
        && UnpricedFacts.SequenceEqual(other.UnpricedFacts) && Modes.SequenceEqual(other.Modes);

    public override int GetHashCode() => HashCode.Combine(Requests, Output, Models.Count, Modes.Count);
}

/// <summary>A <c>historyDay</c> line. <paramref name="AggregateJson"/> is the aggregate's exact text on the line: the app stores it and the digest covers it.</summary>
/// <param name="Digest">16 lowercase hex digits.</param>
internal sealed record RemoteHistoryDay(string Day, SourceKind Source, RemoteDayAggregate Aggregate, string AggregateJson, string Digest)
{
    /// <summary>The key of the <c>history</c> request's <c>digests</c> (REMOTE 5.3).</summary>
    public string DigestKey => RemoteHistoryDigest.Key(Day, Source);
}

/// <summary>A <c>historyPerf</c> line (METRICS 6.11 <i>Records</i>); <paramref name="RecordJson"/> is the record's exact text.</summary>
/// <param name="Model">The raw model; <c>""</c> when unknown.</param>
internal sealed record RemoteHistoryPerf(string Day, SourceKind Source, string Model, PerfSums Record, string RecordJson)
{
    public PerfRecordKey Key => new(Day, Source, Model);

    public bool Equals(RemoteHistoryPerf? other) => other is not null && Day == other.Day && Source == other.Source
        && Model == other.Model && RecordJson == other.RecordJson;

    public override int GetHashCode() => HashCode.Combine(Day, Source, Model, RecordJson);
}

/// <summary>
/// The digest of REMOTE 5.4: the first 16 hex digits of SHA-256 over the aggregate's JSON text followed by the JSON texts of
/// that day and source's performance records in model order (UTF-8 bytes).
/// </summary>
public static class RemoteHistoryDigest
{
    public static string Key(string day, SourceKind source) => day + "/" + SourceNames.Of(source);

    public static string Digest(string aggregateJson, IEnumerable<(string Model, string Json)> perf)
    {
        ArgumentNullException.ThrowIfNull(aggregateJson);
        ArgumentNullException.ThrowIfNull(perf);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.UTF8.GetBytes(aggregateJson));
        foreach (var record in perf.OrderBy(r => r.Model, Utf8Order.Instance))
        {
            hash.AppendData(Encoding.UTF8.GetBytes(record.Json));
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset().AsSpan(0, 8));
    }
}
