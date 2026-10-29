using AgentSpeed.Core.Reporting;

namespace AgentSpeed.Presentation.Reporting;

/// <summary>
/// The caption under a section switch (DESIGN 11, item 5): <c>report.section.notAccepted</c> when the service does not list
/// the section, <c>report.section.trafficOff</c> while traffic measuring is off, and under the models switch what the switch
/// also shares (METRICS 11.10): <c>report.section.models.perfScore</c> while the known document advertises the performance
/// score extension or no document is known yet, <c>report.section.models.thinking</c> while it advertises only the retired
/// task score, which now receives the thinking speed alone. It stays with the switch off, so the user knows what turning it
/// on shares.
/// </summary>
public static class ReportSectionCaption
{
    public const string NotAccepted = "report.section.notAccepted";
    public const string TrafficOff = "report.section.trafficOff";
    public const string PerfScore = "report.section.models.perfScore";
    public const string Thinking = "report.section.models.thinking";

    /// <param name="document">The known discovery document; null before the first fetch.</param>
    /// <param name="trafficMeasured">Traffic measuring is on (DESIGN 10).</param>
    /// <returns>The caption's localization key, or null for none.</returns>
    public static string? KeyOf(ReportSection section, ReportDiscovery? document, bool trafficMeasured)
    {
        if (document is not null && !document.Sections.Contains(section))
        {
            return NotAccepted;
        }

        return section switch
        {
            ReportSection.Traffic when !trafficMeasured => TrafficOff,
            ReportSection.Models when document is null || document.Extensions.PerfScore => PerfScore,
            ReportSection.Models when document.Extensions.TaskScore => Thinking,
            _ => null,
        };
    }
}
