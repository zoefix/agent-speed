using AgentSpeed.Core.Parsing;
using AgentSpeed.Core.Remote;

namespace AgentSpeed.Core.Quota.Estimate;

/// <summary>
/// The estimate's inputs (QUOTAS 10.1–10.4) as they arrive, each under the input gate: the history's share with its cut, the
/// live engine's responses by key (folded per minute an hour after they end, except this computer's Codex ones, which take
/// a token count line), Codex's lines, the sign-in keys (only changes), the snapshots of the sources whose quota switch is on
/// (runs of equal values keep their first and last) and the hosts' quota lines. Everything older than 8 days goes.
/// </summary>
public sealed partial class QuotaEstimator
{
    private const long FoldAfterMs = 3_600_000;

    private readonly EstimateSignIn? _signIn;
    private EstimateSpend? _spend;
    private EstimateStoreData _stored = EstimateStoreData.Empty;
    private bool _loaded;
    private EstimateHistory? _history;
    private readonly Dictionary<string, EstimateResponse> _live = new(StringComparer.Ordinal);
    private List<EstimateResponse> _folded = [];
    private readonly Dictionary<(string File, long Ts), CodexTokenLine> _lines = [];
    private readonly List<(long At, string Key)> _codexKeys = [];
    private readonly List<ClaudeProfile> _profiles = [];
    private readonly Dictionary<SourceKind, List<EstimateSnapshot>> _snapshots = [];
    private readonly Dictionary<(string Host, SourceKind Source), List<HostSnapshot>> _hostSnapshots = [];
    private HashSet<SourceKind> _enabled = [];

    private EstimateSpend Spend => _spend ??= new EstimateSpend(() => _pricing?.Current ?? Pricing.PriceBook.Bundled);

    /// <summary>The sources whose quota switch is on here: only their new values are observed (stored ones age out).</summary>
    public void SetEnabled(IEnumerable<SourceKind> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        var next = sources.ToHashSet();
        lock (_gate)
        {
            if (!next.SetEquals(_enabled))
            {
                _enabled = next;
                _dirty = true;
            }
        }
    }

    /// <summary>The history's share after a complete analysis: this computer's live responses before its cut go.</summary>
    public void HistoryUpdated(EstimateHistory history)
    {
        ArgumentNullException.ThrowIfNull(history);
        lock (_gate)
        {
            _history = history;
            foreach (var key in _live.Where(p => p.Value.Host is null && p.Value.EndMs < history.CutMs).Select(p => p.Key).ToList())
            {
                _live.Remove(key);
            }

            _folded = [.. _folded.Where(r => r.Host is not null || r.EndMs >= history.CutMs)];
            _dirty = true;
        }
    }

    /// <summary>A response the live engine holds (priced as it shows), local or a host's; an update replaces its earlier one.</summary>
    internal void Spent(ParsedResponse response)
    {
        if (!EstimateSpend.Counts(response.Source) || response.EndMs < _clock() - EstimateLimits.KeepMs || Spend.Of(response, response.CostMicroUsd) is not { } item)
        {
            return;
        }

        var key = response.Host + "\u001f" + response.Key;
        lock (_gate)
        {
            if ((item.Host is null && _history is { } history && item.EndMs < history.CutMs) || (_live.TryGetValue(key, out var held) && held == item))
            {
                return;
            }

            _live[key] = item;
            _dirty = true;
        }
    }

    /// <summary>A token count line with a window read from this computer's rollouts (QUOTAS 10.4).</summary>
    public void CodexLine(CodexTokenLine line)
    {
        ArgumentNullException.ThrowIfNull(line);
        if (line.Windows.Count == 0 || line.Ts < _clock() - EstimateLimits.KeepMs)
        {
            return;
        }

        lock (_gate)
        {
            if (_lines.TryAdd((line.File, line.Ts), line))
            {
                _dirty = true;
            }
        }
    }

