using System.Text.Json.Nodes;
using AgentSpeed.Core;
using AgentSpeed.Presentation.Orb;
using AgentSpeed.Presentation.Settings;
using AgentSpeed.Presentation.Settings.Orbs;

namespace AgentSpeed.Presentation.Tests.Character;

/// <summary>DESIGN 2b <c>style</c>, <c>character</c>, <c>characterReadouts</c> and the footprint of DESIGN 2a.6.</summary>
public class CharacterModelTests
{
    private const string IdA = "AAAAAAAA-0000-4000-8000-000000000001";
    private const string IdB = "AAAAAAAA-0000-4000-8000-000000000002";

    private static AppSettings WithOrbsJson(string orbsJson) => SettingsSerializer.Deserialize("{ \"orbs\": " + orbsJson + " }");

    private static OrbDefinition OneOrb(string extra) => WithOrbsJson("{ \"orbs\": [ { \"id\": \"" + IdA + "\", \"style\": \"character\"" + extra + " } ] }").Orbs.Orbs[0];

    private static JsonObject Written(OrbDefinition orb) =>
        JsonNode.Parse(SettingsSerializer.Serialize(AppSettings.Default with { Orbs = new OrbsDocument([orb]) }))!["orbs"]!["orbs"]![0]!.AsObject();

    [Fact]
    public void TheStyleKeyRoundTripsAndUnknownKeysAreLed()
    {
        Assert.Equal("character", OrbStyles.Key(OrbStyle.Character));
        Assert.Equal(OrbStyle.Character, OrbStyles.Parse("character"));
        Assert.Equal(OrbStyle.Led, OrbStyles.Parse("Character"));
        Assert.Contains(OrbStyle.Character, OrbStyles.All);
        Assert.Equal(6, OrbStyles.All.Count);
        Assert.Equal(OrbStyle.Character, OneOrb(string.Empty).Style);
    }

    [Fact]
    public void TheCharacterStandsInADiscTwiceTheSize()
    {
        var orb = new OrbDefinition(IdA) { Style = OrbStyle.Character, Size = 148 };
        Assert.Equal(296, orb.Footprint);
        Assert.Equal(148 * 1.5, orb.RadiusPx(1.5));
        Assert.Equal(148, (orb with { Style = OrbStyle.Led }).Footprint);
        Assert.Equal(148, orb.WithStyle(OrbStyle.Led).Size);
    }

    [Fact]
    public void ClicksLandOnACapsuleAroundHerBody()
    {
        const double d = 296;
        Assert.True(OrbHitShape.Contains(OrbStyle.Character, 0, 0, d));
        Assert.True(OrbHitShape.Contains(OrbStyle.Character, 0, 0.04 * d + 0.44 * d, d));
        Assert.False(OrbHitShape.Contains(OrbStyle.Character, 0, 0.04 * d + 0.46 * d, d));
        Assert.True(OrbHitShape.Contains(OrbStyle.Character, 0.2 * d, 0.04 * d, d));
        Assert.False(OrbHitShape.Contains(OrbStyle.Character, 0.22 * d, 0.04 * d, d));
        Assert.False(OrbHitShape.Contains(OrbStyle.Character, 0.3 * d, -0.3 * d, d));
        Assert.True(OrbHitShape.Contains(OrbStyle.Led, 0.3 * d, -0.3 * d, d));
        Assert.True(OrbHitShape.Contains(OrbStyle.Character, 0.22 * d, 0.04 * d, d, tolerance: 0.02 * d));
        var (x, y, w, h) = OrbHitShape.Bounds(OrbStyle.Character, d);
        Assert.Equal((-0.21 * d, -0.45 * d + 0.04 * d, 0.42 * d, 0.9 * d), (x, y, w, h));
    }

    [Fact]
    public void CharacterAndReadoutsAreWrittenOnlyWhenNotDefault()
    {
        var plain = Written(new OrbDefinition(IdA) { Style = OrbStyle.Character });
        Assert.False(plain.ContainsKey("character"));
        Assert.False(plain.ContainsKey("characterReadouts"));

        var locked = new OrbDefinition(IdA) { Style = OrbStyle.Character, Character = "kimi", CharacterReadouts = CharacterReadouts.Default with { Model = true, Cost = false } };
        var json = Written(locked);
        Assert.Equal("kimi", json["character"]!.GetValue<string>());
        var readouts = json["characterReadouts"]!.AsObject();
        Assert.True(readouts["model"]!.GetValue<bool>());
        Assert.False(readouts["cost"]!.GetValue<bool>());
        Assert.True(readouts["network"]!.GetValue<bool>());

        var back = SettingsSerializer.Deserialize(SettingsSerializer.Serialize(AppSettings.Default with { Orbs = new OrbsDocument([locked]) }));
        Assert.Equal(locked, back.Orbs.Orbs[0]);
    }

    [Fact]
    public void DecodingIsTolerant()
    {
        Assert.Null(OneOrb(", \"character\": \"Bad ID\"").Character);
        Assert.Null(OneOrb(", \"character\": 3").Character);
        Assert.Null(OneOrb(", \"character\": \"-kimi\"").Character);
        Assert.Equal("glm", OneOrb(", \"character\": \" glm \"").Character);
        Assert.Equal(CharacterReadouts.Default, OneOrb(", \"characterReadouts\": 5").CharacterReadouts);
        Assert.Equal(CharacterReadouts.Default with { Thinking = false },
            OneOrb(", \"characterReadouts\": { \"thinking\": false, \"model\": \"yes\", \"cost\": null, \"future\": true }").CharacterReadouts);
        Assert.Equal(CharacterReadouts.Default with { Model = true }, OneOrb(", \"characterReadouts\": { \"model\": true }").CharacterReadouts);
    }

