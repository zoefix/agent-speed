using AgentSpeed.Core.Reporting;
using AgentSpeed.Presentation.Reporting;

namespace AgentSpeed.Presentation.Tests.Reporting;

/// <summary>The caption under each section switch (DESIGN 11, item 5), the score extensions' under the models switch (METRICS 11.10).</summary>
public class ReportSectionCaptionTests
{
    private const string Document = """
        {"protocol":"agent-speed-report","versions":[1],
         "service":{"name":"Live board","terms":"https://example.com/terms","privacy":"https://example.com/privacy"},
         "api":"https://api.example.com/agent-speed"SECTIONS EXTENSIONS}
        """;

    private static ReportDiscovery Parsed(string sections = "", string extensions = "") =>
        ReportSettingsTests.Document(Document.Replace("SECTIONS", sections, StringComparison.Ordinal).Replace("EXTENSIONS", extensions, StringComparison.Ordinal),
            "https://board.example.com");

    [Fact]
    public void TheModelsSwitchSaysWhatItAlsoSharesWhenTheServiceAdvertisesThePerformanceScore()
    {
        var advertised = Parsed(extensions: ""","extensions":{"perfScore":1}""");
        Assert.Equal(ReportSectionCaption.PerfScore, ReportSectionCaption.KeyOf(ReportSection.Models, advertised, trafficMeasured: true));
        Assert.Null(ReportSectionCaption.KeyOf(ReportSection.Sources, advertised, trafficMeasured: true));
        Assert.Null(ReportSectionCaption.KeyOf(ReportSection.Cost, advertised, trafficMeasured: true));

        // Both advertised: perfScore wins. Before any document is known the preview assumes it, and so does the caption.
        var both = Parsed(extensions: ""","extensions":{"perfScore":1,"taskScore":1}""");
        Assert.Equal(ReportSectionCaption.PerfScore, ReportSectionCaption.KeyOf(ReportSection.Models, both, trafficMeasured: true));
        Assert.Equal(ReportSectionCaption.PerfScore, ReportSectionCaption.KeyOf(ReportSection.Models, null, trafficMeasured: true));
    }

    [Fact]
    public void ARetiredTaskScoreAloneSharesOnlyTheThinkingSpeed()
    {
        var legacy = Parsed(extensions: ""","extensions":{"taskScore":1}""");
        Assert.Equal(ReportSectionCaption.Thinking, ReportSectionCaption.KeyOf(ReportSection.Models, legacy, trafficMeasured: true));
    }

    [Fact]
    public void ItHidesWhenNothingMoreIsSentAndTheOtherCaptionsWin()
    {
        Assert.Null(ReportSectionCaption.KeyOf(ReportSection.Models, Parsed(), trafficMeasured: true));
        Assert.Null(ReportSectionCaption.KeyOf(ReportSection.Models, Parsed(extensions: ""","extensions":{"perfScore":2,"taskScore":2}"""), trafficMeasured: true));
        var withoutModels = Parsed(""","sections":["sources","cost"]""", ""","extensions":{"perfScore":1}""");
        Assert.Equal(ReportSectionCaption.NotAccepted, ReportSectionCaption.KeyOf(ReportSection.Models, withoutModels, trafficMeasured: true));
        Assert.Equal(ReportSectionCaption.NotAccepted, ReportSectionCaption.KeyOf(ReportSection.Traffic, withoutModels, trafficMeasured: false));
        Assert.Equal(ReportSectionCaption.TrafficOff, ReportSectionCaption.KeyOf(ReportSection.Traffic, Parsed(), trafficMeasured: false));
        Assert.Equal(ReportSectionCaption.TrafficOff, ReportSectionCaption.KeyOf(ReportSection.Traffic, null, trafficMeasured: false));
    }
}
