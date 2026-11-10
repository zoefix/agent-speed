using AgentSpeed.Core;
using AgentSpeed.Core.Intelligence;
using AgentSpeed.Core.Perf;
using AgentSpeed.Presentation.Colors;
using AgentSpeed.Presentation.Characters;
using AgentSpeed.Presentation.Localization;
using AgentSpeed.Presentation.Menus;
using AgentSpeed.Presentation.Orb;
using AgentSpeed.Presentation.Orb.Lines;
using AgentSpeed.Presentation.Perf;
using AgentSpeed.Presentation.Settings;
using AgentSpeed.Presentation.Settings.Orbs;

namespace AgentSpeed.Presentation.Tests.Character;

/// <summary>
/// DESIGN 2a.6, user's choice 2026-11-10: the 性能 card's 首字 row and the models card — their switches, texts, quiet rule,
/// lines and accessible text.
/// </summary>
public class CharacterReadoutRowsTests
{
    private const string Id = "AAAAAAAA-0000-4000-8000-000000000001";

    private static readonly Localizer En = new(Localizer.LoadEmbeddedTable(), LanguageCodes.En);
    private static readonly Localizer ZhHant = new(Localizer.LoadEmbeddedTable(), LanguageCodes.ZhHant);

    private static OrbDefinition OneOrb(string extra) => SettingsSerializer.Deserialize(
        "{ \"orbs\": { \"orbs\": [ { \"id\": \"" + Id + "\", \"style\": \"character\"" + extra + " } ] } }").Orbs.Orbs[0];

    private static OrbModelRow Row(string model, int? speed = null) => new(model, speed, null);

    // MARK: Switches

    [Fact]
    public void BothNewReadoutsAreOnByDefaultAndOlderSettingsTurnThemOn()
    {
        Assert.True(CharacterReadouts.Default.Latency && CharacterReadouts.Default.Models);
        var older = OneOrb(", \"characterReadouts\": { \"intelligence\": true, \"thinking\": true, \"network\": false, \"cost\": true, \"agents\": true, \"model\": true }");
        Assert.True(older.CharacterReadouts.Latency && older.CharacterReadouts.Models);
        Assert.False(older.CharacterReadouts.Network);
        var odd = OneOrb(", \"characterReadouts\": { \"latency\": 0, \"models\": \"no\" }").CharacterReadouts;
        Assert.Equal(CharacterReadouts.Default, odd);
    }

    [Fact]
    public void TheNewSwitchesRoundTrip()
    {
        var readouts = CharacterReadouts.Default with { Latency = false, Models = false };
        var orb = new OrbDefinition(Id) { Style = OrbStyle.Character, CharacterReadouts = readouts };
        var text = SettingsSerializer.Serialize(AppSettings.Default with { Orbs = new OrbsDocument([orb]) });
        Assert.Contains("\"latency\": false", text, StringComparison.Ordinal);
        Assert.Contains("\"models\": false", text, StringComparison.Ordinal);
        Assert.Equal(readouts, SettingsSerializer.Deserialize(text).Orbs.Orbs[0].CharacterReadouts);
        Assert.Equal(CharacterReadouts.Default with { Models = false },
            OneOrb(", \"characterReadouts\": { \"models\": false }").CharacterReadouts);
    }

    [Fact]
    public void TheReadoutsAreListedInTheMenusOrder()
    {
        Assert.Equal(["intelligence", "thinking", "latency", "network", "cost", "agents", "models", "quota", "model"], CharacterReadouts.All.Select(CharacterReadouts.Key));
        Assert.Equal(["Performance", "Thinking Speed", "First-Token Latency", "Network Speed", "Cost", "Today & Agents", "Running Models", "Quota", "Fastest Model"],
            CharacterReadouts.All.Select(r => En[CharacterReadouts.TitleKey(r)]));
        Assert.Equal("首字延遲", ZhHant["character.readouts.latency"]);
        Assert.Equal("執行中的模型", ZhHant["character.readouts.models"]);
    }

