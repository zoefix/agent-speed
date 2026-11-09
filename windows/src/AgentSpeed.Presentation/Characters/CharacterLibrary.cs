using AgentSpeed.Core.Network;
using AgentSpeed.Presentation.Orb;

namespace AgentSpeed.Presentation.Characters;

/// <summary>
/// The character packs (CHARACTERS 2): what is installed, the catalog of the <c>characters</c> release, downloads the
/// user starts, and the automatic download of every pack the user has not set aside (run when an orb switches to the
/// <c>character</c> style and at launch while one uses it, one pack at a time). The catalog is fetched when Settings shows
/// the list (at most every 10 minutes), when a run needs it, and at most once a day otherwise, only while network features
/// are on; its failures stay quiet, download failures are shown. Thread-safe: state changes under a lock and
/// <see cref="Changed"/> is raised through the poster given at construction (the UI thread in the app).
/// </summary>
public sealed class CharacterLibrary : IDisposable
{
    public static readonly TimeSpan ListRefresh = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan BackgroundRefresh = TimeSpan.FromDays(1);

    public enum CatalogState
    {
        Idle,
        Loading,
        Loaded,
        Failed,
    }

    public readonly record struct Download(double Progress, bool Installing = false);

    private readonly object _gate = new();
    private readonly IHttpTransport _transport;
    private readonly ICharacterPackFetcher _fetcher;
    private readonly string _userAgent;
    private readonly Action<IReadOnlyList<string>>? _saveSetAside;
    private readonly Action<Action> _post;
    private readonly Dictionary<string, CancellationTokenSource> _tasks = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Download> _downloads = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _errors = new(StringComparer.Ordinal);
    private readonly List<string> _autoQueue = [];
    private readonly HashSet<string> _skipped = new(StringComparer.Ordinal);
    private HashSet<string> _setAside;
    private IReadOnlyList<InstalledCharacter> _installed = [];
    private CharacterCatalog? _catalog;
    private CatalogState _catalogState = CatalogState.Idle;
    private string? _etag;
    private DateTimeOffset? _fetchedAt;
    private string? _autoCurrent;
    private int _autoTotal;
    private IReadOnlyList<string>? _pendingRun;
    private bool _disposed;

    /// <param name="setAside">The packs set aside so far (settings key <c>charactersSetAside</c>).</param>
    /// <param name="saveSetAside">Persists the set-aside list whenever it changes.</param>
    /// <param name="post">Runs <see cref="Changed"/> handlers on the owner's thread; null runs them inline.</param>
    public CharacterLibrary(CharacterStore store, IHttpTransport transport, ICharacterPackFetcher fetcher, string appVersion,
        IEnumerable<string>? setAside = null, Action<IReadOnlyList<string>>? saveSetAside = null, Action<Action>? post = null)
    {
        Store = store;
        _transport = transport;
        _fetcher = fetcher;
        _userAgent = HttpFetchRequest.UserAgentFor(appVersion);
        _saveSetAside = saveSetAside;
        _post = post ?? (a => a());
        _setAside = new HashSet<string>((setAside ?? []).Where(CharacterId.IsValid), StringComparer.Ordinal);
        Store.CleanLeftovers();
        ReloadInstalled();
    }

    public CharacterStore Store { get; }

    /// <summary>Network features are on (NETWORK 1); nothing is fetched while this returns false.</summary>
    public Func<bool> NetworkEnabled { get; set; } = () => true;

    public Uri CatalogUrl { get; set; } = CharacterCatalog.Url;

    /// <summary>Something shown changed: installed packs, the catalog, a download's progress or error, the automatic run.</summary>
    public event EventHandler? Changed;

    public IReadOnlyList<InstalledCharacter> Installed
    {
        get
        {
            lock (_gate)
            {
                return _installed;
            }
        }
    }

    public CharacterCatalog? Catalog
    {
        get
        {
            lock (_gate)
            {
                return _catalog;
            }
        }
    }

    public CatalogState CatalogStatus
    {
        get
        {
            lock (_gate)
            {
                return _catalogState;
            }
        }
    }

    public IReadOnlyDictionary<string, Download> Downloads
    {
        get
        {
            lock (_gate)
            {
                return new Dictionary<string, Download>(_downloads, StringComparer.Ordinal);
            }
        }
    }

    /// <summary>Localization keys of the last failed download per character.</summary>
    public IReadOnlyDictionary<string, string> Errors
    {
        get
        {
            lock (_gate)
            {
                return new Dictionary<string, string>(_errors, StringComparer.Ordinal);
            }
        }
    }

