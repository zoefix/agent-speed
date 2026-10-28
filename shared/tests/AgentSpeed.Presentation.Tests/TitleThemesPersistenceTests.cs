using System.Text;
using System.Text.Json;
using AgentSpeed.Core;
using AgentSpeed.Presentation.Settings;
using AgentSpeed.Presentation.Settings.Orbs;
using AgentSpeed.Presentation.Titles;

namespace AgentSpeed.Presentation.Tests;

public class TitleThemesPersistenceTests
{
    private static TitleThemesDocument? Read(string json)
    {
        using var document = JsonDocument.Parse(json);
        return TitleThemesJson.Read(document.RootElement);
    }

    private static string Write(TitleThemesDocument document)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            TitleThemesJson.Write(writer, document);
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    [Fact]
    public void TheSpecExampleReadsAndRoundTrips()
    {
        const string json = """
            {"version":1,"selected":"cultivation","custom":[{"id":"3E0B6C1A-0000","name":"我的修仙路","base":"cultivation","idle":"",
             "titles":["","","","結丹","","","","","","","","飛升",""]}]}
            """;
        var doc = Read(json)!;
        Assert.Equal("cultivation", doc.Selected);
        var set = Assert.Single(doc.Custom);
        Assert.Equal("3E0B6C1A-0000", set.Id);
        Assert.Equal("我的修仙路", set.Name);
        Assert.Equal(TitlePreset.Cultivation, set.Base);
        Assert.Equal("結丹", set.Field(4));
        Assert.Equal("飛升", set.Field(12));
        Assert.Equal(doc, Read(Write(doc)));
    }

    [Fact]
    public void ReadingIsTolerant()
    {
        const string json = """
            {"version":7,"selected":"  ","custom":[
              {"id":"a","name":"A","base":"future","idle":"\u0007idle ","titles":["x","y"],"extra":1},
              {"id":"a","name":"dup"},
              {"name":"no id"},
              {"id":"wind","name":"preset id"},
              "junk",
              {"id":"b","name":42,"base":"space","titles":["1","2","3","4","5","6","7","8","9","10","11","12","13","14","15"]}
            ]}
            """;
        var doc = Read(json)!;
        Assert.Equal("vehicles", doc.Selected);
        Assert.Equal(["a", "b"], doc.Custom.Select(s => s.Id));
        var a = doc.Custom[0];
        Assert.Equal(TitlePreset.Vehicles, a.Base);
        Assert.Equal("idle", a.Idle);
        Assert.Equal(["x", "y", "", "", "", "", "", "", "", "", "", "", ""], a.Titles.ToArray());
        var b = doc.Custom[1];
        Assert.Equal(string.Empty, b.Name);
        Assert.Equal(TitlePreset.Space, b.Base);
        Assert.Equal("13", b.Field(13));
        Assert.Null(Read("[]"));
        Assert.Null(Read("\"vehicles\""));
        Assert.Equal(TitleThemesDocument.Default, Read("{}"));
    }

    [Fact]
    public void StoredTextIsRenormalizedButNotCut()
    {
        // MAGNIFICENT SUPERLATIVE is far wider than the width budget; only the text rules apply on load.
        var doc = Read("""{"selected":"x","custom":[{"id":"x","name":"n","titles":["MAGNIFICENT 🚀  SUPERLATIVE"]}]}""")!;
        Assert.Equal("MAGNIFICENT SUPERLATIVE", doc.Custom[0].Field(1));
    }

    [Fact]
    public void ACustomSetIsLimitedToEight()
    {
        var sets = string.Join(',', Enumerable.Range(0, 12).Select(i => $$"""{"id":"s{{i}}","name":"S{{i}}"}"""));
        Assert.Equal(8, Read($$"""{"custom":[{{sets}}]}""")!.Custom.Length);
    }

