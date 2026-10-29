using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentSpeed.Core;
using AgentSpeed.Presentation.Heat;
using AgentSpeed.Presentation.Layout;
using AgentSpeed.Presentation.Orb;
using AgentSpeed.Presentation.Settings;
using AgentSpeed.Presentation.Settings.Orbs;

namespace AgentSpeed.Presentation.Tests;

public class OrbSettingsTests
{
    private const string IdA = "6F1C2A9E-0000-4000-8000-00000000000A";
    private const string IdB = "6F1C2A9E-0000-4000-8000-00000000000B";
    private const string IdC = "6F1C2A9E-0000-4000-8000-00000000000C";

    internal static OrbsDocument SampleDocument()
    {
        var a = new OrbDefinition(IdA)
        {
            Name = "Work",
            Sources = OrbSources.Of(SourceSet.Create([SourceKind.ClaudeCode, SourceKind.ClaudeDesktop])),
            Style = OrbStyle.Needle,
            Size = 196,
            Opacity = 0.65,
            AlwaysOnTop = false,
            ClickThrough = true,
            FadeWhenIdle = true,
            AllSpaces = false,
            RedlineOutput = 650,
            ShowName = true,
        };
        var b = new OrbDefinition(IdB)
        {
            Sources = OrbSources.FromKeys(["codex", "futureSource"]),
            Style = OrbStyle.Minimal,
            Size = 64,
            Visible = false,
            RedlineTotal = 350_000,
            ShowName = false,
        };
        var c = new OrbDefinition(IdC) { Style = OrbStyle.Reactor };
        return new OrbsDocument([a, b, c])
            .WithPosition("DISPLAY2|1920x1080", IdB, new OrbPositionRecord("DISPLAY2|1920x1080", OrbAxis.Fraction(0.25), OrbAxis.High(12)))
            .WithPosition("DISPLAY1|2560x1440+DISPLAY2|1920x1080", IdA, new OrbPositionRecord("DISPLAY1|2560x1440", OrbAxis.High(12), OrbAxis.Fraction(0.31)))
            .WithPosition("DISPLAY1|2560x1440+DISPLAY2|1920x1080", IdC, new OrbPositionRecord("DISPLAY2|1920x1080", OrbAxis.Low(12), OrbAxis.Low(12)));
    }

    private static JsonNode Root(AppSettings settings) => JsonNode.Parse(SettingsSerializer.Serialize(settings))!;

    private static AppSettings WithOrbsJson(string orbsJson) =>
        SettingsSerializer.Deserialize("{ \"orb\": { \"size\": 112 }, \"orbs\": " + orbsJson + " }");

    private static string OneOrb(string fields) => "{ \"version\": 1, \"orbs\": [ { \"id\": \"" + IdA + "\"" + fields + " } ] }";

    [Fact]
    public void FreshInstallHasOneDefaultOrb()
    {
        var orbs = AppSettings.Default.Orbs;
        var orb = Assert.Single(orbs.Orbs);
        Assert.Null(orb.Name);
        Assert.True(orb.Sources.IsAll);
        Assert.Null(orb.TrackedSources);
        Assert.Equal(OrbStyle.Led, orb.Style);
        Assert.Equal(148, orb.Size);
        Assert.Equal(1, orb.Opacity);
        Assert.True(orb.AlwaysOnTop);
        Assert.False(orb.ClickThrough);
        Assert.False(orb.FadeWhenIdle);
        Assert.True(orb.AllSpaces);
        Assert.True(orb.Visible);
        Assert.Null(orb.RedlineOutput);
        Assert.Null(orb.RedlineTotal);
        Assert.Null(orb.ShowName);
        Assert.Empty(orbs.Layouts);
        Assert.True(AppSettings.Default.ShowOrbs);
        Assert.Equal(AppSettings.Default.Orbs, new AppSettings().Orbs);
        Assert.True(Guid.TryParse(orb.Id, out _));
    }

    [Fact]
    public void DocumentRoundTripsWithEveryField()
    {
        var settings = AppSettings.Default with { Orbs = SampleDocument(), ShowOrbs = false };
        var back = SettingsSerializer.Deserialize(SettingsSerializer.Serialize(settings));
        Assert.Equal(settings.Orbs, back.Orbs);
        Assert.False(back.ShowOrbs);
        SettingsSerializerTests.AssertEquivalent(settings, back);
        Assert.Equal(["claudeCode", "claudeDesktop"], back.Orbs.Orbs[0].Sources.ToKeys());
        Assert.Equal(["codex", "futureSource"], back.Orbs.Orbs[1].Sources.ToKeys());
        Assert.Equal(SourceSet.Of(SourceKind.Codex), back.Orbs.Orbs[1].TrackedSources);
    }