    public IReadOnlyList<string> AutoQueue
    {
        get
        {
            lock (_gate)
            {
                return _autoQueue.ToArray();
            }
        }
    }

    public string? AutoCurrent
    {
        get
        {
            lock (_gate)
            {
                return _autoCurrent;
            }
        }
    }

    public int AutoTotal
    {
        get
        {
            lock (_gate)
            {
                return _autoTotal;
            }
        }
    }

    /// <summary>The run's position for its status line: the pack downloading now, counted from 1.</summary>
    public int AutoPosition
    {
        get
        {
            lock (_gate)
            {
                return _autoTotal - _autoQueue.Count;
            }
        }
    }

    /// <summary>Packs the user removed or cancelled: a run never downloads them (CHARACTERS 2, <i>Automatic download</i>).</summary>
    public IReadOnlySet<string> SetAside
    {
        get
        {
            lock (_gate)
            {
                return new HashSet<string>(_setAside, StringComparer.Ordinal);
            }
        }
    }

    public bool IsBusy
    {
        get
        {
            lock (_gate)
            {
                return _autoTotal > 0 || _catalogState == CatalogState.Loading || _downloads.Count > 0;
            }
        }
    }

    public void ReloadInstalled()
    {
        var next = Store.Installed();
        lock (_gate)
        {
            if (next.SequenceEqual(_installed))
            {
                return;
            }

            _installed = next;
        }

        Notify();
    }

    public InstalledCharacter? InstalledPack(string id) => Installed.FirstOrDefault(p => p.Id == id);

    /// <summary>Installed packs in catalog order (the order Auto falls back on), then the rest by id.</summary>
    public IReadOnlyList<CharacterSelection.Pack> Packs
    {
        get
        {
            IReadOnlyList<InstalledCharacter> installed;
            CharacterCatalog? catalog;
            lock (_gate)
            {
                installed = _installed;
                catalog = _catalog;
            }

            var order = new Dictionary<string, int>(StringComparer.Ordinal);
            for (var i = 0; i < (catalog?.Characters.Count ?? 0); i++)
            {
                order.TryAdd(catalog!.Characters[i].Id, i);
            }

            return installed.OrderBy(p => order.GetValueOrDefault(p.Id, int.MaxValue)).ThenBy(p => p.Id, StringComparer.Ordinal)
                .Select(p => new CharacterSelection.Pack(p.Id, p.Manifest.Vendors)).ToArray();
        }
    }

    /// <summary>A newer pack version is in the catalog.</summary>
    public bool HasUpdate(string id) =>
        InstalledPack(id) is { IsDevelopment: false } pack && Catalog?.Find(id) is { } entry && entry.PackVersion > pack.Manifest.PackVersion;

    /// <summary>Settings shows the list: refresh unless fetched in the last 10 minutes.</summary>
    public void RefreshForList() => RefreshCatalog(ListRefresh);

    /// <summary>At launch and daily.</summary>
    public void RefreshInBackground() => RefreshCatalog(BackgroundRefresh);

    public void RefreshCatalog(TimeSpan maxAge)
    {
        HttpFetchRequest request;
        lock (_gate)
        {
            if (_disposed || !NetworkEnabled() || _catalogState == CatalogState.Loading)
            {
                return;
            }

            if (_fetchedAt is { } at && DateTimeOffset.UtcNow - at < maxAge && _catalog is not null)
            {
                return;
            }

            _catalogState = CatalogState.Loading;
            request = new HttpFetchRequest(CatalogUrl, _userAgent, CharacterCatalog.MaxCatalogBytes, CharacterCatalog.AllowedHosts)
            {
                IfNoneMatch = _catalog is null ? null : _etag,
            };
        }

        Notify();
        _ = FetchCatalogAsync(request);
    }

    private async Task FetchCatalogAsync(HttpFetchRequest request)
    {
        HttpFetchResponse? response = null;
        try
        {
            response = await _transport.GetAsync(request).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpFetchException or OperationCanceledException or IOException)
        {
            response = null;
        }

        bool queued;
        lock (_gate)
        {
            if (response is { Status: 304 } && _catalog is not null)
            {
                _fetchedAt = DateTimeOffset.UtcNow;
                _catalogState = CatalogState.Loaded;
            }
            else if (response is { Status: 200 })
            {
                try
                {
                    _catalog = CharacterCatalog.Load(response.Body);
                    _etag = response.ETag;
                    _fetchedAt = DateTimeOffset.UtcNow;
                    _catalogState = CatalogState.Loaded;
                }
                catch (CharacterPackException)
                {
                    _catalogState = CatalogState.Failed;
                }
            }
            else
            {
                _catalogState = _catalog is null ? CatalogState.Failed : CatalogState.Loaded;
            }

            // The pending run is queued before the lock is released, so the library never looks idle in between.
            var pending = _pendingRun;
            _pendingRun = null;
            queued = pending is not null && _catalog is not null && !_disposed && NetworkEnabled() && QueueLocked(pending);
        }

        Notify();
        if (queued)
        {
            StartNextAutomatic();
            Notify();
        }
    }

