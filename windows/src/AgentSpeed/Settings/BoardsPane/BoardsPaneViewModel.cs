using System.Collections.ObjectModel;
using System.Windows.Input;
using AgentSpeed.Controls;
using AgentSpeed.Core;
using AgentSpeed.Presentation.Boards;
using AgentSpeed.Presentation.Formatting;
using AgentSpeed.Presentation.Localization;
using AgentSpeed.Presentation.Menus;
using AgentSpeed.Presentation.Settings;
using AgentSpeed.Presentation.Settings.Boards;
using AgentSpeed.Presentation.Settings.Orbs;
using AgentSpeed.Remote;
using AgentSpeed.Services;
using AgentSpeed.Settings.Remote;
using AgentSpeed.Settings.OrbsPane;

namespace AgentSpeed.Settings.BoardsPane;

/// <summary>One row of the Boards list: style thumbnail, style name, sources and size, the visible switch.</summary>
internal sealed class BoardRowViewModel(string id) : ObservableObject
{
    private string _name = string.Empty;
    private string _detail = string.Empty;
    private string _accessibleName = string.Empty;
    private bool _shown = true;
    private BoardStyle _style;
    private BoardReadouts _readouts = BoardReadouts.All;

    public string Id { get; } = id;

    public string Name { get => _name; set => Set(ref _name, value); }

    public string Detail { get => _detail; set => Set(ref _detail, value); }

    public string AccessibleName { get => _accessibleName; set => Set(ref _accessibleName, value); }

    public bool IsShown { get => _shown; set => Set(ref _shown, value); }

    public BoardStyle Style { get => _style; set => Set(ref _style, value); }

    /// <summary>The live metrics switches the thumbnail shows.</summary>
    public BoardReadouts Readouts { get => _readouts; set => Set(ref _readouts, value); }
}

/// <summary>A style card of the board editor (<c>boards.style</c>).</summary>
internal sealed class BoardStyleCardViewModel(BoardStyle style) : ObservableObject
{
    private string _name = string.Empty;
    private bool _selected;

    public BoardStyle Style { get; } = style;

    public string Name { get => _name; set => Set(ref _name, value); }

    public bool IsSelected { get => _selected; set => Set(ref _selected, value); }
}

/// <summary>
/// Settings → 懸浮窗, Boards (DESIGN 2c Interaction and window): the list of boards under the orbs with add, remove and a
/// visible switch per board, and the editor of the selected board (sources, style, live metrics switches, size, opacity,
/// window toggles). Every
/// change applies live and persists.
/// </summary>
internal sealed class BoardsPaneViewModel : ObservableObject
{
    private readonly SettingsService _settings;
    private readonly SpeedModel _speed;
    private readonly Action<string?> _add;
    private readonly Action<string> _remove;
    private Localizer _loc;
    private BoardRowViewModel? _selected;
    private string? _selectNext;
    private bool _syncing;
    private bool _dark = true;
    private bool _reduceMotion;
    private RemoteHostsModel? _remote;

    /// <param name="add"><c>boards.add</c> next to the selected board (its id, or null).</param>
    /// <param name="remove">Removes a board after asking.</param>
    public BoardsPaneViewModel(SettingsService settings, SpeedModel speed, Localizer loc, Action<string?> add, Action<string> remove)
    {
        _settings = settings;
        _speed = speed;
        _loc = loc;
        _add = add;
        _remove = remove;
        foreach (var kind in Enum.GetValues<SourceKind>())
        {
            SourceRows.Add(new OrbSourceRowViewModel(kind));
        }

        foreach (var style in BoardStyles.All)
        {
            StyleCards.Add(new BoardStyleCardViewModel(style));
        }

        AddCommand = new RelayCommand(() =>
        {
            _selectNext = "#new";
            _add(SelectedId);
            _selectNext = null;
        }, () => S.Boards.CanAdd);
        RemoveCommand = new RelayCommand(() =>
        {
            if (SelectedId is { } id)
            {
                _remove(id);
            }
        }, () => _selected is not null);
        ToggleVisibleCommand = new RelayCommand(p =>
        {
            if (p is BoardRowViewModel row && S.Boards.Find(row.Id) is { } board)
            {
                Update(d => BoardEditing.SetVisible(d, row.Id, !board.Visible));
            }
        });
        SelectStyleCommand = new RelayCommand(p =>
        {
            if (p is BoardStyleCardViewModel card && SelectedId is { } id)
            {
                Update(d => BoardEditing.SetStyle(d, id, card.Style));
            }
        });
        HostPicker = new HostPickerViewModel(
            hosts =>
            {
                if (SelectedId is { } id)
                {
                    Update(d => BoardEditing.SetHosts(d, id, hosts));
                }
            },
            host =>
            {
                if (SelectedId is { } id && _remote is { } remote)
                {
                    Update(d => BoardEditing.ToggleHost(d, id, host, remote.Order));
                }
            });
        _settings.Changed += (_, _) => Sync();
        Sync();
    }