    [Fact]
    public void JsonMatchesTheSpecShape()
    {
        var root = Root(AppSettings.Default with { Orbs = SampleDocument() });
        var orbs = root["orbs"]!;
        Assert.Equal(1, (int)orbs["version"]!);
        var first = orbs["orbs"]![0]!;
        Assert.Equal(IdA, (string)first["id"]!);
        Assert.Equal("Work", (string)first["name"]!);
        Assert.Equal("needle", (string)first["style"]!);
        Assert.Equal(196, (double)first["size"]!);
        Assert.Equal(0.65, (double)first["opacity"]!);
        Assert.False((bool)first["alwaysOnTop"]!);
        Assert.True((bool)first["clickThrough"]!);
        Assert.True((bool)first["fadeWhenIdle"]!);
        Assert.False((bool)first["allSpaces"]!);
        Assert.True((bool)first["visible"]!);
        Assert.Equal(650, (double)first["redlineOutput"]!);
        Assert.Null(first["redlineTotal"]);
        Assert.True((bool)first["showName"]!);
        Assert.False((bool)orbs["orbs"]![1]!["showName"]!);
        Assert.True(orbs["orbs"]![2]!.AsObject().ContainsKey("showName"));
        Assert.Null(orbs["orbs"]![2]!["showName"]);
        Assert.Equal("all", (string)orbs["orbs"]![2]!["sources"]!);
        Assert.Null(orbs["orbs"]![2]!["name"]);

        var layouts = orbs["layouts"]!.AsObject();
        Assert.Equal(["DISPLAY1|2560x1440+DISPLAY2|1920x1080", "DISPLAY2|1920x1080"], layouts.Select(p => p.Key));
        var record = layouts["DISPLAY1|2560x1440+DISPLAY2|1920x1080"]![IdA]!;
        Assert.Equal("DISPLAY1|2560x1440", (string)record["display"]!);
        Assert.Equal("high", (string)record["xAnchor"]!);
        Assert.Equal(12, (double)record["x"]!);
        Assert.Equal("fraction", (string)record["yAnchor"]!);
        Assert.Equal(0.31, (double)record["y"]!);
        Assert.True((bool)root["showOrbs"]!);
    }

    [Theory]
    [InlineData(", \"style\": \"hologram\"", OrbStyle.Led)]
    [InlineData(", \"style\": 3", OrbStyle.Led)]
    [InlineData(", \"style\": \"NEEDLE\"", OrbStyle.Led)]
    [InlineData(", \"style\": \"liquid\"", OrbStyle.Liquid)]
    [InlineData("", OrbStyle.Led)]
    public void UnknownStylesReadAsLed(string fields, OrbStyle expected)
    {
        Assert.Equal(expected, WithOrbsJson(OneOrb(fields)).Orbs.Orbs[0].Style);
    }

    [Theory]
    [InlineData(", \"size\": 10", 96)]
    [InlineData(", \"size\": 1000", 240)]
    [InlineData(", \"size\": \"big\"", 148)]
    [InlineData(", \"size\": 150.4", 150)]
    [InlineData(", \"style\": \"minimal\", \"size\": 10", 64)]
    [InlineData(", \"style\": \"minimal\", \"size\": 72", 72)]
    [InlineData(", \"style\": \"led\", \"size\": 72", 96)]
    public void SizesAreClampedPerStyle(string fields, double expected)
    {
        Assert.Equal(expected, WithOrbsJson(OneOrb(fields)).Orbs.Orbs[0].Size);
    }

    [Theory]
    [InlineData(", \"opacity\": 0", 0.4)]
    [InlineData(", \"opacity\": 7", 1.0)]
    [InlineData(", \"opacity\": 0.75", 0.75)]
    [InlineData(", \"opacity\": 0.123456", 0.4)]
    [InlineData(", \"opacity\": 0.56789", 0.568)]
    [InlineData(", \"opacity\": null", 1.0)]
    public void OpacityIsClamped(string fields, double expected)
    {
        Assert.Equal(expected, WithOrbsJson(OneOrb(fields)).Orbs.Orbs[0].Opacity);
    }

    [Fact]
    public void RedlinesAreClampedOrFollowTheGeneralOne()
    {
        var orb = WithOrbsJson(OneOrb(", \"redlineOutput\": 9999, \"redlineTotal\": 123456")).Orbs.Orbs[0];
        Assert.Equal(HeatMath.MaxRedline, orb.RedlineOutput);
        Assert.Equal(120_000, orb.RedlineTotal);

        var follows = WithOrbsJson(OneOrb(", \"redlineOutput\": null, \"redlineTotal\": \"fast\"")).Orbs.Orbs[0];
        Assert.Null(follows.RedlineOutput);
        Assert.Null(follows.RedlineTotal);
        var general = AppSettings.Default.WithRedline(SpeedMetric.Output, 800);
        Assert.Equal(800, follows.EffectiveRedline(SpeedMetric.Output, general));
        Assert.Equal(HeatMath.MaxRedline, orb.EffectiveRedline(SpeedMetric.Output, general));
        Assert.Equal(120_000, orb.EffectiveRedline(SpeedMetric.Total, general));
        Assert.Equal(450, orb.WithRedline(SpeedMetric.Output, 433).RedlineOutput);
        Assert.Null(orb.WithRedline(SpeedMetric.Total, null).RedlineTotal);
    }

    [Fact]
    public void TogglesUseTheirDefaultsWhenInvalid()
    {
        var orb = WithOrbsJson(OneOrb(", \"alwaysOnTop\": \"no\", \"clickThrough\": 1, \"fadeWhenIdle\": null, \"allSpaces\": [], \"visible\": {}")).Orbs.Orbs[0];
        Assert.True(orb.AlwaysOnTop);
        Assert.False(orb.ClickThrough);
        Assert.False(orb.FadeWhenIdle);
        Assert.True(orb.AllSpaces);
        Assert.True(orb.Visible);
    }