    [Fact]
    public void EditingSetsTheCharacterAndEachCard()
    {
        var doc = new OrbsDocument([new OrbDefinition(IdA) { Style = OrbStyle.Character }]);
        doc = OrbEditing.SetCharacter(doc, IdA, "claude");
        Assert.Equal("claude", doc.Find(IdA)!.Character);
        Assert.Null(OrbEditing.SetCharacter(doc, IdA, "../x").Find(IdA)!.Character);
        Assert.Null(OrbEditing.SetCharacter(doc, IdA, null).Find(IdA)!.Character);
        foreach (var readout in CharacterReadouts.All)
        {
            var on = !CharacterReadouts.Default[readout];
            var edited = OrbEditing.SetCharacterReadout(doc, IdA, readout, on).Find(IdA)!.CharacterReadouts;
            Assert.Equal(on, edited[readout]);
            Assert.All(CharacterReadouts.All.Where(r => r != readout), r => Assert.Equal(CharacterReadouts.Default[r], edited[r]));
        }
    }

    [Fact]
    public void AddDuplicateAndSplitCopyTheCharacterAndMergeKeepsTheTargets()
    {
        var readouts = CharacterReadouts.Default with { Model = true, Network = false };
        var a = new OrbDefinition(IdA) { Style = OrbStyle.Character, Character = "kimi", CharacterReadouts = readouts };
        var b = new OrbDefinition(IdB) { Style = OrbStyle.Character, Character = "glm" };
        var doc = new OrbsDocument([a, b]);
        var available = SourceSet.Of(SourceKind.Codex).With(SourceKind.ClaudeCode);

        var added = OrbEditing.Add(doc, IdA, available)!;
        var child = added.Document.Find(added.Created[0])!;
        Assert.Equal(("kimi", readouts), (child.Character, child.CharacterReadouts));

        var duplicated = OrbEditing.Duplicate(doc, IdA)!;
        var copy = duplicated.Document.Find(duplicated.Created[0])!;
        Assert.Equal(("kimi", readouts), (copy.Character, copy.CharacterReadouts));

        var split = OrbEditing.Split(new OrbsDocument([a]), IdA, available)!;
        Assert.All(split.Document.Orbs, o => Assert.Equal(("kimi", readouts), (o.Character, o.CharacterReadouts)));

        var merged = OrbEditing.Merge(doc, IdB)!;
        Assert.Equal(("glm", CharacterReadouts.Default), (merged.Orbs[0].Character, merged.Orbs[0].CharacterReadouts));
    }
}

/// <summary>CHARACTERS 7: Auto follows the fastest live vendor with a pack, holds a change for 3 s, keeps the last one.</summary>
public class CharacterSelectionTests
{
    private static readonly CharacterSelection.Pack[] Packs =
    [
        new("claude", ["anthropic"]), new("gpt", ["openai"]), new("kimi", ["moonshot"]),
    ];

    private static Dictionary<string, double> Rates(params (string Vendor, double Rate)[] rates) => rates.ToDictionary(r => r.Vendor, r => r.Rate);

    [Fact]
    public void NoPackShowsNothing()
    {
        var s = new CharacterSelection("claude");
        Assert.Null(s.Update(null, Rates(("anthropic", 50)), [], 0));
    }

    [Fact]
    public void ALockedInstalledPackWins()
    {
        var s = new CharacterSelection();
        Assert.Equal("kimi", s.Update("kimi", Rates(("anthropic", 90)), Packs, 0));
        Assert.Equal("kimi", s.Update("removed", Rates(("anthropic", 90)), Packs, 1));
        Assert.Equal("claude", s.Update("removed", Rates(("anthropic", 90)), Packs, 4));
    }

    [Fact]
    public void AutoFollowsTheFastestVendorAfterAHold()
    {
        var s = new CharacterSelection();
        Assert.Equal("claude", s.Update(null, Rates(("anthropic", 50), ("openai", 20)), Packs, 0));
        Assert.Equal("claude", s.Update(null, Rates(("anthropic", 50), ("openai", 80)), Packs, 1));
        Assert.Equal("claude", s.Update(null, Rates(("anthropic", 50), ("openai", 80)), Packs, 3.9));
        Assert.Equal("gpt", s.Update(null, Rates(("anthropic", 50), ("openai", 80)), Packs, 4.0));
    }

    [Fact]
    public void AFlickerResetsTheHold()
    {
        var s = new CharacterSelection("claude");
        s.Update(null, Rates(("openai", 80)), Packs, 0);
        s.Update(null, Rates(("anthropic", 80)), Packs, 2);
        Assert.Equal("claude", s.Update(null, Rates(("openai", 80)), Packs, 4));
        Assert.Equal("gpt", s.Update(null, Rates(("openai", 80)), Packs, 7));
    }

    [Fact]
    public void WithoutALiveVendorItKeepsTheLastOrTakesTheFirstPack()
    {
        Assert.Equal("claude", new CharacterSelection().Update(null, Rates(("google", 40)), Packs, 0));
        Assert.Equal("kimi", new CharacterSelection("kimi").Update(null, Rates(), Packs, 0));
        Assert.Equal("claude", new CharacterSelection("gone").Update(null, Rates(), Packs, 0));
    }

    [Fact]
    public void TiesGoToTheVendorName()
    {
        Assert.Equal("claude", CharacterSelection.Fastest(Rates(("openai", 40), ("anthropic", 40)), Packs));
        Assert.Null(CharacterSelection.Fastest(Rates(("openai", double.NaN), ("anthropic", 0)), Packs));
    }
}
