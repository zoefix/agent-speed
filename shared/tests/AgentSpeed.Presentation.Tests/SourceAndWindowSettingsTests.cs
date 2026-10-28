using System.Text.Json;
using AgentSpeed.Core;
using AgentSpeed.Presentation.Settings;
using AgentSpeed.Presentation.Settings.Orbs;

namespace AgentSpeed.Presentation.Tests;

public class SourceAndWindowSettingsTests
{
    private static readonly string OpenCode = Path.Combine(Path.GetTempPath(), "opencode");
    private static readonly string Gemini = Path.Combine(Path.GetTempPath(), ".gemini", "tmp");
    private static readonly string Omp = Path.Combine(Path.GetTempPath(), ".omp", "profiles", "work", "agent", "sessions");
    private static readonly string Antigravity = Path.Combine(Path.GetTempPath(), ".gemini", "antigravity-cli");
    private static readonly string WorkBuddy = Path.Combine(Path.GetTempPath(), "wb", ".workbuddy");
    private static readonly string WorkBuddyAiProjects = Path.Combine(Path.GetTempPath(), "wb", ".workbuddy-ai", "projects");

    [Fact]
    public void EverySourceKindHasItsOwnToggle()
    {
        foreach (var kind in Enum.GetValues<SourceKind>())
        {
            Assert.True(AppSettings.Default.IsSourceEnabled(kind), $"{kind} should default to on");
            var off = AppSettings.Default.WithSourceEnabled(kind, false);
            Assert.False(off.IsSourceEnabled(kind));
            Assert.All(Enum.GetValues<SourceKind>().Where(k => k != kind), other => Assert.True(off.IsSourceEnabled(other)));
            Assert.True(off.WithSourceEnabled(kind, true).IsSourceEnabled(kind));
            Assert.DoesNotContain(kind, off.ToMonitorConfiguration().EnabledSources);
        }

        Assert.True(AppSettings.Default.ToMonitorConfiguration().EnabledSources.SetEquals(Enum.GetValues<SourceKind>()));
    }

    [Fact]
    public void EverySourceToggleIsStoredUnderItsMetricsKey()
    {
        foreach (var kind in Enum.GetValues<SourceKind>())
        {
            var json = SettingsSerializer.Serialize(AppSettings.Default.WithSourceEnabled(kind, false));
            using (var document = JsonDocument.Parse(json))
            {
                var sources = document.RootElement.GetProperty("sources");
                Assert.All(Enum.GetValues<SourceKind>(), other =>
                    Assert.Equal(other != kind, sources.GetProperty(OrbSources.KeyOf(other)).GetBoolean()));
            }

            var back = SettingsSerializer.Deserialize(json);
            Assert.All(Enum.GetValues<SourceKind>(), other => Assert.Equal(other != kind, back.IsSourceEnabled(other)));
        }
    }

    [Fact]
    public void OpenCodeAndGeminiFoldersReachTheMonitorAndRoundTrip()
    {
        var settings = AppSettings.Default.WithSourceEnabled(SourceKind.OpenCode, false) with { OpenCodeRoot = OpenCode, GeminiRoot = Gemini };
        var config = settings.ToMonitorConfiguration();
        Assert.Equal(OpenCode, config.OpenCodeRoot);
        Assert.Equal(Gemini, config.GeminiRoot);
        Assert.NotEqual(AppSettings.Default.ToMonitorConfiguration(), (AppSettings.Default with { GeminiRoot = Gemini }).ToMonitorConfiguration());

        var json = SettingsSerializer.Serialize(settings);
        using (var document = JsonDocument.Parse(json))
        {
            var sources = document.RootElement.GetProperty("sources");
            Assert.False(sources.GetProperty("opencode").GetBoolean());
            Assert.True(sources.GetProperty("gemini").GetBoolean());
            Assert.Equal(OpenCode, sources.GetProperty("opencodeRoot").GetString());
            Assert.Equal(Gemini, sources.GetProperty("geminiRoot").GetString());
        }

        var back = SettingsSerializer.Deserialize(json);
        Assert.False(back.OpenCodeEnabled);
        Assert.True(back.GeminiEnabled);
        Assert.Equal(OpenCode, back.OpenCodeRoot);
        Assert.Equal(Gemini, back.GeminiRoot);
    }