    [Theory]
    [InlineData(", \"sources\": \"all\"", null)]
    [InlineData(", \"sources\": []", null)]
    [InlineData(", \"sources\": 5", null)]
    [InlineData(", \"sources\": null", null)]
    [InlineData("", null)]
    [InlineData(", \"sources\": \"codex\"", "codex")]
    [InlineData(", \"sources\": [\"gemini\", \"codex\", \"gemini\", 7, null]", "codex,gemini")]
    [InlineData(", \"sources\": [\"antigravity\", \"newThing\", \"claudeCode\", \"newThing\"]", "claudeCode,antigravity,newThing")]
    [InlineData(", \"sources\": [\"workbuddyAi\", \"codex\", \"workbuddy\"]", "codex,workbuddy,workbuddyAi")]
    [InlineData(", \"sources\": [\"newThing\"]", "newThing")]
    public void SourcesAreNormalized(string fields, string? expectedKeys)
    {
        var sources = WithOrbsJson(OneOrb(fields)).Orbs.Orbs[0].Sources;
        Assert.Equal(expectedKeys, sources.ToKeys() is { } keys ? string.Join(",", keys) : null);
    }

    [Fact]
    public void UnknownSourceKeysAreKeptButIgnored()
    {
        var orb = WithOrbsJson(OneOrb(", \"sources\": [\"newThing\", \"opencode\"]")).Orbs.Orbs[0];
        Assert.Equal(SourceSet.Of(SourceKind.OpenCode), orb.TrackedSources);
        Assert.Equal(["newThing"], orb.Sources.UnknownKeys.ToArray());

        var onlyUnknown = WithOrbsJson(OneOrb(", \"sources\": [\"newThing\"]")).Orbs.Orbs[0];
        Assert.False(onlyUnknown.Sources.IsAll);
        Assert.True(onlyUnknown.Sources.TracksAll);
        Assert.Null(onlyUnknown.TrackedSources);

        var back = SettingsSerializer.Deserialize(SettingsSerializer.Serialize(AppSettings.Default with
        {
            Orbs = new OrbsDocument([orb]),
        }));
        Assert.Equal(["opencode", "newThing"], back.Orbs.Orbs[0].Sources.ToKeys());
    }

    [Fact]
    public void SourceKeysAreBounded()
    {
        var keys = Enumerable.Range(0, 40).Select(i => $"\"future{i}\"").Append("\"" + new string('k', 65) + "\"").Append("\"bad\\u0001key\"");
        var sources = WithOrbsJson(OneOrb(", \"sources\": [" + string.Join(", ", keys) + ", \"codex\"]")).Orbs.Orbs[0].Sources;
        Assert.Equal(OrbSources.MaxUnknownKeys, sources.UnknownKeys.Length);
        Assert.All(sources.UnknownKeys, k => Assert.True(k.Length <= OrbSources.MaxKeyLength));
        Assert.Equal(SourceSet.Of(SourceKind.Codex), sources.Known);
    }

    [Fact]
    public void AtMostEightOrbsAreRead()
    {
        var orbs = Enumerable.Range(0, 12).Select(i => $"{{ \"id\": \"orb-{i}\", \"size\": {100 + i} }}");
        var document = WithOrbsJson("{ \"orbs\": [ 5, \"x\", null, " + string.Join(", ", orbs) + " ] }").Orbs;
        Assert.Equal(OrbsDocument.MaxOrbs, document.Count);
        Assert.Equal(Enumerable.Range(0, 8).Select(i => $"orb-{i}"), document.Orbs.Select(o => o.Id));
    }

    [Fact]
    public void DuplicateMissingAndOverlongIdsGetFreshUuids()
    {
        var json = "{ \"orbs\": [ { \"id\": \"same\", \"size\": 100 }, { \"id\": \"same\", \"size\": 120 }, { \"size\": 140 }, "
            + "{ \"id\": \"" + new string('x', 65) + "\" }, { \"id\": \"  \" } ], "
            + "\"layouts\": { \"D\": { \"same\": { \"display\": \"D\", \"xAnchor\": \"low\", \"x\": 12, \"yAnchor\": \"low\", \"y\": 12 } } } }";
        var document = WithOrbsJson(json).Orbs;
        Assert.Equal(5, document.Count);
        Assert.Equal("same", document.Orbs[0].Id);
        Assert.Equal(100, document.Orbs[0].Size);
        Assert.Equal(120, document.Orbs[1].Size);
        Assert.Equal(5, document.Orbs.Select(o => o.Id).Distinct().Count());
        Assert.All(document.Orbs.Skip(1), o => Assert.True(Guid.TryParse(o.Id, out _)));
        Assert.Equal(["same"], document.Layouts[0].Positions.Keys);
    }

    [Theory]
    [InlineData("  Work  ", "Work")]
    [InlineData("   ", null)]
    [InlineData("", null)]
    [InlineData("line\nbreak", "line break")]
    [InlineData("0123456789012345678901234567890123456789", "01234567890123456789012345678901")]
    public void NamesAreTrimmedAndLimited(string name, string? expected)
    {
        var json = OneOrb(", \"name\": " + JsonSerializer.Serialize(name));
        Assert.Equal(expected, WithOrbsJson(json).Orbs.Orbs[0].Name);
        Assert.Equal(expected, OrbDefinition.CleanName(name));
    }

    [Fact]
    public void NamesKeepEmojiWhole()
    {
        var name = string.Concat(Enumerable.Repeat("🚀", 40));
        var clean = OrbDefinition.CleanName(name)!;
        Assert.Equal(string.Concat(Enumerable.Repeat("🚀", 32)), clean);
    }

