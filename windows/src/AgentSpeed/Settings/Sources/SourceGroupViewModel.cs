using System.Collections.ObjectModel;
using AgentSpeed.Controls;
using AgentSpeed.Core.Locations;
using AgentSpeed.Presentation.Localization;
using AgentSpeed.Presentation.Settings;
using AgentSpeed.Presentation.Settings.Locations;

namespace AgentSpeed.Settings.Sources;

/// <summary>
/// One group of Settings → 資料來源 (DESIGN 5): the toggles of the sources that share a location list, one row per entry of the
/// list, Add Folder… and Reset, and the group's caption.
/// </summary>
internal sealed class SourceGroupViewModel : ObservableObject
{
    private readonly SourcesPaneViewModel _pane;
    private Localizer _loc;
    private bool _canAdd;
    private bool _canReset;
    private bool _shown;

    public SourceGroupViewModel(LocationFolder folder, IReadOnlyList<SourceSettingsRow> toggles, SourcesPaneViewModel pane, Localizer loc,
        IReadOnlyList<QuotaRowViewModel>? quota = null)
    {
        Folder = folder;
        Toggles = toggles;
        QuotaToggles = quota ?? [];
        _pane = pane;
        _loc = loc;
        AddCommand = new RelayCommand(() => _pane.Add(this), () => CanAdd);
        ResetCommand = new RelayCommand(() => _pane.Reset(this), () => CanReset);
    }

    public LocationFolder Folder { get; }

    public IReadOnlyList<SourceSettingsRow> Toggles { get; }

    /// <summary>The group's quota switches after its source toggles (DESIGN 5, 額度).</summary>
    public IReadOnlyList<QuotaRowViewModel> QuotaToggles { get; }

    public bool HasQuota => QuotaToggles.Count > 0;

    public ObservableCollection<LocationRowViewModel> Entries { get; } = [];

    public RelayCommand AddCommand { get; }

    public RelayCommand ResetCommand { get; }

    public bool CanAdd { get => _canAdd; private set => Set(ref _canAdd, value); }

    public bool CanReset { get => _canReset; private set => Set(ref _canReset, value); }

    /// <summary><c>settings.sources.limit</c>: beside the actions and as Add's tooltip while the list is full.</summary>
    public string LimitText => LocationEntries.LimitText(_loc);

    public string? AddTooltip => CanAdd ? null : LimitText;

    public string Footnote => LocationEntries.FootnoteKey(Folder) is { } key ? _loc[key] : string.Empty;

    public bool HasFootnote => Footnote.Length > 0;

    /// <summary>The group's accessible name: the sources it reads.</summary>
    public string GroupName => string.Join(", ", Toggles.Select(t => t.Name));

    public string AddText => _loc["settings.sources.add"];

    public string ResetText => _loc["settings.sources.reset"];

    /// <summary>Rebuilds the rows from the lists: rows of locations still listed are kept, new ones fade in, gone ones leave.</summary>
    public void Sync(LocationLists lists, bool reduceMotion)
    {
        var entries = LocationEntries.Of(Folder, lists);
        var known = Entries.ToDictionary(row => row.Location);
        var rows = entries.Select(entry => known.TryGetValue(entry.Location, out var row) && Same(row.Entry, entry)
            ? row
            : _pane.NewRow(entry, isNew: _shown && !reduceMotion && !known.ContainsKey(entry.Location))).ToList();
        for (var i = 0; i < rows.Count; i++)
        {
            var at = Entries.IndexOf(rows[i]);
            if (at == i)
            {
                continue;
            }

            if (at > i)
            {
                Entries.Move(at, i);
            }
            else
            {
                Entries.Insert(i, rows[i]);
            }
        }

        while (Entries.Count > rows.Count)
        {
            Entries.RemoveAt(Entries.Count - 1);
        }

        foreach (var row in Entries)
        {
            row.CanRemove = lists.CanRemove(Folder);
            row.IsLeaving = false;
            if (known.TryGetValue(row.Location, out var before) && ReferenceEquals(before, row))
            {
                row.Settle();
            }
        }

        _shown = true;
        CanAdd = lists.CanAdd(Folder);
        CanReset = lists.CanReset(Folder);
        OnPropertyChanged(nameof(AddTooltip));
    }

    private static bool Same(LocationEntry a, LocationEntry b) =>
        a.Location == b.Location && a.Exists == b.Exists && a.IsFile == b.IsFile && a.RevealPath == b.RevealPath && a.Lines.SequenceEqual(b.Lines);

    public void Relocalize(Localizer loc)
    {
        _loc = loc;
        foreach (var row in Entries)
        {
            row.Relocalize(loc);
        }

        foreach (var toggle in QuotaToggles)
        {
            toggle.Relocalize(loc);
        }

        OnPropertyChanged(string.Empty);
    }
}
