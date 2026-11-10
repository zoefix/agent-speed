using AgentSpeed.Core;
using AgentSpeed.Core.Locations;
using AgentSpeed.Core.Reporting;
using AgentSpeed.Presentation.Colors;
using AgentSpeed.Presentation.Formatting;
using AgentSpeed.Presentation.History;
using AgentSpeed.Presentation.Localization;
using AgentSpeed.Presentation.Settings;
using AgentSpeed.Presentation.Settings.Locations;
using AgentSpeed.Presentation.Settings.Orbs;
using AgentSpeed.Presentation.Tests.TestSupport;

namespace AgentSpeed.Presentation.Tests;

/// <summary>Devin in the app (DESIGN 1, 5, 9): its color, its settings group and location list, its CSV code and its strings.</summary>
public sealed class DevinSettingsTests
{
    private static readonly StringTable Embedded = Localizer.LoadEmbeddedTable();

    [Fact]
    public void SettingsSavedBeforeDevinTurnItOnWithItsAutomaticEntry()
    {
        var settings = SettingsSerializer.Deserialize("""
            { "version": 1, "sources": { "codex": true, "hermes": false, "droid": true, "droidRoot": null } }
            """);

        Assert.True(settings.IsSourceEnabled(SourceKind.Devin));
        Assert.False(settings.IsSourceEnabled(SourceKind.Hermes));
        Assert.Equal(LocationLists.AutomaticOnly, settings.Locations[LocationFolder.Devin]);
        Assert.Contains(SourceKind.Devin, settings.ToMonitorConfiguration().EnabledSources);
        Assert.Equal(LocationFolder.Devin, LocationFolders.Of(SourceKind.Devin));
        Assert.Equal([LocationFolder.Devin, LocationFolder.Qoder, LocationFolder.QoderCli], LocationFolders.All.TakeLast(3));

        var off = SettingsSerializer.Deserialize(SettingsSerializer.Serialize(settings.WithSourceEnabled(SourceKind.Devin, false)));
        Assert.False(off.IsSourceEnabled(SourceKind.Devin));
        Assert.DoesNotContain(SourceKind.Devin, off.ToMonitorConfiguration().EnabledSources);
    }

    [Fact]
    public void TheAutomaticEntryNamesTheCliFolderOnly()
    {
        var entry = LocationEntries.Entry(LocationFolder.Devin, DataLocation.Automatic);
        var line = Assert.Single(entry.Lines);
        Assert.Equal(LiveMonitor.DefaultDevinRoot, line.Path);
        Assert.Equal([LiveMonitor.DefaultDevinRoot], AutomaticLocations.All(LocationFolder.Devin));
        if (Environment.GetEnvironmentVariable("CHISEL_SESSION_DB") is not { Length: > 0 })
        {
            Assert.EndsWith(Path.Combine("devin", "cli"), line.Path, StringComparison.Ordinal);
            Assert.EndsWith(Path.Combine("devin", "cli", "sessions.db"), LiveMonitor.DefaultDevinDatabase, StringComparison.Ordinal);
        }

        Assert.Equal("settings.sources.devinHint", LocationEntries.FootnoteKey(LocationFolder.Devin));
        Assert.False(AutomaticLocations.AcceptsFile(LocationFolder.Devin));
        Assert.False(LocationFolder.Devin.AcceptsDatabaseFile());
    }

    [Fact]
    public void AFolderCountsWhenItHoldsTheDatabase()
    {
        using var temp = new TempDirectory();
        var cli = Directory.CreateDirectory(temp.Combine("devin", "cli")).FullName;
        Assert.False(AutomaticLocations.IsReadable(LocationFolder.Devin, cli), "an empty folder holds nothing Devin wrote");
        Assert.False(LocationEntries.Entry(LocationFolder.Devin, DataLocation.At(cli)).Exists);
        Assert.Null(LocationEntries.Entry(LocationFolder.Devin, DataLocation.At(cli)).RevealPath);

        File.WriteAllBytes(Path.Combine(cli, "sessions.db"), []);
        var entry = LocationEntries.Entry(LocationFolder.Devin, DataLocation.At(cli));
        Assert.True(entry.Exists && entry.Lines.Single().Readable);
        Assert.Equal(cli, entry.RevealPath);
        Assert.False(entry.IsFile);
        Assert.True(AutomaticLocations.IsReadable(LocationFolder.Devin, temp.Combine("devin")), "the devin folder above cli");

        var desktop = Directory.CreateDirectory(temp.Combine("Support", "Devin", "User", "acp-events")).FullName;
        File.WriteAllText(Path.Combine(desktop, "d.ndjson"), "{}\n");
        Assert.False(AutomaticLocations.IsReadable(LocationFolder.Devin, temp.Combine("Support", "Devin")), "Devin Desktop is not read");

        var lists = LocationLists.Automatic;
        (lists, var outcome) = lists.Add(temp.Combine("devin"), LocationFolder.Devin, []);
        Assert.Equal(LocationAddOutcome.Added, outcome);
        Assert.Equal(LocationAddOutcome.AlreadyListed, lists.Add(cli, LocationFolder.Devin, []).Outcome);
    }

    [Fact]
    public void ColorCodeAndStrings()
    {
        Assert.Equal("#557F14", HeatPalette.SourceColor(SourceKind.Devin).ToHex());
        Assert.Equal("devin", HistoryCsv.SourceId(SourceKind.Devin));
        Assert.Equal("devin", ReportBody.SourceId(SourceKind.Devin));
        Assert.Equal("devin", OrbSources.KeyOf(SourceKind.Devin));
        Assert.Equal(SourceSet.Of(SourceKind.Devin), OrbSources.FromKeys(["devin"]).Known);
        Assert.Equal("source.devin", DisplayText.SourceKey(SourceKind.Devin));
        foreach (var language in LanguageCodes.All)
        {
            Assert.True(Embedded.TryGet("source.devin", language, out var name), language);
            Assert.Equal("Devin", name);
            Assert.True(Embedded.TryGet("settings.sources.devinHint", language, out var hint), language);
            Assert.Contains("Devin", hint, StringComparison.Ordinal);
            Assert.Contains("%APPDATA%\\devin\\cli", hint, StringComparison.Ordinal);
            Assert.DoesNotContain("Desktop", hint, StringComparison.Ordinal);
            Assert.DoesNotContain("acp-events", hint, StringComparison.Ordinal);
        }

        Assert.Contains("工作階段", new Localizer(Embedded, LanguageCodes.ZhHant)["settings.sources.devinHint"], StringComparison.Ordinal);
    }
}
