using System.Text.Json.Nodes;
using AgentSpeed.Core;
using AgentSpeed.Presentation.Settings;
using AgentSpeed.Presentation.Settings.Orbs;

namespace AgentSpeed.Presentation.Tests;

/// <summary>
/// DESIGN 2b, Data model <c>hideWhenIdle</c>: off for a new orb and for one saved by an earlier version, a boolean or
/// off, persisted with the orb, copied by Add, Duplicate and Split, and the target's kept by Merge.
/// </summary>
public class OrbHideWhenIdleTests
{
    private const string IdA = "6F1C2A9E-0000-4000-8000-00000000000A";
    private const string IdB = "6F1C2A9E-0000-4000-8000-00000000000B";

    private static readonly SourceSet Two = SourceSet.Create([SourceKind.Codex, SourceKind.ClaudeCode]);

    private static AppSettings Read(string orbsJson) => SettingsSerializer.Deserialize("{ \"orbs\": " + orbsJson + " }");

    private static Func<string> Ids(params string[] ids)
    {
        var queue = new Queue<string>(ids);
        return () => queue.Count > 0 ? queue.Dequeue() : Guid.NewGuid().ToString();
    }

    [Fact]
    public void ANewOrbAndOneFromAnEarlierVersionDoNotHide()
    {
        Assert.False(Assert.Single(AppSettings.Default.Orbs.Orbs).HideWhenIdle);
        Assert.False(new OrbDefinition(IdA).HideWhenIdle);

        var settings = Read("{ \"version\": 1, \"orbs\": [ { \"id\": \"" + IdA + "\", \"style\": \"needle\", \"fadeWhenIdle\": true, " +
            "\"visible\": true } ] }");
        var orb = settings.Orbs.Find(IdA)!;
        Assert.False(orb.HideWhenIdle);
        Assert.True(orb.FadeWhenIdle);
    }

    [Theory]
    [InlineData("\"yes\"")]
    [InlineData("1")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{}")]
    public void AValueThatIsNotABooleanReadsAsOff(string value)
    {
        var settings = Read("{ \"version\": 1, \"orbs\": [ { \"id\": \"" + IdA + "\", \"hideWhenIdle\": " + value + " } ] }");

        Assert.False(settings.Orbs.Find(IdA)!.HideWhenIdle);
    }

    [Fact]
    public void TheSwitchRoundTripsWithItsOrbAndNeverTouchesVisible()
    {
        var document = new OrbsDocument([new OrbDefinition(IdA), new OrbDefinition(IdB)]);
        var hiding = OrbEditing.SetHideWhenIdle(document, IdB, true);
        Assert.Same(hiding, OrbEditing.SetHideWhenIdle(hiding, "missing", true));
        Assert.True(hiding.Find(IdB)!.Visible);

        var json = SettingsSerializer.Serialize(AppSettings.Default with { Orbs = hiding });
        var orbs = JsonNode.Parse(json)!["orbs"]!["orbs"]!;
        Assert.False(orbs[0]!["hideWhenIdle"]!.GetValue<bool>());
        Assert.True(orbs[1]!["hideWhenIdle"]!.GetValue<bool>());
        Assert.True(orbs[1]!["visible"]!.GetValue<bool>());

        var back = SettingsSerializer.Deserialize(json).Orbs;
        Assert.Equal(hiding, back);
        Assert.False(back.Find(IdA)!.HideWhenIdle);
        Assert.True(back.Find(IdB)!.HideWhenIdle);
        Assert.False(OrbEditing.SetHideWhenIdle(back, IdB, false).Find(IdB)!.HideWhenIdle);
    }

    [Fact]
    public void AddDuplicateAndSplitCopyItAndMergeKeepsTheTargets()
    {
        var hiding = new OrbDefinition("h") { HideWhenIdle = true, Sources = OrbSources.All };
        var plain = new OrbDefinition("p");
        var document = new OrbsDocument([hiding, plain]);

        Assert.True(OrbEditing.Add(document, "h", Two, Ids("added"))!.Document.Find("added")!.HideWhenIdle);
        Assert.False(OrbEditing.Add(document, "p", Two, Ids("added"))!.Document.Find("added")!.HideWhenIdle);
        Assert.True(OrbEditing.Duplicate(document, "h", Ids("copy"))!.Document.Find("copy")!.HideWhenIdle);

        var split = OrbEditing.Split(document, "h", Two, Ids("c1"))!.Document;
        Assert.True(split.Find("h")!.HideWhenIdle);
        Assert.True(split.Find("c1")!.HideWhenIdle);

        Assert.True(Assert.Single(OrbEditing.Merge(document, "h")!.Orbs).HideWhenIdle);
        Assert.False(Assert.Single(OrbEditing.Merge(document, "p")!.Orbs).HideWhenIdle);
    }
}