    [Fact]
    public void LayoutEntriesAreValidatedAndCapped()
    {
        var arrangements = Enumerable.Range(0, 20)
            .Select(i => $"\"A{i}\": {{ \"{IdA}\": {{ \"display\": \"D{i}\", \"xAnchor\": \"fraction\", \"x\": {(i / 20.0).ToString("0.00", CultureInfo.InvariantCulture)}, \"yAnchor\": \"low\", \"y\": 12 }} }}");
        var bad = new[]
        {
            "\"bad1\": 5",
            $"\"bad2\": {{ \"{IdA}\": {{ \"display\": \"D\", \"xAnchor\": \"middle\", \"x\": 1, \"yAnchor\": \"low\", \"y\": 1 }} }}",
            $"\"bad3\": {{ \"{IdA}\": {{ \"display\": \"D\", \"xAnchor\": \"low\", \"x\": \"1\", \"yAnchor\": \"low\", \"y\": 1 }} }}",
            $"\"bad4\": {{ \"{IdA}\": {{ \"display\": \"{new string('d', 257)}\", \"xAnchor\": \"low\", \"x\": 1, \"yAnchor\": \"low\", \"y\": 1 }} }}",
            "\"bad5\": { \"unknown-orb\": { \"display\": \"D\", \"xAnchor\": \"low\", \"x\": 1, \"yAnchor\": \"low\", \"y\": 1 } }",
            $"\"clamped\": {{ \"{IdA}\": {{ \"display\": \"D\", \"xAnchor\": \"high\", \"x\": 500, \"yAnchor\": \"fraction\", \"y\": -3 }} }}",
            $"\"a0\": {{ \"{IdA}\": {{ \"display\": \"Dup\", \"xAnchor\": \"low\", \"x\": 1, \"yAnchor\": \"low\", \"y\": 1 }} }}",
        };
        var json = "{ \"orbs\": [ { \"id\": \"" + IdA + "\" } ], \"layouts\": { " + string.Join(", ", bad.Concat(arrangements)) + " } }";
        var document = WithOrbsJson(json).Orbs;

        Assert.Equal(OrbsDocument.MaxArrangements, document.Layouts.Length);
        Assert.Equal("clamped", document.Layouts[0].Arrangement);
        var clamped = document.Layouts[0].Positions[IdA];
        Assert.Equal(OrbAxis.High(OrbGeometry.SnapThreshold), clamped.X);
        Assert.Equal(OrbAxis.Fraction(0), clamped.Y);
        Assert.Equal("Dup", document.Layouts[1].Positions[IdA].Display);
        Assert.DoesNotContain(document.Layouts, l => l.Arrangement.StartsWith("bad", StringComparison.Ordinal));
        Assert.Equal(["clamped", "a0", "A1", "A2"], document.Layouts.Take(4).Select(l => l.Arrangement));
    }

    [Theory]
    [InlineData("5")]
    [InlineData("\"orbs\"")]
    [InlineData("[]")]
    [InlineData("{ }")]
    [InlineData("{ \"orbs\": [] }")]
    [InlineData("{ \"orbs\": [ 1, 2 ] }")]
    [InlineData("{ \"orbs\": { \"id\": \"x\" } }")]
    public void UnusableDocumentsMigrateTheSingleOrb(string orbsJson)
    {
        var settings = WithOrbsJson(orbsJson);
        var orb = Assert.Single(settings.Orbs.Orbs);
        Assert.Equal(112, orb.Size);
        Assert.True(orb.Sources.IsAll);
    }

    [Fact]
    public void DeserializeNeverThrowsForBadOrbValues()
    {
        string[] values =
        [
            "{ \"orbs\": [ { \"id\": 5, \"name\": 7, \"sources\": { }, \"style\": [], \"size\": true, \"opacity\": \"x\" } ], \"layouts\": [] }",
            "{ \"orbs\": [ { \"id\": \"a\" } ], \"layouts\": { \"\": { \"a\": { } }, \" \": 1, \"k\": [] } }",
            "{ \"orbs\": [ { \"id\": \"a\", \"size\": 1e308, \"opacity\": -1e308, \"redlineOutput\": 1e308 } ] }",
            "{ \"version\": \"two\", \"orbs\": [ { \"id\": \"a\" } ], \"layouts\": { \"k\": { \"a\": { \"display\": \"d\", \"xAnchor\": \"low\", \"x\": 1e308, \"yAnchor\": \"fraction\", \"y\": -1e308 } } } }",
        ];
        foreach (var value in values)
        {
            var settings = WithOrbsJson(value);
            Assert.InRange(settings.Orbs.Count, 1, 8);
            Assert.Equal(settings.Orbs, SettingsSerializer.Deserialize(SettingsSerializer.Serialize(settings)).Orbs);
        }
    }

    [Fact]
    public void ShowOrbsFallsBackToTheOldOrbShow()
    {
        Assert.False(SettingsSerializer.Deserialize("{ \"orb\": { \"show\": false } }").ShowOrbs);
        Assert.True(SettingsSerializer.Deserialize("{ \"orb\": { \"show\": true } }").ShowOrbs);
        Assert.True(SettingsSerializer.Deserialize("{}").ShowOrbs);

        var both = SettingsSerializer.Deserialize("{ \"showOrbs\": true, \"orb\": { \"show\": false } }");
        Assert.True(both.ShowOrbs);
        Assert.False(both.ShowOrb);
        Assert.False(SettingsSerializer.Deserialize("{ \"showOrbs\": false, \"orb\": { \"show\": true } }").ShowOrbs);
        Assert.False(SettingsSerializer.Deserialize("{ \"showOrbs\": \"yes\", \"orb\": { \"show\": false } }").ShowOrbs);
    }

