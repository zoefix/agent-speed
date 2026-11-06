using System.Collections.ObjectModel;
using AgentSpeed.Controls;
using AgentSpeed.Core;
using AgentSpeed.Core.Remote;
using AgentSpeed.Presentation.Cost;
using AgentSpeed.Presentation.Formatting;
using AgentSpeed.Presentation.Localization;
using AgentSpeed.Presentation.Remote;
using AgentSpeed.Presentation.Settings;
using AgentSpeed.Remote;

namespace AgentSpeed.Dashboard;

/// <summary>
/// One row of Dashboard → Hosts (DESIGN 13): status dot, name, today's output (with its cost) and the live value, or the
/// status word of a host that is not connected. The row opens the Usage History window on its host.
/// </summary>
internal sealed class HostRow(string id) : ObservableObject
{
    private string _name = string.Empty;
    private RemoteDotKind _dot;
    private bool _connected = true;
    private string _todayText = string.Empty;
    private string _liveText = "0";
    private LiveValueKind _liveKind = LiveValueKind.Dash;
    private string _liveAccessibleName = string.Empty;
    private string? _word;
    private string _accessibleName = string.Empty;

    public string Id { get; } = id;

    public string Name { get => _name; set => Set(ref _name, value); }

    public RemoteDotKind Dot { get => _dot; set => Set(ref _dot, value); }

    /// <summary>The name is secondary while the host cannot be seen.</summary>
    public bool IsConnected { get => _connected; set => Set(ref _connected, value); }

    public string TodayText { get => _todayText; set => Set(ref _todayText, value); }

    public string LiveText { get => _liveText; set => Set(ref _liveText, value); }

    public LiveValueKind LiveKind { get => _liveKind; set => Set(ref _liveKind, value); }

    public string LiveAccessibleName { get => _liveAccessibleName; set => Set(ref _liveAccessibleName, value); }

    /// <summary>The status word instead of the live value while the host is not connected; null otherwise.</summary>
    public string? Word
    {
        get => _word;
        set
        {
            if (Set(ref _word, value))
            {
                OnPropertyChanged(nameof(ShowsWord));
                OnPropertyChanged(nameof(ShowsLive));
            }
        }
    }

    public bool ShowsWord => _word is not null;

    public bool ShowsLive => _word is null;

    public string AccessibleName { get => _accessibleName; set => Set(ref _accessibleName, value); }
}

/// <summary>Dashboard → Hosts (DESIGN 13), between Sources and Agents while remote hosts exist.</summary>
internal sealed partial class DashboardViewModel
{
    private bool _showsHosts;

    /// <summary>The remote hosts as the settings know them; set by the app.</summary>
    public RemoteHostsModel? RemoteHosts { get; set; }

    /// <summary>One row per host that is connected or produced output today, this computer first.</summary>
    public ObservableCollection<HostRow> Hosts { get; } = [];

    public bool ShowsHosts { get => _showsHosts; private set => Set(ref _showsHosts, value); }

    /// <summary>The name of an agent's remote host for its secondary line; null for this computer's agents.</summary>
    private string? RemoteHostName(AgentStatus agent) => agent.IsLocal ? null : RemoteHosts?.NameOf(agent.Host) ?? agent.Host;

    private void UpdateHosts(SpeedSnapshot snapshot, SpeedMetric metric, Localizer loc, string unit)
    {
        var remote = RemoteHosts;
        ShowsHosts = remote is { HasHosts: true } && snapshot.Hosts.Count > 0;
        var listed = ShowsHosts ? snapshot.Hosts.Where(h => h.IsListed).ToList() : [];
        while (Hosts.Count > listed.Count)
        {
            Hosts.RemoveAt(Hosts.Count - 1);
        }

        for (var i = 0; i < listed.Count; i++)
        {
            if (i >= Hosts.Count || Hosts[i].Id != listed[i].Id)
            {
                if (i < Hosts.Count)
                {
                    Hosts.RemoveAt(i);
                }

                Hosts.Insert(i, new HostRow(listed[i].Id));
            }

            Fill(Hosts[i], listed[i], snapshot, remote!, metric, loc, unit);
        }
    }

    private void Fill(HostRow row, HostStatus host, SpeedSnapshot snapshot, RemoteHostsModel remote, SpeedMetric metric, Localizer loc, string unit)
    {
        row.Name = host.IsLocal ? RemoteHostStates.LocalName(loc) : remote.NameOf(host.Id) ?? host.Name;
        var status = host.IsLocal ? null : remote.StatusOf(host.Id) ?? host.Remote;
        row.Dot = status is null ? RemoteDotKind.Connected : RemoteDots.Of(status.Status);
        row.IsConnected = host.IsConnected;

        var costText = string.Empty;
        var costSpoken = string.Empty;
        CostReading? reading = null;
        if (Cost is { } cost)
        {
            costText = cost.TallyText(host.TodayCost, host.Today.Requests);
            costSpoken = cost.SpokenTally(host.TodayCost, host.Today.Requests);
            reading = OrbSubset.Cost(snapshot, null, new HashSet<string>(StringComparer.Ordinal) { host.Id }, cost.Billable);
        }

        var output = SpeedFormat.Tokens(host.Today.Output);
        row.TodayText = costText.Length > 0 ? $"{output} · {costText}" : output;
        row.Word = status is { Status.Kind: not RemoteStatusKind.Connected } ? RemoteStatusText.Word(status.Status, loc) : null;

        var live = host.Live.ValueFor(metric);
        if (LiveUnit == LiveUnit.Cost && Cost is { } money && reading is { } costReading)
        {
            var value = money.Live(costReading, 0);
            var idle = DisplayText.IsNegligibleSpeed(host.Live.Output) && costReading.MicroUsdPerSecond <= 0;
            if (idle || value.Unknown)
            {
                row.LiveKind = LiveValueKind.Dash;
                row.LiveAccessibleName = loc["phase.idle"];
            }
            else
            {
                row.LiveText = $"{value.NumberText} {value.Unit}";
                row.LiveKind = LiveValueKind.Number;
                row.LiveAccessibleName = value.SpokenAmount;
            }
        }
        else if (DisplayText.IsNegligibleSpeed(live))
        {
            row.LiveKind = LiveValueKind.Dash;
            row.LiveAccessibleName = loc["phase.idle"];
        }
        else
        {
            row.LiveText = $"{SpeedFormat.Speed(live)} {unit}";
            row.LiveKind = LiveValueKind.Number;
            row.LiveAccessibleName = row.LiveText;
        }

        var separator = loc.UsesLatinScript ? ", " : "，";
        var today = costSpoken.Length > 0
            ? $"{loc["today.output"]} {output}{separator}{loc["today.cost"]} {costSpoken}"
            : $"{loc["today.output"]} {output}";
        row.AccessibleName = string.Join(separator, row.Name, today, row.Word ?? row.LiveAccessibleName);
    }
}