    [Fact]
    public void TheMenuChecksTheNewReadoutsAndTogglesThem()
    {
        var orb = new OrbDefinition("o0") { Style = OrbStyle.Character, CharacterReadouts = CharacterReadouts.Default with { Models = false } };
        var settings = AppSettings.Default with { Orbs = new OrbsDocument([orb]) };
        var menu = OrbMenuModel.Build("o0", settings, SourceAvailability.All, En)!;
        var readouts = menu.Rows.Single(r => r.Label == En["character.readouts"]).Submenu!;
        var latency = readouts.Rows.Single(r => r.Label == En["character.readouts.latency"]);
        var models = readouts.Rows.Single(r => r.Label == En["character.readouts.models"]);
        Assert.True(latency.IsChecked && latency.IsEnabled);
        Assert.False(models.IsChecked);
        Assert.Equal(new MenuAction.SetOrbCharacterReadout("o0", CharacterReadout.Latency, false), latency.Action);
        Assert.Equal(new MenuAction.SetOrbCharacterReadout("o0", CharacterReadout.Models, true), models.Action);
        var edited = OrbEditing.SetCharacterReadout(settings.Orbs, "o0", CharacterReadout.Models, true).Find("o0")!.CharacterReadouts;
        Assert.Equal(CharacterReadouts.Default, edited);
    }

    // MARK: 首字

    [Theory]
    [InlineData(850, "850", "ms")]
    [InlineData(4, "0", "ms")]
    [InlineData(845, "850", "ms")]
    [InlineData(994, "990", "ms")]
    [InlineData(995, "1.0", "s")]
    [InlineData(1180, "1.2", "s")]
    [InlineData(1250, "1.3", "s")]
    [InlineData(9949, "9.9", "s")]
    [InlineData(9950, "10", "s")]
    [InlineData(14_400, "14", "s")]
    [InlineData(95_000, "95", "s")]
    public void TheLatencyRowReadsMillisecondsThenTenthsThenWholeSeconds(double ms, string number, string unit)
    {
        var latency = OrbLatency.Of(ms, En)!;
        Assert.Equal(new OrbLine("TTFT", number, unit), latency.Line);
        Assert.Equal($"first-token latency {number} {unit}", latency.Spoken);
    }

    [Theory]
    [InlineData(LanguageCodes.ZhHant, 850, "首字", "毫秒", "首字延遲 850 毫秒")]
    [InlineData(LanguageCodes.ZhHans, 1200, "首字", "秒", "首字延迟 1.2 秒")]
    [InlineData(LanguageCodes.Ja, 14_000, "初回", "秒", "初回出力まで 14 秒")]
    public void TheLatencyRowSpeaksTheAppLanguage(string language, double ms, string glyph, string unit, string spoken)
    {
        var latency = OrbLatency.Of(ms, new Localizer(Localizer.LoadEmbeddedTable(), language))!;
        Assert.Equal((glyph, unit, spoken), (latency.Line.Glyph, latency.Line.Unit, latency.Spoken));
    }

    [Fact]
    public void NoLatencyMeansNoRow()
    {
        Assert.Null(OrbLatency.Of(null, En));
        Assert.Null(OrbLatency.Of(double.NaN, En));
    }

    [Fact]
    public void TheRowFadesInAndOutAndKeepsItsLastValueWhileItFades()
    {
        var first = OrbLatency.Of(1200, En);
        var fade = OrbLatencyFade.None.Next(first, 10);
        Assert.Equal(0, fade.Alpha(10));
        Assert.Equal(0.5, fade.Alpha(10.125), 9);
        Assert.Equal(1, fade.Alpha(11));
        fade = fade.Next(null, 12);
        Assert.Same(first, fade.Latency);
        Assert.Equal(0.6, fade.Alpha(12.1), 9);
        Assert.True(fade.IsFading(12.1));
        var back = fade.Next(OrbLatency.Of(900, En), 12.1);
        Assert.Equal(0.6, back.Alpha(12.1), 9);
        Assert.Equal("900", back.Latency!.Line.Number);
        Assert.True(fade.Next(null, 12.2).IsGone(12.3));
        Assert.Equal(1, fade.Share(12.1, reduceMotion: true));
        Assert.Equal(OrbLatencyFade.None, OrbLatencyFade.None.Next(null, 5));
    }

    // MARK: Models