    [Fact]
    public void OldKeysAreWrittenBackExactlyAsLoaded()
    {
        const string legacy = """
            {
              "version": 1,
              "orb": {
                "show": false, "size": 196, "opacity": 0.65, "alwaysOnTop": false, "clickThrough": true, "fadeWhenIdle": true,
                "monitor": "\\\\.\\DISPLAY2|1920x1080",
                "anchors": {
                  "\\\\.\\DISPLAY1|2560x1440": { "x": 0.5, "y": 0.125 },
                  "\\\\.\\DISPLAY2|1920x1080": { "x": 0.91234, "y": 0.8 }
                }
              }
            }
            """;
        var loaded = SettingsSerializer.Deserialize(legacy);
        var original = Root(loaded)["orb"]!.ToJsonString();

        var display = new OrbDisplay(@"\\.\DISPLAY2|1920x1080", new RectD(0, 0, 1920, 1040), 1, true);
        var orbs = loaded.Orbs;
        var added = OrbEditing.Add(orbs, orbs.Orbs[0].Id, SourceSet.Everything, () => IdB)!;
        orbs = OrbArrangement.PlaceNew(added.Document, [display], null, IdB, orbs.Orbs[0].Id).Document;
        orbs = OrbEditing.SetStyle(orbs, orbs.Orbs[0].Id, OrbStyle.Minimal);
        orbs = OrbEditing.SetSize(orbs, orbs.Orbs[0].Id, 64);
        orbs = OrbArrangement.Commit(orbs, [display], orbs.Orbs[0].Id, new PointD(400, 300));
        var changed = loaded.WithShowOrbs(true) with { Orbs = orbs };

        var written = Root(changed);
        Assert.Equal(original, written["orb"]!.ToJsonString());
        var reloaded = SettingsSerializer.Deserialize(written.ToJsonString());
        Assert.False(reloaded.ShowOrb);
        Assert.Equal(196, reloaded.OrbSize);
        Assert.Equal(0.65, reloaded.OrbOpacity);
        Assert.False(reloaded.OrbAlwaysOnTop);
        Assert.True(reloaded.OrbClickThrough);
        Assert.True(reloaded.OrbFadeWhenIdle);
        Assert.Equal(@"\\.\DISPLAY2|1920x1080", reloaded.OrbMonitor);
        Assert.Equal(new OrbAnchor(0.91234, 0.8), reloaded.OrbAnchors[@"\\.\DISPLAY2|1920x1080"]);
        Assert.Equal(2, reloaded.Orbs.Count);
        Assert.True(reloaded.ShowOrbs);
        Assert.Equal(orbs, reloaded.Orbs);
    }

    [Fact]
    public void MigrationCopiesTheSingleOrb()
    {
        var settings = SettingsSerializer.Deserialize("""
            {
              "orb": {
                "show": false, "size": 200, "opacity": 0.5, "alwaysOnTop": false, "clickThrough": true, "fadeWhenIdle": true,
                "monitor": "B",
                "anchors": { "A": { "x": 0.1, "y": 0.2 }, "B": { "x": 0.3, "y": 0.4 }, "C": { "x": 1, "y": 0 } }
              }
            }
            """);
        Assert.False(settings.ShowOrbs);
        var orb = Assert.Single(settings.Orbs.Orbs);
        Assert.True(Guid.TryParse(orb.Id, out _));
        Assert.Null(orb.Name);
        Assert.True(orb.Sources.IsAll);
        Assert.Equal(OrbStyle.Led, orb.Style);
        Assert.Equal(200, orb.Size);
        Assert.Equal(0.5, orb.Opacity);
        Assert.False(orb.AlwaysOnTop);
        Assert.True(orb.ClickThrough);
        Assert.True(orb.FadeWhenIdle);
        Assert.True(orb.Visible);

        Assert.Equal(["B", "A", "C"], settings.Orbs.Layouts.Select(l => l.Arrangement));
        var b = settings.Orbs.RecordFor("B", orb.Id)!.Value;
        Assert.Equal(new OrbPositionRecord("B", OrbAxis.Fraction(0.3), OrbAxis.Fraction(0.4)), b);
        Assert.Equal(new OrbPositionRecord("C", OrbAxis.Fraction(1), OrbAxis.Fraction(0)), settings.Orbs.RecordFor("C", orb.Id));
    }

    [Fact]
    public void MigrationWithoutAnchorsIsTheFreshDefault()
    {
        var settings = SettingsSerializer.Deserialize("{}");
        var orb = Assert.Single(settings.Orbs.Orbs);
        Assert.Equal(AppSettings.Default.Orbs.Orbs[0] with { Id = orb.Id }, orb);
        Assert.Empty(settings.Orbs.Layouts);
        Assert.True(settings.ShowOrbs);
        Assert.NotEqual(orb.Id, SettingsSerializer.Deserialize("{}").Orbs.Orbs[0].Id);
    }

    [Fact]
    public void MigrationIsStableOnceWritten()
    {
        var migrated = SettingsSerializer.Deserialize("{ \"orb\": { \"size\": 120, \"monitor\": \"A\", \"anchors\": { \"A\": { \"x\": 0.5, \"y\": 0.5 } } } }");
        var again = SettingsSerializer.Deserialize(SettingsSerializer.Serialize(migrated));
        Assert.Equal(migrated.Orbs, again.Orbs);
        SettingsSerializerTests.AssertEquivalent(migrated, again);
    }

