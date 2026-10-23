using AgentSpeed.Core;
using AgentSpeed.Presentation.Formatting;
using AgentSpeed.Presentation.Settings;

namespace AgentSpeed.Presentation.Tests;

public sealed class AntigravitySettingsTests
{
    [Theory]
    [InlineData(SourceKind.Antigravity, "source.antigravity")]
    [InlineData(SourceKind.AntigravityCli, "source.antigravityCli")]
    public void EachClientHasAnIndependentPersistedToggle(SourceKind source, string label)
    {
        Assert.True(SettingsSerializer.Deserialize("{}").IsSourceEnabled(source));
        var settings = AppSettings.Default.WithSourceEnabled(source, false);
        var back = SettingsSerializer.Deserialize(SettingsSerializer.Serialize(settings));
        Assert.False(back.IsSourceEnabled(source));
        Assert.DoesNotContain(source, back.ToMonitorConfiguration().EnabledSources);
        Assert.True(back.IsSourceEnabled(source == SourceKind.Antigravity ? SourceKind.AntigravityCli : SourceKind.Antigravity));
        Assert.Equal(label, DisplayText.SourceKey(source));
    }
}