    /// <summary>
    /// Queues every pack that is neither installed, set aside, downloading nor queued — the packs of
    /// <paramref name="vendors"/> first (fastest first), then catalog order — and downloads them one at a time. Waits for
    /// the catalog when it is not loaded yet; does nothing while network features are off.
    /// </summary>
    public void DownloadAll(IReadOnlyList<string> vendors)
    {
        ArgumentNullException.ThrowIfNull(vendors);
        bool fetch;
        lock (_gate)
        {
            if (_disposed || !NetworkEnabled())
            {
                return;
            }

            fetch = _catalog is null;
            if (fetch)
            {
                _pendingRun = vendors;
            }
            else if (!QueueLocked(vendors))
            {
                return;
            }
        }

        if (fetch)
        {
            RefreshCatalog(ListRefresh);
            return;
        }

        StartNextAutomatic();
        Notify();
    }

    /// <summary>Adds the packs a run would fetch to the queue; false when there are none.</summary>
    private bool QueueLocked(IReadOnlyList<string> vendors)
    {
        var busy = new HashSet<string>(_downloads.Keys.Concat(_autoQueue), StringComparer.Ordinal);
        var skipped = new HashSet<string>(_skipped.Concat(busy), StringComparer.Ordinal);
        var fresh = CharacterAutoDownload.Queue(_catalog!, _installed.Select(p => p.Id).ToHashSet(StringComparer.Ordinal), _setAside, skipped, vendors);
        if (fresh.Count == 0)
        {
            return false;
        }

        if (_autoCurrent is null && _autoQueue.Count == 0)
        {
            _autoTotal = 0;
            _skipped.Clear();
        }

        _autoQueue.AddRange(fresh);
        _autoTotal += fresh.Count;
        return true;
    }

    /// <summary>Stop: every pack still queued, and the one downloading, is set aside.</summary>
    public void StopAutomatic()
    {
        CancellationTokenSource? cancel = null;
        lock (_gate)
        {
            var ids = _autoQueue.ToList();
            if (_autoCurrent is { } current)
            {
                ids.Add(current);
            }

            _autoQueue.Clear();
            SetAsideLocked(ids, true);
            if (_autoCurrent is { } running)
            {
                _tasks.TryGetValue(running, out cancel);
            }
            else
            {
                _autoTotal = 0;
            }
        }

        cancel?.Cancel();
        Notify();
    }

    private void StartNextAutomatic()
    {
        CharacterCatalog.Entry? next = null;
        lock (_gate)
        {
            if (_autoCurrent is not null)
            {
                return;
            }

            while (NetworkEnabled() && _autoQueue.Count > 0)
            {
                var id = _autoQueue[0];
                _autoQueue.RemoveAt(0);
                if (_installed.Any(p => p.Id == id) || _setAside.Contains(id) || _tasks.ContainsKey(id) || _catalog?.Find(id) is not { } entry)
                {
                    continue;
                }

                _autoCurrent = id;
                next = entry;
                break;
            }

            if (next is null)
            {
                _autoQueue.Clear();
                _autoTotal = 0;
            }
        }

        if (next is not null)
        {
            Start(next);
        }
    }

    private void SetAsideLocked(IEnumerable<string> ids, bool aside)
    {
        var next = new HashSet<string>(_setAside, StringComparer.Ordinal);
        foreach (var id in ids)
        {
            if (aside)
            {
                next.Add(id);
            }
            else
            {
                next.Remove(id);
            }
        }

        if (next.SetEquals(_setAside))
        {
            return;
        }

        _setAside = next;
        var sorted = next.Order(StringComparer.Ordinal).ToArray();
        _post(() => _saveSetAside?.Invoke(sorted));
    }

    /// <summary>The user's Download or Update: also takes the pack back from the set-aside list.</summary>
    public void DownloadPack(CharacterCatalog.Entry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        lock (_gate)
        {
            SetAsideLocked([entry.Id], false);
        }

        Start(entry);
    }

