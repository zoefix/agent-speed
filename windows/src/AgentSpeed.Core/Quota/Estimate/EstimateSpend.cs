using AgentSpeed.Core.Parsing;
using AgentSpeed.Core.Pricing;
using AgentSpeed.Core.Reporting;

namespace AgentSpeed.Core.Quota.Estimate;

/// <summary>
/// Responses as the spend ledger takes them (QUOTAS 10.3): only sources with a quota; the price table's model id (the
/// model as logged, lowercased, where the table does not know it); the cost it was priced at; the input + output tokens;
/// Codex's provider; whether a Claude model id is in a Bedrock or Vertex form. Ids are remembered while one table is
/// current. Thread-safe.
/// </summary>
internal sealed class EstimateSpend(Func<PriceTable> table)
{
    private readonly Lock _gate = new();
    private readonly Dictionary<(string Provider, string Model), (string Id, bool Cloud)> _ids = [];
    private int _version = int.MinValue;

    /// <summary>The sources with a quota (QUOTAS 9): only their spend enters the ledger.</summary>
    public static bool Counts(SourceKind source) => source is SourceKind.Codex or SourceKind.ClaudeCode or SourceKind.ClaudeDesktop
        or SourceKind.GrokBuild or SourceKind.Devin or SourceKind.Antigravity or SourceKind.Kimi or SourceKind.Gemini or SourceKind.Droid
        or SourceKind.ZCode or SourceKind.Qoder or SourceKind.QoderCli or SourceKind.WorkBuddy;

    /// <summary>
    /// The response as the ledger counts it, priced at <paramref name="cost"/>, with <paramref name="source"/> in place of its own
    /// (a file's final source); null for a source without a quota.
    /// </summary>
    public EstimateResponse? Of(ParsedResponse response, long? cost, SourceKind? source = null)
    {
        ArgumentNullException.ThrowIfNull(response);
        var kind = source ?? response.Source;
        if (!Counts(kind))
        {
            return null;
        }

        var (id, cloud) = Ids(response.Model, response.Provider);
        var local = string.Equals(response.Host, Remote.RemoteHost.LocalId, StringComparison.Ordinal);
        return new EstimateResponse(kind, local ? response.AgentId : null, response.StartMs is { } start and not 0 ? start : null, response.EndMs, id,
            cost, response.Tokens.Total, kind == SourceKind.Codex ? response.Provider : null,
            cloud && kind is SourceKind.ClaudeCode or SourceKind.ClaudeDesktop, local ? null : response.Host, response.Requests);
    }

    /// <summary>
    /// A file's responses summed per source, model, UTC minute and pricing (the history's share stays small); Codex
    /// responses stay whole, since each one takes the token count line that reports it (QUOTAS 10.1).
    /// </summary>
    public static List<EstimateResponse> PerMinute(IEnumerable<EstimateResponse> responses)
    {
        ArgumentNullException.ThrowIfNull(responses);
        var order = new List<(SourceKind, string, long, bool, bool)>();
        var sums = new Dictionary<(SourceKind Source, string Model, long Minute, bool Priced, bool Cloud), EstimateResponse>();
        var whole = new List<EstimateResponse>();
        foreach (var r in responses)
        {
            if (r.Source == SourceKind.Codex)
            {
                whole.Add(r);
                continue;
            }

            var key = (r.Source, r.Model, SpendLedger.MinuteOf(r.EndMs), r.CostMicroUsd is not null, r.CloudRoute);
            if (!sums.TryGetValue(key, out var sum))
            {
                order.Add(key);
                sums.Add(key, r with { File = null, Provider = null });
                continue;
            }

            sums[key] = sum with
            {
                CostMicroUsd = sum.CostMicroUsd + (r.CostMicroUsd ?? 0),
                Tokens = sum.Tokens + r.Tokens,
                Requests = sum.Requests + r.Requests,
                EndMs = Math.Max(sum.EndMs, r.EndMs),
                StartMs = sum.StartMs is { } a && r.StartMs is { } b ? Math.Min(a, b) : sum.StartMs ?? r.StartMs,
            };
        }

        whole.Sort((a, b) => a.EndMs.CompareTo(b.EndMs));
        return [.. whole, .. order.Select(k => sums[k])];
    }

    private (string Id, bool Cloud) Ids(string? model, string? provider)
    {
        if (string.IsNullOrEmpty(model))
        {
            return (ReportModelIds.Unknown, false);
        }

        var current = table();
        lock (_gate)
        {
            if (_version != current.Version)
            {
                _version = current.Version;
                _ids.Clear();
            }

            var key = (provider ?? string.Empty, model);
            if (!_ids.TryGetValue(key, out var ids))
            {
                var id = current.ReportModelId(model, provider);
                if (id is ReportModelIds.Unknown or ReportModelIds.Local && model.ToLowerInvariant() is var raw && ReportModelIds.IsValid(raw))
                {
                    id = raw;
                }

                ids = (id, current.Resolver.IsCloudForm(model));
                if (_ids.Count >= 4096)
                {
                    _ids.Clear();
                }

                _ids[key] = ids;
            }

            return ids;
        }
    }
}
