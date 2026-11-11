using System.Text.Json;
using AgentSpeed.Core.Reporting;
using AgentSpeed.Presentation.Reporting;
using AgentSpeed.Presentation.Settings;

namespace AgentSpeed.Presentation.Tests.Reporting;

/// <summary>The 額度 switch (REPORTING-EXTENSIONS 11.1, DESIGN 11 item 5): its stored preference and its caption.</summary>
public class ReportQuotaSwitchTests
{
    private const string Document = """
        {"protocol":"agent-speed-report","versions":[1],
         "service":{"name":"Live board","terms":"https://example.com/terms","privacy":"https://example.com/privacy"},
         "api":"https://api.example.com/agent-speed"EXTENSIONS}
        """;

    private static ReportDiscovery Parsed(string extensions = "") =>
        ReportSettingsTests.Document(Document.Replace("EXTENSIONS", extensions, StringComparison.Ordinal), "https://board.example.com");

    private static ReportPreferences RoundTrip(ReportPreferences report) =>
        SettingsSerializer.Deserialize(SettingsSerializer.Serialize(AppSettings.Default with { Report = report })).Report;

    [Fact]
    public void TheSwitchIsOnByDefaultAndForSettingsOfEarlierBuilds()
    {
        Assert.True(ReportPreferences.Default.ShareQuota);
        Assert.True(SettingsSerializer.Deserialize("""{"version":2}""").Report.ShareQuota);
        // Earlier builds stored only the sections: even with every section off, the quota switch stays on.
        Assert.True(SettingsSerializer.Deserialize("""{"report":{"enabled":true,"sections":[]}}""").Report.ShareQuota);
        Assert.True(SettingsSerializer.Deserialize("""{"report":{"shareQuota":"no"}}""").Report.ShareQuota);
        Assert.False(SettingsSerializer.Deserialize("""{"report":{"shareQuota":false}}""").Report.ShareQuota);
    }

    [Fact]
    public void TheSwitchRoundTripsOnItsOwnKey()
    {
        var off = ReportPreferences.Default with { ShareQuota = false };
        Assert.False(RoundTrip(off).ShareQuota);
        Assert.True(RoundTrip(ReportPreferences.Default).ShareQuota);
        Assert.Equal(ReportPreferences.Default.Sections.ToArray(), RoundTrip(off).Sections.ToArray());
        using var json = JsonDocument.Parse(SettingsSerializer.Serialize(AppSettings.Default with { Report = off }));
        Assert.Equal(JsonValueKind.False, json.RootElement.GetProperty("report").GetProperty("shareQuota").ValueKind);
    }

    [Fact]
    public void TheSwitchTakesPartInEquality()
    {
        var off = ReportPreferences.Default with { ShareQuota = false };
        Assert.NotEqual(ReportPreferences.Default, off);
        Assert.Equal(off, ReportPreferences.Default with { ShareQuota = false });
        Assert.NotEqual(AppSettings.Default, AppSettings.Default with { Report = off });
    }

    [Fact]
    public void TheSwitchIsUsableOnlyWhileTheServiceAdvertisesTheExtension()
    {
        Assert.True(ReportSectionCaption.AcceptsQuota(null));
        Assert.Null(ReportSectionCaption.QuotaKeyOf(null));
        Assert.True(ReportSectionCaption.AcceptsQuota(Parsed(""","extensions":{"quotaEstimate":1}""")));
        Assert.Null(ReportSectionCaption.QuotaKeyOf(Parsed(""","extensions":{"quotaEstimate":1}""")));
        foreach (var refused in new[] { "", ""","extensions":{"perfScore":1}""", ""","extensions":{"quotaEstimate":2}""", ""","extensions":{"quotaEstimate":true}""" })
        {
            Assert.False(ReportSectionCaption.AcceptsQuota(Parsed(refused)));
            Assert.Equal(ReportSectionCaption.NotAccepted, ReportSectionCaption.QuotaKeyOf(Parsed(refused)));
        }
    }
}
