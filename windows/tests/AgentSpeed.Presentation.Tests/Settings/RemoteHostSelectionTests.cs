using System.Text.Json;
using AgentSpeed.Core;
using AgentSpeed.Core.Remote;
using AgentSpeed.Presentation.Localization;
using AgentSpeed.Presentation.Orb;
using AgentSpeed.Presentation.Settings;
using AgentSpeed.Presentation.Settings.Boards;
using AgentSpeed.Presentation.Settings.Orbs;

namespace AgentSpeed.Presentation.Tests;

/// <summary>DESIGN 13 <i>Orbs and boards</i>: the <c>hosts</c> selection, its persistence, pickers, names and removal; <c>remoteHosts</c> in the settings.</summary>
public class RemoteHostSelectionTests
{
    private const string A = "8C0D6B0A-1111-4222-8333-00000000000A";
    private const string B = "8C0D6B0A-1111-4222-8333-00000000000B";
    private const string Local = RemoteHost.LocalId;

    private static readonly string[] Known = [Local, A, B];

    [Fact]
    public void SelectionsAreNormalizedLocalFirstAndEmptyMeansAll()
    {
        Assert.True(OrbHosts.All.IsAll);
        Assert.Null(OrbHosts.All.HostSet);
        Assert.True(OrbHosts.Normalized([]).IsAll);
        Assert.True(OrbHosts.Normalized(["", null]).IsAll);
        Assert.Equal<string>([Local, A, "future"], OrbHosts.Normalized([A, Local, A, "future"]).Ids);
        Assert.Equal(OrbHosts.Of(Local, A), OrbHosts.Of(A, Local));
        Assert.True(OrbHosts.Of(A).Tracks(A));
        Assert.False(OrbHosts.Of(A).Tracks(Local));
        Assert.True(OrbHosts.All.Tracks("anything"));
    }

    [Fact]
    public void PickersFollowTheLastHostRule()
    {
        Assert.Equal(OrbHosts.Of(A), OrbHosts.All.Toggling(A, Known));
        Assert.Equal(OrbHosts.Of(Local, A), OrbHosts.Of(A).Toggling(Local, Known));
        Assert.Equal<string>([A, B], OrbHosts.Of(B).Toggling(A, Known)!.Value.Ids);
        Assert.Null(OrbHosts.Of(A).Toggling(A, Known));
        Assert.Null(OrbHosts.Of(A, "future").Toggling(A, Known));
        Assert.Equal(OrbHosts.Of(A), OrbHosts.Of(A, B).Toggling(B, Known));
        OrbHostChoice[] choices = [new(Local, "This PC"), new(A, "devbox"), new(B, "gpu")];
        var entries = OrbHosts.Of(A).Entries(choices);
        Assert.Equal([false, true, false], entries.Select(e => e.Checked));
        Assert.Equal([true, false, true], entries.Select(e => e.Enabled));
        Assert.All(OrbHosts.All.Entries(choices), e => Assert.False(e.Checked));
        Assert.True(OrbHostChoice.PickerShown(choices));
        Assert.False(OrbHostChoice.PickerShown([new(Local, "This PC")]));
    }

    [Fact]
    public void RemovingAHostLeavesEverySelection()
    {
        Assert.True(OrbHosts.Of(A).Removing(A).IsAll);
        Assert.Equal(OrbHosts.Of(Local), OrbHosts.Of(Local, A).Removing(A));
        var orbs = new OrbsDocument([new OrbDefinition("o1") { Hosts = OrbHosts.Of(A) }, new OrbDefinition("o2") { Hosts = OrbHosts.Of(Local, A) }]);
        var boards = new BoardsDocument([new BoardDefinition("b1") { Hosts = OrbHosts.Of(A, B) }]);
        var settings = AppSettings.Default with
        {
            Orbs = orbs,
            Boards = boards,
            RemoteHosts = new RemoteHostsDocument([new RemoteHost("devbox", A), new RemoteHost("gpu", B)]),
        };
        var removed = settings.WithoutRemoteHost(A);
        Assert.Equal([B], removed.RemoteHosts.Hosts.Select(h => h.Id));
        Assert.True(removed.Orbs.Orbs[0].Hosts.IsAll);
        Assert.Equal(OrbHosts.Of(Local), removed.Orbs.Orbs[1].Hosts);
        Assert.Equal(OrbHosts.Of(B), removed.Boards.Boards[0].Hosts);
        Assert.Same(orbs, OrbEditing.RemovingHost(orbs, "unknown"));
    }