    private static readonly (string Json, OrbDisplay[] Displays)[] LegacyCases =
    [
        (
            """{ "orb": { "size": 148, "monitor": "\\\\.\\DISPLAY1|1920x1080", "anchors": { "\\\\.\\DISPLAY1|1920x1080": { "x": 0.5, "y": 0.125 } } } }""",
            [new(@"\\.\DISPLAY1|1920x1080", new RectD(0, 0, 1920, 1040), 1, true)]
        ),
        (
            """{ "orb": { "size": 196, "monitor": "B|1920x1080", "anchors": { "A|2560x1440": { "x": 0.2, "y": 0.3 }, "B|1920x1080": { "x": 0.98765, "y": 0.01234 } } } }""",
            [new("A|2560x1440", new RectD(0, 0, 2560, 1392), 1.5, true), new("B|1920x1080", new RectD(2560, 0, 1920, 1080), 1, false)]
        ),
        (
            """{ "orb": { "size": 112, "monitor": "C|3840x2160", "anchors": { "C|3840x2160": { "x": 1, "y": 1 }, "A|2560x1440": { "x": 0.5, "y": 0.5 } } } }""",
            [
                new("A|2560x1440", new RectD(0, 0, 2560, 1392), 1.25, true),
                new("B|1920x1080", new RectD(-1920, -200, 1920, 1040), 1, false),
                new("C|3840x2160", new RectD(2560, -400, 3840, 2100), 2, false),
            ]
        ),
        (
            """{ "orb": { "size": 240, "monitor": "B|1920x1080", "anchors": { "B|1920x1080": { "x": 0, "y": 0.6 } } } }""",
            [new("A|2560x1440", new RectD(0, 0, 2560, 1392), 1.5, true), new("B|1920x1080", new RectD(-1920, 0, 1920, 1040), 1.75, false)]
        ),
        (
            """{ "orb": { "size": 160 } }""",
            [new("A|2560x1440", new RectD(0, 0, 2560, 1392), 1.5, false), new("B|1920x1080", new RectD(2560, 0, 1920, 1040), 1.25, true)]
        ),
        (
            """{ "orb": { "size": 96, "monitor": "A|1366x768", "anchors": { "A|1366x768": { "x": 0.33333, "y": 0.66667 } } } }""",
            [new("A|1366x768", new RectD(0, 40, 1366, 728), 1, true)]
        ),
    ];

    [Fact]
    public void MigratedOrbSitsExactlyWhereTodaysCodePutsIt()
    {
        foreach (var (json, displays) in LegacyCases)
        {
            var settings = SettingsSerializer.Deserialize(json);
            var orb = Assert.Single(settings.Orbs.Orbs);
            var (monitor, expected) = TodaysCenter(settings, displays);

            var resolved = OrbArrangement.Resolve(settings.Orbs, displays);
            var location = resolved.Locations[orb.Id];
            Assert.Equal(monitor.Key, location.Display);
            Assert.Equal(expected, location.Center);
            Assert.Equal(settings.OrbSize / 2 * monitor.Scale, location.Radius);
            Assert.Equal(settings.OrbSize, orb.Size);
            Assert.Equal(OrbStyle.Led, orb.Style);

            var persisted = SettingsSerializer.Deserialize(SettingsSerializer.Serialize(settings with { Orbs = resolved.Document }));
            Assert.Equal(expected, OrbArrangement.Resolve(persisted.Orbs, displays).Locations[orb.Id].Center);
        }
    }

    /// <summary>The single-orb placement of the previous version (OrbWindow.RestorePlacement).</summary>
    private static (OrbDisplay Monitor, PointD Center) TodaysCenter(AppSettings settings, OrbDisplay[] monitors)
    {
        var monitor = monitors.FirstOrDefault(m => string.Equals(m.Key, settings.OrbMonitor, StringComparison.OrdinalIgnoreCase));
        if (monitor.Key is null)
        {
            monitor = monitors.FirstOrDefault(m => m.IsMain);
        }

        if (monitor.Key is null)
        {
            monitor = monitors[0];
        }

        OrbAnchor? anchor = settings.OrbMonitor is { } key && settings.OrbAnchors.TryGetValue(key, out var a) ? a : null;
        var radius = settings.OrbSize / 2 * monitor.Scale;
        var margin = 12 * monitor.Scale;
        var found = anchor is not null && string.Equals(monitor.Key, settings.OrbMonitor, StringComparison.OrdinalIgnoreCase);
        var center = found
            ? PlacementMath.ClampDisc(PlacementMath.FromFraction(anchor!.Value.X, anchor.Value.Y, monitor.Work), radius, monitor.Work, margin)
            : PlacementMath.DefaultOrbCenter(monitor.Work, radius, margin * 2);
        return (monitor, center);
    }

    [Fact]
    public void LayoutsKeepTheSixteenMostRecentlyUsed()
    {
        var document = OrbsDocument.Fresh(IdA);
        for (var i = 0; i < 20; i++)
        {
            document = document.WithPosition($"arr-{i}", IdA, new OrbPositionRecord($"d{i}", OrbAxis.Fraction(i / 20.0), OrbAxis.Low(12)));
        }

        Assert.Equal(OrbsDocument.MaxArrangements, document.Layouts.Length);
        Assert.Equal("arr-19", document.Layouts[0].Arrangement);
        Assert.Equal("arr-4", document.Layouts[^1].Arrangement);
        Assert.Null(document.LayoutFor("arr-3"));

        document = document.Touch("ARR-10");
        Assert.Equal("arr-10", document.Layouts[0].Arrangement);
        Assert.Equal("arr-19", document.Layouts[1].Arrangement);
        Assert.Same(document, document.Touch("missing"));

        document = document.WithPosition("arr-20", IdA, new OrbPositionRecord("d", OrbAxis.Low(0), OrbAxis.Low(0)));
        Assert.Null(document.LayoutFor("arr-4"));
        Assert.NotNull(document.LayoutFor("arr-10"));

        var back = SettingsSerializer.Deserialize(SettingsSerializer.Serialize(AppSettings.Default with { Orbs = document })).Orbs;
        Assert.Equal(document.Layouts.Select(l => l.Arrangement), back.Layouts.Select(l => l.Arrangement));
        Assert.Equal(document, back);
    }

