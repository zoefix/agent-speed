using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AgentSpeed.App;
using AgentSpeed.Characters;
using AgentSpeed.Controls;
using AgentSpeed.Presentation.Characters;
using AgentSpeed.Presentation.Orb;
using AgentSpeed.Presentation.Settings.Orbs;

namespace AgentSpeed.Settings.OrbsPane;

/// <summary>An entry of the <c>characters.choice</c> pop-up: Auto (null) or an installed character.</summary>
internal sealed record CharacterChoice(string? Id, string Name);

/// <summary>
/// One character of the Settings list (DESIGN 5, <c>characters.title</c>): the portrait (the installed <c>preview.png</c>,
/// else a silhouette), the accent dot and name, the artist credit and size, and Download / progress with Cancel /
/// Installing / Update and Remove, with a failed download's error under it.
/// </summary>
internal sealed class CharacterRowViewModel(string id) : ObservableObject
{
    private string _name = string.Empty;
    private Brush _accent = Brushes.Gray;
    private ImageSource? _portrait;
    private string? _artist;
    private Uri? _artistLink;
    private string _size = string.Empty;
    private string? _error;
    private bool _downloading;
    private bool _installing;
    private double _progress;
    private bool _canDownload;
    private bool _downloadEnabled;
    private bool _canUpdate;
    private bool _canRemove;
    private bool _development;
    private string _accessibleName = string.Empty;

    public string Id { get; } = id;

    public string Name { get => _name; set => Set(ref _name, value); }

    public Brush Accent { get => _accent; set => Set(ref _accent, value); }

    public ImageSource? Portrait
    {
        get => _portrait;
        set
        {
            if (Set(ref _portrait, value))
            {
                OnPropertyChanged(nameof(HasPortrait));
            }
        }
    }

    public bool HasPortrait => _portrait is not null;

    public string? Artist
    {
        get => _artist;
        set
        {
            if (Set(ref _artist, value))
            {
                OnPropertyChanged(nameof(HasArtist));
            }
        }
    }

    public bool HasArtist => _artist is not null;

    public Uri? ArtistLink
    {
        get => _artistLink;
        set
        {
            if (Set(ref _artistLink, value))
            {
                OnPropertyChanged(nameof(HasArtistLink));
                OnPropertyChanged(nameof(HasArtistText));
            }
        }
    }

    public bool HasArtistLink => _artistLink is not null;

    public bool HasArtistText => _artistLink is null;

    public string Size { get => _size; set => Set(ref _size, value); }

    public string? Error
    {
        get => _error;
        set
        {
            if (Set(ref _error, value))
            {
                OnPropertyChanged(nameof(HasError));
            }
        }
    }

    public bool HasError => _error is not null;

    public bool Downloading { get => _downloading; set => Set(ref _downloading, value); }

    public bool Installing { get => _installing; set => Set(ref _installing, value); }

    public double Progress { get => _progress; set => Set(ref _progress, value); }

    public bool CanDownload { get => _canDownload; set => Set(ref _canDownload, value); }

    /// <summary>Download is disabled while network features are off.</summary>
    public bool DownloadEnabled { get => _downloadEnabled; set => Set(ref _downloadEnabled, value); }

    public bool CanUpdate { get => _canUpdate; set => Set(ref _canUpdate, value); }

    public bool CanRemove { get => _canRemove; set => Set(ref _canRemove, value); }

    public bool Development { get => _development; set => Set(ref _development, value); }

    public string AccessibleName { get => _accessibleName; set => Set(ref _accessibleName, value); }
}

/// <summary>
/// Settings → 懸浮窗, the selected <c>character</c> orb (DESIGN 5): the character section under the style cards
/// (<c>characters.choice</c>, the automatic download's status with Stop, the catalog list, the empty states, the footer)
/// and the <c>character.readouts</c> section in place of the unit and two-line readout sections.
/// </summary>
internal sealed partial class OrbsPaneViewModel
{
    private static readonly Dictionary<(string Path, int Version), ImageSource?> Portraits = new();
    private CharacterService? _characters;
    private bool _characterShown;
    private int _characterChoiceIndex;

    /// <summary>The app's character packs; the section stays empty without them.</summary>
    public CharacterService? Characters
    {
        get => _characters;
        set
        {
            if (ReferenceEquals(_characters, value))
            {
                return;
            }

            if (_characters is not null)
            {
                _characters.Changed -= OnCharactersChanged;
            }

            _characters = value;
            if (value is not null)
            {
                value.Changed += OnCharactersChanged;
            }

            RefreshCharacters();
        }
    }