    [Fact]
    public void OlderFilesKeepOpenCodeAndGeminiOnWithDefaultFolders()
    {
        var s = SettingsSerializer.Deserialize("""{ "version": 1, "sources": { "codex": true, "deepseek": false } }""");
        Assert.True(s.OpenCodeEnabled);
        Assert.True(s.GeminiEnabled);
        Assert.Null(s.OpenCodeRoot);
        Assert.Null(s.GeminiRoot);
        Assert.Null(s.HistoryWindow);
    }

    [Fact]
    public void OmpAndAntigravityFoldersReachTheMonitorAndRoundTrip()
    {
        var settings = AppSettings.Default.WithSourceEnabled(SourceKind.Omp, false) with { OmpRoot = Omp, AntigravityRoot = Antigravity };
        var config = settings.ToMonitorConfiguration();
        Assert.DoesNotContain(SourceKind.Omp, config.EnabledSources);
        Assert.Contains(SourceKind.Antigravity, config.EnabledSources);
        Assert.Equal(Omp, config.OmpRoot);
        Assert.Equal(Antigravity, config.AntigravityRoot);
        Assert.NotEqual(AppSettings.Default.ToMonitorConfiguration(), (AppSettings.Default with { OmpRoot = Omp }).ToMonitorConfiguration());
        Assert.NotEqual(AppSettings.Default.ToMonitorConfiguration(),
            (AppSettings.Default with { AntigravityRoot = Antigravity }).ToMonitorConfiguration());

        var json = SettingsSerializer.Serialize(settings);
        using (var document = JsonDocument.Parse(json))
        {
            var sources = document.RootElement.GetProperty("sources");
            Assert.False(sources.GetProperty("omp").GetBoolean());
            Assert.True(sources.GetProperty("antigravity").GetBoolean());
            Assert.Equal(Omp, sources.GetProperty("ompRoot").GetString());
            Assert.Equal(Antigravity, sources.GetProperty("antigravityRoot").GetString());
        }

        var back = SettingsSerializer.Deserialize(json);
        Assert.False(back.OmpEnabled);
        Assert.True(back.AntigravityEnabled);
        Assert.Equal(Omp, back.OmpRoot);
        Assert.Equal(Antigravity, back.AntigravityRoot);

        using var defaults = JsonDocument.Parse(SettingsSerializer.Serialize(AppSettings.Default));
        var defaultSources = defaults.RootElement.GetProperty("sources");
        Assert.True(defaultSources.GetProperty("omp").GetBoolean());
        Assert.True(defaultSources.GetProperty("antigravity").GetBoolean());
        Assert.Equal(JsonValueKind.Null, defaultSources.GetProperty("ompRoot").ValueKind);
        Assert.Equal(JsonValueKind.Null, defaultSources.GetProperty("antigravityRoot").ValueKind);
    }

    [Fact]
    public void OlderFilesKeepOmpAndAntigravityOnWithDefaultFolders()
    {
        var s = SettingsSerializer.Deserialize("""
            { "version": 1, "sources": { "codex": true, "opencode": false, "gemini": true, "geminiRoot": null } }
            """);
        Assert.True(s.OmpEnabled);
        Assert.True(s.AntigravityEnabled);
        Assert.Null(s.OmpRoot);
        Assert.Null(s.AntigravityRoot);
        Assert.False(s.OpenCodeEnabled);

        var config = s.ToMonitorConfiguration();
        Assert.Contains(SourceKind.Omp, config.EnabledSources);
        Assert.Contains(SourceKind.Antigravity, config.EnabledSources);
        Assert.Null(config.OmpRoot);
        Assert.Null(config.AntigravityRoot);
    }

