using AgentSpeed.Controls;
using AgentSpeed.Core;
using AgentSpeed.Presentation.Formatting;
using AgentSpeed.Presentation.Localization;
using AgentSpeed.Presentation.Settings;
using AgentSpeed.Services;

namespace AgentSpeed.Settings;

/// <summary>One entry of the <c>settings.bar.quota</c> popup: off, or a quota source by its name.</summary>
internal sealed class BarQuotaChoice(SourceKind? source) : ObservableObject
{
    private string _name = string.Empty;
    private bool _enabled = true;

    public SourceKind? Source { get; } = source;

    public string Name { get => _name; set => Set(ref _name, value); }

    /// <summary>False for a source whose quota is not read (disabled, or its quota switch off); it stays selectable as the stored choice.</summary>
    public bool IsEnabled { get => _enabled; set => Set(ref _enabled, value); }
}

/// <summary>
/// 選單列 / 工作列's 額度 controls (DESIGN 10, Settings, <i>Quota control</i>): <c>settings.bar.quota</c> with
/// <c>settings.bar.quota.off</c> and every quota source in source order, captioned <c>settings.bar.quota.hint</c> while a source
/// is chosen, then <c>settings.bar.quota.percent</c> (used or remaining), disabled while off. Every change applies live.
/// </summary>
internal sealed class BarQuotaViewModel : ObservableObject
{
    private readonly SettingsService _settings;
    private Localizer _loc;

    public BarQuotaViewModel(SettingsService settings, Localizer localizer)
    {
        _settings = settings;
        _loc = localizer;
        Choices = [new BarQuotaChoice(null), .. QuotaSettings.Sources.Select(s => new BarQuotaChoice(s))];
        _settings.Changed += (_, _) => Sync();
        Relocalize(localizer);
    }

    private AppSettings S => _settings.Current;

    /// <summary>Off first, then every quota source; the list never changes, only its names and which are enabled.</summary>
    public IReadOnlyList<BarQuotaChoice> Choices { get; }

    public int Index
    {
        get
        {
            var chosen = S.BarQuota;
            for (var i = 1; i < Choices.Count; i++)
            {
                if (Choices[i].Source == chosen)
                {
                    return i;
                }
            }

            return 0;
        }

        set
        {
            if (value >= 0 && value < Choices.Count && value != Index)
            {
                var source = Choices[value].Source;
                _settings.Update(s => s with { BarQuota = source });
            }
        }
    }

    /// <summary>A source is chosen: the caption shows and the percent control is enabled.</summary>
    public bool IsOn => S.BarQuota is not null;

    public string Hint => IsOn ? _loc["settings.bar.quota.hint"] : string.Empty;

    /// <summary>The <c>settings.bar.quota.percent.used</c> segment; only a check selects, as in every other segmented choice.</summary>
    public bool Used
    {
        get => S.BarQuotaPercent == BarQuotaPercent.Used;
        set
        {
            if (value)
            {
                _settings.Update(s => s with { BarQuotaPercent = BarQuotaPercent.Used });
            }
        }
    }

    public bool Remaining
    {
        get => S.BarQuotaPercent == BarQuotaPercent.Remaining;
        set
        {
            if (value)
            {
                _settings.Update(s => s with { BarQuotaPercent = BarQuotaPercent.Remaining });
            }
        }
    }

    public void Relocalize(Localizer localizer)
    {
        _loc = localizer;
        foreach (var choice in Choices)
        {
            choice.Name = choice.Source is { } source ? localizer[DisplayText.SourceKey(source)] : localizer["settings.bar.quota.off"];
        }

        Sync();
    }

    private void Sync()
    {
        var s = S;
        foreach (var choice in Choices)
        {
            choice.IsEnabled = choice.Source is not { } source || s.ShowsQuota(source);
        }

        OnPropertyChanged(string.Empty);
    }
}
