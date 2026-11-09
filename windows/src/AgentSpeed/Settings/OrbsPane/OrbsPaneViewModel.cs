using System.Collections.ObjectModel;
using System.Windows.Input;
using System.Windows.Media;
using AgentSpeed.Controls;
using AgentSpeed.Core;
using AgentSpeed.Orb;
using AgentSpeed.Presentation.Colors;
using AgentSpeed.Presentation.Formatting;
using AgentSpeed.Presentation.Heat;
using AgentSpeed.Presentation.Localization;
using AgentSpeed.Presentation.Orb;
using AgentSpeed.Presentation.Orb.Names;
using AgentSpeed.Presentation.Settings;
using AgentSpeed.Presentation.Settings.Orbs;
using AgentSpeed.Presentation.Titles;
using AgentSpeed.Remote;
using AgentSpeed.Services;
using AgentSpeed.Settings.Remote;
using AgentSpeed.Theme;
using AgentSpeed.Titles;

namespace AgentSpeed.Settings.OrbsPane;

/// <summary>One row of the orb list (DESIGN 5, Floating orb pane, item 2).</summary>
internal sealed class OrbRowViewModel(string id) : ObservableObject
{
    private string _name = string.Empty;
    private string _detail = string.Empty;
    private string _value = string.Empty;
    private bool _visible = true;
    private OrbDefinition? _definition;
    private double _live;
    private SpeedTier _tier;
    private SpeedTitle _title;
    private bool _unavailable;

    public string Id { get; } = id;

    public string Name { get => _name; set => Set(ref _name, value); }

    public string Detail { get => _detail; set => Set(ref _detail, value); }

    /// <summary>The orb's live value (<c>128 tok/s</c>), <c>–</c> when unavailable, <c>orbs.hidden</c> when hidden.</summary>
    public string Value { get => _value; set => Set(ref _value, value); }

    public bool IsShown { get => _visible; set => Set(ref _visible, value); }

    public OrbDefinition? Definition { get => _definition; set => Set(ref _definition, value); }

    public double Live { get => _live; set => Set(ref _live, value); }

    public SpeedTier Tier { get => _tier; set => Set(ref _tier, value); }

    public SpeedTitle Title { get => _title; set => Set(ref _title, value); }

    public bool Unavailable { get => _unavailable; set => Set(ref _unavailable, value); }
}

/// <summary>A source row of the editor (checkbox, colour dot, name, status).</summary>
internal sealed class OrbSourceRowViewModel(SourceKind kind) : ObservableObject
{
    private bool _checked;
    private bool _enabled = true;
    private string _name = string.Empty;
    private string _status = string.Empty;
    private string? _tooltip;

    public SourceKind Kind { get; } = kind;

    public Brush Dot { get; } = MediaColors.Frozen(MediaColors.ToColor(HeatPalette.SourceColor(kind)));

    public bool IsChecked { get => _checked; set => Set(ref _checked, value); }

    public bool IsEnabled { get => _enabled; set => Set(ref _enabled, value); }

    public string Name { get => _name; set => Set(ref _name, value); }

    public string Status { get => _status; set => Set(ref _status, value); }

    public string? Tooltip { get => _tooltip; set => Set(ref _tooltip, value); }
}

/// <summary>A style card of the editor (DESIGN 5, item 3, `orbs.style`).</summary>
internal sealed class OrbStyleCardViewModel(OrbStyle style, int index) : ObservableObject
{
    private string _name = string.Empty;
    private bool _selected;
    private string _accessibleName = string.Empty;

    public OrbStyle Style { get; } = style;

    /// <summary>Card order: each card's preview loop starts 1.6 s after the previous one.</summary>
    public int Index { get; } = index;

    public string Name { get => _name; set => Set(ref _name, value); }

    public bool IsSelected { get => _selected; set => Set(ref _selected, value); }

    public string AccessibleName { get => _accessibleName; set => Set(ref _accessibleName, value); }
}

/// <summary>An entry of the Advanced <c>titles.theme</c> popup: General, a preset, a custom set, or a separator.</summary>
/// <param name="Id">The theme id stored on the orb; null for <c>orbs.titleTheme.global</c>.</param>
internal sealed record OrbTitleThemeChoice(string? Id, string Text, bool IsSeparator = false);