    [Fact]
    public void WorkBuddyFoldersReachTheMonitorAndRoundTrip()
    {
        var settings = AppSettings.Default.WithSourceEnabled(SourceKind.WorkBuddy, false) with
        {
            WorkBuddyRoot = WorkBuddy,
            WorkBuddyAiRoot = WorkBuddyAiProjects,
        };
        Assert.False(settings.WorkBuddyEnabled);
        Assert.True(settings.WorkBuddyAiEnabled);

        var config = settings.ToMonitorConfiguration();
        Assert.DoesNotContain(SourceKind.WorkBuddy, config.EnabledSources);
        Assert.Contains(SourceKind.WorkBuddyAi, config.EnabledSources);
        Assert.Equal(WorkBuddy, config.WorkBuddyRoot);
        Assert.Equal(WorkBuddyAiProjects, config.WorkBuddyAiRoot);
        var defaults = AppSettings.Default.ToMonitorConfiguration();
        var china = (AppSettings.Default with { WorkBuddyRoot = WorkBuddy }).ToMonitorConfiguration();
        var international = (AppSettings.Default with { WorkBuddyAiRoot = WorkBuddy }).ToMonitorConfiguration();
        Assert.NotEqual(defaults, china);
        Assert.NotEqual(defaults, international);
        Assert.NotEqual(china, international);
        Assert.Null(china.WorkBuddyAiRoot);
        Assert.Null(international.WorkBuddyRoot);

        var json = SettingsSerializer.Serialize(settings);
        using (var document = JsonDocument.Parse(json))
        {
            var sources = document.RootElement.GetProperty("sources");
            Assert.False(sources.GetProperty("workbuddy").GetBoolean());
            Assert.True(sources.GetProperty("workbuddyAi").GetBoolean());
            Assert.Equal(WorkBuddy, sources.GetProperty("workbuddyRoot").GetString());
            Assert.Equal(WorkBuddyAiProjects, sources.GetProperty("workbuddyAiRoot").GetString());
        }

        var back = SettingsSerializer.Deserialize(json);
        Assert.False(back.WorkBuddyEnabled);
        Assert.True(back.WorkBuddyAiEnabled);
        Assert.Equal(WorkBuddy, back.WorkBuddyRoot);
        Assert.Equal(WorkBuddyAiProjects, back.WorkBuddyAiRoot);

        using var defaultDocument = JsonDocument.Parse(SettingsSerializer.Serialize(AppSettings.Default));
        var defaultSources = defaultDocument.RootElement.GetProperty("sources");
        Assert.True(defaultSources.GetProperty("workbuddy").GetBoolean());
        Assert.True(defaultSources.GetProperty("workbuddyAi").GetBoolean());
        Assert.Equal(JsonValueKind.Null, defaultSources.GetProperty("workbuddyRoot").ValueKind);
        Assert.Equal(JsonValueKind.Null, defaultSources.GetProperty("workbuddyAiRoot").ValueKind);
    }

    [Fact]
    public void EachWorkBuddyRowShowsItsOwnDataFolder()
    {
        Assert.Equal(".workbuddy", Path.GetFileName(LiveMonitor.DefaultWorkBuddyFolder));
        Assert.Equal(".workbuddy-ai", Path.GetFileName(LiveMonitor.DefaultWorkBuddyAiFolder));
        var d = AppSettings.Default;
        Assert.Equal(LiveMonitor.DefaultWorkBuddyFolder, LiveMonitor.WorkBuddyDataFolder(SourceKind.WorkBuddy, d.WorkBuddyRoot));
        Assert.Equal(LiveMonitor.DefaultWorkBuddyAiFolder, LiveMonitor.WorkBuddyDataFolder(SourceKind.WorkBuddyAi, d.WorkBuddyAiRoot));

        var changed = SettingsSerializer.Deserialize(SettingsSerializer.Serialize(d with { WorkBuddyAiRoot = WorkBuddyAiProjects }));
        Assert.Equal(LiveMonitor.DefaultWorkBuddyFolder, LiveMonitor.WorkBuddyDataFolder(SourceKind.WorkBuddy, changed.WorkBuddyRoot));
        Assert.Equal(Path.GetDirectoryName(WorkBuddyAiProjects), LiveMonitor.WorkBuddyDataFolder(SourceKind.WorkBuddyAi, changed.WorkBuddyAiRoot));
        Assert.Equal(WorkBuddy, LiveMonitor.WorkBuddyDataFolder(SourceKind.WorkBuddy, WorkBuddy));

        var reset = changed with { WorkBuddyAiRoot = null };
        Assert.Equal(LiveMonitor.DefaultWorkBuddyAiFolder, LiveMonitor.WorkBuddyDataFolder(SourceKind.WorkBuddyAi, reset.WorkBuddyAiRoot));
    }