    [Fact]
    public void RowsCarryTheRoundedSpeedAndTheVendorsAccent()
    {
        var accent = Rgba.FromHex("#D97757");
        var rows = OrbModelsText.Rows(
            [new PerfRunningModel("claude-opus-5-5", 99.5, 2, SourceKind.ClaudeCode), new PerfRunningModel("gpt-6.1-sol", null, 1, SourceKind.Codex)],
            m => m.Source == SourceKind.ClaudeCode ? accent : null);
        Assert.Equal([new OrbModelRow("claude-opus-5-5", 100, accent), new OrbModelRow("gpt-6.1-sol", null, null)], rows);
        Assert.Equal(("100", "–"), (rows[0].Number, rows[1].Number));
        Assert.Equal("+3 more", OrbModelsText.More(3, En));
        Assert.Equal("+3 個模型", OrbModelsText.More(3, ZhHant));
    }

    [Fact]
    public void TheVendorDotTakesTheAccentOfItsCharacter()
    {
        var catalog = CharacterCatalog.Load("""
            {"format": "agent-speed-character-catalog", "version": 1, "characters": [
              {"id": "claude", "packVersion": 1, "name": {"zh-Hant": "C", "zh-Hans": "C", "en": "C", "ja": "C"}, "vendors": ["anthropic"],
               "accent": "#D97757", "size": 10, "sha256": "0000000000000000000000000000000000000000000000000000000000000000",
               "url": "https://github.com/zoefix/agent-speed/releases/download/characters/claude.zip"}]}
            """u8);
        Assert.Single(catalog.Characters);
        Assert.Equal(Rgba.FromHex("#D97757"), VendorAccents.Of("anthropic", [], catalog));
        Assert.Null(VendorAccents.Of("openai", [], catalog));
        Assert.Null(VendorAccents.Of(null, [], catalog));
    }

    [Fact]
    public void TheModelsCardHoldsItsLastRowsForTenSecondsThenLetsThemGo()
    {
        var rule = new OrbModelsQuietRule();
        Assert.True(rule.Apply([Row("a", 50)], 10));
        Assert.True(rule.IsLoud(10));
        var version = rule.Version;
        Assert.False(rule.Apply([Row("a", 50)], 10.5));
        Assert.Equal(version, rule.Version);
        Assert.False(rule.Apply([], 11));
        Assert.Equal([Row("a", 50)], rule.Rows);
        Assert.True(rule.IsLoud(20.4));
        Assert.False(rule.IsLoud(20.6));
        Assert.Equal(20.5, rule.QuietAt);
        Assert.False(rule.Apply([], 20.8));
        Assert.Single(rule.Rows);
        Assert.True(rule.Apply([], 21.1));
        Assert.Empty(rule.Rows);
        Assert.True(rule.Apply([Row("b")], 30));
        Assert.True(rule.IsLoud(30));
    }

    [Fact]
    public void AtMostFourRowsThenTheMoreLineAndTwoBelowNinetySix()
    {
        var rows = new[] { Row("a", 90), Row("b", 80), Row("c", 70), Row("d", 60), Row("e", 50), Row("f") };
        var motion = new OrbModelsMotion();
        motion.Sync(rows, 1, OrbModelsText.MaxRows, 10, reduceMotion: false);
        Assert.Equal(5, motion.Count);
        Assert.Equal(["a", "b", "c", "d", null], motion.Lines.OrderBy(l => l.To).Select(l => l.Row?.Model));
        Assert.Equal(2, motion.Lines.Single(l => l.Row is null).Hidden);
        Assert.All(motion.Lines, l => Assert.Equal(1, l.Alpha(10)));

        var small = new OrbModelsMotion();
        small.Sync(rows, 1, OrbModelsText.SmallRows, 10, reduceMotion: false);
        Assert.Equal(["a", "b", null], small.Lines.OrderBy(l => l.To).Select(l => l.Row?.Model));
        Assert.Equal(4, small.Lines.Single(l => l.Row is null).Hidden);

        var exact = new OrbModelsMotion();
        exact.Sync(rows[..4], 1, OrbModelsText.MaxRows, 10, reduceMotion: false);
        Assert.Equal(4, exact.Count);
        Assert.DoesNotContain(exact.Lines, l => l.Row is null);
    }

