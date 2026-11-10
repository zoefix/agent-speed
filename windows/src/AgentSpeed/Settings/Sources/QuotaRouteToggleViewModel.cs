using System.Windows.Threading;
using AgentSpeed.Core.Quota.Routes;
using AgentSpeed.Presentation.Localization;
using AgentSpeed.Presentation.Quota;

namespace AgentSpeed.Settings.Sources;

/// <summary>
/// A phase 2 quota switch (DESIGN 5, <i>額度, phase 2</i>): its hint and <c>settings.quota.unverified</c> as the caption and,
/// while it runs, the route's status line. Grok's live request is indented under the log's switch; ZCode carries its key
/// and region; Qoder and WorkBuddy ask before the browser's session is read.
/// </summary>
internal sealed class QuotaRouteToggleViewModel : QuotaRowViewModel
{
    private QuotaRowContext? _context;

    public QuotaRouteToggleViewModel(QuotaRoute route, Localizer loc, Func<IQuotaSettingsActions?> actions)
        : base(loc, actions)
    {
        Route = route;
        if (route == QuotaRoute.Zcode)
        {
            Zcode = new ZcodeFieldsViewModel(actions);

            // A key saved or removed here shows or hides the status line at once.
            Zcode.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(ZcodeFieldsViewModel.IsSaved) && _context is { } context)
                {
                    Update(context);
                }
            };
        }
    }

    public QuotaRoute Route { get; }

    public override ZcodeFieldsViewModel? Zcode { get; }

    public override string Title => Loc[QuotaSwitchRows.TitleKey(Route)];

    public override string Hint => QuotaSwitchRows.Hint(Route, Loc);

    public override string Note => QuotaSwitchRows.HasNote(Route) ? Loc["settings.quota.unverified"] : string.Empty;

    public override bool IsIndented => QuotaSwitchRows.IsIndented(Route);

    protected override void Toggle(bool on)
    {
        if (!on || !Route.ReadsBrowser())
        {
            ShowOn(on);
            Actions()?.SetRoute(Route, on);
            return;
        }

        // Reading the browser's session asks first: the switch stays off until the dialog confirms.
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            ShowOn(Actions()?.EnableBrowser(Route) ?? false);
            RefreshOn();
        });
    }

    public override void Update(QuotaRowContext context)
    {
        _context = context;
        var settings = context.Settings;
        ShowOn(QuotaSwitchRows.StoredOn(Route, settings.Quota));
        CanSwitch = QuotaSwitchRows.CanSwitch(Route, settings);
        Zcode?.Update(settings.Quota.ZcodeRegion, context.State.ZcodeKeySaved);
        var shown = QuotaStatusText.Shown(Route, context.State.StatusOf(Route), settings.RouteIsOn(Route), settings.NetworkEnabled,
            Zcode?.IsSaved ?? false);
        Status = shown is { } status ? QuotaStatusText.Text(status, Route, context.NowMs, Loc, settings.Quota.ZcodeRegion) : string.Empty;
        StatusMark = shown is { } s ? MarkOf(QuotaStatusText.Glyph(s)) : QuotaStatusMark.None;
    }
}
