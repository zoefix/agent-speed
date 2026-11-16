using AgentSpeed.Core;
using AgentSpeed.Core.Locations;
using AgentSpeed.Core.Reporting;
using AgentSpeed.Presentation.Characters;
using AgentSpeed.Presentation.Colors;
using AgentSpeed.Presentation.Formatting;
using AgentSpeed.Presentation.History;
using AgentSpeed.Presentation.Localization;
using AgentSpeed.Presentation.Settings;
using AgentSpeed.Presentation.Settings.Locations;
using AgentSpeed.Presentation.Settings.Orbs;
using AgentSpeed.Presentation.Tests.TestSupport;

namespace AgentSpeed.Presentation.Tests;

/// <summary>
/// CodeBuddy Code in the app (DESIGN 1, 5, 9; METRICS 4.20): its color, its settings group and location list, its ids and its strings.
/// </summary>
public sealed class CodeBuddySettingsTests
{
    private static readonly StringTable Embedded = Localizer.LoadEmbeddedTable();

    [Fact]
    public void SettingsSavedBeforeCodeBuddyCodeTurnItOnWithItsAutomaticEntry()
    {
        var settings = SettingsSerializer.Deserialize("""
            { "version": 1, "sources": { "codex": true, "qoder": true, "qoderCli": false } }
            """);

        Assert.True(settings.IsSourceEnabled(SourceKind.CodeBuddyCode));
        Assert.False(settings.IsSourceEnabled(SourceKind.QoderCli));
        Assert.Equal(LocationLists.AutomaticOnly, settings.Locations[LocationFolder.CodeBuddyCode]);
        Assert.Contains(SourceKind.CodeBuddyCode, settings.ToMonitorConfiguration().EnabledSources);
        Assert.Equal(LocationFolder.CodeBuddyCode, LocationFolders.All[^1]);
        Assert.Equal(SourceKind.CodeBuddyCode, Enum.GetValues<SourceKind>()[^1]);

        var off = SettingsSerializer.Deserialize(SettingsSerializer.Serialize(settings.WithSourceEnabled(SourceKind.CodeBuddyCode, false)));
        Assert.False(off.IsSourceEnabled(SourceKind.CodeBuddyCode));
        Assert.False(off.CodeBuddyCodeEnabled);
        Assert.DoesNotContain(SourceKind.CodeBuddyCode, off.ToMonitorConfiguration().EnabledSources);
        Assert.Contains("\"codebuddyCode\": false", SettingsSerializer.Serialize(off), StringComparison.Ordinal);
    }

    [Fact]
    public void TheAutomaticEntryNamesTheProjectsFolder()
    {
        var entry = LocationEntries.Entry(LocationFolder.CodeBuddyCode, DataLocation.Automatic);
        Assert.Equal([LiveMonitor.DefaultCodeBuddyCodeRoot], entry.Lines.Select(l => l.Path));
        if (Environment.GetEnvironmentVariable("CODEBUDDY_CONFIG_DIR") is not { Length: > 0 })
        {
            Assert.Equal(Path.Combine(".codebuddy", "projects"),
                Path.Combine(Path.GetFileName(Path.GetDirectoryName(entry.Lines[0].Path)!), Path.GetFileName(entry.Lines[0].Path)));
        }

        Assert.Equal("settings.sources.codebuddyCodeHint", LocationEntries.FootnoteKey(LocationFolder.CodeBuddyCode));
        Assert.False(AutomaticLocations.AcceptsFile(LocationFolder.CodeBuddyCode));
    }

    /// <summary>Add Folder takes the config folder or its <c>projects</c> folder; a config folder without <c>projects</c> is not found.</summary>
    [Fact]
    public void AConfigFolderAndItsProjectsFolderAreOneLocationFoundByItsProjects()
    {
        using var temp = new TempDirectory();
        var config = Directory.CreateDirectory(temp.Combine(".codebuddy")).FullName;
        File.WriteAllText(Path.Combine(config, "settings.json"), "{}");
        var lists = LocationLists.Automatic;
        (lists, var outcome) = lists.Add(config, LocationFolder.CodeBuddyCode, []);
        Assert.Equal(LocationAddOutcome.Added, outcome);
        var entry = LocationEntries.Entry(LocationFolder.CodeBuddyCode, DataLocation.At(config));
        Assert.False(entry.Exists);
        Assert.Equal(config, entry.RevealPath);

        var projects = Directory.CreateDirectory(Path.Combine(config, "projects")).FullName;
        Assert.Equal(LocationAddOutcome.AlreadyListed, lists.Add(projects, LocationFolder.CodeBuddyCode, []).Outcome);
        Assert.True(LocationEntries.Entry(LocationFolder.CodeBuddyCode, DataLocation.At(config)).Exists);
        Assert.True(LocationEntries.Entry(LocationFolder.CodeBuddyCode, DataLocation.At(projects)).Exists);
        Assert.Equal([DataLocation.Automatic, DataLocation.At(config)], lists[LocationFolder.CodeBuddyCode]);

        var settings = AppSettings.Default.WithLocations(lists);
        var back = SettingsSerializer.Deserialize(SettingsSerializer.Serialize(settings));
        Assert.Equal(lists[LocationFolder.CodeBuddyCode], back.Locations[LocationFolder.CodeBuddyCode]);
        Assert.Equal(lists[LocationFolder.CodeBuddyCode], back.ToMonitorConfiguration().ListOf(LocationFolder.CodeBuddyCode));
    }

    [Fact]
    public void ColorsIdsAndStrings()
    {
        Assert.Equal("#18AB06", HeatPalette.SourceColor(SourceKind.CodeBuddyCode).ToHex());
        Assert.Equal("codebuddy-code", HistoryCsv.SourceId(SourceKind.CodeBuddyCode));
        Assert.Equal("codebuddyCode", ReportBody.SourceId(SourceKind.CodeBuddyCode));
        Assert.Equal("codebuddyCode", OrbSources.KeyOf(SourceKind.CodeBuddyCode));
        Assert.Equal(SourceSet.Of(SourceKind.CodeBuddyCode), OrbSources.FromKeys(["codebuddyCode"]).Known);
        Assert.Equal("source.codebuddyCode", DisplayText.SourceKey(SourceKind.CodeBuddyCode));
        Assert.Equal("tencent", CharacterVendors.Fallback(SourceKind.CodeBuddyCode));
        foreach (var language in LanguageCodes.All)
        {
            Assert.True(Embedded.TryGet("source.codebuddyCode", language, out var name), language);
            Assert.Equal("CodeBuddy Code", name);
            Assert.True(Embedded.TryGet("settings.sources.codebuddyCodeHint", language, out var hint), language);
            Assert.Contains("~/.codebuddy/projects", hint, StringComparison.Ordinal);
            Assert.Contains(".codebuddy", hint, StringComparison.Ordinal);
            Assert.Contains("30", hint, StringComparison.Ordinal);
        }
    }

    /// <summary>CodeBuddy has no quota reader: the source never enters the quota switches or the spend ledger.</summary>
    [Fact]
    public void CodeBuddyCodeHasNoQuota()
    {
        Assert.DoesNotContain(SourceKind.CodeBuddyCode, QuotaSettings.Sources);
        Assert.False(QuotaSettings.Default.IsOn(SourceKind.CodeBuddyCode));
    }
}
