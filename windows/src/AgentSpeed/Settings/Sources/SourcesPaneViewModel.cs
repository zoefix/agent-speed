using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using AgentSpeed.App;
using AgentSpeed.Controls;
using AgentSpeed.Core.Locations;
using AgentSpeed.Presentation.Localization;
using AgentSpeed.Presentation.Quota;
using AgentSpeed.Presentation.Settings.Locations;
using AgentSpeed.Services;

namespace AgentSpeed.Settings.Sources;

/// <summary>
/// Settings → 資料來源 (DESIGN 5, METRICS 2.1): one group per location list with its toggles, entries and actions. Every edit
/// is written through <see cref="SettingsService"/> at once; refused picks say why in a notice.
/// </summary>
internal sealed class SourcesPaneViewModel : ObservableObject
{
    private static readonly TimeSpan LeaveDuration = TimeSpan.FromMilliseconds(170);
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(2);

    private readonly SettingsService _settings;
    private readonly Func<string?, string?> _pickFolder;
    private Localizer _loc;
    private long _refreshedAt;

    public SourcesPaneViewModel(SettingsService settings, Localizer loc, Func<string?, string?> pickFolder, IEnumerable<SourceSettingsRow> toggles)
    {
        _settings = settings;
        _loc = loc;
        _pickFolder = pickFolder;
        var rows = toggles.ToList();
        Groups = [.. LocationFolders.All.Select(folder =>
            new SourceGroupViewModel(folder, [.. folder.Sources().Select(kind => rows.First(row => row.Kind == kind))], this, loc, QuotaFor(folder, loc)))];
        Sync();
        UpdateQuota(QuotaState.Empty, settings.Current);
    }

    /// <summary>What the quota switches do; set by the app.</summary>
    public IQuotaSettingsActions? QuotaActions { get; set; }

    /// <summary>Claude Code's settings file, abbreviated with <c>~</c>, for the bridge's messages.</summary>
    public string CliSettingsPath { get; set; } = "~\\.claude\\settings.json";

    private string? _cliFailure;
    private QuotaState _quotaState = QuotaState.Empty;
    private Presentation.Settings.AppSettings? _quotaSettings;

    /// <summary>The group's quota switches (DESIGN 5): Codex, Claude Desktop and the bridge, then the phase 2 routes.</summary>
    private List<QuotaRowViewModel> QuotaFor(LocationFolder folder, Localizer loc)
    {
        var sources = folder.Sources();
        var switches = new List<QuotaRowViewModel>();
        if (sources.Contains(Core.SourceKind.Codex))
        {
            switches.Add(new QuotaToggleViewModel(QuotaSwitch.Codex, loc, () => QuotaActions));
        }

        if (sources.Contains(Core.SourceKind.ClaudeDesktop) || sources.Contains(Core.SourceKind.ClaudeCode))
        {
            switches.Add(new QuotaToggleViewModel(QuotaSwitch.ClaudeDesktop, loc, () => QuotaActions));
            switches.Add(new QuotaToggleViewModel(QuotaSwitch.ClaudeCli, loc, () => QuotaActions));
        }

        switches.AddRange(QuotaSwitchRows.Of(folder).Select(route => new QuotaRouteToggleViewModel(route, loc, () => QuotaActions)));
        return switches;
    }

