using System.Text.Json;
using AgentSpeed.Core;
using AgentSpeed.Core.Locations;
using AgentSpeed.Presentation.Settings;
using AgentSpeed.Presentation.Settings.Locations;
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

    private static AppSettings With(AppSettings settings, params (LocationFolder Folder, string Path)[] lists) =>
        settings.WithLocations(lists.Aggregate(settings.Locations, (all, list) => all.With(list.Folder, [DataLocation.At(list.Path)])));

    private static IReadOnlyList<DataLocation> Only(string path) => [DataLocation.At(path)];

    /// <summary>The folders of a settings file's stored lists, by list name.</summary>
    private static string ListedPath(JsonElement root, string list) =>
        root.GetProperty("sourceLocations").GetProperty("lists").GetProperty(list)[0].GetProperty("path").GetString()!;

    [Theory]
    [InlineData(SourceKind.OpenCode, SourceKind.Gemini)]
    [InlineData(SourceKind.Omp, SourceKind.Antigravity)]
    public void FoldersOfTwoSourcesReachTheMonitorAndRoundTrip(SourceKind off, SourceKind on)
    {
        string PathOf(SourceKind kind) => kind switch
        {
            SourceKind.OpenCode => OpenCode,
            SourceKind.Gemini => Gemini,
            SourceKind.Omp => Omp,
            _ => Antigravity,
        };

        var (offFolder, onFolder) = (LocationFolders.Of(off), LocationFolders.Of(on));
        var settings = With(AppSettings.Default.WithSourceEnabled(off, false), (offFolder, PathOf(off)), (onFolder, PathOf(on)));
        var config = settings.ToMonitorConfiguration();
        Assert.DoesNotContain(off, config.EnabledSources);
        Assert.Contains(on, config.EnabledSources);
        Assert.Equal(Only(PathOf(off)), config.ListOf(offFolder));
        Assert.Equal(Only(PathOf(on)), config.ListOf(onFolder));
        Assert.NotEqual(AppSettings.Default.ToMonitorConfiguration(), With(AppSettings.Default, (onFolder, PathOf(on))).ToMonitorConfiguration());

        var json = SettingsSerializer.Serialize(settings);
        using (var document = JsonDocument.Parse(json))
        {
            var sources = document.RootElement.GetProperty("sources");
            Assert.False(sources.GetProperty(OrbSources.KeyOf(off)).GetBoolean());
            Assert.True(sources.GetProperty(OrbSources.KeyOf(on)).GetBoolean());
            Assert.Equal(PathOf(off), ListedPath(document.RootElement, offFolder.Key()));
            Assert.Equal(PathOf(on), ListedPath(document.RootElement, onFolder.Key()));
            Assert.False(sources.TryGetProperty(offFolder.LegacyKey(), out _), "lists never write the single-folder keys");
        }

        var back = SettingsSerializer.Deserialize(json);
        Assert.False(back.IsSourceEnabled(off));
        Assert.True(back.IsSourceEnabled(on));
        Assert.Equal(settings.Locations, back.Locations);
    }

    [Theory]
    [InlineData("""{ "version": 1, "sources": { "codex": true, "deepseek": false } }""")]
    [InlineData("""{ "version": 1, "sources": { "codex": true, "opencode": false, "gemini": true, "geminiRoot": null } }""")]
    [InlineData("""{ "version": 1, "sources": { "codex": true, "omp": false, "antigravity": true, "antigravityRoot": null } }""")]
    public void OlderFilesKeepNewerSourcesOnWithTheirAutomaticRoots(string json)
    {
        var s = SettingsSerializer.Deserialize(json);
        Assert.True(s.GeminiEnabled);
        Assert.True(s.AntigravityEnabled);
        Assert.True(s.WorkBuddyEnabled);
        Assert.True(s.WorkBuddyAiEnabled);
        Assert.True(s.Locations.IsAutomaticOnly);
        Assert.Null(s.HistoryWindow);
        var config = s.ToMonitorConfiguration();
        Assert.Contains(SourceKind.Antigravity, config.EnabledSources);
        Assert.Contains(SourceKind.WorkBuddyAi, config.EnabledSources);
        Assert.All(LocationFolders.All, folder => Assert.Equal(LocationLists.AutomaticOnly, config.ListOf(folder)));
    }

    [Fact]
    public void WorkBuddyFoldersReachTheMonitorAndRoundTrip()
    {
        var settings = With(AppSettings.Default.WithSourceEnabled(SourceKind.WorkBuddy, false),
            (LocationFolder.WorkBuddy, WorkBuddy), (LocationFolder.WorkBuddyAi, WorkBuddyAiProjects));
        Assert.False(settings.WorkBuddyEnabled);
        Assert.True(settings.WorkBuddyAiEnabled);

        var config = settings.ToMonitorConfiguration();
        Assert.DoesNotContain(SourceKind.WorkBuddy, config.EnabledSources);
        Assert.Contains(SourceKind.WorkBuddyAi, config.EnabledSources);
        Assert.Equal(Only(WorkBuddy), config.ListOf(LocationFolder.WorkBuddy));
        Assert.Equal(Only(WorkBuddyAiProjects), config.ListOf(LocationFolder.WorkBuddyAi));
        var defaults = AppSettings.Default.ToMonitorConfiguration();
        var china = With(AppSettings.Default, (LocationFolder.WorkBuddy, WorkBuddy)).ToMonitorConfiguration();
        var international = With(AppSettings.Default, (LocationFolder.WorkBuddyAi, WorkBuddy)).ToMonitorConfiguration();
        Assert.NotEqual(defaults, china);
        Assert.NotEqual(defaults, international);
        Assert.NotEqual(china, international);
        Assert.Equal(LocationLists.AutomaticOnly, china.ListOf(LocationFolder.WorkBuddyAi));
        Assert.Equal(LocationLists.AutomaticOnly, international.ListOf(LocationFolder.WorkBuddy));

        var back = SettingsSerializer.Deserialize(SettingsSerializer.Serialize(settings));
        Assert.False(back.WorkBuddyEnabled);
        Assert.True(back.WorkBuddyAiEnabled);
        Assert.Equal(settings.Locations, back.Locations);
    }

    [Fact]
    public void EachWorkBuddyRowShowsItsOwnDataFolder()
    {
        Assert.Equal(".workbuddy", Path.GetFileName(LiveMonitor.DefaultWorkBuddyFolder));
        Assert.Equal(".workbuddy-ai", Path.GetFileName(LiveMonitor.DefaultWorkBuddyAiFolder));
        Assert.Equal(LiveMonitor.DefaultWorkBuddyFolder, LiveMonitor.WorkBuddyDataFolder(SourceKind.WorkBuddy, null));
        Assert.Equal(LiveMonitor.DefaultWorkBuddyAiFolder, LiveMonitor.WorkBuddyDataFolder(SourceKind.WorkBuddyAi, null));
        Assert.Equal(Path.GetDirectoryName(WorkBuddyAiProjects), LiveMonitor.WorkBuddyDataFolder(SourceKind.WorkBuddyAi, WorkBuddyAiProjects));
        Assert.Equal(WorkBuddy, LiveMonitor.WorkBuddyDataFolder(SourceKind.WorkBuddy, WorkBuddy));
        Assert.Equal([LiveMonitor.DefaultWorkBuddyFolder], AutomaticLocations.All(LocationFolder.WorkBuddy));
        Assert.Equal([LiveMonitor.DefaultWorkBuddyAiFolder], AutomaticLocations.All(LocationFolder.WorkBuddyAi));
    }

    [Theory]
    [InlineData("""
        { "sources": { "workbuddy": "no", "workbuddyAi": 0, "workbuddyRoot": 5, "workbuddyAiRoot": "relative/projects" } }
        """)]
    [InlineData("""
        { "sources": { "workbuddy": null, "workbuddyAi": [], "workbuddyRoot": "", "workbuddyAiRoot": "   " } }
        """)]
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
        Assert.All(Enum.GetValues<SourceKind>(), kind => Assert.True(s.IsSourceEnabled(kind)));
        Assert.Equal(0, s.LegacyRoots.Count);
        Assert.True(s.Locations.IsAutomaticOnly);
    }

    [Fact]
    public void EarlierSingleFoldersAreCleanedLikeBefore()
    {
        var legacy = LegacyLocations.Empty.With(LocationFolder.WorkBuddy, $"  {WorkBuddy}  ").With(LocationFolder.WorkBuddyAi, $"{WorkBuddyAiProjects}\0")
            .With(LocationFolder.Omp, $"  {Omp}  ").With(LocationFolder.Antigravity, Path.Combine("relative", "store"));
        var s = (AppSettings.Default with { LegacyRoots = legacy }).Validated();
        Assert.Equal(WorkBuddy, s.LegacyRoots[LocationFolder.WorkBuddy]);
        Assert.Null(s.LegacyRoots[LocationFolder.WorkBuddyAi]);
        Assert.Equal(Omp, s.LegacyRoots[LocationFolder.Omp]);
        Assert.Null(s.LegacyRoots[LocationFolder.Antigravity]);
        Assert.Equal(Only(WorkBuddy), s.Locations[LocationFolder.WorkBuddy]);
        Assert.Equal(Only(Omp), s.Locations[LocationFolder.Omp]);
        Assert.Equal(LocationLists.AutomaticOnly, s.Locations[LocationFolder.Antigravity]);
    }

    [Fact]
    public void AChosenBackupStoreIsKeptForTheMonitorToSkip()
    {
        var backup = Path.Combine(Path.GetTempPath(), ".gemini", "antigravity-backup");
        var back = SettingsSerializer.Deserialize(SettingsSerializer.Serialize(With(AppSettings.Default, (LocationFolder.Antigravity, backup))));
        Assert.Equal(Only(backup), back.Locations[LocationFolder.Antigravity]);
        Assert.Equal(Only(backup), back.ToMonitorConfiguration().ListOf(LocationFolder.Antigravity));
        Assert.Empty(LiveMonitor.AntigravityStores(backup));
        Assert.Equal([Antigravity], LiveMonitor.AntigravityStores(Antigravity));
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