    [Fact]
    public void ArrangementKeysAreSortedAndJoined()
    {
        Assert.Equal("A|1+B|2+c|3", OrbsDocument.ArrangementKey(["c|3", "B|2", "A|1", "a|1"]));
        Assert.Equal("X", OrbsDocument.ArrangementKey(["X"]));
        Assert.Equal("", OrbsDocument.ArrangementKey([]));
        var displays = new[] { new OrbDisplay("B", new RectD(0, 0, 10, 10), 1, true), new OrbDisplay("A", new RectD(10, 0, 10, 10), 1, false) };
        Assert.Equal("A+B", OrbArrangement.Key(displays));
    }

    [Fact]
    public void RemovingAnOrbDropsItsPositions()
    {
        var document = SampleDocument();
        var removed = OrbEditing.Remove(document, IdB)!;
        Assert.Equal([IdA, IdC], removed.Orbs.Select(o => o.Id));
        Assert.Null(removed.LayoutFor("DISPLAY2|1920x1080"));
        Assert.NotNull(removed.RecordFor("DISPLAY1|2560x1440+DISPLAY2|1920x1080", IdA));
        Assert.Equal(removed, removed.WithoutPosition("missing", IdA));
        Assert.Null(removed.WithoutPosition("DISPLAY1|2560x1440+DISPLAY2|1920x1080", IdA).RecordFor("DISPLAY1|2560x1440+DISPLAY2|1920x1080", IdA));
    }

    [Fact]
    public void DocumentsCompareByValue()
    {
        Assert.Equal(SampleDocument(), SampleDocument());
        Assert.Equal(SampleDocument().GetHashCode(), SampleDocument().GetHashCode());
        Assert.NotEqual(SampleDocument(), OrbEditing.SetVisible(SampleDocument(), IdA, false));
        Assert.NotEqual(SampleDocument(), SampleDocument().Touch("DISPLAY2|1920x1080"));
    }

    [Fact]
    public void ValidatedFixesInvalidValues()
    {
        var orb = new OrbDefinition(" id ")
        {
            Name = "  ",
            Style = (OrbStyle)42,
            Size = double.NaN,
            Opacity = double.PositiveInfinity,
            RedlineOutput = double.NaN,
            RedlineTotal = 5,
        }.Validated();
        Assert.Equal("id", orb.Id);
        Assert.Null(orb.Name);
        Assert.Equal(OrbStyle.Led, orb.Style);
        Assert.Equal(148, orb.Size);
        Assert.Equal(1, orb.Opacity);
        Assert.Null(orb.RedlineOutput);
        Assert.Equal(HeatMath.MinRedlineTotal, orb.RedlineTotal);
        Assert.True(Guid.TryParse(new OrbDefinition("").Validated().Id, out _));

        var empty = new OrbsDocument([]).Validated();
        Assert.Single(empty.Orbs);
        var settings = (AppSettings.Default with { Orbs = null! }).Validated();
        Assert.Equal(AppSettings.Default.Orbs, settings.Orbs);
    }

    [Fact]
    public void StyleChangesClampTheSize()
    {
        var orb = new OrbDefinition(IdA) { Style = OrbStyle.Minimal, Size = 72 };
        Assert.Equal(72, orb.Validated().Size);
        var needle = orb.WithStyle(OrbStyle.Needle);
        Assert.Equal(OrbStyle.Needle, needle.Style);
        Assert.Equal(96, needle.Size);
        Assert.Equal(96, needle.WithStyle(OrbStyle.Minimal).Size);
        Assert.Equal(64, needle.WithStyle(OrbStyle.Minimal).WithSize(10).Size);
        Assert.Equal(240, orb.WithSize(500).Size);
        Assert.Equal(OrbStyle.Led, orb.WithStyle((OrbStyle)99).Style);
    }

    [Fact]
    public void MasterSwitchShowsEveryOrbWhenAllAreHidden()
    {
        var document = new OrbsDocument([new OrbDefinition(IdA) { Visible = false }, new OrbDefinition(IdB) { Visible = false }]);
        var settings = (AppSettings.Default with { Orbs = document }).WithShowOrbs(false);
        Assert.False(settings.ShowOrbs);
        Assert.All(settings.Orbs.Orbs, o => Assert.False(o.Visible));

        var on = settings.WithShowOrbs(true);
        Assert.True(on.ShowOrbs);
        Assert.All(on.Orbs.Orbs, o => Assert.True(o.Visible));

        var oneVisible = AppSettings.Default with { ShowOrbs = false, Orbs = OrbEditing.SetVisible(document, IdA, true) };
        var kept = oneVisible.WithShowOrbs(true);
        Assert.True(kept.Orbs.Orbs[0].Visible);
        Assert.False(kept.Orbs.Orbs[1].Visible);
    }