    [Fact]
    public void HostsPersistOnlyWhenNotAllAndKeepUnknownIds()
    {
        var settings = AppSettings.Default with
        {
            Orbs = new OrbsDocument([new OrbDefinition("o1") { Hosts = OrbHosts.Of(A, Local, "future") }, new OrbDefinition("o2")]),
            Boards = new BoardsDocument([new BoardDefinition("b1") { Hosts = OrbHosts.Of(B) }]),
        };
        var json = SettingsSerializer.Serialize(settings);
        using (var parsed = JsonDocument.Parse(json))
        {
            var orbList = parsed.RootElement.GetProperty("orbs").GetProperty("orbs");
            Assert.Equal([Local, A, "future"], orbList[0].GetProperty("hosts").EnumerateArray().Select(h => h.GetString()));
            Assert.False(orbList[1].TryGetProperty("hosts", out _));
        }

        var back = SettingsSerializer.Deserialize(json);
        Assert.Equal(settings.Orbs.Orbs[0].Hosts, back.Orbs.Orbs[0].Hosts);
        Assert.True(back.Orbs.Orbs[1].Hosts.IsAll);
        Assert.Equal(OrbHosts.Of(B), back.Boards.Boards[0].Hosts);

        var single = SettingsSerializer.Deserialize("""{"orbs":{"orbs":[{"id":"x","hosts":"all"},{"id":"y","hosts":"8C0D6B0A-1111-4222-8333-00000000000A"},{"id":"z","hosts":[]}]}}""");
        Assert.True(single.Orbs.Orbs[0].Hosts.IsAll);
        Assert.Equal(OrbHosts.Of(A), single.Orbs.Orbs[1].Hosts);
        Assert.True(single.Orbs.Orbs[2].Hosts.IsAll);
    }

    [Fact]
    public void RemoteHostsRoundTripThroughTheSettingsFile()
    {
        var hosts = new RemoteHostsDocument([new RemoteHost("devbox", A) { Name = "Dev", RelayFolder = "/opt/as" }, new RemoteHost("user@gpu", B) { Enabled = false }]);
        var settings = AppSettings.Default with { RemoteHosts = hosts };
        var back = SettingsSerializer.Deserialize(SettingsSerializer.Serialize(settings));
        Assert.Equal(hosts, back.RemoteHosts);
        Assert.Equal(RemoteHostsDocument.Empty, SettingsSerializer.Deserialize("{}").RemoteHosts);
        Assert.Equal(RemoteHostsDocument.Empty, SettingsSerializer.Deserialize("""{"remoteHosts":"nonsense"}""").RemoteHosts);
    }

    [Fact]
    public void AutomaticNamesNameTheirHosts()
    {
        var localizer = new Localizer(Localizer.LoadEmbeddedTable(), "en");
        OrbHostChoice[] hosts = [new(Local, "This PC"), new(A, "devbox"), new(B, "gpu")];
        var codex = new OrbDefinition("o") { Sources = OrbSources.Of(SourceKind.Codex) };
        var plain = OrbNaming.AutomaticName(codex.Sources, localizer);
        Assert.Equal(plain, OrbNaming.DisplayName(codex, localizer, hosts));
        Assert.Equal(plain + " · devbox", OrbNaming.DisplayName(codex with { Hosts = OrbHosts.Of(A) }, localizer, hosts));
        Assert.Equal(plain + " · " + localizer.Format("orbs.name.more", "This PC", 2),
            OrbNaming.DisplayName(codex with { Hosts = OrbHosts.Of(Local, A, B) }, localizer, hosts));
        Assert.Equal(plain, OrbNaming.DisplayName(codex with { Hosts = OrbHosts.Of("future") }, localizer, hosts));
        Assert.Equal(plain, OrbNaming.DisplayName(codex with { Hosts = OrbHosts.Of(A) }, localizer, [new(Local, "This PC")]));
        Assert.Equal("Mine", OrbNaming.DisplayName(codex with { Name = "Mine", Hosts = OrbHosts.Of(A) }, localizer, hosts));
    }

    [Fact]
    public void SplitKeepsHostsAndAddStartsWithEveryHost()
    {
        var template = new OrbDefinition("t") { Hosts = OrbHosts.Of(A) };
        var doc = new OrbsDocument([template]);
        var split = OrbEditing.Split(doc, "t", SourceSet.Create([SourceKind.Codex, SourceKind.ClaudeCode]))!;
        Assert.All(split.Document.Orbs, o => Assert.Equal(OrbHosts.Of(A), o.Hosts));
        var added = OrbEditing.Add(doc, "t", SourceSet.Create([SourceKind.Codex]))!;
        Assert.True(added.Document.Find(added.Created[0])!.Hosts.IsAll);
        Assert.Equal(OrbHosts.Of(A), OrbEditing.Merge(split.Document, "t")!.Orbs[0].Hosts);
        Assert.Equal(OrbHosts.Of(Local), OrbEditing.ToggleHost(doc, "t", Local, Known)!.Orbs[0].Hosts.Toggling(A, Known));
        Assert.Null(OrbEditing.ToggleHost(doc, "t", A, Known));
    }

    [Fact]
    public void AnOrbOnOneHostReadsItsCellsAndCelebratesItsOwnPeaks()
    {
        var feed = new OrbFeed("o");
        var hosts = new HashSet<string> { A };
        feed.Configure(null, SpeedMetric.Output, 100, 0, hosts: hosts);
        Assert.False(feed.TracksEverything);
        Assert.Equal(new SubsetKey(SourceSet.Everything, [A]), feed.SubsetKey);
        var completed = new SpeedEvent.ResponseCompleted(new ResponseSummary(SourceKind.Codex, "p", null, 1, 2, null, null, DateTimeOffset.UnixEpoch) { Host = Local });
        Assert.False(feed.Accepts(completed));
        Assert.False(feed.Accepts(new SpeedEvent.NewPeak(80)));
        feed.Configure(null, SpeedMetric.Output, 100, 0);
        Assert.True(feed.TracksEverything);
        Assert.Null(feed.SubsetKey);
        Assert.True(feed.Accepts(completed));
    }
}