    /// <summary>
    /// Reads the sign-in files again where they changed (Codex's while its quota shows, Claude Code's while Claude Code's or
    /// Desktop's does) and keeps each new key; called from the quota readers' worker.
    /// </summary>
    public void RefreshSignIns(long nowMs, bool codex, bool claude)
    {
        if (_signIn is not { } signIn)
        {
            return;
        }

        var codexChanged = codex && signIn.RefreshCodex(nowMs);
        var claudeChanged = claude && signIn.RefreshClaude(nowMs);
        if (!codexChanged && !claudeChanged)
        {
            return;
        }

        lock (_gate)
        {
            EnsureLoaded();
            if (codexChanged && signIn.CodexKey is { } key && key != (_codexKeys.Count > 0 ? _codexKeys[^1].Key : LatestStored(SourceKind.Codex)?.Key))
            {
                _codexKeys.Add((nowMs, key));
                _dirty = true;
            }

            if (claudeChanged && signIn.ClaudeProfile is { } profile && !SameAsLast(profile))
            {
                _profiles.Add(profile);
                _dirty = true;
            }
        }
    }

    /// <summary>A snapshot of this computer's Claude CLI, Claude Desktop (with its organization) or phase 2 source.</summary>
    public void Snapshot(QuotaSnapshot snapshot, string? desktopOrganization = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.Source == SourceKind.Codex || !snapshot.HasWindows)
        {
            return;
        }