    [Fact]
    public void SourcesSelectionRules()
    {
        Assert.Equal(OrbSources.All, default(OrbSources));
        Assert.Equal(OrbSources.All, OrbSources.Of(SourceSet.Empty));
        Assert.Equal(OrbSources.All, OrbSources.From(null));
        Assert.Equal(OrbSources.Of(SourceKind.Gemini), OrbSources.From(SourceSet.Of(SourceKind.Gemini)));

        var single = OrbSources.All.Checking(SourceKind.Codex);
        Assert.Equal(SourceSet.Of(SourceKind.Codex), single.Set);
        Assert.Null(single.Unchecking(SourceKind.Codex));
        Assert.Null(OrbSources.All.Unchecking(SourceKind.Codex));
        Assert.Equal(single, single.Unchecking(SourceKind.Gemini));

        var pair = single.Checking(SourceKind.ClaudeCode);
        Assert.Equal(["codex", "claudeCode"], pair.ToKeys());
        Assert.Equal(single, pair.Unchecking(SourceKind.ClaudeCode));
        Assert.Equal(pair, single.Toggling(SourceKind.ClaudeCode));
        Assert.Equal(single, pair.Toggling(SourceKind.ClaudeCode));
        Assert.Equal(OrbSources.Of(SourceKind.Omp), OrbSources.All.Toggling(SourceKind.Omp));
        Assert.True(pair.Tracks(SourceKind.Codex));
        Assert.False(pair.Tracks(SourceKind.Gemini));
        Assert.True(OrbSources.All.Tracks(SourceKind.Gemini));
        Assert.Equal(SourceSet.Of(SourceKind.Codex), pair.Resolve(SourceSet.Create([SourceKind.Codex, SourceKind.Gemini])));
        Assert.Equal(SourceSet.Everything, OrbSources.All.Resolve(SourceSet.Everything));

        var future = OrbSources.FromKeys(["future", "codex"]);
        Assert.Null(future.Unchecking(SourceKind.Codex));
        Assert.Equal(["codex", "gemini", "future"], future.Checking(SourceKind.Gemini).ToKeys());
        Assert.Equal("all", OrbSources.All.ToString());
        Assert.Null(OrbSources.All.ToKeys());
        Assert.Throws<ArgumentOutOfRangeException>(() => OrbSources.KeyOf((SourceKind)99));
        Assert.All(Enum.GetValues<SourceKind>(), k => Assert.True(OrbSources.TryParseKey(OrbSources.KeyOf(k), out var back) && back == k));
    }

    [Fact]
    public void SourceKeysAreTheMetricsKeysInSourceOrder()
    {
        Assert.Equal(
            ["codex", "claudeCode", "claudeDesktop", "deepseek", "opencode", "gemini", "omp", "antigravity", "workbuddy", "workbuddyAi", "zcode", "kimi", "grokBuild", "pi", "openclaw", "hermes", "droid", "trae"],
            Enum.GetValues<SourceKind>().Select(OrbSources.KeyOf));
        Assert.Equal(["openclaw", "hermes", "droid", "trae"], OrbSources.Of(SourceSet.Everything).ToKeys()!.TakeLast(4));
        Assert.False(OrbSources.TryParseKey("workbuddy-ai", out _));
        Assert.False(OrbSources.TryParseKey("WorkBuddy", out _));
    }

    [Theory]
    [InlineData(", \"showName\": true", true)]
    [InlineData(", \"showName\": false", false)]
    [InlineData(", \"showName\": null", null)]
    [InlineData(", \"showName\": \"yes\"", null)]
    [InlineData(", \"showName\": 1", null)]
    [InlineData(", \"showName\": {}", null)]
    [InlineData("", null)]
    public void ShowNameReadsTrueFalseOrAutomatic(string fields, bool? expected)
    {
        Assert.Equal(expected, WithOrbsJson(OneOrb(fields)).Orbs.Orbs[0].ShowName);
    }

    [Fact]
    public void TheGlobalNamePlateSettingRoundTripsAndDefaultsToAuto()
    {
        Assert.Equal(OrbNamesMode.Auto, AppSettings.Default.OrbNames);
        Assert.Equal("auto", (string)Root(AppSettings.Default)["orbNames"]!);
        foreach (var mode in Enum.GetValues<OrbNamesMode>())
        {
            var back = SettingsSerializer.Deserialize(SettingsSerializer.Serialize(AppSettings.Default with { OrbNames = mode }));
            Assert.Equal(mode, back.OrbNames);
        }

        Assert.Equal("never", (string)Root(AppSettings.Default with { OrbNames = OrbNamesMode.Never })["orbNames"]!);
        Assert.Equal("always", (string)Root(AppSettings.Default with { OrbNames = OrbNamesMode.Always })["orbNames"]!);
        Assert.Equal(OrbNamesMode.Auto, (AppSettings.Default with { OrbNames = (OrbNamesMode)42 }).Validated().OrbNames);
    }

    [Theory]
    [InlineData("\"sometimes\"")]
    [InlineData("\"Always\"")]
    [InlineData("true")]
    [InlineData("2")]
    [InlineData("null")]
    public void UnknownGlobalNamePlateValuesReadAsAuto(string value)
    {
        Assert.Equal(OrbNamesMode.Auto, SettingsSerializer.Deserialize("{ \"orbNames\": " + value + " }").OrbNames);
    }

    [Fact]
    public void TheGlobalNamePlateSettingSurvivesTheMigrations()
    {
        // A version 1 file (redline migration) without an orbs value (single-orb migration).
        var settings = SettingsSerializer.Deserialize("{ \"version\": 1, \"redline\": 400, \"orbNames\": \"never\", \"orb\": { \"size\": 112 } }");
        Assert.Equal(OrbNamesMode.Never, settings.OrbNames);
        Assert.Null(Assert.Single(settings.Orbs.Orbs).ShowName);
        Assert.Equal(OrbNamesMode.Always, SettingsSerializer.Deserialize("{ \"orbNames\": \"always\" }").OrbNames);
    }
}
