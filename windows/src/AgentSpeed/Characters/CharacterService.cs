using System.IO;
using System.Windows.Threading;
using AgentSpeed.App;
using AgentSpeed.Core.Network;
using AgentSpeed.Presentation.Character;
using AgentSpeed.Presentation.Characters;
using AgentSpeed.Presentation.Settings;
using AgentSpeed.Services;

namespace AgentSpeed.Characters;

/// <summary>
/// The app's character packs (CHARACTERS 2, 7): the library in <c>%LOCALAPPDATA%\AgentSpeed\Characters</c> with the
/// catalog, the user's downloads and the automatic download of every pack not set aside, the set-aside list kept in the
/// settings (<c>charactersSetAside</c>), network features followed, and each installed pack loaded for the player off the
/// UI thread. <see cref="Changed"/> runs on the UI thread.
/// </summary>
internal sealed class CharacterService : IDisposable
{
    /// <summary>Developer variable: a fully qualified folder for the installed packs (test sessions), instead of the user's.</summary>
    public const string RootVariable = "AGENT_SPEED_CHARACTERS_ROOT";

    private readonly Dispatcher _ui;
    private readonly SettingsService _settings;
    private readonly HttpClientTransport _transport = new();
    private readonly HttpCharacterPackFetcher _fetcher;
    private readonly Dictionary<(string Folder, int Version), PackState> _packs = new();
    private readonly DispatcherTimer _daily;
    private bool _anyCharacterOrb;

    private sealed class PackState
    {
        public CharacterPack? Pack { get; set; }

        public bool Failed { get; set; }
    }

    public CharacterService(Dispatcher ui, SettingsService settings, string appVersion)
    {
        _ui = ui;
        _settings = settings;
        _fetcher = new HttpCharacterPackFetcher(appVersion);
        var root = Environment.GetEnvironmentVariable(RootVariable) is { Length: > 0 } custom && Path.IsPathFullyQualified(custom)
            && !custom.StartsWith(@"\\", StringComparison.Ordinal)
            ? custom
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppPaths.ProductFolder, "Characters");
        var dev = Environment.GetEnvironmentVariable(CharacterStore.DevelopmentVariable);
        var store = new CharacterStore(root, string.IsNullOrWhiteSpace(dev) ? null : dev, appVersion);
        Library = new CharacterLibrary(store, _transport, _fetcher, appVersion, settings.Current.CharactersSetAside.Ids,
            ids => _settings.Update(s => s with { CharactersSetAside = new CharacterSetAside(ids) }),
            action => _ui.BeginInvoke(action))
        {
            NetworkEnabled = () => _settings.Current.NetworkEnabled,
        };
        _logged = Library.Installed.Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
        Library.Changed += (_, _) =>
        {
            LogChanges();
            Changed?.Invoke(this, EventArgs.Empty);
        };
        _daily = new DispatcherTimer(TimeSpan.FromHours(1), DispatcherPriority.Background, (_, _) =>
        {
            if (_anyCharacterOrb)
            {
                Library.RefreshInBackground();
            }
        }, ui);
    }

    public CharacterLibrary Library { get; }

    private readonly HashSet<string> _logged;
    private readonly Dictionary<string, string> _loggedErrors = new(StringComparer.Ordinal);

    /// <summary>Installs, removals and download errors go to the log (ids and error keys only).</summary>
    private void LogChanges()
    {
        var now = Library.Installed.Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var id in now.Except(_logged))
        {
            AppLog.Info($"Character installed: {id} ({Library.AutoPosition}/{Library.AutoTotal} of the automatic run).");
        }

        foreach (var id in _logged.Except(now))
        {
            AppLog.Info($"Character removed: {id}.");
        }

        _logged.Clear();
        _logged.UnionWith(now);
        foreach (var (id, key) in Library.Errors)
        {
            if (!_loggedErrors.TryGetValue(id, out var last) || last != key)
            {
                _loggedErrors[id] = key;
                AppLog.Warn($"Character {id}: {key}.");
            }
        }
    }

    /// <summary>Installed packs, the catalog, a download's progress, or a loaded pack changed (UI thread).</summary>
    public event EventHandler? Changed;

    /// <summary>
    /// The player's pack for an installed character, or null while it loads (the load starts on the first call and
    /// <see cref="Changed"/> follows) or when it cannot be played.
    /// </summary>
    public CharacterPack? Pack(InstalledCharacter installed)
    {
        ArgumentNullException.ThrowIfNull(installed);
        var key = (installed.Folder, installed.Manifest.PackVersion);
        if (_packs.TryGetValue(key, out var state))
        {
            return state.Pack;
        }

        state = new PackState();
        _packs[key] = state;
        var folder = installed.Folder;
        _ = Task.Run(() =>
        {
            CharacterPack? pack = null;
            try
            {
                pack = CharacterPack.Load(folder);
            }
            catch (PackLoadException ex)
            {
                AppLog.Warn($"Character pack {Path.GetFileName(folder)} cannot be played: {ex.Kind}.");
            }

            _ui.BeginInvoke(() =>
            {
                state.Pack = pack;
                state.Failed = pack is null;
                Changed?.Invoke(this, EventArgs.Empty);
            });
        });
        return null;
    }

    /// <summary>Whether the pack of an installed character is still loading.</summary>
    public bool IsLoading(InstalledCharacter installed) =>
        _packs.TryGetValue((installed.Folder, installed.Manifest.PackVersion), out var state) && state.Pack is null && !state.Failed;

    /// <summary>
    /// Some orb uses the <c>character</c> style: at launch, and whenever that becomes true, the catalog refreshes and every
    /// pack not set aside downloads (CHARACTERS 2, <i>Automatic download</i>).
    /// </summary>
    public void SetCharacterOrbs(bool any)
    {
        var started = any && !_anyCharacterOrb;
        _anyCharacterOrb = any;
        if (any)
        {
            _daily.Start();
        }
        else
        {
            _daily.Stop();
        }

        if (started)
        {
            Library.DownloadAll([]);
        }
    }

    /// <summary>An orb switched to the <c>character</c> style: download every pack not set aside, the orb's live vendors first.</summary>
    public void StyleChosen(IReadOnlyList<string> vendors)
    {
        _anyCharacterOrb = true;
        _daily.Start();
        Library.DownloadAll(vendors);
    }

    /// <summary>Network features changed: back on while a character orb exists, the automatic run resumes.</summary>
    public void OnSettingsChanged(AppSettings previous, AppSettings current)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(current);
        if (!previous.NetworkEnabled && current.NetworkEnabled && _anyCharacterOrb)
        {
            Library.DownloadAll([]);
        }
    }

    public void Dispose()
    {
        _daily.Stop();
        Library.Dispose();
        _fetcher.Dispose();
        _transport.Dispose();
    }
}