    [Fact]
    public void SettingsWithoutTitleThemesReadAsVehiclesWithoutCustomSets()
    {
        // Settings written before title themes (no titleThemes value, orbs without titleTheme): nothing to migrate.
        const string json = """
            {"version":2,"orbs":{"version":1,"orbs":[{"id":"O1","sources":"all","style":"led","size":148}]}}
            """;
        var settings = SettingsSerializer.Deserialize(json);
        Assert.Equal(TitleThemesDocument.Default, settings.TitleThemes);
        Assert.Equal("vehicles", settings.TitleThemes.General.Id);
        Assert.Null(Assert.Single(settings.Orbs.Orbs).TitleTheme);
    }

    [Fact]
    public void TitleThemesAndOrbThemesRoundTripThroughTheSettingsFile()
    {
        var set = new CustomTitleSet("C-1", "我的修仙路", TitlePreset.Cultivation, "", ["", "", "", "結丹"]);
        var orbs = new OrbsDocument([new OrbDefinition("O1") { TitleTheme = "C-1" }, new OrbDefinition("O2") { TitleTheme = "newerPreset" },
            new OrbDefinition("O3")]);
        var settings = AppSettings.Default with
        {
            TitleThemes = new TitleThemesDocument { Selected = "wuxia", Custom = [set] },
            Orbs = orbs,
        };
        var json = SettingsSerializer.Serialize(settings);
        Assert.Contains("\"titleThemes\"", json, StringComparison.Ordinal);
        Assert.Contains("\"titleTheme\": null", json, StringComparison.Ordinal);
        var back = SettingsSerializer.Deserialize(json);
        Assert.Equal(settings.TitleThemes, back.TitleThemes);
        Assert.Equal(["C-1", "newerPreset", null], back.Orbs.Orbs.Select(o => o.TitleTheme));
        Assert.Equal("wuxia", back.TitleThemes.OrbTheme(back.Orbs.Find("O2")!.TitleTheme).Id);
        Assert.Equal("C-1", back.TitleThemes.OrbTheme(back.Orbs.Find("O1")!.TitleTheme).Id);
    }

    [Fact]
    public void AddDuplicateAndSplitCopyTheOrbsTitleTheme()
    {
        var available = SourceSet.Create([SourceKind.Codex, SourceKind.ClaudeCode]);
        var doc = new OrbsDocument([new OrbDefinition("t") { TitleTheme = "space" }]);
        var added = OrbEditing.Add(doc, "t", available, () => "n1")!;
        Assert.Equal("space", added.Document.Find("n1")!.TitleTheme);
        var duplicated = OrbEditing.Duplicate(doc, "t", () => "n2")!;
        Assert.Equal("space", duplicated.Document.Find("n2")!.TitleTheme);
        var split = OrbEditing.Split(doc, "t", available)!;
        Assert.All(split.Document.Orbs, orb => Assert.Equal("space", orb.TitleTheme));
        var bySource = OrbEditing.SplitBySource(doc, "t", available)!;
        Assert.All(bySource.Document.Orbs, orb => Assert.Equal("space", orb.TitleTheme));
        Assert.Null(OrbEditing.SetTitleTheme(doc, "t", null).Find("t")!.TitleTheme);
        Assert.Equal("C1", OrbEditing.SetTitleTheme(doc, "t", " C1 ").Find("t")!.TitleTheme);
    }

    [Fact]
    public void DeletingASetMovesItsOrbsToTheBaseAndUndoRestoresThem()
    {
        var orbs = new OrbsDocument([new OrbDefinition("a") { TitleTheme = "C1" }, new OrbDefinition("b") { TitleTheme = "wind" },
            new OrbDefinition("c")]);
        var replaced = OrbEditing.ReplaceTitleTheme(orbs, "C1", "cultivation");
        Assert.Equal(["cultivation", "wind", null], replaced.Orbs.Select(o => o.TitleTheme));
        Assert.Same(orbs, OrbEditing.ReplaceTitleTheme(orbs, "missing", "vehicles"));

        var afterRemove = replaced with { Orbs = replaced.Orbs.RemoveAt(2) };
        var restored = OrbEditing.RestoreTitleThemes(afterRemove, orbs);
        Assert.Equal(["C1", "wind"], restored.Orbs.Select(o => o.TitleTheme));
    }
}
