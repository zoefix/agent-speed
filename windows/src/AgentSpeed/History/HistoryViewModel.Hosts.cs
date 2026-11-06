using System.Collections.ObjectModel;
using AgentSpeed.Core;
using AgentSpeed.Core.Remote;
using AgentSpeed.Presentation.History;
using AgentSpeed.Presentation.Remote;

namespace AgentSpeed.History;

/// <summary>The host filter (DESIGN 13): every host, this computer or one remote host, combined with the source filter.</summary>
internal sealed partial class HistoryViewModel
{
    private static readonly IReadOnlyDictionary<string, string> NoHostNames = new Dictionary<string, string>();

    private string? _host;
    private HistoryHostView _hosts = new(null, [], []);
    private Func<IReadOnlyDictionary<string, string>> _configuredHostNames = static () => NoHostNames;
    private HostOption? _selectedHostOption;
    private bool _syncingHosts;
    private bool _showsHostPicker;

    /// <summary>The host picker's choice: every host when null, else <see cref="RemoteHost.LocalId"/> or a remote host's id.</summary>
    public string? SelectedHost => _host;

    /// <summary>The filter in effect: <see cref="SelectedHost"/> while host history exists, else every host.</summary>
    public string? Host => _hosts.Host;

    /// <summary>The picker's hosts, this computer first; empty while no remote host has history, so no picker shows.</summary>
    public IReadOnlyList<HistoryHost> Hosts => _hosts.Hosts;

    /// <summary>The selected host's name under the title (this computer's own word for it); null for every host.</summary>
    public string? HostName => _hosts.Name(RemoteHostStates.LocalName(_loc));

    /// <summary>The host picker's entries: every host, this computer, then the remote hosts with history.</summary>
    public ObservableCollection<HostOption> HostOptions { get; } = [];

    /// <summary>The picker is shown only while any host history exists (or a dashboard row opened the window on a host).</summary>
    public bool ShowsHostPicker { get => _showsHostPicker; private set => Set(ref _showsHostPicker, value); }

    public HostOption? SelectedHostOption
    {
        get => _selectedHostOption;
        set
        {
            if (value is null || _syncingHosts || ReferenceEquals(value, _selectedHostOption))
            {
                return;
            }

            _selectedHostOption = value;
            OnPropertyChanged();
            SetHost(value.Id);
        }
    }

    /// <summary>The settings' display names of the remote hosts; they win over the names stored with the history.</summary>
    public void SetConfiguredHostNames(Func<IReadOnlyDictionary<string, string>> names)
    {
        ArgumentNullException.ThrowIfNull(names);
        _configuredHostNames = names;
        if (!_disposed)
        {
            ScheduleRecompute();
        }
    }

    /// <summary>The host picker's choice, or the host a dashboard row opened the window on (all sources).</summary>
    public void SetHost(string? host)
    {
        if (host == _host)
        {
            return;
        }

        _host = host;
        Recompute(HistoryUpdate.Filter);
        FollowTraffic();
    }

    /// <summary>The picker and the records of the host filter, for one recomputation.</summary>
    private IReadOnlyList<DailyUsage> SelectHost(IReadOnlyList<DailyUsage> all)
    {
        _hosts = HistoryHosts.Select(all, HistoryHosts.Names(_hub.HostNames, _configuredHostNames()), _host);
        return _hosts.Usage;
    }

    /// <summary><c>devbox · 2026/10/7 – 11/5 · 30 天</c>: the selected host before the range.</summary>
    private string WithHost(string subtitle) => HostName is { } name ? $"{name} · {subtitle}" : subtitle;

    private void UpdateHostOptions()
    {
        var hosts = _hosts.Hosts;
        var ids = hosts.Count == 0 ? [] : new List<string?> { null }.Concat(hosts.Select(h => (string?)h.Id)).ToList();
        _syncingHosts = true;
        try
        {
            if (!HostOptions.Select(o => o.Id).SequenceEqual(ids))
            {
                var compact = HostOptions.FirstOrDefault()?.Compact ?? false;
                HostOptions.Clear();
                foreach (var id in ids)
                {
                    var glyph = id is null ? HostOption.AllGlyph : id == RemoteHost.LocalId ? HostOption.LocalGlyph : HostOption.RemoteGlyph;
                    HostOptions.Add(new HostOption(id, glyph) { Compact = compact });
                }
            }

            foreach (var option in HostOptions)
            {
                option.Name = option.Id switch
                {
                    null => _loc["remote.history.allHosts"],
                    RemoteHost.LocalId => RemoteHostStates.LocalName(_loc),
                    var id => hosts.FirstOrDefault(h => h.Id == id)?.Name ?? id,
                };
            }
        }
        finally
        {
            _syncingHosts = false;
        }

        ShowsHostPicker = HostOptions.Count > 0;
        var selected = HostOptions.FirstOrDefault(o => o.Id == _hosts.Host) ?? HostOptions.FirstOrDefault();
        if (!ReferenceEquals(selected, _selectedHostOption))
        {
            _selectedHostOption = selected;
            OnPropertyChanged(nameof(SelectedHostOption));
        }
    }
}