    [Fact]
    public void RowsGlideToTheirNewPlacesAndALeavingModelFadesOut()
    {
        var motion = new OrbModelsMotion();
        motion.Sync([Row("a", 90), Row("b", 80), Row("c", 70)], 1, 4, 10, reduceMotion: false);
        motion.Sync([Row("b", 95), Row("a", 90)], 2, 4, 20, reduceMotion: false);
        var a = motion.Lines.Single(l => l.Row?.Model == "a");
        var b = motion.Lines.Single(l => l.Row?.Model == "b");
        var c = motion.Lines.Single(l => l.Row?.Model == "c");
        Assert.Equal((0, 1), (a.Position(20), b.Position(20)));
        Assert.Equal(0.5, a.Position(20.15), 9);
        Assert.Equal((1, 0), (a.Position(20.3), b.Position(20.3)));
        Assert.Equal(95, b.Row!.Speed);
        Assert.True(c.Leaving);
        Assert.Equal(0.5, c.Alpha(20.125), 9);
        Assert.Equal(2, motion.Count);
        Assert.True(motion.IsMoving(20.2));
        Assert.False(motion.IsMoving(20.4));
        motion.Sync([Row("b", 95), Row("a", 90)], 2, 4, 20.4, reduceMotion: false);
        Assert.DoesNotContain(motion.Lines, l => l.Row?.Model == "c");

        motion.Sync([Row("b", 95), Row("a", 90), Row("d", 10)], 3, 4, 30, reduceMotion: false);
        Assert.Equal(0, motion.Lines.Single(l => l.Row?.Model == "d").Alpha(30));

        var still = new OrbModelsMotion();
        still.Sync([Row("a", 90), Row("b", 80)], 1, 4, 10, reduceMotion: true);
        still.Sync([Row("b", 95), Row("a", 90)], 2, 4, 20, reduceMotion: true);
        Assert.Equal(1, still.Lines.Single(l => l.Row?.Model == "a").Position(20));
    }

    [Theory]
    [InlineData("claude-sonnet-4-5-20250929", "claude-sonnet-4-5")]
    [InlineData("gpt-4o-2024-08-06", "gpt-4o")]
    [InlineData("claude-opus-5-5", "claude-opus-5-5")]
    [InlineData("gemini-4-argon-preview", "gemini-4-argon-preview")]
    [InlineData("glm-5-0929", "glm-5-0929")]
    [InlineData("model-2025-9-29", "model-2025-9-29")]
    [InlineData("x-20250929-20251001", "x-20250929")]
    [InlineData("20250929", "20250929")]
    [InlineData("2025-09-29", "2025-09-29")]
    [InlineData("-20250929", "-20250929")]
    public void DisplayNamesDropATrailingDate(string id, string shown) => Assert.Equal(shown, OrbModelName.Display(id));

    [Fact]
    public void NamesAreCutAtTheirEnd()
    {
        static double Measure(string s) => s.Length;
        Assert.Equal("claude-opus-5-5", OrbModelName.Truncate("claude-opus-5-5", 15, Measure));
        Assert.Equal("claude-o…", OrbModelName.Truncate("claude-opus-5-5", 9, Measure));
        Assert.Equal("c…", OrbModelName.Truncate("claude-opus-5-5", 2, Measure));
        Assert.Equal("…", OrbModelName.Truncate("claude", 0.5, Measure));
    }

    /// <summary>Columns of 8 pt text: a 72 pt name, 15 pt of digits, a 16 pt unit; the narrowest form is 55 pt wide.</summary>
    private static OrbModelsColumns Columns(double longest = 72) => new(longest, 15, 16, 0, 8.32, 6.4, 3, 12.8);

    [Fact]
    public void NamesNarrowFromNineTToFourTThenTheRowsKeepTheDotAndTheSpeed()
    {
        const double t = 8;
        var columns = Columns();
        Assert.Equal(12.8 + 8.32 + 15 + 3 + 16, columns.Width(0), 9);
        Assert.Equal(columns.Width(0) + 6.4 + 72, columns.Width(9 * t), 9);
        Assert.Equal(9, OrbModelsNames.Fitting(columns, t, 200));
        Assert.Equal(6.5, OrbModelsNames.Fitting(columns, t, columns.Width(6.5 * t) + 2));
        Assert.Equal(4, OrbModelsNames.Fitting(columns, t, columns.Width(4 * t)));
        Assert.Equal(0, OrbModelsNames.Fitting(columns, t, columns.Width(4 * t) - 1));
        Assert.Equal(9, OrbModelsNames.Fitting(Columns(longest: 20), t, Columns(longest: 20).Width(0) + 30));
    }