/// <summary>
/// Settings → 懸浮窗 (DESIGN 5, Floating orb pane): master switch, the orb list with live thumbnails, add / remove /
/// duplicate / split / merge with undo, and the editor of the selected orb. Every change applies live and persists.
/// </summary>
internal sealed partial class OrbsPaneViewModel : ObservableObject
{
    private readonly SettingsService _settings;
    private readonly OrbManager _manager;
    private readonly SpeedModel _speed;
    private Localizer _loc;
    private OrbRowViewModel? _selected;
    private bool _syncing;
    private bool _advancedExpanded;
    private string _nameDraft = string.Empty;
    private bool _dark = true;
    private bool _reduceMotion;
    private TitleThemesService? _titles;
    private IReadOnlyList<string> _titleLabels = [];
    private IReadOnlyList<OrbTitleThemeChoice> _titleChoices = [];
    private RemoteHostsModel? _remote;

    public OrbsPaneViewModel(SettingsService settings, OrbManager manager, SpeedModel speed, Localizer loc)
    {
        _settings = settings;
        _manager = manager;
        _speed = speed;
        _loc = loc;
        foreach (var kind in Enum.GetValues<SourceKind>())
        {
            SourceRows.Add(new OrbSourceRowViewModel(kind));
        }

        var index = 0;
        foreach (var style in OrbStyles.All)
        {
            StyleCards.Add(new OrbStyleCardViewModel(style, index++));
        }

        for (var i = 0; i < SpeedTitles.Scaled.Count; i++)
        {
            TitleLadder.Add(new TitleLadderRow(SpeedTitles.Scaled[i].Title, i));
        }

        AddCommand = new RelayCommand(() => _manager.Add(SelectedId), () => S.Orbs.CanAdd);
        RemoveCommand = new RelayCommand(RemoveSelected, () => S.Orbs.CanRemove && _selected is not null);
        DuplicateCommand = new RelayCommand(() =>
        {
            if (SelectedId is { } id)
            {
                _manager.Duplicate(id);
            }
        }, () => S.Orbs.CanAdd && _selected is not null);
        SplitCommand = new RelayCommand(() => _manager.SplitBySource(SelectedId), () => OrbEditing.CanSplitBySource(_manager.AvailableSources()));
        MergeCommand = new RelayCommand(() =>
        {
            if (SelectedId is { } id)
            {
                _manager.Merge(id);
            }
        }, () => S.Orbs.CanMerge && _selected is not null);
        UndoCommand = new RelayCommand(() => _manager.Undo(), () => _manager.PendingUndo is not null);
        ToggleVisibleCommand = new RelayCommand(p =>
        {
            if (p is OrbRowViewModel row && S.Orbs.Find(row.Id) is { } orb)
            {
                Update(d => OrbEditing.SetVisible(d, row.Id, !orb.Visible), showOrbsIfVisible: !orb.Visible);
            }
        });
        SelectStyleCommand = new RelayCommand(p =>
        {
            if (p is OrbStyleCardViewModel card && SelectedId is { } id)
            {
                Update(d => OrbEditing.SetStyle(d, id, card.Style));
            }
        });
        SizePresetCommand = new RelayCommand(p =>
        {
            if (p is string text && double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture,
                    out var size) && SelectedId is { } id)
            {
                Update(d => OrbEditing.SetSize(d, id, size));
            }
        });
        ResetPositionCommand = new RelayCommand(() =>
        {
            if (SelectedId is { } id)
            {
                _manager.ResetPosition(id);
            }
        });
        ToggleAdvancedCommand = new RelayCommand(() => AdvancedExpanded = !AdvancedExpanded);
        HostPicker = new HostPickerViewModel(
            hosts =>
            {
                if (SelectedId is { } id)
                {
                    Update(d => OrbEditing.SetHosts(d, id, hosts));
                }
            },
            host =>
            {
                if (SelectedId is { } id && _remote is { } remote)
                {
                    Update(d => OrbEditing.ToggleHost(d, id, host, remote.Order));
                }
            });