    private void Start(CharacterCatalog.Entry entry)
    {
        CancellationTokenSource? cancel = null;
        lock (_gate)
        {
            if (_disposed || _tasks.ContainsKey(entry.Id))
            {
                return;
            }

            if (NetworkEnabled())
            {
                _errors.Remove(entry.Id);
                _downloads[entry.Id] = new Download(0);
                cancel = new CancellationTokenSource();
                _tasks[entry.Id] = cancel;
            }
            else
            {
                _errors[entry.Id] = "characters.error.offline";
                if (_autoCurrent == entry.Id)
                {
                    _autoCurrent = null;
                }
            }
        }

        Notify();
        if (cancel is not null)
        {
            _ = RunAsync(entry, cancel);
        }
    }

    private async Task RunAsync(CharacterCatalog.Entry entry, CancellationTokenSource cancel)
    {
        Exception? error = null;
        try
        {
            var installer = new CharacterInstaller(Store, _fetcher);
            var progress = new Progress(this, entry.Id);
            await Task.Run(() => installer.InstallAsync(entry, progress, cancel.Token), cancel.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            error = ex;
        }

        FinishDownload(entry.Id, error);
    }

    private sealed class Progress(CharacterLibrary library, string id) : IProgress<double>
    {
        public void Report(double value) => library.ReportProgress(id, value);
    }

    private void ReportProgress(string id, double fraction)
    {
        lock (_gate)
        {
            if (!_downloads.TryGetValue(id, out var download))
            {
                return;
            }

            var next = new Download(Math.Clamp(fraction, 0, 1), fraction >= 1);
            if (Math.Abs(next.Progress - download.Progress) < 0.004 && next.Installing == download.Installing)
            {
                return;
            }

            _downloads[id] = next;
        }

        Notify();
    }

    /// <summary>The user's cancel sets the pack aside, so a run does not fetch it again.</summary>
    public void Cancel(string id)
    {
        CancellationTokenSource? cancel;
        lock (_gate)
        {
            SetAsideLocked([id], true);
            _tasks.TryGetValue(id, out cancel);
        }

        cancel?.Cancel();
    }

    /// <summary>Removing sets the pack aside as well.</summary>
    public void Remove(string id)
    {
        if (InstalledPack(id) is not { IsDevelopment: false })
        {
            return;
        }

        lock (_gate)
        {
            SetAsideLocked([id], true);
        }

        try
        {
            Store.Remove(id);
            lock (_gate)
            {
                _errors.Remove(id);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CharacterPackException)
        {
            lock (_gate)
            {
                _errors[id] = "characters.error.remove";
            }
        }

        ReloadInstalled();
        Notify();
    }

    private void FinishDownload(string id, Exception? error)
    {
        var key = error is null ? null : ErrorKey(error);
        // The installed list is current before the download ends, so the pack is never in neither.
        ReloadInstalled();
        lock (_gate)
        {
            if (_tasks.Remove(id, out var cancel))
            {
                cancel.Dispose();
            }

            _downloads.Remove(id);
            if (key is not null)
            {
                _errors[id] = key;
            }
        }

        var next = false;
        lock (_gate)
        {
            if (id == _autoCurrent)
            {
                _autoCurrent = null;
                if (key == "characters.error.network")
                {
                    // The connection is down: end the run; the next launch or style switch starts it again.
                    _autoQueue.Clear();
                }
                else if (key is not null)
                {
                    _skipped.Add(id);
                }

                next = true;
            }
        }

        if (next)
        {
            StartNextAutomatic();
        }

        Notify();
    }

    /// <summary>The localization key of a failed download; null when it was cancelled.</summary>
    public static string? ErrorKey(Exception error) => error switch
    {
        OperationCanceledException => null,
        CharacterInstallException { Failure: CharacterInstallFailure.Checksum } => "characters.error.checksum",
        CharacterInstallException { Failure: CharacterInstallFailure.InvalidPack } => "characters.error.invalid",
        CharacterPackException { Kind: CharacterPackErrorKind.NewerApp } => "characters.error.newerApp",
        CharacterPackException => "characters.error.invalid",
        InvalidDataException => "characters.error.invalid",
        _ => "characters.error.network",
    };

    private void Notify()
    {
        if (Changed is { } handler)
        {
            _post(() => handler(this, EventArgs.Empty));
        }
    }

    public void Dispose()
    {
        List<CancellationTokenSource> running;
        lock (_gate)
        {
            _disposed = true;
            running = _tasks.Values.ToList();
            _autoQueue.Clear();
        }

        foreach (var cancel in running)
        {
            cancel.Cancel();
        }
    }
}