    [Fact]
    public void OlderFilesKeepBothWorkBuddyEditionsOnWithDefaultFolders()
    {
        var s = SettingsSerializer.Deserialize("""
            { "version": 1, "sources": { "codex": true, "omp": false, "antigravity": true, "antigravityRoot": null } }
            """);
        Assert.True(s.WorkBuddyEnabled);
        Assert.True(s.WorkBuddyAiEnabled);
        Assert.Null(s.WorkBuddyRoot);
        Assert.Null(s.WorkBuddyAiRoot);
        Assert.False(s.OmpEnabled);

        var config = s.ToMonitorConfiguration();
        Assert.Contains(SourceKind.WorkBuddy, config.EnabledSources);
        Assert.Contains(SourceKind.WorkBuddyAi, config.EnabledSources);
        Assert.Null(config.WorkBuddyRoot);
        Assert.Null(config.WorkBuddyAiRoot);
    }

    [Theory]
    [InlineData("""
        { "sources": { "workbuddy": "no", "workbuddyAi": 0, "workbuddyRoot": 5, "workbuddyAiRoot": "relative/projects" } }
        """)]
    [InlineData("""
        { "sources": { "workbuddy": null, "workbuddyAi": [], "workbuddyRoot": "", "workbuddyAiRoot": "   " } }
        """)]
    public void InvalidWorkBuddyValuesFallBackPerField(string json)
    {
        var s = SettingsSerializer.Deserialize(json);
        Assert.True(s.WorkBuddyEnabled);
        Assert.True(s.WorkBuddyAiEnabled);
        Assert.Null(s.WorkBuddyRoot);
        Assert.Null(s.WorkBuddyAiRoot);
    }

    [Fact]
    public void WorkBuddyPathsAreCleaned()
    {
        var s = (AppSettings.Default with { WorkBuddyRoot = $"  {WorkBuddy}  ", WorkBuddyAiRoot = $"{WorkBuddyAiProjects}\0" }).Validated();
        Assert.Equal(WorkBuddy, s.WorkBuddyRoot);
        Assert.Null(s.WorkBuddyAiRoot);
        Assert.Null((AppSettings.Default with { WorkBuddyRoot = Path.Combine("relative", ".workbuddy") }).Validated().WorkBuddyRoot);
        Assert.Null((AppSettings.Default with { WorkBuddyAiRoot = Path.Combine("relative", "projects") }).Validated().WorkBuddyAiRoot);
    }

    [Fact]
    public void AChosenBackupStoreIsKeptForTheMonitorToSkip()
    {
        var backup = Path.Combine(Path.GetTempPath(), ".gemini", "antigravity-backup");
        var back = SettingsSerializer.Deserialize(SettingsSerializer.Serialize(AppSettings.Default with { AntigravityRoot = backup }));
        Assert.Equal(backup, back.AntigravityRoot);
        Assert.Equal(backup, back.ToMonitorConfiguration().AntigravityRoot);
        Assert.Empty(LiveMonitor.AntigravityStores(backup));
        Assert.Equal([Antigravity], LiveMonitor.AntigravityStores(Antigravity));
    }

    [Theory]
    [InlineData("""
        { "sources": { "opencode": "no", "gemini": 0, "omp": "off", "antigravity": 1,
          "opencodeRoot": 5, "geminiRoot": "relative/tmp", "ompRoot": 7, "antigravityRoot": "relative/store" } }
        """)]
    [InlineData("""
        { "sources": { "opencode": null, "gemini": [], "omp": null, "antigravity": {},
          "opencodeRoot": null, "geminiRoot": "", "ompRoot": null, "antigravityRoot": "   " } }
        """)]
    public void InvalidSourceValuesFallBackPerField(string json)
    {
        var s = SettingsSerializer.Deserialize(json);
        Assert.True(s.OpenCodeEnabled);
        Assert.True(s.GeminiEnabled);
        Assert.True(s.OmpEnabled);
        Assert.True(s.AntigravityEnabled);
        Assert.Null(s.OpenCodeRoot);
        Assert.Null(s.GeminiRoot);
        Assert.Null(s.OmpRoot);
        Assert.Null(s.AntigravityRoot);
    }

