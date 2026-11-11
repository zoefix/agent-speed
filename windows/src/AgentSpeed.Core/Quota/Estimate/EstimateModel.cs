using System.Collections.Concurrent;

namespace AgentSpeed.Core.Quota.Estimate;

/// <summary>
/// The estimate's inputs resolved (QUOTAS 10.1–10.4): accounts of every response, the spend ledger and the observations.
/// Built in one go from an <see cref="EstimateInput"/>; immutable afterwards.
/// </summary>
public sealed partial class EstimateModel
{
    private readonly byte[] _salt;
    private readonly string _install;
    private readonly ConcurrentDictionary<(string Machine, SourceKind Source), string> _pairs = new();
    private readonly EstimateStoreData _stored;
    private readonly EstimateInput _input;
    private readonly Dictionary<SourceKind, List<KeyEntry>> _timelines = [];
    private readonly Dictionary<KeyEntry, string> _orgKeys = [];
    private readonly List<QuotaObservation> _observations = [];
    private List<KeyEntry> _desktopOrgs = [];
    private string?[] _accounts = [];

    private EstimateModel(EstimateInput input, ReadOnlySpan<byte> salt, string install, int priceVersion)
    {
        _input = input;
        _salt = salt.ToArray();
        _install = install;
        _stored = input.Stored is { Version: EstimateLimits.StoreVersion } stored ? stored : EstimateStoreData.Empty;
        PriceVersion = priceVersion;
    }

    public int PriceVersion { get; }

    public SpendLedger Ledger { get; } = new();

    public IReadOnlyList<EstimateResponse> Responses => _input.Responses;

    /// <summary>The account each response spends on, aligned with <see cref="Responses"/>; null when left out.</summary>
    public IReadOnlyList<string?> ResponseAccounts => _accounts;

    public IReadOnlyList<QuotaObservation> Observations => _observations;

    /// <summary>The key timeline per source; Claude Desktop's holds the keys it spends on, derived on this build.</summary>
    public IReadOnlyList<KeyEntry> Timeline(SourceKind source) => _timelines.TryGetValue(source, out var t) ? t : [];

    internal IReadOnlyList<StoredInstance> StoredInstances => _stored.Instances;

    /// <summary>The sources with a key timeline, Claude Desktop's holding its own organization keys (as stored).</summary>
    internal IEnumerable<(SourceKind Source, IReadOnlyList<KeyEntry> Entries)> StoredTimelines =>
        _timelines.Select(t => (t.Key, t.Key == SourceKind.ClaudeDesktop ? (IReadOnlyList<KeyEntry>)_desktopOrgs : t.Value));

    internal string? OrgKeyOf(KeyEntry entry) => _orgKeys.GetValueOrDefault(entry);

    /// <summary>Claude Desktop's own organization keys as they changed.</summary>
    public IReadOnlyList<KeyEntry> DesktopOrganizations => _desktopOrgs;

