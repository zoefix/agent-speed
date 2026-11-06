using System.Windows.Media;
using AgentSpeed.Controls;
using AgentSpeed.Core.Locations;
using AgentSpeed.Presentation.Localization;
using AgentSpeed.Presentation.Settings.Locations;

namespace AgentSpeed.Settings.Sources;

/// <summary>One folder line of a row: the path as shown, the full path as tooltip, the 預設 badge after the automatic entry's first.</summary>
internal sealed class LocationLineViewModel(LocationLine line, bool showsBadge, bool dimmed)
{
    public string Display { get; } = line.Display;

    public string FullPath { get; } = line.Path;

    public bool ShowsBadge { get; } = showsBadge;

    /// <summary>A folder that does not exist (an automatic entry with several folders dims only the missing ones).</summary>
    public bool Dimmed { get; } = dimmed;
}

/// <summary>
/// One entry of a source's location list (DESIGN 5): icon, path(s), the 預設 badge, the missing state, Show in File Explorer
/// and Remove with their accessible names.
/// </summary>
internal sealed class LocationRowViewModel : ObservableObject
{
    private readonly Action<LocationRowViewModel> _remove;
    private readonly Action<string> _reveal;
    private readonly Action<string> _copy;
    private bool _canRemove;
    private bool _isLeaving;

    public LocationRowViewModel(LocationEntry entry, Localizer loc, bool isNew, Action<LocationRowViewModel> remove, Action<string> reveal,
        Action<string> copy)
    {
        _remove = remove;
        _reveal = reveal;
        _copy = copy;
        Entry = entry;
        IsNew = isNew;
        var several = entry.Lines.Count > 1;
        Lines = [.. entry.Lines.Select((line, index) => new LocationLineViewModel(line, index == 0 && entry.IsAutomatic,
            several ? !line.Readable : !entry.Exists))];
        RevealCommand = new RelayCommand(() => _reveal(Entry.RevealPath!), () => Entry.RevealPath is not null && !IsLeaving);
        RemoveCommand = new RelayCommand(() => _remove(this), () => CanRemove && !IsLeaving);
        CopyCommand = new RelayCommand(() => _copy(string.Join(Environment.NewLine, Entry.Lines.Select(l => l.Path))));
        Relocalize(loc);
    }

    public LocationEntry Entry { get; }

    public DataLocation Location => Entry.Location;

    public IReadOnlyList<LocationLineViewModel> Lines { get; }

    public bool IsMissing => !Entry.Exists;

    public bool IsDefault => Entry.IsAutomatic;

    /// <summary>Added after the list was first shown: the row fades in (not with reduce motion), once.</summary>
    public bool IsNew { get; private set; }

    public ImageSource? Icon => Entry.IsFile ? ShellIcons.Document : ShellIcons.Folder;

    /// <summary>Segoe Fluent Icons glyph when the shell icon is unavailable.</summary>
    public string FallbackGlyph => Entry.IsFile ? "" : "";

    public bool HasIcon => Icon is not null;

    public bool CanRemove
    {
        get => _canRemove;
        set
        {
            if (Set(ref _canRemove, value))
            {
                OnPropertyChanged(nameof(RemoveTooltip));
            }
        }
    }

    /// <summary>Removing or resetting: the row fades out before the list changes.</summary>
    public bool IsLeaving { get => _isLeaving; set => Set(ref _isLeaving, value); }

    public RelayCommand RevealCommand { get; }

    public RelayCommand RemoveCommand { get; }

    public RelayCommand CopyCommand { get; }

    public string RevealText { get; private set; } = string.Empty;

    public string RemoveText { get; private set; } = string.Empty;

    public string CopyText { get; private set; } = string.Empty;

    public string RemoveTooltip => CanRemove ? RemoveText : KeepOneText;

    /// <summary><c>settings.sources.removeLocation</c> with the path (DESIGN 5).</summary>
    public string RemoveName { get; private set; } = string.Empty;

    public string RevealName { get; private set; } = string.Empty;

    public string MissingText { get; private set; } = string.Empty;

    public string BadgeText { get; private set; } = string.Empty;

    /// <summary>The path column as one element: the folder, valued with its paths, then 預設, then the missing state.</summary>
    public string AccessibleName { get; private set; } = string.Empty;

    private string KeepOneText { get; set; } = string.Empty;

    /// <summary>The row has been shown: a view built again later shows it without fading in.</summary>
    public void Settle() => IsNew = false;

    public void Relocalize(Localizer loc)
    {
        var first = Lines.Count > 0 ? Lines[0].Display : string.Empty;
        RevealText = loc["win.settings.sources.reveal"];
        RemoveText = loc["settings.sources.remove"];
        CopyText = loc["win.settings.sources.copyPath"];
        KeepOneText = loc["settings.sources.keepOne"];
        RemoveName = loc.Format("settings.sources.removeLocation", first);
        RevealName = $"{RevealText}, {first}";
        MissingText = loc["source.folderMissing"];
        BadgeText = loc["settings.sources.default"];
        var parts = Lines.Select(l => l.Display).ToList();
        if (IsDefault)
        {
            parts.Add(BadgeText);
        }

        if (IsMissing)
        {
            parts.Add(MissingText);
        }

        AccessibleName = $"{loc["win.settings.sources.folder"]}: {string.Join(", ", parts)}";
        OnPropertyChanged(string.Empty);
    }
}
