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

/// <summary>
/// Qoder IDE and Qoder CLI in the app (DESIGN 1, 5, 9): their colors, settings groups and location lists, their CSV and report ids
/// and their strings.
/// </summary>
public sealed class QoderSettingsTests
{
    private static readonly StringTable Embedded = Localizer.LoadEmbeddedTable();

    [Fact]
    public void SettingsSavedBeforeQoderTurnBothOnWithTheirAutomaticEntries()
    {
        var settings = SettingsSerializer.Deserialize("""
            { "version": 1, "sources": { "codex": true, "droid": false, "devin": true, "devinRoot": null } }
            """);

        foreach (var kind in new[] { SourceKind.Qoder, SourceKind.QoderCli })
        {
            Assert.True(settings.IsSourceEnabled(kind));
            Assert.Equal(LocationLists.AutomaticOnly, settings.Locations[LocationFolders.Of(kind)]);
            Assert.Contains(kind, settings.ToMonitorConfiguration().EnabledSources);

            var off = SettingsSerializer.Deserialize(SettingsSerializer.Serialize(settings.WithSourceEnabled(kind, false)));
            Assert.False(off.IsSourceEnabled(kind));
            Assert.DoesNotContain(kind, off.ToMonitorConfiguration().EnabledSources);
            Assert.All(new[] { SourceKind.Qoder, SourceKind.QoderCli }.Where(other => other != kind), other => Assert.True(off.IsSourceEnabled(other)));
        }

        Assert.False(settings.IsSourceEnabled(SourceKind.Droid));
        Assert.Equal((LocationFolder.Qoder, LocationFolder.QoderCli), (LocationFolders.Of(SourceKind.Qoder), LocationFolders.Of(SourceKind.QoderCli)));
        Assert.Equal([LocationFolder.Devin, LocationFolder.Qoder, LocationFolder.QoderCli], LocationFolders.All.TakeLast(3));
    }

    [Fact]
    public void TheAutomaticIdeEntryNamesBothEditionsDataFolders()
    {
        var entry = LocationEntries.Entry(LocationFolder.Qoder, DataLocation.Automatic);
        Assert.Equal(LiveMonitor.DefaultQoderRoots, entry.Lines.Select(l => l.Path));
        Assert.Equal(["Qoder", "QoderCN"], entry.Lines.Select(l => Path.GetFileName(l.Path)));
        Assert.Equal(LiveMonitor.DefaultQoderRoots, AutomaticLocations.All(LocationFolder.Qoder));
        Assert.Equal("settings.sources.qoderHint", LocationEntries.FootnoteKey(LocationFolder.Qoder));
        Assert.False(AutomaticLocations.AcceptsFile(LocationFolder.Qoder));
        Assert.False(LocationFolder.Qoder.AcceptsDatabaseFile());
    }

    [Fact]
    public void TheAutomaticCliEntryNamesBothProjectsFolders()
    {
        var entry = LocationEntries.Entry(LocationFolder.QoderCli, DataLocation.Automatic);
        Assert.Equal(LiveMonitor.DefaultQoderCliRoots, entry.Lines.Select(l => l.Path));
        if (Environment.GetEnvironmentVariable("QODER_CONFIG_DIR") is not { Length: > 0 }
            && Environment.GetEnvironmentVariable("QODERCN_CONFIG_DIR") is not { Length: > 0 })
        {
            Assert.Equal([Path.Combine(".qoder", "projects"), Path.Combine(".qoder-cn", "projects")],
                entry.Lines.Select(l => Path.Combine(Path.GetFileName(Path.GetDirectoryName(l.Path)!), Path.GetFileName(l.Path))));
        }

        Assert.Equal("settings.sources.qoderCliHint", LocationEntries.FootnoteKey(LocationFolder.QoderCli));
        Assert.False(AutomaticLocations.AcceptsFile(LocationFolder.QoderCli));
    }