    [Fact]
    public void OmpAndAntigravityPathsAreCleaned()
    {
        var s = (AppSettings.Default with { OmpRoot = $"  {Omp}  ", AntigravityRoot = $"{Antigravity}\0" }).Validated();
        Assert.Equal(Omp, s.OmpRoot);
        Assert.Null(s.AntigravityRoot);
        Assert.Null((AppSettings.Default with { OmpRoot = Path.Combine("relative", "sessions") }).Validated().OmpRoot);
    }

    [Fact]
    public void HistoryWindowFrameRoundTrips()
    {
        var frame = new WindowFrame(-1920, 40.5, 920, 680, Maximized: false);
        var json = SettingsSerializer.Serialize(AppSettings.Default with { HistoryWindow = frame });
        using (var document = JsonDocument.Parse(json))
        {
            var window = document.RootElement.GetProperty("history").GetProperty("window");
            Assert.Equal(-1920, window.GetProperty("left").GetDouble());
            Assert.Equal(680, window.GetProperty("height").GetDouble());
            Assert.False(window.GetProperty("maximized").GetBoolean());
        }

        Assert.Equal(frame, SettingsSerializer.Deserialize(json).HistoryWindow);

        using var defaults = JsonDocument.Parse(SettingsSerializer.Serialize(AppSettings.Default));
        Assert.Equal(JsonValueKind.Null, defaults.RootElement.GetProperty("history").GetProperty("window").ValueKind);
        Assert.Null(SettingsSerializer.Deserialize(SettingsSerializer.Serialize(AppSettings.Default)).HistoryWindow);
    }

    [Theory]
    [InlineData(0, 0, 719, 680)]
    [InlineData(0, 0, 920, 539)]
    [InlineData(0, 0, 20_000, 680)]
    [InlineData(0, 0, 920, 20_000)]
    [InlineData(100_001, 0, 920, 680)]
    [InlineData(0, -100_001, 920, 680)]
    [InlineData(double.NaN, 0, 920, 680)]
    [InlineData(0, double.PositiveInfinity, 920, 680)]
    [InlineData(0, 0, double.NaN, 680)]
    public void InvalidHistoryFramesAreDropped(double left, double top, double width, double height)
    {
        var settings = AppSettings.Default with { HistoryWindow = new WindowFrame(left, top, width, height, false) };
        Assert.Null(settings.Validated().HistoryWindow);
        Assert.Null(SettingsSerializer.Deserialize(SettingsSerializer.Serialize(settings)).HistoryWindow);
    }

    [Theory]
    [InlineData(720, 540)]
    [InlineData(16_384, 16_384)]
    public void HistoryFrameLimitsAreInclusive(double width, double height)
    {
        var frame = new WindowFrame(-100_000, 100_000, width, height, true);
        Assert.Equal(frame, (AppSettings.Default with { HistoryWindow = frame }).Validated().HistoryWindow);
    }

    [Theory]
    [InlineData("""{ "history": { "window": { "left": 10, "top": 10, "width": 920 } } }""")]
    [InlineData("""{ "history": { "window": { "left": "10", "top": 10, "width": 920, "height": 680 } } }""")]
    [InlineData("""{ "history": { "window": [] } }""")]
    [InlineData("""{ "history": { "window": null } }""")]
    [InlineData("""{ "history": [] }""")]
    [InlineData("""{ "history": 3 }""")]
    public void MalformedHistoryFramesAreIgnored(string json)
    {
        Assert.Null(SettingsSerializer.Deserialize(json).HistoryWindow);
    }

    [Fact]
    public void MissingMaximizedDefaultsToFalse()
    {
        var s = SettingsSerializer.Deserialize("""{ "history": { "window": { "left": 10, "top": 20, "width": 900, "height": 600 } } }""");
        Assert.Equal(new WindowFrame(10, 20, 900, 600, false), s.HistoryWindow);
    }
}