    public ObservableCollection<CharacterChoice> CharacterChoices { get; } = [];

    public ObservableCollection<CharacterRowViewModel> CharacterRows { get; } = [];

    public bool IsCharacter => Orb?.Style == OrbStyle.Character;

    /// <summary>The selected orb's locked character, for the <c>character</c> style card's portrait.</summary>
    public string? CardCharacter => Orb?.Character;

    public bool IsNotCharacter => !IsCharacter;

    /// <summary>The <c>characters.choice</c> pop-up: 0 Auto, then the installed characters.</summary>
    public int CharacterChoiceIndex
    {
        get => _characterChoiceIndex;
        set
        {
            if (value < 0 || value >= CharacterChoices.Count || _characterChoiceIndex == value)
            {
                return;
            }

            _characterChoiceIndex = value;
            if (!_syncing && !_refreshingCharacters && SelectedId is { } id)
            {
                var character = CharacterChoices[value].Id;
                Update(d => OrbEditing.SetCharacter(d, id, character));
            }
        }
    }

    private bool _refreshingCharacters;

    public bool AutoRunning => _characters?.Library.AutoTotal > 0;

    public string AutoStatus => _characters is { } c && c.Library.AutoTotal > 0
        ? _loc.Format("characters.auto.status", Math.Max(1, c.Library.AutoPosition), c.Library.AutoTotal)
        : string.Empty;

    public double AutoProgress => _characters is { } c && c.Library.AutoTotal > 0
        ? Math.Max(0, c.Library.AutoPosition - 1) / (double)Math.Max(1, c.Library.AutoTotal)
        : 0;

    public bool CharacterListEmpty => CharacterRows.Count == 0;

    public bool CharacterLoading => CharacterListEmpty && _characters?.Library.CatalogStatus == CharacterLibrary.CatalogState.Loading;

    public string CharacterEmptyText => CharacterLoading ? _loc["characters.loading"]
        : S.NetworkEnabled ? _loc["characters.unavailable"] : _loc["characters.offline"];

    public bool CharacterRetryVisible => CharacterListEmpty && !CharacterLoading && S.NetworkEnabled;

    public ICommand CharacterDownloadCommand => new RelayCommand(p => WithEntry(p, e => _characters?.Library.DownloadPack(e)));

    public ICommand CharacterCancelCommand => new RelayCommand(p =>
    {
        if (p is CharacterRowViewModel row)
        {
            _characters?.Library.Cancel(row.Id);
        }
    });

    public ICommand CharacterRemoveCommand => new RelayCommand(p =>
    {
        if (p is CharacterRowViewModel row)
        {
            _characters?.Library.Remove(row.Id);
        }
    });

    public ICommand CharacterArtistCommand => new RelayCommand(p =>
    {
        if (p is CharacterRowViewModel { ArtistLink: { Scheme: "https" } link })
        {
            Process.Start(new ProcessStartInfo(link.AbsoluteUri) { UseShellExecute = true });
        }
    });

    public ICommand CharacterStopCommand => new RelayCommand(() => _characters?.Library.StopAutomatic());

    public ICommand CharacterRetryCommand => new RelayCommand(() => _characters?.Library.RefreshCatalog(TimeSpan.Zero));

