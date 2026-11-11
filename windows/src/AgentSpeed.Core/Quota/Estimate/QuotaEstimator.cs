using AgentSpeed.Core.Pricing;
using AgentSpeed.Core.Remote;

namespace AgentSpeed.Core.Quota.Estimate;

/// <summary>
/// Keeps the quota estimate (QUOTAS 10) up to date: it gathers the history's and the live engine's spend, Codex's token
/// count lines, the sign-in keys and every snapshot of this computer and its hosts (<c>QuotaEstimator.Inputs.cs</c>), builds
/// the estimate when asked at most every 5 s while something changed and every minute anyway (weights decay, resets pass),
/// and keeps <c>&lt;data&gt;/quota/estimate.json</c> (at most once a minute, and at quit). The surfaces read each window's
/// estimate and the reports their entries from the latest build. Ids become keys as they arrive; neither ids, keys nor the
/// salt are ever logged. Thread-safe.
/// </summary>
public sealed partial class QuotaEstimator
{
    public const long BuildIntervalMs = 5_000;
    public const long HeartbeatMs = 60_000;
    public const long WriteIntervalMs = 60_000;

    private readonly Lock _gate = new();
    private readonly Lock _buildGate = new();
    private readonly Lock _latestGate = new();
    private readonly byte[] _salt;
    private readonly string? _storePath;
    private readonly PriceBook? _pricing;
    private readonly Func<long> _clock;
    private EstimateEvaluation? _latest;
    private long _lastBuildMs = long.MinValue;
    private long _lastWriteMs = long.MinValue;
    private byte[]? _written;
    private bool _dirty = true;

    /// <param name="dataDirectory">The app's data folder (<c>quota/estimate-salt</c>, <c>quota/estimate.json</c>); null, or a salt
    /// that cannot be written, keeps everything in memory.</param>
    /// <param name="signIn">Where the sign-in files are (<see cref="QuotaSignIn.Files"/>); null reads none.</param>
    public QuotaEstimator(string? dataDirectory, PriceBook? pricing, (string CodexAuth, string ClaudeState)? signIn = null, Func<long>? clock = null)
    {
        _pricing = pricing;
        _clock = clock ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        byte[]? salt = null;
        if (dataDirectory is not null)
        {
            try
            {
                salt = EstimateSalt.LoadOrCreate(Path.Combine(dataDirectory, "quota", "estimate-salt"));
                _storePath = Path.Combine(dataDirectory, "quota", "estimate.json");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                salt = null;
            }
        }

        _salt = salt ?? System.Security.Cryptography.RandomNumberGenerator.GetBytes(AccountKeys.SaltBytes);
        _signIn = signIn is { } files ? new EstimateSignIn(_salt, files.CodexAuth, files.ClaudeState) : null;
        Salt = RemoteSalt.Of(_salt);
    }

    /// <summary>The salt as the hosts' quota config carries it (REMOTE 11.1).</summary>
    public RemoteSalt? Salt { get; }

    /// <summary>Called on the building thread after a failed build, with the exception's type only.</summary>
    public Action<string>? Log { get; set; }

    /// <summary>The latest build has an evaluation (tests).</summary>
    internal EstimateEvaluation? Latest
    {
        get
        {
            lock (_latestGate)
            {
                return _latest;
            }
        }
    }

    /// <summary>
    /// Builds now when something changed and the last build is at least 5 s old, or the last one is a minute old; writes the
    /// store when it is due. Called from the quota readers' worker.
    /// </summary>
    public bool BuildIfDue()
    {
        if (!_buildGate.TryEnter())
        {
            return false;
        }

        try
        {
            var now = _clock();
            EstimateInput input;
            lock (_gate)
            {
                EnsureLoaded();
                var due = _lastBuildMs == long.MinValue || now - _lastBuildMs >= HeartbeatMs || (_dirty && now - _lastBuildMs >= BuildIntervalMs);
                if (!due)
                {
                    return false;
                }

                Prune(now);
                input = Inputs(now);
                _dirty = false;
            }

            var model = EstimateModel.Build(input, _salt, _pricing?.CurrentVersion ?? PriceBook.Bundled.Version);
            var evaluation = EstimateEvaluation.Of(model, now);
            lock (_latestGate)
            {
                _latest = evaluation;
            }

            _lastBuildMs = now;
            Write(evaluation, now, force: false);
            return true;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Log?.Invoke("Quota estimate: a build failed (" + e.GetType().Name + ").");
            _lastBuildMs = _clock();
            return false;
        }
        finally
        {
            _buildGate.Exit();
        }
    }

    /// <summary>Builds once more when anything changed and writes the store (quit).</summary>
    public void Flush()
    {
        lock (_buildGate)
        {
            _lastBuildMs = long.MinValue;
        }

        BuildIfDue();
        if (Latest is { } latest)
        {
            lock (_buildGate)
            {
                Write(latest, _clock(), force: true);
            }
        }
    }

    /// <summary>
    /// What a window of this computer's source (<paramref name="host"/> null) or of a host's shows (QUOTAS 10.7, 10.8): the
    /// newest instance of the account the source spends on now; null without one.
    /// </summary>
    public QuotaWindowEstimate? Display(SourceKind source, string? host, string windowId, long nowMs)
    {
        ArgumentNullException.ThrowIfNull(windowId);
        lock (_latestGate)
        {
            if (_latest is not { } latest)
            {
                return null;
            }

            var account = host is null ? latest.Model.AccountOf(source, nowMs) : latest.Model.HostAccountOf(host, source, nowMs);
            return account is not null && latest.EntryFor(account, windowId, nowMs) is { } entry ? QuotaWindowEstimate.From(entry) : null;
        }
    }

    /// <summary>The report entries at <paramref name="nowMs"/> (REPORTING-EXTENSIONS 11.2) of the sources <paramref name="include"/> keeps.</summary>
    public IReadOnlyList<QuotaReportEntry> Entries(long nowMs, Func<SourceKind, bool> include)
    {
        ArgumentNullException.ThrowIfNull(include);
        lock (_latestGate)
        {
            return _latest?.Entries(nowMs, include) ?? [];
        }
    }

    /// <summary>The store when a minute passed since the last write (or <paramref name="force"/>) and it changed; under the build gate.</summary>
    private void Write(EstimateEvaluation evaluation, long now, bool force)
    {
        if (_storePath is null || (!force && _lastWriteMs != long.MinValue && now - _lastWriteMs < WriteIntervalMs))
        {
            return;
        }

        byte[] data;
        lock (_latestGate)
        {
            data = EstimateStore.Serialize(evaluation.Store());
        }

        if (_written is not null && data.AsSpan().SequenceEqual(_written))
        {
            return;
        }

        try
        {
            Core.Network.AtomicFile.Write(_storePath, data);
            _written = data;
            _lastWriteMs = now;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Tried again at the next build.
        }
    }
}