    /// <summary>The editor's Hosts section (DESIGN 13), shown while remote hosts exist.</summary>
    public HostPickerViewModel HostPicker { get; }

    /// <summary>The remote hosts (DESIGN 13); set by the app.</summary>
    public RemoteHostsModel? RemoteHosts
    {
        get => _remote;
        set
        {
            if (_remote is not null || value is null)
            {
                return;
            }

            _remote = value;
            value.Changed += (_, _) => RefreshHostPicker();
            Sync();
        }
    }

    public bool ReduceMotion
    {
        get => _reduceMotion;
        set
        {
            if (Set(ref _reduceMotion, value))
            {
                HostPicker.ReduceMotion = value;
            }
        }
    }

    private IReadOnlyList<OrbHostChoice> HostChoices => _remote?.Choices(_loc) ?? [];

    public ObservableCollection<BoardRowViewModel> Rows { get; } = [];

    public ObservableCollection<OrbSourceRowViewModel> SourceRows { get; } = [];

    public ObservableCollection<BoardStyleCardViewModel> StyleCards { get; } = [];

    public ICommand AddCommand { get; }

    public ICommand RemoveCommand { get; }

    public ICommand ToggleVisibleCommand { get; }

    public ICommand SelectStyleCommand { get; }

    /// <summary>The app's appearance: the thumbnails follow it.</summary>
    public bool DarkTheme { get => _dark; set => Set(ref _dark, value); }

    public bool HasBoards => Rows.Count > 0;

    public string CountText => BoardRowText.Count(S.Boards, _loc);

    public string AddTooltip => BoardRowText.AddTooltip(S.Boards, _loc);

