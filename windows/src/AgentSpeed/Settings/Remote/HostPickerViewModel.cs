using System.Collections.ObjectModel;
using AgentSpeed.Controls;
using AgentSpeed.Presentation.Localization;
using AgentSpeed.Presentation.Remote;
using AgentSpeed.Presentation.Settings.Orbs;
using AgentSpeed.Remote;

namespace AgentSpeed.Settings.Remote;

/// <summary>One host of an editor's Hosts section: checkbox, status dot, name and, while not connected, its word.</summary>
internal sealed class HostPickerRowViewModel(string id) : ObservableObject
{
    private string _name = string.Empty;
    private string? _word;
    private RemoteDotKind _dot;
    private bool _checked;
    private bool _enabled = true;
    private string? _tooltip;

    public string Id { get; } = id;

    public string Name { get => _name; set => Set(ref _name, value); }

    public string? Word { get => _word; set => Set(ref _word, value); }

    public RemoteDotKind Dot { get => _dot; set => Set(ref _dot, value); }

    public bool IsChecked { get => _checked; set => Set(ref _checked, value); }

    public bool IsEnabled { get => _enabled; set => Set(ref _enabled, value); }

    /// <summary><c>remote.hosts.lastOne</c> on the last checked host.</summary>
    public string? Tooltip { get => _tooltip; set => Set(ref _tooltip, value); }
}

/// <summary>
/// The Hosts section of the orb and board editors (DESIGN 13), only while remote hosts exist: <i>All Hosts</i> and one
/// checkbox per host (this computer first) with its status dot; the last checked host cannot be unchecked.
/// </summary>
internal sealed class HostPickerViewModel : ObservableObject
{
    private readonly Action<OrbHosts> _set;
    private readonly Action<string> _toggle;
    private bool _allHosts = true;
    private bool _isShown;
    private bool _syncing;
    private bool _reduceMotion;

    /// <param name="set">Stores a selection (<i>All Hosts</i>, or every listed host one by one).</param>
    /// <param name="toggle">Checks or unchecks one host by id (<see cref="OrbHosts.Toggling"/>).</param>
    public HostPickerViewModel(Action<OrbHosts> set, Action<string> toggle)
    {
        _set = set;
        _toggle = toggle;
    }

    public ObservableCollection<HostPickerRowViewModel> Rows { get; } = [];

    public bool IsShown { get => _isShown; private set => Set(ref _isShown, value); }

    public bool ReduceMotion { get => _reduceMotion; set => Set(ref _reduceMotion, value); }

    /// <summary>Checking it tracks every host, including ones added later; unchecking it keeps every listed host checked one by one.</summary>
    public bool AllHosts
    {
        get => _allHosts;
        set
        {
            if (_syncing || value == _allHosts)
            {
                return;
            }

            _set(value ? OrbHosts.All : OrbHosts.Normalized(Rows.Select(r => r.Id)));
        }
    }

    public void Toggle(HostPickerRowViewModel row)
    {
        if (!_syncing)
        {
            _toggle(row.Id);
        }
    }

    /// <summary>The rows for <paramref name="selection"/> over the hosts the settings know.</summary>
    public void Refresh(OrbHosts selection, RemoteHostsModel hosts, Localizer loc)
    {
        var choices = hosts.Choices(loc);
        IsShown = OrbHostChoice.PickerShown(choices);
        _syncing = true;
        try
        {
            var entries = selection.Entries(choices);
            while (Rows.Count > entries.Count)
            {
                Rows.RemoveAt(Rows.Count - 1);
            }

            for (var i = 0; i < entries.Count; i++)
            {
                var entry = entries[i];
                if (i >= Rows.Count || Rows[i].Id != entry.Id)
                {
                    if (i < Rows.Count)
                    {
                        Rows.RemoveAt(i);
                    }

                    Rows.Insert(i, new HostPickerRowViewModel(entry.Id));
                }

                var row = Rows[i];
                row.Name = entry.Name;
                row.IsChecked = selection.IsAll || entry.Checked;
                row.IsEnabled = !selection.IsAll && entry.Enabled;
                row.Tooltip = entry.Checked && !entry.Enabled ? loc["remote.hosts.lastOne"] : null;
                row.Dot = hosts.DotOf(entry.Id);
                row.Word = hosts.WordOf(entry.Id, loc);
            }

            _allHosts = selection.IsAll;
            OnPropertyChanged(nameof(AllHosts));
        }
        finally
        {
            _syncing = false;
        }
    }
}