    /// <param name="salt">The account salt (<see cref="AccountKeys.Salt"/>).</param>
    /// <param name="install">The install id (QUOTAS 10.1) that keys the accounts without an id.</param>
    public static EstimateModel Build(EstimateInput input, ReadOnlySpan<byte> salt, string install, int priceVersion)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(install);
        var model = new EstimateModel(input, salt, install, priceVersion);
        model.BuildTimelines();
        var lines = model.ResolveCodex(out var lineOf);
        model.BuildHosts(lines);
        model.FillLedger(lines, lineOf);
        model.CollectObservations(lines);
        return model;
    }

    /// <summary>The account a source of this computer spends on at <paramref name="atMs"/>, null when unknown.</summary>
    public string? AccountOf(SourceKind source, long atMs) => source switch
    {
        SourceKind.Codex => CodexAccount,
        SourceKind.ClaudeCode or SourceKind.ClaudeDesktop => KeyTimeline.InForce(Timeline(source), atMs)?.Key,
        _ => LocalPair(source),
    };

    /// <summary>The account of the newest Codex token count line.</summary>
    public string? CodexAccount { get; private set; }

    /// <summary>The account of this computer's source without an id: the pair (<c>local</c>, source) of this install.</summary>
    internal string LocalPair(SourceKind source) => Pair(AccountKeys.LocalMachine, source);

    /// <summary>The account of a host's source that names none: the pair (host, source) of this install.</summary>
    internal string HostPair(string host, SourceKind source) => Pair(host, source);

    private string Pair(string machine, SourceKind source) =>
        _pairs.GetOrAdd((machine, source), static (k, m) => AccountKeys.PairKey(m._salt, m._install, k.Machine, EstimateNames.Source(k.Source)), this);

    private void FillLedger(IReadOnlyList<CodexLineState> lines, int[] lineOf)
    {
        var responses = _input.Responses;
        _accounts = new string?[responses.Count];
        for (var i = 0; i < responses.Count; i++)
        {
            var r = responses[i];
            var account = r.Host is { } host ? HostResponseAccount(host, r) : LocalResponseAccount(r, lines, lineOf[i]);
            _accounts[i] = account;
            if (account is not null)
            {
                Ledger.Add(account, r.Source, r.Model, r.EndMs, r.CostMicroUsd, r.Requests, r.Tokens);
            }
        }
    }

    private string? LocalResponseAccount(EstimateResponse r, IReadOnlyList<CodexLineState> lines, int line)
    {
        switch (r.Source)
        {
            case SourceKind.Codex:
                return IsOpenAi(r.Provider) && line >= 0 ? lines[line].Account : null;
            case SourceKind.ClaudeCode or SourceKind.ClaudeDesktop:
                return r.CloudRoute ? null : KeyTimeline.InForce(Timeline(r.Source), r.EndMs)?.Key;
            default:
                return LocalPair(r.Source);
        }
    }

    private static bool IsOpenAi(string? provider) => provider is null || string.Equals(provider, "openai", StringComparison.Ordinal);

    private void CollectObservations(IReadOnlyList<CodexLineState> lines)
    {
        foreach (var line in lines)
        {
            foreach (var w in line.Windows)
            {
                _observations.Add(new QuotaObservation(line.Account, SourceKind.Codex, w.Kind, w.Name, w.Minutes!.Value, w.UsedPercent,
                    line.Raw.Ts, line.TLo, w.ResetsAtMs!.Value, line.Plan, null));
            }
        }

        foreach (var s in _input.Snapshots)
        {
            if (s.Source == SourceKind.Codex)
            {
                continue;
            }

            string account;
            string? plan = null, tier = null;
            if (s.Source is SourceKind.ClaudeCode or SourceKind.ClaudeDesktop)
            {
                if (KeyTimeline.InForce(Timeline(s.Source), s.ObservedMs) is not { } e)
                {
                    continue;
                }

                (account, plan, tier) = (e.Key, e.Plan, e.Tier);
            }
            else
            {
                account = LocalPair(s.Source);
            }

            var tLo = s.Source == SourceKind.ClaudeCode ? EarliestLiveStart(account, s.ObservedMs) : s.ObservedMs;
            AddWindows(account, s.Source, s.ObservedMs, tLo, s.Windows, plan, tier);
        }

        CollectHostObservations();
        var seen = new HashSet<(string, QuotaWindowKind, string?, int, long, double, long)>(_observations.Select(Identity));
        var desktop = Timeline(SourceKind.ClaudeDesktop);
        foreach (var stored in _stored.Observations)
        {
            var o = stored;
            if (o.Source == SourceKind.ClaudeDesktop && desktop.Count > 0 && KeyTimeline.InForce(desktop, o.T) is { } e)
            {
                o = o with { Account = e.Key, Plan = e.Plan, Tier = e.Tier };
            }

            if (seen.Add(Identity(o)))
            {
                _observations.Add(o);
            }
        }
    }

    private static (string, QuotaWindowKind, string?, int, long, double, long) Identity(QuotaObservation o) =>
        (o.Account, o.Kind, o.Name, o.Minutes, o.T, o.P, o.R);

    private void AddWindows(string account, SourceKind source, long t, long tLo, IReadOnlyList<QuotaWindow> windows, string? plan, string? tier)
    {
        foreach (var w in windows)
        {
            if (w.ResetsAtMs is not { } r || w.Minutes is not { } minutes)
            {
                continue;
            }

            _observations.Add(new QuotaObservation(account, source, w.Kind, w.Name, minutes, w.UsedPercent, t, tLo, r, plan, tier));
        }
    }

    /// <summary>
    /// The Claude CLI's <c>tLo</c>: the start of the earliest Claude CLI response of the account that started before
    /// <paramref name="t"/> and ended at or after <c>t − λ</c>, else <paramref name="t"/>.
    /// </summary>
    private long EarliestLiveStart(string account, long t)
    {
        if (!AccountIndex().TryGetValue(account, out var index))
        {
            return t;
        }

        var i = EstimateMath.BisectLeft(index.CliEnds, t - EstimateLimits.LagMs);
        return i < index.CliEnds.Length ? Math.Min(t, index.CliMinStart[i]) : t;
    }
}