    public BoardRowViewModel? SelectedRow
    {
        get => _selected;
        set
        {
            if (Set(ref _selected, value))
            {
                RefreshEditor();
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    public bool HasSelection => Board is not null;

    public string EditorTitle => Board is { } board ? BoardMenuModel.Title(board, _loc, HostChoices) : string.Empty;

    public bool AllSources
    {
        get => Board?.Sources.TracksAll ?? true;
        set
        {
            if (SelectedId is not { } id || _syncing)
            {
                return;
            }

            if (value)
            {
                Update(d => BoardEditing.SetAllSources(d, id));
            }
            else if (Board is { Sources.TracksAll: true })
            {
                // Unchecking "all" keeps every available source checked explicitly.
                var available = SourceAvailability.From(_speed.Snapshot.Sources).Available;
                var set = available.IsEmpty ? SourceSet.Everything : available;
                Update(d => BoardEditing.SetSources(d, id, OrbSources.Of(set)));
            }
        }
    }

    public bool SizeSmall { get => Board?.Size == BoardSize.S; set => SetSize(value, BoardSize.S); }

    public bool SizeMedium { get => Board?.Size == BoardSize.M; set => SetSize(value, BoardSize.M); }

    public bool SizeLarge { get => Board?.Size == BoardSize.L; set => SetSize(value, BoardSize.L); }

    public double OpacityPercent
    {
        get => (Board?.Opacity ?? 1) * 100;
        set
        {
            if (SelectedId is { } id && !_syncing)
            {
                Update(d => BoardEditing.SetOpacity(d, id, value / 100));
            }
        }
    }

    public string OpacityText => $"{OpacityPercent:0}%";

    public bool AlwaysOnTop
    {
        get => Board?.AlwaysOnTop ?? true;
        set
        {
            if (SelectedId is { } id && !_syncing)
            {
                Update(d => BoardEditing.SetAlwaysOnTop(d, id, value));
            }
        }
    }

    public bool ClickThrough
    {
        get => Board?.ClickThrough ?? false;
        set
        {
            if (SelectedId is { } id && !_syncing)
            {
                Update(d => BoardEditing.SetClickThrough(d, id, value));
            }
        }
    }

    public string ClickThroughHint => _loc["win.settings.clickThrough.description"];

    public bool FadeWhenIdle
    {
        get => Board?.FadeWhenIdle ?? false;
        set
        {
            if (SelectedId is { } id && !_syncing)
            {
                Update(d => BoardEditing.SetFadeWhenIdle(d, id, value));
            }
        }
    }

    public string FadeHint => _loc["win.settings.fadeIdle.description"];

    /// <summary>The selected board's live metrics switches (the style cards' thumbnails show them).</summary>
    public BoardReadouts Readouts => Board?.Readouts ?? BoardReadouts.All;

    public bool ReadoutIntelligence { get => Readouts.Intelligence; set => SetReadout(BoardReadout.Intelligence, value); }

    public bool ReadoutThinking { get => Readouts.Thinking; set => SetReadout(BoardReadout.Thinking, value); }

    public bool ReadoutCost { get => Readouts.Cost; set => SetReadout(BoardReadout.Cost, value); }

    private AppSettings S => _settings.Current;

    private string? SelectedId => _selected?.Id;

    private BoardDefinition? Board => S.Boards.Find(SelectedId);

    public void Relocalize(Localizer loc)
    {
        _loc = loc;
        Sync();
    }

    /// <summary>Selects a board (the board menu's <c>boards.settings</c>).</summary>
    public void Select(string? boardId)
    {
        if (boardId is not null && Rows.FirstOrDefault(r => r.Id == boardId) is { } row)
        {
            SelectedRow = row;
        }
    }

    public void ToggleSource(OrbSourceRowViewModel row)
    {
        if (SelectedId is { } id && !_syncing)
        {
            Update(d => BoardEditing.ToggleSource(d, id, row.Kind));
        }
    }

    private void SetReadout(BoardReadout readout, bool on)
    {
        if (SelectedId is { } id && !_syncing)
        {
            Update(d => BoardEditing.SetReadout(d, id, readout, on));
        }
    }

    private void SetSize(bool on, BoardSize size)
    {
        if (on && SelectedId is { } id && !_syncing)
        {
            Update(d => BoardEditing.SetSize(d, id, size));
        }
    }

    private void Update(Func<BoardsDocument, BoardsDocument?> change) =>
        _settings.Update(s => change(s.Boards) is { } next ? s with { Boards = next } : s);

    private void Sync()
    {
        _syncing = true;
        try
        {
            var document = S.Boards;
            var ids = document.Boards.Select(b => b.Id).ToList();
            for (var i = Rows.Count - 1; i >= 0; i--)
            {
                if (!ids.Contains(Rows[i].Id))
                {
                    Rows.RemoveAt(i);
                }
            }

            BoardRowViewModel? added = null;
            for (var i = 0; i < document.Boards.Length; i++)
            {
                var board = document.Boards[i];
                var row = Rows.FirstOrDefault(r => r.Id == board.Id);
                if (row is null)
                {
                    row = new BoardRowViewModel(board.Id);
                    Rows.Insert(Math.Min(i, Rows.Count), row);
                    added = row;
                }
                else if (Rows.IndexOf(row) != i)
                {
                    Rows.Move(Rows.IndexOf(row), i);
                }

                row.Name = BoardRowText.Name(board, _loc);
                row.Detail = BoardRowText.Detail(board, _loc, HostChoices);
                row.AccessibleName = BoardRowText.Accessible(board, _loc, HostChoices);
                row.IsShown = board.Visible;
                row.Style = board.Style;
                row.Readouts = board.Readouts;
            }

            if (added is not null && _selectNext is not null)
            {
                _selected = added;
                _selectNext = null;
            }
            else if (_selected is null || !ids.Contains(_selected.Id))
            {
                _selected = Rows.FirstOrDefault();
            }

            foreach (var card in StyleCards)
            {
                card.Name = BoardMenuModel.StyleName(card.Style, _loc);
            }

            RefreshEditor();
            OnPropertyChanged(string.Empty);
            CommandManager.InvalidateRequerySuggested();
        }
        finally
        {
            _syncing = false;
        }
    }

    private void RefreshEditor()
    {
        var board = Board;
        foreach (var card in StyleCards)
        {
            card.IsSelected = board?.Style == card.Style;
        }

        var all = board?.Sources.TracksAll ?? true;
        var snapshot = _speed.Snapshot;
        var wasSyncing = _syncing;
        _syncing = true;
        try
        {
            foreach (var row in SourceRows)
            {
                row.Name = _loc[DisplayText.SourceKey(row.Kind)];
                var status = snapshot.Sources.FirstOrDefault(s => s.Kind == row.Kind);
                row.Status = status is null ? string.Empty
                    : !status.Enabled ? _loc["source.disabled"]
                    : OrbSubset.IsUnavailable(status) ? _loc["source.notFound"]
                    : string.Empty;
                row.IsChecked = all || board?.Sources.Known.Contains(row.Kind) == true;
                var lastOne = board is not null && !all && row.IsChecked && !BoardEditing.CanUncheck(board, row.Kind);
                row.IsEnabled = !all && !lastOne;
                row.Tooltip = lastOne ? _loc["orbs.sources.lastOne"] : null;
            }
        }
        finally
        {
            _syncing = wasSyncing;
        }

        RefreshHostPicker();
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(EditorTitle));
        OnPropertyChanged(nameof(AllSources));
        OnPropertyChanged(nameof(SizeSmall));
        OnPropertyChanged(nameof(SizeMedium));
        OnPropertyChanged(nameof(SizeLarge));
        OnPropertyChanged(nameof(OpacityPercent));
        OnPropertyChanged(nameof(OpacityText));
        OnPropertyChanged(nameof(AlwaysOnTop));
        OnPropertyChanged(nameof(ClickThrough));
        OnPropertyChanged(nameof(FadeWhenIdle));
        OnPropertyChanged(nameof(Readouts));
        OnPropertyChanged(nameof(ReadoutIntelligence));
        OnPropertyChanged(nameof(ReadoutThinking));
        OnPropertyChanged(nameof(ReadoutCost));
    }

    private void RefreshHostPicker()
    {
        if (_remote is { } remote)
        {
            HostPicker.Refresh(Board?.Hosts ?? OrbHosts.All, remote, _loc);
        }
    }
}