        _settings.Changed += (_, _) => Sync();
        _manager.UndoChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(UndoText));
            OnPropertyChanged(nameof(HasUndo));
            CommandManager.InvalidateRequerySuggested();
        };
        Sync();
    }

    public ObservableCollection<OrbRowViewModel> Rows { get; } = [];

    public ObservableCollection<OrbSourceRowViewModel> SourceRows { get; } = [];

    public ObservableCollection<OrbStyleCardViewModel> StyleCards { get; } = [];

    /// <summary>The editor's Hosts section (DESIGN 13), shown while remote hosts exist.</summary>
    public HostPickerViewModel HostPicker { get; }

    /// <summary>The remote hosts (DESIGN 13); set by the app. Names gain their hosts and the Hosts section appears with the first host.</summary>
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

    /// <summary>This computer and the remote hosts for the automatic names; empty without remote hosts.</summary>
    private IReadOnlyList<OrbHostChoice> HostChoices => _remote?.Choices(_loc) ?? [];

    /// <summary>The custom redline's title list, in the selected orb's theme (DESIGN 1, Where the theme applies).</summary>
    public ObservableCollection<TitleLadderRow> TitleLadder { get; } = [];

    /// <summary>Heat positions of the title ticks under the custom redline slider.</summary>
    public IReadOnlyList<double> TitleTicks { get; } = SpeedTitles.Scaled.Select(step => SpeedTitles.HeatAt(step.Title, 1)).ToArray();

    /// <summary>The title themes (DESIGN 1); set by the app. Names the previews and the list in the selected orb's theme.</summary>
    public TitleThemesService? TitleService
    {
        get => _titles;
        set
        {
            if (_titles is not null || value is null)
            {
                return;
            }

            _titles = value;
            value.Changed += (_, _) => RefreshTitles();
            RefreshTitles();
        }
    }

    /// <summary>
    /// The Advanced <c>titles.theme</c> popup: <c>orbs.titleTheme.global</c> with the General theme's name, a separator,
    /// the nine presets, then (after a separator) the custom sets.
    /// </summary>
    public IReadOnlyList<OrbTitleThemeChoice> TitleThemeChoices
    {
        get => _titleChoices;
        private set
        {
            if (!_titleChoices.SequenceEqual(value))
            {
                _titleChoices = value;
                OnPropertyChanged();
            }
        }
    }

    /// <summary>The selected orb's entry in <see cref="TitleThemeChoices"/>; an unknown theme reads as General.</summary>
    public int TitleThemeIndex
    {
        get
        {
            var id = Orb?.TitleTheme;
            var known = id is not null && _titles?.Document.Theme(id) is not null;
            for (var i = 0; i < _titleChoices.Count; i++)
            {
                if (!_titleChoices[i].IsSeparator && (known ? _titleChoices[i].Id == id : _titleChoices[i].Id is null))
                {
                    return i;
                }
            }

            return 0;
        }
        set
        {
            if (_syncing || SelectedId is not { } id || value < 0 || value >= _titleChoices.Count || _titleChoices[value].IsSeparator)
            {
                return;
            }

            var themeId = _titleChoices[value].Id;
            if (Orb?.TitleTheme != themeId)
            {
                Update(d => OrbEditing.SetTitleTheme(d, id, themeId));
            }
        }
    }

    public string TitleThemeHint => _loc["orbs.titleTheme.hint"];

    /// <summary>The selected orb's titles as its readout draws them (the style preview cards).</summary>
    public IReadOnlyList<string> OrbTitleLabels
    {
        get => _titleLabels;
        private set
        {
            if (!_titleLabels.SequenceEqual(value, StringComparer.Ordinal))
            {
                _titleLabels = value;
                OnPropertyChanged();
            }
        }
    }

    public ICommand AddCommand { get; }

    public ICommand RemoveCommand { get; }

    public ICommand DuplicateCommand { get; }

    public ICommand SplitCommand { get; }

    public ICommand MergeCommand { get; }

    public ICommand UndoCommand { get; }

    public ICommand ToggleVisibleCommand { get; }

    public ICommand SelectStyleCommand { get; }

    public ICommand SizePresetCommand { get; }

    public ICommand ResetPositionCommand { get; }

    public ICommand ToggleAdvancedCommand { get; }

    public bool DarkTheme
    {
        get => _dark;
        set
        {
            if (Set(ref _dark, value))
            {
                Sync();
            }
        }
    }

    public bool ReduceMotion { get => _reduceMotion; set => Set(ref _reduceMotion, value); }

    private AppSettings S => _settings.Current;

    private string? SelectedId => _selected?.Id;

    private OrbDefinition? Orb => SelectedId is { } id ? S.Orbs.Find(id) : null;

    public bool ShowOrbs
    {
        get => S.ShowOrbs;
        set
        {
            if (!_syncing)
            {
                _settings.Update(s => s.WithShowOrbs(value));
            }
        }
    }

    public string AllNote => _loc.Format("orbs.allNote", _loc["settings.taskbar"]);

    /// <summary>Master group <c>orbs.names</c>: 0 automatic, 1 always, 2 never (DESIGN 2b, Name plate).</summary>
    public int OrbNamesIndex
    {
        get => S.OrbNames switch
        {
            OrbNamesMode.Always => 1,
            OrbNamesMode.Never => 2,
            _ => 0,
        };
        set
        {
            var mode = value switch
            {
                1 => OrbNamesMode.Always,
                2 => OrbNamesMode.Never,
                _ => OrbNamesMode.Auto,
            };
            if (!_syncing && value is >= 0 and <= 2 && mode != S.OrbNames)
            {
                _settings.Update(s => s with { OrbNames = mode });
            }
        }
    }

    public string OrbNamesHint => _loc["orbs.names.hint"];

    public string CountText => _loc.Format("orbs.count", S.Orbs.Count, OrbsDocument.MaxOrbs);

    public string AddTooltip => S.Orbs.CanAdd ? _loc["orbs.add"] : _loc.Format("orbs.limit", OrbsDocument.MaxOrbs);

    public string SplitTooltip => OrbEditing.CanSplitBySource(_manager.AvailableSources()) ? _loc["orbs.split.hint"] : _loc["orbs.split.unavailable"];

    public string MergeTooltip => _loc["orbs.merge.hint"];

    public bool HasUndo => _manager.PendingUndo is not null;

    public string UndoText => _manager.PendingUndo?.Text ?? string.Empty;

    public OrbRowViewModel? SelectedRow
    {
        get => _selected;
        set
        {
            if (Set(ref _selected, value))
            {
                _nameDraft = Orb?.Name ?? string.Empty;
                RefreshEditor();
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    public bool HasSelection => _selected is not null && Orb is not null;

    public string EditorTitle => Orb is { } orb ? OrbNaming.DisplayName(orb, _loc, HostChoices) : string.Empty;

    public IReadOnlyList<Brush> EditorDots => Orb?.TrackedSources is { } set
        ? set.Select(k => (Brush)MediaColors.Frozen(MediaColors.ToColor(HeatPalette.SourceColor(k)))).ToArray()
        : [];

    public bool AllSources
    {
        get => Orb?.TrackedSources is null;
        set
        {
            if (SelectedId is not { } id || _syncing)
            {
                return;
            }

            if (value)
            {
                Update(d => OrbEditing.SetAllSources(d, id));
            }
            else if (Orb is { } orb && orb.TrackedSources is null)
            {
                // Unchecking "all" keeps every available source checked explicitly.
                var available = _manager.AvailableSources();
                var set = available.IsEmpty ? SourceSet.Everything : available;
                Update(d => OrbEditing.SetSources(d, id, OrbSources.Of(set)));
            }
        }
    }

    public string SelectedStyleName => Orb is { } orb ? _loc["style." + OrbStyles.Key(orb.Style)] : string.Empty;

    public string SelectedStyleDescription => Orb is { } orb ? _loc["style." + OrbStyles.Key(orb.Style) + ".desc"] : string.Empty;

    public double SizeMinimum => OrbStyles.MinSize;

    public double SizeValue
    {
        get => Orb?.Size ?? OrbDefinition.DefaultSize;
        set
        {
            if (SelectedId is { } id)
            {
                Update(d => OrbEditing.SetSize(d, id, value));
            }
        }
    }

    public string SizeText => $"{SizeValue:0} pt";

    public int SizePresetIndex => Orb is { } orb ? OrbMenuPreset(orb.Size) : -1;

    /// <summary>The orb's unit (DESIGN 9 Orb, Switching 3): the two-segment control under the style cards.</summary>
    public bool UnitTokens { get => Orb?.Unit != OrbUnit.Cost; set => SetUnit(value, OrbUnit.Tokens); }

    public bool UnitCost { get => Orb?.Unit == OrbUnit.Cost; set => SetUnit(value, OrbUnit.Cost); }

    /// <summary><c>US$/s</c> in the display currency.</summary>
    public string UnitCostLabel => _loc.Format("cost.unitPerSecond",
        Core.Money.CurrencyTable.Bundled.CurrencyOrUsd(S.Cost.Currency).SymbolFor(_loc.LanguageCode));

    private void SetUnit(bool selected, OrbUnit unit)
    {
        if (selected && SelectedId is { } id && !_syncing)
        {
            Update(d => OrbEditing.SetUnit(d, id, unit));
        }
    }

    /// <summary>What the orb's two-line readout shows (DESIGN 10): the popup under the unit control, in <see cref="OrbTraffic"/> order.</summary>
    public int TrafficIndex
    {
        get => (int)(Orb?.Traffic ?? OrbTraffic.Sources);
        set
        {
            if (SelectedId is { } id && !_syncing && Enum.IsDefined((OrbTraffic)value))
            {
                Update(d => OrbEditing.SetTraffic(d, id, (OrbTraffic)value));
            }
        }
    }

    /// <summary><c>orbs.traffic.hint</c>, or <c>orbs.traffic.hint.intel</c> while the intelligence readout is chosen.</summary>
    public string TrafficHint => _loc[Orb?.Traffic == OrbTraffic.Intelligence ? "orbs.traffic.hint.intel" : "orbs.traffic.hint"];

    public bool SizeSmall { get => SizePresetIndex == 0; set => SetPreset(value, 0); }

    public bool SizeMedium { get => SizePresetIndex == 1; set => SetPreset(value, 1); }

    public bool SizeLarge { get => SizePresetIndex == 2; set => SetPreset(value, 2); }

    private void SetPreset(bool selected, int index)
    {
        if (selected && SelectedId is { } id && !_syncing)
        {
            Update(d => OrbEditing.SetSize(d, id, AppSettings.OrbSizePresets[index]));
        }
    }

    public double OpacityPercent
    {
        get => Math.Round((Orb?.Opacity ?? 1) * 100);
        set
        {
            if (SelectedId is { } id)
            {
                Update(d => OrbEditing.SetOpacity(d, id, value / 100));
            }
        }
    }

    public string OpacityText => $"{OpacityPercent:0}%";

    /// <summary>
    /// The editor's <c>orbs.showName</c> popup: 0 automatic (with its current result), 1 the separator, 2 on, 3 off.
    /// Changes apply live; the desktop plate fades in or out.
    /// </summary>
    public int ShowNameIndex
    {
        get => Orb?.ShowName switch
        {
            true => 2,
            false => 3,
            _ => 0,
        };
        set
        {
            if (SelectedId is not { } id || _syncing || value is not (0 or 2 or 3))
            {
                return;
            }

            bool? show = value switch
            {
                2 => true,
                3 => false,
                _ => null,
            };
            if (Orb?.ShowName != show)
            {
                Update(d => OrbEditing.SetShowName(d, id, show));
            }
        }
    }

    /// <summary><c>orbs.showName.auto</c> filled with what the global setting does for this orb now.</summary>
    public string ShowNameAutoText =>
        _loc.Format("orbs.showName.auto", _loc[Orb is { } orb && NamePlateRules.AutomaticResult(orb, S) ? "orbs.showName.on" : "orbs.showName.off"]);

    public bool AlwaysOnTop
    {
        get => Orb?.AlwaysOnTop ?? true;
        set
        {
            if (SelectedId is { } id)
            {
                Update(d => OrbEditing.SetAlwaysOnTop(d, id, value));
            }
        }
    }

    public bool ClickThrough
    {
        get => Orb?.ClickThrough ?? false;
        set
        {
            if (SelectedId is { } id)
            {
                Update(d => OrbEditing.SetClickThrough(d, id, value));
            }
        }
    }

    public bool FadeWhenIdle
    {
        get => Orb?.FadeWhenIdle ?? false;
        set
        {
            if (SelectedId is { } id)
            {
                Update(d => OrbEditing.SetFadeWhenIdle(d, id, value));
            }
        }
    }

    public bool AdvancedExpanded { get => _advancedExpanded; set => Set(ref _advancedExpanded, value); }

    public string AdvancedSummary =>
        string.Join(_loc["list.separator"], _loc["orbs.name"], _loc["titles.theme"], _loc["settings.redline"]);

    public string NamePlaceholder => Orb is { } orb
        ? _loc.Format("orbs.name.auto", OrbNaming.AutomaticName(orb.Sources, _loc) + OrbNaming.HostSuffix(orb.Hosts, HostChoices, _loc))
        : string.Empty;

    /// <summary>The custom name as typed; empty is automatic. Applied on every change (trimmed, 1–32 characters).</summary>
    public string NameText
    {
        get => _nameDraft;
        set
        {
            _nameDraft = value ?? string.Empty;
            OnPropertyChanged();
            if (SelectedId is { } id)
            {
                var trimmed = _nameDraft.Trim();
                Update(d => OrbEditing.SetName(d, id, trimmed.Length == 0 ? null : trimmed));
            }
        }
    }

    public string RedlineGlobalText => _loc.Format("orbs.redline.global", $"{SpeedFormat.Speed(S.Redline)} {_loc["unit.tokS"]}");

    public string RedlineCustomText => _loc["orbs.redline.custom"];

    public bool RedlineCustom
    {
        get => Orb is { } orb && (S.Metric == SpeedMetric.Total ? orb.RedlineTotal : orb.RedlineOutput) is not null;
        set
        {
            if (SelectedId is not { } id || _syncing)
            {
                return;
            }

            Update(d => OrbEditing.SetRedline(d, id, S.Metric, value ? S.Redline : null));
        }
    }

    public bool RedlineGlobal
    {
        get => !RedlineCustom;
        set
        {
            if (value)
            {
                RedlineCustom = false;
            }
        }
    }

    public bool IsTotalMetric => S.Metric == SpeedMetric.Total;

    public bool IsOutputMetric => !IsTotalMetric;

    public double RedlineOutputValue
    {
        get => Orb?.RedlineOutput ?? S.RedlineOutput;
        set
        {
            if (SelectedId is { } id)
            {
                Update(d => OrbEditing.SetRedline(d, id, SpeedMetric.Output, value));
            }
        }
    }

    public double RedlineTotalMinimumLog => Math.Log10(HeatMath.MinRedlineTotal);

    public double RedlineTotalMaximumLog => Math.Log10(HeatMath.MaxRedlineTotal);

    public double RedlineTotalLog
    {
        get => Math.Log10(Orb?.RedlineTotal ?? S.RedlineTotal);
        set
        {
            if (SelectedId is { } id)
            {
                Update(d => OrbEditing.SetRedline(d, id, SpeedMetric.Total, Math.Pow(10, value)));
            }
        }
    }

    public string RedlineText => Orb is { } orb ? $"{SpeedFormat.Speed(orb.EffectiveRedline(S.Metric, S))} {_loc["unit.tokS"]}" : string.Empty;

    public string RedlineHint => _loc["orbs.redline.hint"];

    public string ClickThroughHint => _loc["win.settings.clickThrough.description"];

    public string FadeHint => _loc["win.settings.fadeIdle.description"];

    public void Relocalize(Localizer loc)
    {
        _loc = loc;
        Sync();
    }

    /// <summary>Selects an orb (from the orb's menu, `menu.orb.settings`).</summary>
    public void Select(string orbId)
    {
        if (Rows.FirstOrDefault(r => r.Id == orbId) is { } row)
        {
            SelectedRow = row;
        }
    }

    /// <summary>Rings the hovered, selected or focused row's orb on the desktop (null: none).</summary>
    public void Highlight(string? orbId) => _manager.Highlight(orbId);

    /// <summary>Live values for the row trailing text and the thumbnails (at most once per second).</summary>
    public void UpdateLive()
    {
        var feeds = _manager.Feeds;
        foreach (var row in Rows)
        {
            if (!row.IsShown || !S.ShowOrbs)
            {
                row.Value = _loc["orbs.hidden"];
                row.Live = 0;
                row.Tier = SpeedTier.Idle;
                row.Title = SpeedTitle.Idle;
                continue;
            }

            if (feeds.TryGetValue(row.Id, out var feed))
            {
                row.Unavailable = feed.IsUnavailable;
                row.Value = feed.IsUnavailable ? "–"
                    : feed.Money is { } money ? $"{money.NumberText} {money.Unit}"
                    : $"{SpeedFormat.Speed(feed.Value)} {_loc["unit.tokS"]}";
                row.Live = feed.Value;
                row.Tier = feed.Tier;
                row.Title = feed.Title;
            }
        }

        UpdateSourceStatuses();
    }

    /// <summary>Moves a row in the list (drag reorder; list order only).</summary>
    public void Move(string orbId, int index) => Update(d => OrbEditing.Move(d, orbId, index));

    public void ToggleVisibility(OrbRowViewModel row) => ToggleVisibleCommand.Execute(row);

    public void RemoveSelected()
    {
        if (SelectedId is { } id && S.Orbs.CanRemove)
        {
            var index = Rows.IndexOf(_selected!);
            _manager.Remove(id);
            if (Rows.Count > 0)
            {
                SelectedRow = Rows[Math.Clamp(index, 0, Rows.Count - 1)];
            }
        }
    }

    public void ToggleSource(OrbSourceRowViewModel row)
    {
        if (SelectedId is not { } id || _syncing)
        {
            return;
        }

        Update(d => OrbEditing.ToggleSource(d, id, row.Kind));
    }

    private void Update(Func<OrbsDocument, OrbsDocument?> change, bool showOrbsIfVisible = false) =>
        _settings.Update(s =>
        {
            if (change(s.Orbs) is not { } next)
            {
                return s;
            }

            var updated = s with { Orbs = next };
            return showOrbsIfVisible && !s.ShowOrbs ? updated.WithShowOrbs(true) : updated;
        });

    private static int OrbMenuPreset(double size)
    {
        var presets = AppSettings.OrbSizePresets;
        var best = -1;
        var distance = double.MaxValue;
        for (var i = 0; i < presets.Count; i++)
        {
            var d = Math.Abs(presets[i] - size);
            if (d < distance)
            {
                distance = d;
                best = i;
            }
        }

        return distance < 8 ? best : -1;
    }

    private void Sync()
    {
        _syncing = true;
        try
        {
            var document = S.Orbs;
            var ids = document.Orbs.Select(o => o.Id).ToList();
            for (var i = Rows.Count - 1; i >= 0; i--)
            {
                if (!ids.Contains(Rows[i].Id))
                {
                    Rows.RemoveAt(i);
                }
            }

            for (var i = 0; i < document.Orbs.Length; i++)
            {
                var orb = document.Orbs[i];
                var row = Rows.FirstOrDefault(r => r.Id == orb.Id);
                if (row is null)
                {
                    row = new OrbRowViewModel(orb.Id);
                    Rows.Insert(Math.Min(i, Rows.Count), row);
                }
                else if (Rows.IndexOf(row) != i)
                {
                    Rows.Move(Rows.IndexOf(row), i);
                }

                row.Definition = orb;
                row.Name = OrbNaming.DisplayName(orb, _loc, HostChoices);
                row.Detail = _loc.Format("orbs.row.detail", _loc["style." + OrbStyles.Key(orb.Style)], (int)Math.Round(orb.Size));
                row.IsShown = orb.Visible;
            }

            if (_selected is null || !ids.Contains(_selected.Id))
            {
                _selected = Rows.FirstOrDefault();
                _nameDraft = Orb?.Name ?? string.Empty;
            }

            foreach (var card in StyleCards)
            {
                card.Name = _loc["style." + OrbStyles.Key(card.Style)];
                card.AccessibleName = _loc.Format("orbs.preview.a11y", card.Name);
            }

            RefreshEditor();
            UpdateLive();
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
        var orb = Orb;
        foreach (var card in StyleCards)
        {
            card.IsSelected = orb?.Style == card.Style;
        }

        UpdateSourceStatuses();
        RefreshHostPicker();
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(EditorTitle));
        OnPropertyChanged(nameof(EditorDots));
        OnPropertyChanged(nameof(AllSources));
        OnPropertyChanged(nameof(SelectedStyleName));
        OnPropertyChanged(nameof(SelectedStyleDescription));
        OnPropertyChanged(nameof(SizeValue));
        OnPropertyChanged(nameof(SizeText));
        OnPropertyChanged(nameof(SizePresetIndex));
        OnPropertyChanged(nameof(SizeSmall));
        OnPropertyChanged(nameof(SizeMedium));
        OnPropertyChanged(nameof(SizeLarge));
        OnPropertyChanged(nameof(OpacityPercent));
        OnPropertyChanged(nameof(OpacityText));
        OnPropertyChanged(nameof(UnitTokens));
        OnPropertyChanged(nameof(UnitCost));
        OnPropertyChanged(nameof(UnitCostLabel));
        OnPropertyChanged(nameof(TrafficIndex));
        OnPropertyChanged(nameof(TrafficHint));
        OnPropertyChanged(nameof(AlwaysOnTop));
        OnPropertyChanged(nameof(ClickThrough));
        OnPropertyChanged(nameof(FadeWhenIdle));
        OnPropertyChanged(nameof(ShowNameAutoText));
        OnPropertyChanged(nameof(ShowNameIndex));
        OnPropertyChanged(nameof(NamePlaceholder));
        OnPropertyChanged(nameof(NameText));
        OnPropertyChanged(nameof(RedlineCustom));
        OnPropertyChanged(nameof(RedlineGlobal));
        OnPropertyChanged(nameof(RedlineText));
        OnPropertyChanged(nameof(RedlineOutputValue));
        OnPropertyChanged(nameof(RedlineTotalLog));
        RefreshTitles();
        RefreshCharacterEditor();
    }

    /// <summary>The theme popup, the preview cards' labels and the custom redline's title list for the selected orb.</summary>
    private void RefreshTitles()
    {
        var titles = _titles;
        var orb = Orb;
        var theme = titles?.ForOrb(orb) ?? TitleTheme.Vehicles;
        if (titles is not null)
        {
            var document = titles.Document;
            List<OrbTitleThemeChoice> choices =
            [
                new(null, _loc.Format("orbs.titleTheme.global", titles.Name(titles.General))),
                new(null, string.Empty, true),
                .. TitlePresets.All.Select(preset => new OrbTitleThemeChoice(TitlePresets.Id(preset), _loc[TitlePresets.NameKey(preset)])),
            ];
            if (!document.Custom.IsEmpty)
            {
                choices.Add(new OrbTitleThemeChoice(null, string.Empty, true));
                choices.AddRange(document.Custom.Select(set => new OrbTitleThemeChoice(set.Id, titles.Name(TitleTheme.Of(set)))));
            }

            var wasSyncing = _syncing;
            _syncing = true;
            try
            {
                TitleThemeChoices = choices;
                OnPropertyChanged(nameof(TitleThemeIndex));
            }
            finally
            {
                _syncing = wasSyncing;
            }
        }

        OrbTitleLabels = theme.Labels(_loc);
        var redline = orb?.EffectiveRedline(S.Metric, S) ?? S.Redline;
        foreach (var row in TitleLadder)
        {
            row.Refresh(_loc, S.Metric, redline, _dark, theme);
        }
    }

    private void RefreshHostPicker()
    {
        if (_remote is { } remote)
        {
            HostPicker.Refresh(Orb?.Hosts ?? OrbHosts.All, remote, _loc);
        }

        HostPicker.ReduceMotion = _reduceMotion;
    }

    private void UpdateSourceStatuses()
    {
        var orb = Orb;
        var all = orb?.TrackedSources is null;
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
                row.IsChecked = all || orb?.TrackedSources?.Contains(row.Kind) == true;
                var lastOne = orb is not null && !all && row.IsChecked && !OrbEditing.CanUncheck(orb, row.Kind);
                row.IsEnabled = !all && !lastOne;
                row.Tooltip = lastOne ? _loc["orbs.sources.lastOne"] : null;
            }
        }
        finally
        {
            _syncing = wasSyncing;
        }
    }
}
