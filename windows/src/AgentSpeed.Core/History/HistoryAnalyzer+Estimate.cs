using AgentSpeed.Core.Monitoring;
using AgentSpeed.Core.Parsing;
using AgentSpeed.Core.Quota.Estimate;

namespace AgentSpeed.Core.History;

/// <summary>
/// The quota estimate's share of the history (QUOTAS 10.3): each analysed file of a source with a quota keeps its spend
/// within 8 days per minute and its Codex token count lines beside its days; a recent file analysed before (or with another
/// share version) is analysed again once. After a complete run the shares go to the estimate with the minute before which
/// they hold the spend.
/// </summary>
internal sealed partial class HistoryAnalyzer
{
    private Dictionary<string, (int Version, EstimateFileShare Share)> _estimates = [];
    private EstimateSpend? _spend;
    private long? _lastCompleteStartMs;

    /// <summary>The quota estimate's share of the history after the last complete run; null before one.</summary>
    public EstimateHistory? EstimateHistory => _lastCompleteStartMs is { } start
        ? new EstimateHistory(_estimates.Where(e => !e.Value.Share.IsEmpty).ToDictionary(e => e.Key, e => e.Value.Share, PathComparer),
            EstimateHistory.Cut(start))
        : null;

    private EstimateSpend Spend => _spend ??= new EstimateSpend(() => Pricing.Current);

    private void LoadEstimates() => _estimates = _store.EstimateShares(PathComparer);

    /// <summary>Families whose source has a quota: only their files keep a share.</summary>
    private static bool KeepsEstimate(RootFamily family) => family is RootFamily.Codex or RootFamily.Claude or RootFamily.Gemini
        or RootFamily.Antigravity or RootFamily.WorkBuddy or RootFamily.ZCode or RootFamily.Kimi or RootFamily.GrokBuild or RootFamily.Droid
        or RootFamily.Devin or RootFamily.Qoder or RootFamily.QoderCli;

    /// <summary>A file of such a family modified within 8 days (unknown: always) whose share is missing or of another version.</summary>
    private bool EstimateMissing(RootFamily family, string path, long? modifiedMs, long nowMs) =>
        KeepsEstimate(family)
        && (!_estimates.TryGetValue(path, out var stored) || stored.Version != EstimateFileShare.Version)
        && (modifiedMs is not { } modified || modified >= nowMs - EstimateLimits.KeepMs);

    /// <summary>A Codex parser collects its token count lines while its batch is applied.</summary>
    private static void CollectEstimateLines(TranscriptParser parser)
    {
        if (parser is CodexParser codex)
        {
            codex.EstimateLines = [];
        }
    }

    /// <summary>The file's share from its final responses (priced as the days are) and its lines; recorded with the entry.</summary>
    private void NoteEstimate(StoredFile entry, RootFamily family, IEnumerable<ParsedResponse> responses, SourceKind source, DayLabeler labeler,
        long nowMs, TranscriptParser? parser = null)
    {
        if (!KeepsEstimate(family))
        {
            return;
        }

        var keep = nowMs - EstimateLimits.KeepMs;
        var spend = new List<EstimateResponse>();
        foreach (var response in responses)
        {
            if (response.EndMs < keep)
            {
                continue;
            }

            var cost = labeler.Label(response.EndMs) is { } day ? Pricing.Cost(response, day) : null;
            if (Spend.Of(response, cost, source) is { } item)
            {
                spend.Add(item);
            }
        }

        IReadOnlyList<CodexTokenLine> lines = [];
        if (parser is CodexParser { EstimateLines: { } collected } codex)
        {
            lines = collected;
            codex.EstimateLines = null;
        }

        var share = EstimateFileShare.Of(entry.Path, spend, lines, nowMs);
        entry.Estimate = share;
        _estimates[entry.Path] = (EstimateFileShare.Version, share);
    }

    private static long ToMs(DateTime utc) => new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeMilliseconds();
}
