using AgentSpeed.Core;
using AgentSpeed.Core.Locations;
using AgentSpeed.Core.Reporting;
using AgentSpeed.Presentation.Formatting;
using AgentSpeed.Presentation.History;
using AgentSpeed.Presentation.Settings;
using AgentSpeed.Presentation.Settings.Orbs;
using AgentSpeed.Presentation.Tests.TestSupport;

namespace AgentSpeed.Presentation.Tests;

public sealed class HarnessSourceSettingsTests
{
    private static AppSettings WithFolder(AppSettings settings, LocationFolder folder, string path) =>
        settings.WithLocations(settings.Locations.With(folder, [DataLocation.At(path)]));

    private static IReadOnlyList<DataLocation> Only(string path) => [DataLocation.At(path)];

    [Fact]
    public void NewSourceFoldersAndTogglesRoundTripAndReachTheMonitorIndependently()
    {
        var zcode = Path.Combine(Path.GetTempPath(), ".zcode", "cli", "db");
        var kimi = Path.Combine(Path.GetTempPath(), ".kimi-code", "sessions");
        var grok = Path.Combine(Path.GetTempPath(), ".grok", "sessions");
        var settings = WithFolder(WithFolder(WithFolder(AppSettings.Default, LocationFolder.ZCode, zcode), LocationFolder.Kimi, kimi),
            LocationFolder.GrokBuild, grok);
        settings = settings.WithSourceEnabled(SourceKind.Kimi, false);
        var back = SettingsSerializer.Deserialize(SettingsSerializer.Serialize(settings));
        Assert.Equal(settings.Locations, back.Locations);
        var config = back.ToMonitorConfiguration();
        Assert.Equal(Only(zcode), config.ListOf(LocationFolder.ZCode));
        Assert.Equal(Only(kimi), config.ListOf(LocationFolder.Kimi));
        Assert.Equal(Only(grok), config.ListOf(LocationFolder.GrokBuild));
        Assert.DoesNotContain(SourceKind.Kimi, config.EnabledSources);
        Assert.Contains(SourceKind.ZCode, config.EnabledSources);
        Assert.Contains(SourceKind.GrokBuild, config.EnabledSources);
        Assert.NotEqual(AppSettings.Default.ToMonitorConfiguration(), config);
    }

    [Theory]
    [InlineData(SourceKind.Pi, "pi-sessions")]
    [InlineData(SourceKind.OpenClaw, ".openclaw/agents/main/agent/openclaw-agent.sqlite")]
    [InlineData(SourceKind.Hermes, "hermes-home")]
    [InlineData(SourceKind.Droid, ".factory")]
    [InlineData(SourceKind.Devin, "devin/cli")]
    [InlineData(SourceKind.Qoder, "Qoder/SharedClientCache")]
    [InlineData(SourceKind.QoderCli, ".qoder")]
    public void ALocationAndToggleRoundTripAndReachTheMonitor(SourceKind kind, string relative)
    {
        var folder = LocationFolders.Of(kind);
        var path = Path.Combine([Path.GetTempPath(), .. relative.Split('/')]);
        var settings = WithFolder(AppSettings.Default.WithSourceEnabled(kind, false), folder, path);
        var back = SettingsSerializer.Deserialize(SettingsSerializer.Serialize(settings.Validated()));

        Assert.False(back.IsSourceEnabled(kind));
        Assert.Equal(Only(Path.TrimEndingDirectorySeparator(Path.GetFullPath(path))), back.Locations[folder]);
        var config = back.ToMonitorConfiguration();
        Assert.Equal(back.Locations[folder], config.ListOf(folder));
        Assert.DoesNotContain(kind, config.EnabledSources);
        Assert.NotEqual(AppSettings.Default.ToMonitorConfiguration(), WithFolder(AppSettings.Default, folder, path).ToMonitorConfiguration());
        Assert.Equal(LocationLists.AutomaticOnly, WithFolder(AppSettings.Default, folder, Path.Combine("relative", "x")).Locations[folder]);
    }

    [Theory]
    [InlineData(SourceKind.Pi, """{ "version": 1, "sources": { "codex": true, "omp": false, "grokBuild": true, "grokBuildRoot": null } }""")]
    [InlineData(SourceKind.OpenClaw, """{ "version": 1, "sources": { "codex": true, "pi": false, "piRoot": null } }""")]
    [InlineData(SourceKind.Hermes, """{ "version": 1, "sources": { "codex": true, "openclaw": false, "openclawRoot": null } }""")]
    [InlineData(SourceKind.Droid, """{ "version": 1, "sources": { "codex": true, "hermes": false, "hermesRoot": null } }""")]
    [InlineData(SourceKind.Devin, """{ "version": 1, "sources": { "codex": true, "droid": false, "droidRoot": null } }""")]
    [InlineData(SourceKind.Qoder, """{ "version": 1, "sources": { "codex": true, "devin": false, "devinRoot": null } }""")]
    [InlineData(SourceKind.QoderCli, """{ "version": 1, "sources": { "codex": true, "devin": true, "qoder": false } }""")]
    public void OlderSettingsFilesKeepANewSourceOnWithItsAutomaticRoots(SourceKind kind, string json)
    {
        var s = SettingsSerializer.Deserialize(json);

        Assert.True(s.IsSourceEnabled(kind));
        Assert.Equal(LocationLists.AutomaticOnly, s.Locations[LocationFolders.Of(kind)]);
        var config = s.ToMonitorConfiguration();
        Assert.Contains(kind, config.EnabledSources);
        Assert.Equal(LocationLists.AutomaticOnly, config.ListOf(LocationFolders.Of(kind)));
        Assert.Null(s.StoredLocations);
    }

    [Fact]
    public void TheDroidRowShowsTheSessionsFolderAChosenFolderStandsFor()
    {
        using var temp = new TempDirectory();
        var factory = temp.Combine(".factory");
        var sessions = Directory.CreateDirectory(Path.Combine(factory, "sessions")).FullName;
        var other = Directory.CreateDirectory(temp.Combine("elsewhere")).FullName;

        Assert.Equal(sessions, LiveMonitor.DroidRoot(factory));
        Assert.Equal(sessions, LiveMonitor.DroidRoot(sessions));
        Assert.Equal(other, LiveMonitor.DroidRoot(other));
        Assert.Equal(temp.Combine("missing"), LiveMonitor.DroidRoot(temp.Combine("missing")));
        Assert.Equal(LiveMonitor.DefaultDroidRoot, LiveMonitor.DroidRoot(null));
        Assert.Equal([".factory", "sessions"], LiveMonitor.DefaultDroidRoot.Split(Path.DirectorySeparatorChar)[^2..]);
    }

    [Theory]
    [InlineData(SourceKind.ZCode, "zcode")]
    [InlineData(SourceKind.Kimi, "kimi")]
    [InlineData(SourceKind.GrokBuild, "grokBuild")]
    [InlineData(SourceKind.Pi, "pi")]
    [InlineData(SourceKind.OpenClaw, "openclaw")]
    [InlineData(SourceKind.Hermes, "hermes")]
    [InlineData(SourceKind.Droid, "droid")]
    [InlineData(SourceKind.Devin, "devin")]
    [InlineData(SourceKind.Qoder, "qoder")]
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