    /// <summary>The quota switches, their status lines and the rows a disabled source dims follow the settings and the readers.</summary>
    public void UpdateQuota(QuotaState state, Presentation.Settings.AppSettings settings)
    {
        _quotaState = state;
        _quotaSettings = settings;
        var context = new QuotaRowContext(settings, state, _cliFailure, CliSettingsPath, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        foreach (var toggle in Groups.SelectMany(g => g.QuotaToggles))
        {
            toggle.Update(context);
        }
    }

    /// <summary>Why the bridge could not be turned on (warning style under its switch); null clears it.</summary>
    public void ShowCliFailure(string? failure)
    {
        _cliFailure = failure;
        UpdateQuota(_quotaState, _quotaSettings ?? _settings.Current);
    }

    public IReadOnlyList<SourceGroupViewModel> Groups { get; }

    public bool ReduceMotion { get; set; }

    public string Caption => _loc["settings.sources.locationsHint"];

    /// <summary>Shows a refused pick: why, and the picked path; the app sets it, tests replace it.</summary>
    public Action<string, string>? ShowNotice { get; set; }

    /// <summary>After a row left its group: the view moves keyboard focus inside that group.</summary>
    public event Action<SourceGroupViewModel>? RowRemoved;

    /// <summary>The lists changed: every group shows its entries again.</summary>
    public void Sync()
    {
        var lists = _settings.Current.Locations;
        foreach (var group in Groups)
        {
            group.Sync(lists, ReduceMotion);
        }

        _refreshedAt = Environment.TickCount64;
    }

    /// <summary>Folders may appear or vanish at any time: the rows' states are checked again every few seconds while shown.</summary>
    public void RefreshIfDue()
    {
        if (Environment.TickCount64 - _refreshedAt >= RefreshInterval.TotalMilliseconds)
        {
            Sync();
        }
    }

    public void Relocalize(Localizer loc)
    {
        _loc = loc;
        foreach (var group in Groups)
        {
            group.Relocalize(loc);
        }

        // The status lines are sentences of the language: they are written again.
        UpdateQuota(_quotaState, _quotaSettings ?? _settings.Current);
        OnPropertyChanged(string.Empty);
    }

    internal LocationRowViewModel NewRow(LocationEntry entry, bool isNew) => new(entry, _loc, isNew, Remove, Reveal, Copy);

    /// <summary>Add Folder…: the platform's folder picker, then the list's rules; a refused pick changes nothing and says why.</summary>
    internal void Add(SourceGroupViewModel group)
    {
        var picked = _pickFolder(LocationEntries.PickerStart(group.Folder, _settings.Current.Locations));
        if (string.IsNullOrWhiteSpace(picked))
        {
            return;
        }

        var outcome = LocationAddOutcome.Invalid;
        _settings.Update(s =>
        {
            var (next, result) = s.AddLocation(group.Folder, picked);
            outcome = result;
            return next;
        });
        if (LocationEntries.RefusalText(outcome, _loc) is { } reason)
        {
            (ShowNotice ?? DefaultNotice)(reason, picked);
        }
    }

    internal void Reset(SourceGroupViewModel group) =>
        Leave(group, [.. group.Entries.Where(row => !row.IsDefault)], s => s.ResetLocations(group.Folder));

    private void Remove(LocationRowViewModel row)
    {
        if (Groups.FirstOrDefault(g => g.Entries.Contains(row)) is { } group)
        {
            Leave(group, [row], s => s.RemoveLocation(group.Folder, row.Location));
        }
    }

    /// <summary>The rows that go fade out first (not with reduce motion), then the list changes.</summary>
    private void Leave(SourceGroupViewModel group, IReadOnlyList<LocationRowViewModel> rows,
        Func<Presentation.Settings.AppSettings, Presentation.Settings.AppSettings> change)
    {
        void Commit()
        {
            _settings.Update(change);
            RowRemoved?.Invoke(group);
        }

        if (ReduceMotion || rows.Count == 0)
        {
            Commit();
            return;
        }

        foreach (var row in rows)
        {
            row.IsLeaving = true;
        }

        var timer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = LeaveDuration };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            Commit();
        };
        timer.Start();
    }

    private void DefaultNotice(string reason, string path) =>
        NoticeDialog.Show(Application.Current?.Windows.OfType<SettingsWindow>().FirstOrDefault(w => w.IsVisible),
            _loc["settings.sources"], reason, path, _loc["win.common.ok"]);

    /// <summary>Show in File Explorer: the location selected in its folder.</summary>
    private static void Reveal(string path)
    {
        if (path.Contains('"', StringComparison.Ordinal) || !Path.IsPathFullyQualified(path))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = false });
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException)
        {
            AppLog.Warn($"Could not open File Explorer ({e.GetType().Name}).");
        }
    }

    private static void Copy(string text)
    {
        try
        {
            Clipboard.SetText(text);
        }
        catch (ExternalException e)
        {
            AppLog.Warn($"Could not copy the path ({e.GetType().Name}).");
        }
    }
}