    [Fact]
    public void AnIdeFolderCountsWhenItHoldsTheDatabase()
    {
        using var temp = new TempDirectory();
        var data = Directory.CreateDirectory(temp.Combine("Qoder")).FullName;
        Assert.False(AutomaticLocations.IsReadable(LocationFolder.Qoder, data), "an empty folder holds nothing Qoder wrote");
        Assert.False(LocationEntries.Entry(LocationFolder.Qoder, DataLocation.At(data)).Exists);
        Assert.Null(LocationEntries.Entry(LocationFolder.Qoder, DataLocation.At(data)).RevealPath);

        var db = Directory.CreateDirectory(Path.Combine(data, "SharedClientCache", "cache", "db")).FullName;
        File.WriteAllBytes(Path.Combine(db, "local.db"), []);
        var entry = LocationEntries.Entry(LocationFolder.Qoder, DataLocation.At(data));
        Assert.True(entry.Exists && entry.Lines.Single().Readable);
        Assert.Equal(data, entry.RevealPath);
        Assert.False(entry.IsFile);
        Assert.True(AutomaticLocations.IsReadable(LocationFolder.Qoder, Path.Combine(data, "SharedClientCache")));
        Assert.True(AutomaticLocations.IsReadable(LocationFolder.Qoder, db));
        Assert.False(AutomaticLocations.IsReadable(LocationFolder.Qoder, Path.Combine(data, "SharedClientCache", "cache")));

        var state = Directory.CreateDirectory(Path.Combine(data, "User", "globalStorage")).FullName;
        File.WriteAllBytes(Path.Combine(state, "state.vscdb"), []);
        Assert.False(AutomaticLocations.IsReadable(LocationFolder.Qoder, state), "state.vscdb is not read");

        var lists = LocationLists.Automatic;
        (lists, var outcome) = lists.Add(data, LocationFolder.Qoder, []);
        Assert.Equal(LocationAddOutcome.Added, outcome);
        Assert.Equal(LocationAddOutcome.AlreadyListed, lists.Add(Path.Combine(data, "SharedClientCache"), LocationFolder.Qoder, []).Outcome);
        Assert.Equal(LocationAddOutcome.AlreadyListed, lists.Add(db, LocationFolder.Qoder, []).Outcome);
    }

    [Fact]
    public void ACliConfigFolderAndItsProjectsFolderAreOneLocation()
    {
        using var temp = new TempDirectory();
        var config = Directory.CreateDirectory(temp.Combine(".qoder")).FullName;
        var projects = Directory.CreateDirectory(Path.Combine(config, "projects")).FullName;
        var lists = LocationLists.Automatic;
        (lists, var outcome) = lists.Add(config, LocationFolder.QoderCli, []);
        Assert.Equal(LocationAddOutcome.Added, outcome);
        Assert.Equal(LocationAddOutcome.AlreadyListed, lists.Add(projects, LocationFolder.QoderCli, []).Outcome);
        Assert.True(LocationEntries.Entry(LocationFolder.QoderCli, DataLocation.At(config)).Exists);
        Assert.Equal([DataLocation.Automatic, DataLocation.At(config)], lists[LocationFolder.QoderCli]);
    }

    [Fact]
    public void ColorsIdsAndStrings()
    {
        Assert.Equal(("#B3329D", "#7E61B6"), (HeatPalette.SourceColor(SourceKind.Qoder).ToHex(), HeatPalette.SourceColor(SourceKind.QoderCli).ToHex()));
        Assert.Equal(("qoder", "qoder-cli"), (HistoryCsv.SourceId(SourceKind.Qoder), HistoryCsv.SourceId(SourceKind.QoderCli)));
        Assert.Equal(("qoder", "qoderCli"), (ReportBody.SourceId(SourceKind.Qoder), ReportBody.SourceId(SourceKind.QoderCli)));
        Assert.Equal(("qoder", "qoderCli"), (OrbSources.KeyOf(SourceKind.Qoder), OrbSources.KeyOf(SourceKind.QoderCli)));
        Assert.Equal(SourceSet.Of(SourceKind.QoderCli), OrbSources.FromKeys(["qoderCli"]).Known);
        Assert.Equal(("source.qoder", "source.qoderCli"), (DisplayText.SourceKey(SourceKind.Qoder), DisplayText.SourceKey(SourceKind.QoderCli)));
        foreach (var language in LanguageCodes.All)
        {
            Assert.True(Embedded.TryGet("source.qoder", language, out var ide), language);
            Assert.Equal("Qoder IDE", ide);
            Assert.True(Embedded.TryGet("source.qoderCli", language, out var cli), language);
            Assert.Equal("Qoder CLI", cli);
            Assert.True(Embedded.TryGet("settings.sources.qoderHint", language, out var ideHint), language);
            Assert.Contains("%APPDATA%\\Qoder", ideHint, StringComparison.Ordinal);
            Assert.Contains("QoderCN", ideHint, StringComparison.Ordinal);
            Assert.Contains("SharedClientCache", ideHint, StringComparison.Ordinal);
            Assert.True(Embedded.TryGet("settings.sources.qoderCliHint", language, out var cliHint), language);
            Assert.Contains("~/.qoder/projects", cliHint, StringComparison.Ordinal);
            Assert.Contains("QODER_EXPOSE_TOKEN_USAGE=1", cliHint, StringComparison.Ordinal);
            Assert.Contains("QODERCN_EXPOSE_TOKEN_USAGE=1", cliHint, StringComparison.Ordinal);
        }

        Assert.Contains("費用", new Localizer(Embedded, LanguageCodes.ZhHant)["settings.sources.qoderHint"], StringComparison.Ordinal);
    }
}
