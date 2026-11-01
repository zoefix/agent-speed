using AgentSpeed.Core;
using AgentSpeed.Core.Reporting;
using AgentSpeed.Presentation.Formatting;
using AgentSpeed.Presentation.History;
using AgentSpeed.Presentation.Settings;
using AgentSpeed.Presentation.Settings.Orbs;
using AgentSpeed.Presentation.Tests.TestSupport;

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

    [Fact]
    public void ThePiFolderAndToggleRoundTripAndReachTheMonitor()
    {
        var folder = Path.Combine(Path.GetTempPath(), "pi-sessions");
        var settings = AppSettings.Default.WithSourceEnabled(SourceKind.Pi, false) with { PiRoot = $"  {folder}  " };
        var back = SettingsSerializer.Deserialize(SettingsSerializer.Serialize(settings.Validated()));

        Assert.Equal((false, folder), (back.PiEnabled, back.PiRoot));
        var config = back.ToMonitorConfiguration();
        Assert.Equal(folder, config.PiRoot);
        Assert.DoesNotContain(SourceKind.Pi, config.EnabledSources);
        Assert.NotEqual(AppSettings.Default.ToMonitorConfiguration(), (AppSettings.Default with { PiRoot = folder }).ToMonitorConfiguration());
        Assert.Null((AppSettings.Default with { PiRoot = Path.Combine("relative", "sessions") }).Validated().PiRoot);
    }

    [Fact]
    public void OlderSettingsFilesKeepPiOnWithItsAutomaticRoots()
    {
        var s = SettingsSerializer.Deserialize("""
            { "version": 1, "sources": { "codex": true, "omp": false, "grokBuild": true, "grokBuildRoot": null } }
            """);

        Assert.True(s.PiEnabled);
        Assert.Null(s.PiRoot);
        var config = s.ToMonitorConfiguration();
        Assert.Contains(SourceKind.Pi, config.EnabledSources);
        Assert.Null(config.PiRoot);
    }

    [Fact]
    public void TheOpenClawFolderAndToggleRoundTripAndReachTheMonitor()
    {
        var database = Path.Combine(Path.GetTempPath(), ".openclaw", "agents", "main", "agent", "openclaw-agent.sqlite");
        var settings = AppSettings.Default.WithSourceEnabled(SourceKind.OpenClaw, false) with { OpenClawRoot = $"  {database}  " };
        var back = SettingsSerializer.Deserialize(SettingsSerializer.Serialize(settings.Validated()));

        Assert.Equal((false, database), (back.OpenClawEnabled, back.OpenClawRoot));
        var config = back.ToMonitorConfiguration();
        Assert.Equal(database, config.OpenClawRoot);
        Assert.DoesNotContain(SourceKind.OpenClaw, config.EnabledSources);
        Assert.NotEqual(AppSettings.Default.ToMonitorConfiguration(),
            (AppSettings.Default with { OpenClawRoot = database }).ToMonitorConfiguration());
        Assert.Null((AppSettings.Default with { OpenClawRoot = Path.Combine("relative", ".openclaw") }).Validated().OpenClawRoot);
    }

    [Fact]
    public void TheHermesFolderAndToggleRoundTripAndReachTheMonitor()
    {
        var home = Path.Combine(Path.GetTempPath(), "hermes-home");
        var settings = AppSettings.Default.WithSourceEnabled(SourceKind.Hermes, false) with { HermesRoot = $"  {home}  " };
        var back = SettingsSerializer.Deserialize(SettingsSerializer.Serialize(settings.Validated()));

        Assert.Equal((false, home), (back.HermesEnabled, back.HermesRoot));
        var config = back.ToMonitorConfiguration();
        Assert.Equal(home, config.HermesRoot);
        Assert.DoesNotContain(SourceKind.Hermes, config.EnabledSources);
        Assert.NotEqual(AppSettings.Default.ToMonitorConfiguration(), (AppSettings.Default with { HermesRoot = home }).ToMonitorConfiguration());
        Assert.Null((AppSettings.Default with { HermesRoot = Path.Combine("relative", "logs") }).Validated().HermesRoot);
    }

    [Fact]
    public void OlderSettingsFilesKeepHermesOnWithItsAutomaticHome()
    {
        var s = SettingsSerializer.Deserialize("""
            { "version": 1, "sources": { "codex": true, "openclaw": false, "openclawRoot": null } }
            """);

        Assert.True(s.HermesEnabled);
        Assert.Null(s.HermesRoot);
        var config = s.ToMonitorConfiguration();
        Assert.Contains(SourceKind.Hermes, config.EnabledSources);
        Assert.Null(config.HermesRoot);
    }

    [Fact]
    public void TheDroidFolderAndToggleRoundTripAndReachTheMonitor()
    {
        var factory = Path.Combine(Path.GetTempPath(), ".factory");
        var settings = AppSettings.Default.WithSourceEnabled(SourceKind.Droid, false) with { DroidRoot = $"  {factory}  " };
        var back = SettingsSerializer.Deserialize(SettingsSerializer.Serialize(settings.Validated()));

        Assert.Equal((false, factory), (back.DroidEnabled, back.DroidRoot));
        Assert.True(back.HermesEnabled);
        var config = back.ToMonitorConfiguration();
        Assert.Equal(factory, config.DroidRoot);
        Assert.DoesNotContain(SourceKind.Droid, config.EnabledSources);
        Assert.NotEqual(AppSettings.Default.ToMonitorConfiguration(), (AppSettings.Default with { DroidRoot = factory }).ToMonitorConfiguration());
        Assert.Null((AppSettings.Default with { DroidRoot = Path.Combine("relative", "sessions") }).Validated().DroidRoot);
    }

    [Fact]
    public void OlderSettingsFilesKeepDroidOnWithItsAutomaticRoot()
    {
        var s = SettingsSerializer.Deserialize("""
            { "version": 1, "sources": { "codex": true, "hermes": false, "hermesRoot": null } }
            """);

        Assert.True(s.DroidEnabled);
        Assert.Null(s.DroidRoot);
        var config = s.ToMonitorConfiguration();
        Assert.Contains(SourceKind.Droid, config.EnabledSources);
        Assert.DoesNotContain(SourceKind.Hermes, config.EnabledSources);
        Assert.Null(config.DroidRoot);
    }

    [Fact]
    public void TheDevinFolderAndToggleRoundTripAndReachTheMonitor()
    {
        var devin = Path.Combine(Path.GetTempPath(), "devin-data");
        var settings = AppSettings.Default.WithSourceEnabled(SourceKind.Devin, false) with { DevinRoot = $"  {devin}  " };
        var back = SettingsSerializer.Deserialize(SettingsSerializer.Serialize(settings.Validated()));

        Assert.Equal((false, devin), (back.DevinEnabled, back.DevinRoot));
        var config = back.ToMonitorConfiguration();
        Assert.Equal(devin, config.DevinRoot);
        Assert.DoesNotContain(SourceKind.Devin, config.EnabledSources);
        Assert.NotEqual(AppSettings.Default.ToMonitorConfiguration(), (AppSettings.Default with { DevinRoot = devin }).ToMonitorConfiguration());
        Assert.Null((AppSettings.Default with { DevinRoot = Path.Combine("relative", "devin") }).Validated().DevinRoot);
    }

    [Fact]
    public void OlderSettingsFilesKeepDevinOnWithItsAutomaticRoot()
    {
        var s = SettingsSerializer.Deserialize("""
            { "version": 1, "sources": { "codex": true, "droid": false, "droidRoot": null } }
            """);

        Assert.True(s.DevinEnabled);
        Assert.Null(s.DevinRoot);
        var config = s.ToMonitorConfiguration();
        Assert.Contains(SourceKind.Devin, config.EnabledSources);
        Assert.DoesNotContain(SourceKind.Droid, config.EnabledSources);
        Assert.Null(config.DevinRoot);
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

    [Fact]
    public void OlderSettingsFilesKeepOpenClawOnWithItsAutomaticRoots()
    {
        var s = SettingsSerializer.Deserialize("""
            { "version": 1, "sources": { "codex": true, "pi": false, "piRoot": null } }
            """);

        Assert.True(s.OpenClawEnabled);
        Assert.Null(s.OpenClawRoot);
        var config = s.ToMonitorConfiguration();
        Assert.Contains(SourceKind.OpenClaw, config.EnabledSources);
        Assert.Null(config.OpenClawRoot);
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
