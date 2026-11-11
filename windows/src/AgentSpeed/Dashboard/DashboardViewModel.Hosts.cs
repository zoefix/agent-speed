using System.Collections.ObjectModel;
using System.Windows.Media;
using AgentSpeed.Controls;
using AgentSpeed.Core;
using AgentSpeed.Core.Remote;
using AgentSpeed.Dashboard.Quota;
using AgentSpeed.Presentation.Cost;
using AgentSpeed.Presentation.Formatting;
using AgentSpeed.Presentation.Localization;
using AgentSpeed.Presentation.Remote;
using AgentSpeed.Presentation.Settings;
using AgentSpeed.Remote;

namespace AgentSpeed.Dashboard;

/// <summary>
/// One row of Dashboard → Hosts (DESIGN 13): status dot, name, today's output (with its cost) and the live value, or the
/// status word of a host that is not connected, and a remote host's quota sub-lines (<i>Host quotas</i>). The row opens the
/// Usage History window on its host.
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
    private bool _hasQuota;

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

    /// <summary>Each source with a quota on the host, in source order (kept across updates so bars and texts move).</summary>
    public ObservableCollection<HostQuotaGroupRow> QuotaGroups { get; } = [];

    public bool HasQuota { get => _hasQuota; set => Set(ref _hasQuota, value); }

    /// <summary>Updates <see cref="QuotaGroups"/> in place to <paramref name="groups"/>: kept sources animate, others come or go.</summary>
    public void SyncQuota(IReadOnlyList<HostQuotaGroup> groups, Func<SourceKind, Brush> dot, bool dark, bool highContrast)
    {
        for (var i = 0; i < groups.Count; i++)
        {
            var group = groups[i];
            var row = QuotaGroups.FirstOrDefault(r => r.Source == group.Source);
            if (row is null)
            {
                row = new HostQuotaGroupRow(group.Source, dot(group.Source));
                QuotaGroups.Insert(Math.Min(i, QuotaGroups.Count), row);
            }
            else if (QuotaGroups.IndexOf(row) is var at && at != i)
            {
                QuotaGroups.Move(at, i);
            }

            row.Name = group.Name;
            QuotaLineRow.Sync(row.Lines, group.Lines, row.DotBrush, dark, highContrast);
        }

        while (QuotaGroups.Count > groups.Count)
        {
            QuotaGroups.RemoveAt(QuotaGroups.Count - 1);
        }

        HasQuota = QuotaGroups.Count > 0;
    }
}

/// <summary>A source's sub-line of a host row (DESIGN 13 <i>Host quotas</i>): its dot and name, then its quota lines.</summary>
internal sealed class HostQuotaGroupRow(SourceKind source, Brush dot) : ObservableObject
{
    private string _name = string.Empty;

    public SourceKind Source { get; } = source;

    public Brush DotBrush { get; } = dot;

    public string Name { get => _name; set => Set(ref _name, value); }

    public ObservableCollection<QuotaLineRow> Lines { get; } = [];
}

/// <summary>Dashboard → Hosts (DESIGN 13), between Sources and Agents while remote hosts exist.</summary>
internal sealed partial class DashboardViewModel
{
    private bool _showsHosts;

    /// <summary>The remote hosts as the settings know them; set by the app.</summary>
    public RemoteHostsModel? RemoteHosts { get; set; }

    /// <summary>One row per host that is connected or produced output today, this computer first.</summary>
    public ObservableCollection<HostRow> Hosts { get; } = [];

    /// <summary>The widths the Hosts section's quota lines share (its lines sit deeper than the Sources rows').</summary>
    public QuotaColumns HostQuotaColumns { get; } = new();

    /// <summary>
    /// Developer capture only: hosts shown as connected although the sandbox never connects them, so the section and its quota
    /// sub-lines can be reviewed. Null in normal use.
    /// </summary>
    internal IReadOnlySet<string>? CaptureConnected { get; set; }

    public bool ShowsHosts { get => _showsHosts; private set => Set(ref _showsHosts, value); }

    /// <summary>The name of an agent's remote host for its secondary line; null for this computer's agents.</summary>
    private string? RemoteHostName(AgentStatus agent) => agent.IsLocal ? null : RemoteHosts?.NameOf(agent.Host) ?? agent.Host;

    private void UpdateHosts(SpeedSnapshot snapshot, SpeedMetric metric, Localizer loc, string unit, bool dark)
    {
        var remote = RemoteHosts;
        var captured = CaptureConnected;
        var hosts = captured is null || remote is null ? snapshot.Hosts : WithCaptured(snapshot, captured, remote);
        ShowsHosts = remote is { HasHosts: true } && hosts.Count > 0;
        var listed = ShowsHosts ? hosts.Where(h => h.IsListed || captured?.Contains(h.Id) == true).ToList() : [];
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
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

            var groups = HostQuotaGroups.Of(Quota, listed[i].Id, now, loc);
            Hosts[i].SyncQuota(groups, SourceDot, dark, HighContrast);
            Fill(Hosts[i], listed[i], snapshot, remote!, metric, loc, unit, HostQuotaGroups.Spoken(groups, loc));
        }
    }

    /// <summary>
    /// The capture's hosts even where the snapshot has none (a <c>--demo-speed</c> snapshot): this computer with the
    /// snapshot's values, then each captured host in settings order with nothing today.
    /// </summary>
    private static IReadOnlyList<HostStatus> WithCaptured(SpeedSnapshot snapshot, IReadOnlySet<string> captured, RemoteHostsModel remote)
    {
        var hosts = snapshot.Hosts.ToList();
        if (!hosts.Any(h => h.IsLocal))
        {
            hosts.Insert(0, new HostStatus(RemoteHost.LocalId, RemoteHost.LocalId, null, snapshot.Live, Rate.Zero, snapshot.LiveCost, snapshot.Today,
                CostTally.Zero, 0, true));
        }

        foreach (var host in remote.Document.Hosts.Where(h => captured.Contains(h.Id) && !hosts.Any(s => s.Id == h.Id)))
        {
            hosts.Add(new HostStatus(host.Id, host.AutomaticName, null, Rate.Zero, Rate.Zero, LiveCost.Zero, TokenTotals.Zero, CostTally.Zero, 0, true));
        }

        return hosts;
    }

    private Brush SourceDot(SourceKind kind) => Sources.First(r => r.Kind == kind).DotBrush;

    /// <param name="quotaSpoken">The host's quota windows, spoken after today's cost (DESIGN 4 <i>Accessibility</i>); empty without.</param>
    private void Fill(HostRow row, HostStatus host, SpeedSnapshot snapshot, RemoteHostsModel remote, SpeedMetric metric, Localizer loc, string unit,
        string quotaSpoken)
    {
        row.Name = host.IsLocal ? RemoteHostStates.LocalName(loc) : remote.NameOf(host.Id) ?? host.Name;
        var captured = CaptureConnected?.Contains(host.Id) == true;
        var status = host.IsLocal || captured ? null : remote.StatusOf(host.Id) ?? host.Remote;
        row.Dot = status is null ? RemoteDotKind.Connected : RemoteDots.Of(status.Status);
        row.IsConnected = host.IsConnected || captured;

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
        row.AccessibleName = quotaSpoken.Length > 0
            ? string.Join(separator, row.Name, today, quotaSpoken, row.Word ?? row.LiveAccessibleName)
            : string.Join(separator, row.Name, today, row.Word ?? row.LiveAccessibleName);
    }
}
