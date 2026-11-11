using System.Windows.Input;
using AgentSpeed.Controls;
using AgentSpeed.Core.Reporting;
using AgentSpeed.Presentation.Localization;
using AgentSpeed.Presentation.Reporting;
using AgentSpeed.Settings;

namespace AgentSpeed.Reporting;

/// <summary>
/// The consent sheet (DESIGN 11): the service card, the username, the API key when needed, the section switches with their
/// defaults and the live preview of the exact report body. Nothing is created or requested before Start.
/// </summary>
internal sealed class ReportConsentViewModel : ObservableObject
{
    private readonly ReportController _report;
    private readonly Localizer _loc;
    private readonly bool _trafficMeasured;
    private string _username;

    public ReportConsentViewModel(ReportController report, Localizer localizer, bool trafficMeasured)
    {
        _report = report;
        _loc = localizer;
        _trafficMeasured = trafficMeasured;
        _username = report.Consent?.Username ?? string.Empty;
        Quota = new ReportSectionRow(null, ReportSectionCaption.Quota, on => _report.EditConsent(d => d with { ShareQuota = on }),
            ReportSectionCaption.QuotaHint);
        Sections =
        [
            new ReportSectionRow(ReportSection.Sources, "report.section.sources", SetSection),
            new ReportSectionRow(ReportSection.Models, "report.section.models", SetSection),
            new ReportSectionRow(ReportSection.Cost, "report.section.cost", SetSection),
            new ReportSectionRow(ReportSection.Traffic, "report.section.traffic", SetSection),
            Quota,
        ];
        StartCommand = new RelayCommand(() => _report.StartReporting(), () => CanStart);
        TransferCommand = new RelayCommand(() => _report.TransferUsername(), () => Draft is { IsStarting: false, OffersTransfer: true } && _report.NetworkEnabled);
        RandomCommand = new RelayCommand(() => Username = ReportUsername.Random());
        TermsCommand = new RelayCommand(() => ReportLinks.Open(Draft?.Discovery.Terms));
        PrivacyCommand = new RelayCommand(() => ReportLinks.Open(Draft?.Discovery.Privacy));
        LeaderboardCommand = new RelayCommand(() => ReportLinks.Open(Draft?.Discovery.Leaderboard));
        GetApiKeyCommand = new RelayCommand(() => ReportLinks.Open(Draft?.Discovery.ApiKeyUrl));
        Refresh();
    }

    public ReportConsentDraft? Draft => _report.Consent;

    public string Title => _loc.Format("report.consent.title", Draft?.Discovery.Name ?? string.Empty);

    public string Body => _loc.Format("report.consent.body", Draft?.Discovery.Limits.IntervalSeconds ?? 5);

    public string Name => Draft?.Discovery.Name ?? string.Empty;

    public string Host => Draft is { } d ? _loc.Format("report.service.host", d.Discovery.Api.Host) : string.Empty;

    public string? ServiceHost => Draft is { } d && d.Discovery.Api.Host != d.Service.Host ? d.Service.Host : null;

    public string? Description => Draft?.Discovery.Summary;

    public string? Retention => Draft?.Discovery.RetentionDays is { } days ? _loc.Format("report.service.retention", days) : null;

    public bool HasLeaderboard => Draft?.Discovery.Leaderboard is not null;

    public bool IsLoopback => Draft?.Discovery.Api.IsLoopbackHttp == true;

    public string Username
    {
        get => _username;
        set
        {
            if (Set(ref _username, value))
            {
                _report.EditConsent(d => d with { Username = value, UsernameError = null, OffersTransfer = false });
            }
        }
    }

    /// <summary>The live check of the field, else the service's answer.</summary>
    public string? UsernameError
    {
        get
        {
            if (Draft is not { } draft)
            {
                return null;
            }

            var rules = draft.Discovery.UsernameRules;
            var name = ReportUsername.Normalized(_username);
            if (ReportUsername.Validate(name, rules) is { } problem)
            {
                var key = ReportController.UsernameKey(problem, name, rules);
                return key == "report.username.invalid.length" ? _loc.Format(key, rules.MinLength, rules.MaxLength) : _loc[key];
            }

            return draft.UsernameError is { } error ? _loc[error] : null;
        }
    }

    public bool OffersTransfer => Draft?.OffersTransfer == true;

    public bool ShowsApiKey => Draft?.Discovery.ApiKeyMode is ReportApiKeyMode.Optional or ReportApiKeyMode.Required;

    public bool ApiKeyRequired => Draft?.Discovery.ApiKeyMode == ReportApiKeyMode.Required;

    public string ApiKeyPlaceholder => ApiKeyRequired ? string.Empty : _loc["report.apiKey.optional"];

    public bool HasApiKeyUrl => Draft?.Discovery.ApiKeyUrl is not null;

    public string? ApiKeyError => Draft?.ApiKeyError is { } key ? _loc[key] : null;

    /// <summary>The section switches with their defaults, then the 額度 switch.</summary>
    public IReadOnlyList<ReportSectionRow> Sections { get; }

    /// <summary>The 額度 switch (REPORTING-EXTENSIONS 11) with its default and hint.</summary>
    public ReportSectionRow Quota { get; }

    public bool IsStarting => Draft?.IsStarting == true;

    public string? Failure => Draft?.Failure is { } key ? _loc[key] : null;

    public bool CanStart => Draft is { IsStarting: false } && UsernameError is null && _report.NetworkEnabled
        && ReportController.ApiKeyProblem(Draft.ApiKey, Draft.Discovery.ApiKeyMode) is null;

    public ICommand StartCommand { get; }

    public ICommand TransferCommand { get; }

    public ICommand RandomCommand { get; }

    public ICommand TermsCommand { get; }

    public ICommand PrivacyCommand { get; }

    public ICommand LeaderboardCommand { get; }

    public ICommand GetApiKeyCommand { get; }

    public void SetApiKey(string key) => _report.EditConsent(d => d with { ApiKey = key, ApiKeyError = null });

    public void Refresh()
    {
        if (Draft is { } draft)
        {
            foreach (var row in Sections)
            {
                row.Title = _loc[row.TitleKey];
                row.Hint = row.HintKey is { } hint ? _loc[hint] : null;
                if (row.Section is not { } section)
                {
                    continue;
                }

                var accepted = draft.Discovery.Sections.Contains(section);
                var trafficOff = section == ReportSection.Traffic && !_trafficMeasured;
                row.Sync(accepted && draft.Sections.Contains(section), accepted && !trafficOff,
                    ReportSectionCaption.KeyOf(section, draft.Discovery, _trafficMeasured) is { } caption ? _loc[caption] : null);
            }

            var quotaAccepted = ReportSectionCaption.AcceptsQuota(draft.Discovery);
            Quota.Sync(quotaAccepted && draft.ShareQuota, quotaAccepted,
                ReportSectionCaption.QuotaKeyOf(draft.Discovery) is { } quotaCaption ? _loc[quotaCaption] : null);
        }

        OnPropertyChanged(string.Empty);
        CommandManager.InvalidateRequerySuggested();
    }

    private void SetSection(ReportSection section, bool on) => _report.EditConsent(d =>
    {
        var sections = d.Sections.ToHashSet();
        if (on)
        {
            sections.Add(section);
        }
        else
        {
            sections.Remove(section);
        }

        return d with { Sections = sections };
    });
}
