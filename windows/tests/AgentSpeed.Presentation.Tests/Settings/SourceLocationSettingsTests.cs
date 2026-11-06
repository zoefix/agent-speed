using System.Text.Json;
using AgentSpeed.Core;
using AgentSpeed.Core.Locations;
using AgentSpeed.Presentation.Localization;
using AgentSpeed.Presentation.Settings;
using AgentSpeed.Presentation.Settings.Locations;
using AgentSpeed.Presentation.Tests.TestSupport;

namespace AgentSpeed.Presentation.Tests;

/// <summary>
/// DESIGN 5, 資料來源: the location lists in Settings — migration from the single folder of earlier versions, storage, editing
/// and what each row shows (issue #45).
/// </summary>
public sealed class SourceLocationSettingsTests
{
    private static readonly DataLocation Auto = DataLocation.Automatic;

    private static string P(params string[] parts) => Path.Combine([Path.GetPathRoot(Path.GetTempPath())!, "Volumes", .. parts]);

    private static DataLocation At(string path) => DataLocation.At(path);

    private static string Json(params (string Key, string Value)[] legacy) =>
        JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["version"] = 2,
            ["sources"] = legacy.ToDictionary(pair => pair.Key, pair => pair.Value),
        });

    private static JsonElement Sources(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.GetProperty("sources").Clone();
    }

    [Fact]
    public void AnEarlierSingleFolderBecomesTheWholeList()
    {
        var codex = P("work", "codex", "sessions");
        var database = P("dev", ".openclaw", "agents", "main", "agent", "openclaw-agent.sqlite");
        var settings = SettingsSerializer.Deserialize(Json(("codexRoot", codex), ("openclawRoot", database), ("claudeRoot", "")));
        Assert.Equal([At(codex)], settings.Locations[LocationFolder.Codex]);
        Assert.Equal([At(database)], settings.Locations[LocationFolder.OpenClaw]);
        Assert.Equal([Auto], settings.Locations[LocationFolder.Claude]);
        Assert.Equal(settings.Locations, settings.ToMonitorConfiguration().Locations);
        Assert.Null(settings.StoredLocations);
        using var saved = JsonDocument.Parse(SettingsSerializer.Serialize(settings));
        Assert.False(saved.RootElement.TryGetProperty("sourceLocations", out _), "migrating writes nothing");
        Assert.Equal(codex, saved.RootElement.GetProperty("sources").GetProperty("codexRoot").GetString());
        Assert.Equal(("workbuddyAiRoot", "grokBuildRoot"), (LocationFolder.WorkBuddyAi.LegacyKey(), LocationFolder.GrokBuild.LegacyKey()));
    }

    [Fact]
    public void TheOldKeysAreNeverWrittenSoADowngradeKeepsItsFolder()
    {
        var codex = P("work", "codex", "sessions");
        var settings = SettingsSerializer.Deserialize(Json(("codexRoot", codex)));
        settings = settings.AddLocation(LocationFolder.Codex, P("other", "sessions"), []).Settings;
        settings = settings.AddLocation(LocationFolder.Dsh, P("dsh"), []).Settings;
        var json = SettingsSerializer.Serialize(settings);
        var sources = Sources(json);
        Assert.Equal(codex, sources.GetProperty("codexRoot").GetString());
        Assert.False(sources.TryGetProperty("dshRoot", out _));

        var reloaded = SettingsSerializer.Deserialize(json);
        Assert.Equal([At(codex), At(P("other", "sessions"))], reloaded.Locations[LocationFolder.Codex]);
        Assert.Equal([Auto, At(P("dsh"))], reloaded.Locations[LocationFolder.Dsh]);
        var reset = SettingsSerializer.Serialize(reloaded.ResetLocations(LocationFolder.Codex));
        Assert.Equal(codex, Sources(reset).GetProperty("codexRoot").GetString(), StringComparer.Ordinal);
        Assert.Equal([Auto], SettingsSerializer.Deserialize(reset).Locations[LocationFolder.Codex]);
    }

    [Fact]
    public void EditingFollowsTheListRules()
    {
        var folder = LocationFolder.Claude;
        var work = P("work", "claude", "projects");
        var settings = AppSettings.Default;
        LocationAddOutcome outcome;
        (settings, outcome) = settings.AddLocation(folder, work);
        Assert.Equal(LocationAddOutcome.Added, outcome);
        Assert.Equal(LocationAddOutcome.AlreadyListed, settings.AddLocation(folder, work + Path.DirectorySeparatorChar).Outcome);
        Assert.Equal(LocationAddOutcome.AlreadyAutomatic, settings.AddLocation(folder, LiveMonitor.DefaultClaudeRoot).Outcome);
        settings = settings.RemoveLocation(folder, Auto);
        Assert.Equal([At(work)], settings.Locations[folder]);
        settings = settings.RemoveLocation(folder, At(work));
        Assert.Equal([At(work)], settings.Locations[folder]);
        (settings, outcome) = settings.AddLocation(folder, LiveMonitor.DefaultClaudeRoot);
        Assert.Equal(LocationAddOutcome.RestoredAutomatic, outcome);
        Assert.Equal([Auto, At(work)], SettingsSerializer.Deserialize(SettingsSerializer.Serialize(settings)).Locations[folder]);
        for (var index = 0; index < 6; index++)
        {
            settings = settings.AddLocation(folder, P($"w{index}")).Settings;
        }

        Assert.Equal(LocationLists.MaxEntries, settings.Locations[folder].Count);
        var (unchanged, full) = settings.AddLocation(folder, P("w9"));
        Assert.Equal(LocationAddOutcome.Full, full);
        Assert.Same(settings, unchanged);
        settings = settings.ResetLocations(folder);
        Assert.Equal([Auto], SettingsSerializer.Deserialize(SettingsSerializer.Serialize(settings)).Locations[folder]);
        Assert.Same(settings, settings.ResetLocations(folder));
        Assert.Equal(LocationLists.Automatic, settings.StoredLocations);
    }

    [Fact]
    public void RowsShowEachEntry()
    {
        using var temp = new TempDirectory();
        var existing = Directory.CreateDirectory(temp.Combine("sessions")).FullName;
        var missing = temp.Combine("missing", "sessions");
        var settings = AppSettings.Default.AddLocation(LocationFolder.Codex, existing).Settings.AddLocation(LocationFolder.Codex, missing).Settings;
        var entries = LocationEntries.Of(LocationFolder.Codex, settings.Locations);
        Assert.Equal([true, false, false], entries.Select(e => e.IsAutomatic));
        Assert.Equal([LiveMonitor.DefaultCodexRoot], entries[0].Lines.Select(l => l.Path));
        Assert.True(entries[1].Exists);
        Assert.Equal(existing, entries[1].RevealPath);
        Assert.False(entries[2].Exists);
        Assert.Null(entries[2].RevealPath);
        Assert.Equal(LiveMonitor.DefaultGeminiRoot, LocationEntries.Entry(LocationFolder.Gemini, Auto).Lines[0].Path);
        Assert.Equal([LiveMonitor.DefaultOmpRoot], LocationEntries.Entry(LocationFolder.Omp, Auto).Lines.Select(l => l.Path));
        Assert.Equal([LiveMonitor.DefaultPiRoot], LocationEntries.Entry(LocationFolder.Pi, Auto).Lines.Select(l => l.Path));
        Assert.True(LocationFolder.OpenClaw.AcceptsDatabaseFile() && !LocationFolder.Hermes.AcceptsDatabaseFile());

        var database = Directory.CreateDirectory(temp.Combine("agent")).FullName;
        File.WriteAllText(Path.Combine(database, "openclaw-agent.sqlite"), string.Empty);
        var file = LocationEntries.Entry(LocationFolder.OpenClaw, At(Path.Combine(database, "openclaw-agent.sqlite")));
        Assert.True(file.IsFile && file.Exists);
        Assert.False(LocationEntries.Entry(LocationFolder.Codex, At(existing)).IsFile);
        Assert.False(LocationEntries.Entry(LocationFolder.Antigravity, At(temp.Combine("antigravity-backup"))).Exists,
            "a backup store is never read: it shows as missing");
    }

    [Fact]
    public void PathsAreShownWithTheHomeFolderAsTilde()
    {
        var home = P("Users", "me");
        Assert.Equal("~" + Path.DirectorySeparatorChar + Path.Combine(".codex", "sessions"),
            LocationEntries.DisplayPath(Path.Combine(home, ".codex", "sessions"), home));
        Assert.Equal("~", LocationEntries.DisplayPath(home, home));
        Assert.Equal(home + "2", LocationEntries.DisplayPath(home + "2", home));
        Assert.Equal(P("work"), LocationEntries.DisplayPath(P("work"), home));
    }

    [Fact]
    public void TheNewStringsExistInEveryLanguage()
    {
        var table = Localizer.LoadEmbeddedTable();
        foreach (var language in new[] { LanguageCodes.ZhHant, LanguageCodes.ZhHans, LanguageCodes.En, LanguageCodes.Ja })
        {
            var loc = new Localizer(table, language);
            foreach (var key in new[]
                     {
                         "settings.sources.add", "settings.sources.default", "settings.sources.remove", "settings.sources.keepOne",
                         "settings.sources.alreadyListed", "settings.sources.alreadyDefault", "settings.sources.locationsHint",
                         "win.settings.sources.reveal", "win.settings.sources.copyPath", "win.settings.sources.folder", "win.common.ok",
                     })
            {
                Assert.True(table.TryGet(key, language, out var text) && text.Length > 0, $"{key} {language}");
            }

            Assert.Contains("8", LocationEntries.LimitText(loc), StringComparison.Ordinal);
            Assert.Contains("~/work", loc.Format("settings.sources.removeLocation", "~/work"), StringComparison.Ordinal);
            Assert.Equal(loc["settings.sources.alreadyDefault"], LocationEntries.RefusalText(LocationAddOutcome.AlreadyAutomatic, loc));
            Assert.Equal(LocationEntries.LimitText(loc), LocationEntries.RefusalText(LocationAddOutcome.Full, loc));
            Assert.Null(LocationEntries.RefusalText(LocationAddOutcome.Added, loc));
        }

        Assert.Equal("settings.sources.sharedRoot", LocationEntries.FootnoteKey(LocationFolder.Claude));
        Assert.Null(LocationEntries.FootnoteKey(LocationFolder.Codex));
    }

    [Fact]
    public void EarlierSingleFoldersCompareByValue()
    {
        var one = LegacyLocations.Empty.With(LocationFolder.Codex, P("a"));
        var two = LegacyLocations.Empty.With(LocationFolder.Codex, P("a"));
        Assert.Equal(one, two);
        Assert.Equal(AppSettings.Default with { LegacyRoots = one }, AppSettings.Default with { LegacyRoots = two });
        Assert.Same(one, one.Cleaned());
    }
}