    [Fact]
    public void NamesNarrowAtOnceAndWidenOnlyAfterTheyFitWithRoomToSpareFor1point2Seconds()
    {
        const double t = 8;
        const double spare = 0.04 * 296;
        var columns = Columns();
        var names = new OrbModelsNames();
        names.Update(columns, t, 500, spare, 1 / 60.0, atOnce: false);
        Assert.Equal(9, names.Room);
        var tight = columns.Width(5 * t) + 1;
        names.Update(columns, t, tight, spare, 1 / 60.0, atOnce: false);
        Assert.Equal(5, names.Room);
        names.Update(columns, t, columns.Width(4 * t) - 1, spare, 1 / 60.0, atOnce: false);
        Assert.Equal(0, names.Room);

        var roomy = columns.Width(7 * t) + spare;
        for (var n = 0; n < 66; n++)
        {
            names.Update(columns, t, roomy, spare, 1 / 60.0, atOnce: false);
        }

        Assert.Equal(0, names.Room);
        for (var n = 0; n < 10; n++)
        {
            names.Update(columns, t, roomy, spare, 1 / 60.0, atOnce: false);
        }

        Assert.Equal(7, names.Room);

        var close = columns.Width(9 * t) + spare - 1;
        for (var n = 0; n < 120; n++)
        {
            names.Update(columns, t, close, spare, 1 / 60.0, atOnce: false);
        }

        Assert.Equal(8.5, names.Room);
        names.Update(columns, t, 500, spare, 1 / 60.0, atOnce: true);
        Assert.Equal(9, names.Room);
    }

    // MARK: Accessibility

    private static OrbLinesSample Performance(bool generating = true)
    {
        var sums = new IntelligenceSums(Output: 1200, Thinking: 528, ThinkingBasis: 1200) { Score = TestSums.Live(1286.25) };
        return OrbLinesText.Intelligence(PerfSetReading.Of(sums, []), sums, generating, En);
    }

    [Fact]
    public void TheOrbReadsTheLatencyAfterThePerformanceAndTheModelsAfterTheCost()
    {
        var readings = new OrbCharacterReadings();
        var money = new Cost.LiveMoney("0.0042", "US$/s", "$", false, false, 4200, "", "");
        readings.Apply(Performance(), money, true, 10, OrbLatency.Of(1180, En), [Row("claude-opus-5-5", 106), Row("gpt-6.1-sol")]);
        var sentence = OrbCharacterSpeech.Sentence(readings, CharacterReadouts.Default, 10.5, En, "US dollars")!;
        Assert.Equal(
            "performance 1,286, thinking 176 tokens per second, first-token latency 1.2 s, cost 0.0042 US dollars per second (API-equivalent), "
            + "running models: claude-opus-5-5 at 106 tokens per second, gpt-6.1-sol",
            sentence);

        var off = OrbCharacterSpeech.Sentence(readings, CharacterReadouts.Default with { Latency = false, Models = false, Cost = false }, 10.5, En, "US dollars");
        Assert.Equal("performance 1,286, thinking 176 tokens per second", off);

        var dated = new OrbCharacterReadings();
        dated.Apply(null, null, true, 10, null, [Row("claude-sonnet-4-5-20250929", 80)]);
        Assert.Equal("running models: claude-sonnet-4-5-20250929 at 80 tokens per second",
            OrbCharacterSpeech.Sentence(dated, CharacterReadouts.Default, 10, En, "US dollars"));

        var zh = new OrbCharacterReadings();
        zh.Apply(null, null, true, 10, null, [Row("glm-5", 42), Row("kimi-k3", 30)]);
        Assert.Equal("執行中的模型：glm-5 每秒 42 個 token、kimi-k3 每秒 30 個 token",
            OrbCharacterSpeech.Sentence(zh, CharacterReadouts.Default, 10, ZhHant, "美元"));
    }

    [Fact]
    public void AHiddenLatencyRowIsNotRead()
    {
        var readings = new OrbCharacterReadings();
        readings.Apply(Performance(), null, true, 10, OrbLatency.Of(850, En));
        Assert.Contains("first-token latency 850 ms", OrbCharacterSpeech.Sentence(readings, CharacterReadouts.Default, 10, En, "")!, StringComparison.Ordinal);
        readings.Apply(Performance(), null, true, 11);
        Assert.DoesNotContain("latency", OrbCharacterSpeech.Sentence(readings, CharacterReadouts.Default, 11, En, "")!, StringComparison.Ordinal);
    }
}