    public ICommand CharacterFolderCommand => new RelayCommand(() =>
    {
        if (_characters?.Library.Store.Root is not { } root)
        {
            return;
        }

        Directory.CreateDirectory(root);
        Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = false, ArgumentList = { root } });
    });

    private void WithEntry(object? parameter, Action<CharacterCatalog.Entry> action)
    {
        if (parameter is CharacterRowViewModel row && _characters?.Library.Catalog?.Find(row.Id) is { } entry)
        {
            action(entry);
        }
    }

    // ---- Readouts (DESIGN 2a.6, Readouts; 2b characterReadouts) ----

    public bool ReadoutIntel { get => Readout(CharacterReadout.Intelligence); set => SetReadout(CharacterReadout.Intelligence, value); }

    public bool ReadoutThinking { get => Readout(CharacterReadout.Thinking); set => SetReadout(CharacterReadout.Thinking, value); }

    public bool ReadoutLatency { get => Readout(CharacterReadout.Latency); set => SetReadout(CharacterReadout.Latency, value); }

    public bool ReadoutNetwork { get => Readout(CharacterReadout.Network); set => SetReadout(CharacterReadout.Network, value); }

    public bool ReadoutCost { get => Readout(CharacterReadout.Cost); set => SetReadout(CharacterReadout.Cost, value); }

    public bool ReadoutAgents { get => Readout(CharacterReadout.Agents); set => SetReadout(CharacterReadout.Agents, value); }

    public bool ReadoutModels { get => Readout(CharacterReadout.Models); set => SetReadout(CharacterReadout.Models, value); }

    public bool ReadoutQuota { get => Readout(CharacterReadout.Quota); set => SetReadout(CharacterReadout.Quota, value); }

    public bool ReadoutModel { get => Readout(CharacterReadout.Model); set => SetReadout(CharacterReadout.Model, value); }

    /// <summary>The network card needs traffic measured (Settings → 網路流量).</summary>
    public bool TrafficMeasured => S.Traffic.Enabled;

    public bool NetworkScopeEnabled => S.Traffic.Enabled && ReadoutNetwork;

    /// <summary>The network card's scope: 0 the orb's agents (<c>orbs.traffic.sources</c>), 1 this computer.</summary>
    public int NetworkScopeIndex
    {
        get => Orb?.Traffic == OrbTraffic.Machine ? 1 : 0;
        set
        {
            if (!_syncing && SelectedId is { } id && value is 0 or 1)
            {
                Update(d => OrbEditing.SetTraffic(d, id, value == 1 ? OrbTraffic.Machine : OrbTraffic.Sources));
            }
        }
    }

    private bool Readout(CharacterReadout readout) => (Orb?.CharacterReadouts ?? CharacterReadouts.Default)[readout];

    private void SetReadout(CharacterReadout readout, bool on)
    {
        if (!_syncing && SelectedId is { } id)
        {
            Update(d => OrbEditing.SetCharacterReadout(d, id, readout, on));
        }
    }

    /// <summary>The Orb page shows: the catalog refreshes (at most every 10 minutes).</summary>
    public void CharacterSectionShown()
    {
        _characterShown = true;
        if (IsCharacter)
        {
            _characters?.Library.RefreshForList();
        }
    }

    private void OnCharactersChanged(object? sender, EventArgs e) => RefreshCharacters();

    /// <summary>Rebuilds the pop-up and the rows from the library (UI thread).</summary>
    private void RefreshCharacters()
    {
        _refreshingCharacters = true;
        try
        {
            var library = _characters?.Library;
            var language = _loc.LanguageCode;
            var installed = library?.Installed ?? [];
            var choices = new List<CharacterChoice> { new(null, _loc["characters.auto"]) };
            foreach (var pack in library?.Packs ?? [])
            {
                choices.Add(new CharacterChoice(pack.Id, library?.InstalledPack(pack.Id)?.Manifest.Name.For(language) ?? pack.Id));
            }

            if (!choices.SequenceEqual(CharacterChoices))
            {
                CharacterChoices.Clear();
                foreach (var choice in choices)
                {
                    CharacterChoices.Add(choice);
                }
            }

            var locked = Orb?.Character;
            var index = locked is null ? 0 : choices.FindIndex(c => c.Id == locked);
            _characterChoiceIndex = Math.Max(0, index);
            OnPropertyChanged(nameof(CharacterChoiceIndex));

            var entries = library?.Catalog?.Characters ?? [];
            var ids = entries.Select(e => e.Id).Concat(installed.Select(p => p.Id)).Distinct(StringComparer.Ordinal).ToList();
            for (var i = CharacterRows.Count - 1; i >= 0; i--)
            {
                if (!ids.Contains(CharacterRows[i].Id))
                {
                    CharacterRows.RemoveAt(i);
                }
            }

            var downloads = library?.Downloads ?? new Dictionary<string, CharacterLibrary.Download>();
            var errors = library?.Errors ?? new Dictionary<string, string>();
            for (var i = 0; i < ids.Count; i++)
            {
                var id = ids[i];
                var row = CharacterRows.FirstOrDefault(r => r.Id == id);
                if (row is null)
                {
                    row = new CharacterRowViewModel(id);
                    CharacterRows.Insert(Math.Min(i, CharacterRows.Count), row);
                }
                else if (CharacterRows.IndexOf(row) != i)
                {
                    CharacterRows.Move(CharacterRows.IndexOf(row), i);
                }

                var entry = entries.FirstOrDefault(e => e.Id == id);
                var pack = installed.FirstOrDefault(p => p.Id == id);
                var names = pack?.Manifest.Name ?? entry?.Name;
                var artist = pack?.Manifest.Artist ?? entry?.Artist;
                var accent = CharacterManifest.Rgb(pack?.Manifest.Accent ?? entry?.Accent);
                row.Name = names?.For(language) ?? id;
                row.Accent = accent is { } c
                    ? Frozen(Color.FromRgb((byte)Math.Round(c.R * 255), (byte)Math.Round(c.G * 255), (byte)Math.Round(c.B * 255)))
                    : Brushes.Gray;
                row.Portrait = pack is null ? null : Portrait(pack);
                row.Artist = artist is null ? null : _loc.Format("characters.artist", artist.Name);
                row.ArtistLink = artist?.Link;
                row.Size = entry is null ? string.Empty : FileSize(entry.Size);
                row.Development = pack?.IsDevelopment == true;
                var download = downloads.TryGetValue(id, out var d) ? d : (CharacterLibrary.Download?)null;
                row.Downloading = download is { Installing: false };
                row.Installing = download is { Installing: true };
                row.Progress = download?.Progress ?? 0;
                row.CanDownload = download is null && pack is null && entry is not null;
                row.DownloadEnabled = S.NetworkEnabled;
                row.CanUpdate = download is null && pack is not null && library?.HasUpdate(id) == true;
                row.CanRemove = download is null && pack is { IsDevelopment: false };
                row.Error = errors.TryGetValue(id, out var key) ? _loc[key] : null;
                row.AccessibleName = string.Join(", ", new[] { row.Name, row.Artist, row.Size, row.Error }.Where(t => !string.IsNullOrEmpty(t)));
            }

            OnPropertyChanged(nameof(AutoRunning));
            OnPropertyChanged(nameof(AutoStatus));
            OnPropertyChanged(nameof(AutoProgress));
            OnPropertyChanged(nameof(CharacterListEmpty));
            OnPropertyChanged(nameof(CharacterLoading));
            OnPropertyChanged(nameof(CharacterEmptyText));
            OnPropertyChanged(nameof(CharacterRetryVisible));
        }
        finally
        {
            _refreshingCharacters = false;
        }
    }

    /// <summary>The editor switched orbs or the orb changed: the character section and the readouts follow.</summary>
    private void RefreshCharacterEditor()
    {
        OnPropertyChanged(nameof(IsCharacter));
        OnPropertyChanged(nameof(IsNotCharacter));
        OnPropertyChanged(nameof(CardCharacter));
        OnPropertyChanged(nameof(ReadoutIntel));
        OnPropertyChanged(nameof(ReadoutThinking));
        OnPropertyChanged(nameof(ReadoutLatency));
        OnPropertyChanged(nameof(ReadoutNetwork));
        OnPropertyChanged(nameof(ReadoutCost));
        OnPropertyChanged(nameof(ReadoutAgents));
        OnPropertyChanged(nameof(ReadoutModels));
        OnPropertyChanged(nameof(ReadoutQuota));
        OnPropertyChanged(nameof(ReadoutModel));
        OnPropertyChanged(nameof(TrafficMeasured));
        OnPropertyChanged(nameof(NetworkScopeEnabled));
        OnPropertyChanged(nameof(NetworkScopeIndex));
        RefreshCharacters();
        if (_characterShown && IsCharacter)
        {
            _characters?.Library.RefreshForList();
        }
    }

    private static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static string FileSize(long bytes) => bytes >= 1 << 20
        ? (bytes / 1048576.0).ToString("0.0", CultureInfo.CurrentCulture) + " MB"
        : (bytes / 1024.0).ToString("0", CultureInfo.CurrentCulture) + " KB";

    /// <summary>The installed <c>preview.png</c>, decoded small and cached per folder and pack version; null when unreadable.</summary>
    internal static ImageSource? Portrait(InstalledCharacter pack)
    {
        var key = (pack.PreviewPath, pack.Manifest.PackVersion);
        if (Portraits.TryGetValue(key, out var cached))
        {
            return cached;
        }

        ImageSource? image = null;
        try
        {
            using var stream = new FileStream(pack.PreviewPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            bitmap.DecodePixelHeight = 192;
            bitmap.StreamSource = stream;
            bitmap.EndInit();
            bitmap.Freeze();
            image = bitmap;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or InvalidOperationException
            or ArgumentException or System.Runtime.InteropServices.COMException)
        {
            AppLog.Warn("A character preview could not be read.", ex);
        }

        Portraits[key] = image;
        return image;
    }
}
