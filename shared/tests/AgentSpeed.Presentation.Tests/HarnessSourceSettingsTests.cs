using AgentSpeed.Core;
using AgentSpeed.Core.Reporting;
using AgentSpeed.Presentation.Formatting;
using AgentSpeed.Presentation.History;
using AgentSpeed.Presentation.Settings;
using AgentSpeed.Presentation.Settings.Orbs;

namespace AgentSpeed.Presentation.Tests;

public sealed class HarnessSourceSettingsTests
{
    [Fact]
    public void NewSourceFoldersAndTogglesRoundTripAndReachTheMonitorIndependently()
    {
        var settings = AppSettings.Default with
        {
            ZCodeRoot = Path.Combine(Path.GetTempPath(), ".zcode", "cli", "db"),
            KimiRoot = Path.Combine(Path.GetTempPath(), ".kimi-code", "sessions"),
            GrokBuildRoot = Path.Combine(Path.GetTempPath(), ".grok", "sessions"),
        };
        settings = settings.WithSourceEnabled(SourceKind.Kimi, false);
        var back = SettingsSerializer.Deserialize(SettingsSerializer.Serialize(settings));
        Assert.Equal(settings.ZCodeRoot, back.ZCodeRoot);
        Assert.Equal(settings.KimiRoot, back.KimiRoot);
        Assert.Equal(settings.GrokBuildRoot, back.GrokBuildRoot);
        var config = back.ToMonitorConfiguration();
        Assert.Equal(settings.ZCodeRoot, config.ZCodeRoot);
        Assert.Equal(settings.KimiRoot, config.KimiRoot);
        Assert.Equal(settings.GrokBuildRoot, config.GrokBuildRoot);
        Assert.DoesNotContain(SourceKind.Kimi, config.EnabledSources);
        Assert.Contains(SourceKind.ZCode, config.EnabledSources);
        Assert.Contains(SourceKind.GrokBuild, config.EnabledSources);
        Assert.NotEqual(AppSettings.Default.ToMonitorConfiguration(), config);
    }

    [Theory]
    [InlineData(SourceKind.ZCode, "zcode")]
    [InlineData(SourceKind.Kimi, "kimi")]
    [InlineData(SourceKind.GrokBuild, "grokBuild")]
    public void EverySurfaceUsesTheSameStableSourceIdentifier(SourceKind kind, string id)
    {
        Assert.Equal(id, ReportBody.SourceId(kind));
        Assert.Equal(id, OrbSources.KeyOf(kind));
        Assert.Equal("source." + id, DisplayText.SourceKey(kind));
        Assert.Equal(id, HistoryCsv.SourceId(kind));
        Assert.Equal([id], OrbSources.Of(kind).ToKeys());
        Assert.Equal(SourceSet.Of(kind), OrbSources.FromKeys([id]).Known);
        Assert.Equal("deepseek", ReportBody.SourceId(SourceKind.DeepSeek));
    }
}