        var org = snapshot.Source == SourceKind.ClaudeDesktop && desktopOrganization is { Length: > 0 } uuid
            ? AccountKeys.Key(AccountKeys.Salt, AccountKeys.DesktopVendor, uuid)
            : null;
        var item = new EstimateSnapshot(snapshot.Source, snapshot.ObservedMs, snapshot.Windows, org);
        lock (_gate)
        {
            if (!_snapshots.TryGetValue(snapshot.Source, out var list))
            {
                list = [];
                _snapshots.Add(snapshot.Source, list);
            }

            _dirty |= AddThinned(list, item, static (a, b) => a.ObservedMs == b.ObservedMs,
                static (a, b) => a.DesktopOrgKey == b.DesktopOrgKey && a.Windows.SequenceEqual(b.Windows));
        }
    }

    /// <summary>A host's quota line (REMOTE 11.1) with the account key, plan and tier it sent.</summary>
    public void HostQuota(RemoteQuotaReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var snapshot = report.Snapshot;
        if (!snapshot.HasWindows)
        {
            return;
        }

        var item = new HostSnapshot(report.HostId, snapshot.Source, snapshot.ObservedMs, snapshot.Windows, report.Account, report.Plan, report.Tier);
        lock (_gate)
        {
            var key = (report.HostId, snapshot.Source);
            if (!_hostSnapshots.TryGetValue(key, out var list))
            {
                list = [];
                _hostSnapshots.Add(key, list);
            }

            _dirty |= AddThinned(list, item, static (a, b) => a.ObservedMs == b.ObservedMs,
                static (a, b) => a.Account == b.Account && a.Plan == b.Plan && a.Tier == b.Tier && a.Windows.SequenceEqual(b.Windows));
        }
    }

    /// <summary>A removed host's lines go with it.</summary>
    public void ForgetHost(string hostId)
    {
        lock (_gate)
        {
            foreach (var key in _hostSnapshots.Keys.Where(k => k.Host == hostId).ToList())
            {
                _hostSnapshots.Remove(key);
                _dirty = true;
            }
        }
    }

    /// <summary>Appends a new value; within a run of equal values only its first and last stay. False when it was already there.</summary>
    private static bool AddThinned<T>(List<T> list, T item, Func<T, T, bool> sameTime, Func<T, T, bool> sameValues)
    {
        if (list.Count > 0 && sameTime(list[^1], item))
        {
            return false;
        }

        if (list.Count >= 2 && sameValues(list[^1], item) && sameValues(list[^2], item))
        {
            list[^1] = item;
        }
        else
        {
            list.Add(item);
        }

        return true;
    }

    private bool SameAsLast(ClaudeProfile profile)
    {
        if (_profiles.Count > 0)
        {
            var last = _profiles[^1];
            return last.Key == profile.Key && last.OrgKey == profile.OrgKey && last.Plan == profile.Plan && last.Tier == profile.Tier;
        }

        return LatestStored(SourceKind.ClaudeCode) is { } stored && stored.Key == profile.Key && stored.OrgKey == profile.OrgKey
            && stored.Plan == profile.Plan && stored.Tier == profile.Tier;
    }

    private StoredKey? LatestStored(SourceKind source) => _stored.Keys.Where(k => k.Source == source).MaxBy(k => k.At);

    /// <summary>Reads <c>estimate.json</c> once, under the input gate; another version or a file that does not parse reads as empty.</summary>
    private void EnsureLoaded()
    {
        if (_loaded)
        {
            return;
        }

        _loaded = true;
        if (_storePath is not null && EstimateStore.Read(_storePath) is { Version: EstimateLimits.StoreVersion } stored)
        {
            _stored = stored;
        }
    }

    /// <summary>Drops what is older than 8 days and folds live responses an hour after they ended; under the input gate.</summary>
    private void Prune(long now)
    {
        var keep = now - EstimateLimits.KeepMs;
        var fold = new List<EstimateResponse>();
        foreach (var (key, r) in _live.ToList())
        {
            if (r.EndMs < keep)
            {
                _live.Remove(key);
            }
            else if (r.EndMs < now - FoldAfterMs && !(r.Host is null && r.Source == SourceKind.Codex))
            {
                _live.Remove(key);
                fold.Add(r);
            }
        }

        if (fold.Count > 0 || _folded.Any(r => r.EndMs < keep))
        {
            _folded = Fold([.. _folded.Where(r => r.EndMs >= keep), .. fold]);
        }

        foreach (var key in _lines.Where(p => p.Key.Ts < keep).Select(p => p.Key).ToList())
        {
            _lines.Remove(key);
        }

        foreach (var list in _snapshots.Values)
        {
            list.RemoveAll(s => s.ObservedMs < keep);
        }

        foreach (var list in _hostSnapshots.Values)
        {
            list.RemoveAll(s => s.ObservedMs < keep);
        }
    }

    /// <summary>Sums responses that share host, source, model, minute, pricing, route and provider.</summary>
    private static List<EstimateResponse> Fold(IEnumerable<EstimateResponse> responses)
    {
        var order = new List<(string?, SourceKind, string, long, bool, bool, string?)>();
        var sums = new Dictionary<(string?, SourceKind, string, long, bool, bool, string?), EstimateResponse>();
        foreach (var r in responses)
        {
            var key = (r.Host, r.Source, r.Model, SpendLedger.MinuteOf(r.EndMs), r.CostMicroUsd is not null, r.CloudRoute, r.Provider);
            if (!sums.TryGetValue(key, out var sum))
            {
                order.Add(key);
                sums.Add(key, r with { File = null });
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

        return [.. order.Select(k => sums[k])];
    }

    /// <summary>Everything gathered: the history's spend before its cut, the live engine's after it, every host's; under the input gate.</summary>
    private EstimateInput Inputs(long now)
    {
        var keep = now - EstimateLimits.KeepMs;
        var input = new EstimateInput { Stored = _stored };
        var cut = _history?.CutMs ?? long.MinValue;
        var lines = new Dictionary<(string, long), CodexTokenLine>();
        if (_history is { } history)
        {
            foreach (var file in history.Files.Keys.Order(StringComparer.Ordinal))
            {
                var share = history.Files[file];
                input.Responses.AddRange(share.Responses.Where(r => r.EndMs < cut && r.EndMs >= keep));
                foreach (var line in share.Lines)
                {
                    lines.TryAdd((line.File, line.Ts), line);
                }
            }
        }

        input.Responses.AddRange(_folded.Where(r => r.Host is not null || r.EndMs >= cut));
        input.Responses.AddRange(_live.Values.Where(r => r.Host is not null || r.EndMs >= cut).OrderBy(r => r.EndMs));
        foreach (var (key, line) in _lines)
        {
            lines.TryAdd(key, line);
        }

        if (_enabled.Contains(SourceKind.Codex))
        {
            input.CodexLines.AddRange(lines.Values.Where(l => l.Ts >= keep).OrderBy(l => l.File, StringComparer.Ordinal).ThenBy(l => l.Ts));
        }

        input.CodexKeys.AddRange(_codexKeys);
        input.ClaudeProfiles.AddRange(_profiles);
        foreach (var (source, list) in _snapshots.OrderBy(p => p.Key))
        {
            if (_enabled.Contains(source))
            {
                input.Snapshots.AddRange(list);
            }
        }

        foreach (var (_, list) in _hostSnapshots.OrderBy(p => p.Key.Host, StringComparer.Ordinal).ThenBy(p => p.Key.Source))
        {
            input.HostSnapshots.AddRange(list);
        }

        return input;
    }
}
