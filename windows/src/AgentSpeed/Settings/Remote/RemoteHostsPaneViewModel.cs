using System.Collections.ObjectModel;
using System.Windows.Input;
using System.Windows.Threading;
using AgentSpeed.Controls;
using AgentSpeed.Core.Remote;
using AgentSpeed.Presentation.Localization;
using AgentSpeed.Presentation.Remote;
using AgentSpeed.Remote;
using AgentSpeed.Reporting;

namespace AgentSpeed.Settings.Remote;

/// <summary>
/// Settings → 遠端主機 (DESIGN 13): one row per host in the order added with its live status, Add Host… (disabled at 8),
/// the empty state and the footnote. A retry countdown ticks every second while one is shown.
/// </summary>
internal sealed class RemoteHostsPaneViewModel : ObservableObject
{
    private readonly RemoteHostsModel _hosts;
    private readonly DispatcherTimer _tick;
    private Localizer _loc;
    private bool _visible;
    private bool _reduceMotion;

    /// <param name="edit">Opens the Add / Edit dialog: a host to edit, or null to add one.</param>
    /// <param name="confirmRemove">
    /// Asks <c>remote.remove.confirm</c> (name, host: its relay folder and, with its bridge on, <c>remote.quota.removeNote</c>);
    /// confirmed, and whether to delete the history.
    /// </param>
    public RemoteHostsPaneViewModel(RemoteHostsModel hosts, Localizer loc, Dispatcher dispatcher, Action<RemoteHost?> edit,
        Func<string, RemoteHost, (bool Confirmed, bool DeleteHistory)> confirmRemove)
    {
        _hosts = hosts;
        _loc = loc;
        _tick = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => RefreshRows(), dispatcher);
        _tick.Stop();
        AddCommand = new RelayCommand(() => edit(null), () => _hosts.Document.CanAdd);
        EditCommand = new RelayCommand(p =>
        {
            if (p is RemoteHostRowViewModel { Host: { } host })
            {
                edit(host);
            }
        });
        RetryCommand = new RelayCommand(p =>
        {
            if (p is RemoteHostRowViewModel row)
            {
                _hosts.RetryNow(row.Id);
            }
        }, p => p is RemoteHostRowViewModel { CanRetry: true });
        CopyCommand = new RelayCommand(p =>
        {
            if (p is RemoteHostRowViewModel { Host: { } host } row)
            {
                ReportLinks.Copy(SshCommandText.Command(host.Destination));
                ShowCopied(row, dispatcher);
            }
        });
        RemoveCommand = new RelayCommand(p =>
        {
            if (p is not RemoteHostRowViewModel { Host: { } host } row)
            {
                return;
            }

            var (confirmed, deleteHistory) = confirmRemove(row.Name, host);
            if (confirmed)
            {
                _hosts.Remove(host.Id, deleteHistory);
            }
        });
        _hosts.Changed += (_, _) => Sync();
        Sync();
    }

    public ObservableCollection<RemoteHostRowViewModel> Rows { get; } = [];

    public ICommand AddCommand { get; }

    public ICommand EditCommand { get; }

    public ICommand RetryCommand { get; }

    public ICommand CopyCommand { get; }

    public ICommand RemoveCommand { get; }

    public bool IsEmpty => Rows.Count == 0;

    public bool HasHosts => Rows.Count > 0;

    public bool CanAdd => _hosts.Document.CanAdd;

    /// <summary><c>3 / 8</c> beside the header while hosts exist.</summary>
    public string CountText => _loc.Format("orbs.count", Rows.Count, RemoteHostsDocument.MaxHosts);

    /// <summary><c>remote.limit</c>: beside the disabled Add Host… and its tooltip.</summary>
    public string LimitText => _loc.Format("remote.limit", RemoteHostsDocument.MaxHosts);

    public string? AddTooltip => CanAdd ? null : LimitText;

    public bool ReduceMotion { get => _reduceMotion; set => Set(ref _reduceMotion, value); }

    /// <summary>The page is shown: statuses refresh and a countdown ticks only then.</summary>
    public bool IsVisible
    {
        get => _visible;
        set
        {
            if (Set(ref _visible, value))
            {
                RefreshRows();
            }
        }
    }

    public void Relocalize(Localizer loc)
    {
        _loc = loc;
        Sync();
    }

    /// <summary>Today's sessions changed (the connected line's agent count).</summary>
    public void UpdateLive()
    {
        if (_visible)
        {
            RefreshRows();
        }
    }

    /// <summary>The confirmation beside the row's actions, for 1.6 s.</summary>
    private static void ShowCopied(RemoteHostRowViewModel row, Dispatcher dispatcher)
    {
        row.ShowsCopied = true;
        var timer = new DispatcherTimer(TimeSpan.FromSeconds(1.6), DispatcherPriority.Background, (sender, _) =>
        {
            ((DispatcherTimer)sender!).Stop();
            row.ShowsCopied = false;
        }, dispatcher);
        timer.Start();
    }

    private void Sync()
    {
        var document = _hosts.Document;
        var ids = document.Hosts.Select(h => h.Id).ToList();
        for (var i = Rows.Count - 1; i >= 0; i--)
        {
            if (!ids.Contains(Rows[i].Id))
            {
                Rows.RemoveAt(i);
            }
        }

        for (var i = 0; i < ids.Count; i++)
        {
            var row = Rows.FirstOrDefault(r => r.Id == ids[i]);
            if (row is null)
            {
                Rows.Insert(Math.Min(i, Rows.Count), new RemoteHostRowViewModel(ids[i], _hosts.SetEnabled));
            }
            else if (Rows.IndexOf(row) != i)
            {
                Rows.Move(Rows.IndexOf(row), i);
            }
        }

        RefreshRows();
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(HasHosts));
        OnPropertyChanged(nameof(CanAdd));
        OnPropertyChanged(nameof(CountText));
        OnPropertyChanged(nameof(LimitText));
        OnPropertyChanged(nameof(AddTooltip));
        CommandManager.InvalidateRequerySuggested();
    }

    private void RefreshRows()
    {
        var document = _hosts.Document;
        var names = document.DisplayNames();
        var now = DateTimeOffset.Now;
        foreach (var row in Rows)
        {
            if (document.Host(row.Id) is not { } host)
            {
                continue;
            }

            row.Refresh(host, names.GetValueOrDefault(host.Id) ?? host.AutomaticName, _hosts.StatusOf(host), _hosts.SessionsOf(host.Id), now, _loc);
        }

        var counting = _visible && Rows.Any(r => r.IsCountingDown);
        if (counting != _tick.IsEnabled)
        {
            _tick.IsEnabled = counting;
        }
    }
}
