using System.Collections.Immutable;
using System.Text.Json;
using AgentSpeed.Core;
using AgentSpeed.Presentation.Heat;
using AgentSpeed.Presentation.Localization;
using AgentSpeed.Presentation.Settings;
using AgentSpeed.Presentation.Settings.Orbs;

namespace AgentSpeed.Presentation.Tests;

public class SettingsSerializerTests
{
    internal static AppSettings CustomSettings() =>
        (AppSettings.Default with
        {
            Appearance = AppearanceMode.Dark,
            Language = AppLanguage.Ja,
            Metric = SpeedMetric.Total,
            RedlineOutput = 750,
            ShowOnTaskbar = false,
            BarMode = BarDisplayMode.GlyphOnly,
            BarColoredGlyph = false,
            BarShowUnit = false,
            TaskbarOffset = 180.5,
            ShowOrb = false,
            OrbSize = 196,
            OrbOpacity = 0.65,
            OrbAlwaysOnTop = false,
            OrbClickThrough = true,
            OrbFadeWhenIdle = true,
            Effects = EffectsIntensity.Max,
            CelebratePeaks = false,
            ResponsePulses = false,
            CodexEnabled = false,
            ClaudeCodeEnabled = true,
            ClaudeDesktopEnabled = false,
            DeepSeekEnabled = false,
            OpenCodeEnabled = false,
            GeminiEnabled = true,
            OmpEnabled = false,
            AntigravityEnabled = false,
            WorkBuddyEnabled = false,
            WorkBuddyAiEnabled = true,
            CodexRoot = Path.Combine(Path.GetTempPath(), "codex sessions"),
            ClaudeRoot = Path.Combine(Path.GetTempPath(), "claude", "projects"),
            DshRoot = Path.Combine(Path.GetTempPath(), "dsh", "sessions"),
            OpenCodeRoot = Path.Combine(Path.GetTempPath(), "opencode data"),
            GeminiRoot = Path.Combine(Path.GetTempPath(), ".gemini", "tmp"),
            OmpRoot = Path.Combine(Path.GetTempPath(), ".omp", "profiles", "work", "agent", "sessions"),
            AntigravityRoot = Path.Combine(Path.GetTempPath(), ".gemini", "antigravity-ide"),
            WorkBuddyRoot = Path.Combine(Path.GetTempPath(), ".workbuddy"),
            WorkBuddyAiRoot = Path.Combine(Path.GetTempPath(), ".workbuddy-ai", "projects"),
            ChartSeconds = 300,
            AgentsExpanded = true,
            HistoryWindow = new WindowFrame(-1600.5, 120.25, 1040, 720, Maximized: true),
            OrbNames = OrbNamesMode.Never,
        })
        .WithOrbAnchor(@"\\.\DISPLAY2", new OrbAnchor(0.5, 0.125))
        .WithOrbAnchor("Display 1 \"quoted\" ü", new OrbAnchor(0.25, 0.75));

    internal static void AssertEquivalent(AppSettings expected, AppSettings actual)
    {
        Assert.Equal(expected, actual with { OrbAnchors = expected.OrbAnchors });
        Assert.Equal(
            expected.OrbAnchors.OrderBy(kv => kv.Key, StringComparer.Ordinal).ToArray(),
            actual.OrbAnchors.OrderBy(kv => kv.Key, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void DefaultRoundTrips()
    {
        var json = SettingsSerializer.Serialize(AppSettings.Default);
        var back = SettingsSerializer.Deserialize(json);
        AssertEquivalent(AppSettings.Default, back);
        Assert.Empty(back.OrbAnchors);
    }

    [Fact]
    public void DefaultsMatchDesign()
    {
        var d = AppSettings.Default;
        Assert.Equal(AppearanceMode.Auto, d.Appearance);
        Assert.Equal(AppLanguage.System, d.Language);
        Assert.Equal(SpeedMetric.Output, d.Metric);
        Assert.Equal(2000, d.RedlineOutput);
        Assert.Equal(2_000_000, d.RedlineTotal);
        Assert.Equal(148, d.OrbSize);
        Assert.Equal(1, d.OrbOpacity);
        Assert.Equal(EffectsIntensity.Standard, d.Effects);
        Assert.Equal(BarDisplayMode.GlyphAndValue, d.BarMode);
        Assert.Equal(120, d.ChartSeconds);
        Assert.Equal([112.0, 148.0, 196.0], AppSettings.OrbSizePresets);
        Assert.Equal(d, d.Validated() with { OrbAnchors = d.OrbAnchors });
    }

    [Fact]
    public void CustomValuesRoundTrip()
    {
        var settings = CustomSettings();
        Assert.Equal(settings.Validated() with { OrbAnchors = settings.OrbAnchors }, settings);

        var back = SettingsSerializer.Deserialize(SettingsSerializer.Serialize(settings));
        AssertEquivalent(settings, back);
        Assert.Equal(2, back.OrbAnchors.Count);
        Assert.Equal(new OrbAnchor(0.5, 0.125), back.OrbAnchors[@"\\.\display2"]);
        Assert.Equal("Display 1 \"quoted\" ü", back.OrbMonitor);
    }

    [Fact]
    public void SerializedJsonIsVersionedAndGrouped()
    {
        using var document = JsonDocument.Parse(SettingsSerializer.Serialize(CustomSettings()));
        var root = document.RootElement;
        Assert.Equal(SettingsSerializer.CurrentVersion, root.GetProperty("version").GetInt32());
        Assert.Equal("Dark", root.GetProperty("appearance").GetString());
        Assert.Equal("GlyphOnly", root.GetProperty("taskbar").GetProperty("mode").GetString());
        Assert.Equal(0.25, root.GetProperty("orb").GetProperty("anchors").GetProperty("Display 1 \"quoted\" ü").GetProperty("x").GetDouble());
        Assert.Equal("Max", root.GetProperty("effects").GetProperty("intensity").GetString());
        Assert.Equal(300, root.GetProperty("dashboard").GetProperty("chartSeconds").GetInt32());
        Assert.True(root.GetProperty("dashboard").GetProperty("agentsExpanded").GetBoolean());

        var sources = root.GetProperty("sources");
        Assert.False(sources.GetProperty("deepseek").GetBoolean());
        Assert.Equal(Path.Combine(Path.GetTempPath(), "dsh", "sessions"), sources.GetProperty("dshRoot").GetString());

        using var defaults = JsonDocument.Parse(SettingsSerializer.Serialize(AppSettings.Default));
        var defaultSources = defaults.RootElement.GetProperty("sources");
        Assert.Equal(JsonValueKind.Null, defaultSources.GetProperty("codexRoot").ValueKind);
        Assert.True(defaultSources.GetProperty("deepseek").GetBoolean());
        Assert.Equal(JsonValueKind.Null, defaultSources.GetProperty("dshRoot").ValueKind);
        Assert.False(defaults.RootElement.GetProperty("dashboard").GetProperty("agentsExpanded").GetBoolean());
        Assert.False(defaults.RootElement.GetProperty("orb").TryGetProperty("monitor", out _));
    }

    [Fact]
    public void SerializeWritesValidatedValues()
    {
        var json = SettingsSerializer.Serialize(AppSettings.Default with { RedlineOutput = 9999, OrbSize = 5 });
        var back = SettingsSerializer.Deserialize(json);
        Assert.Equal(2000, back.RedlineOutput);
        Assert.Equal(96, back.OrbSize);
    }

    [Theory]
    [InlineData(37, 50)]
    [InlineData(9999, 2000)]
    [InlineData(433, 450)]
    [InlineData(400, 400)]
    [InlineData(double.NaN, 2000)]
    public void ValidatedClampsRedline(double input, double expected)
    {
        Assert.Equal(expected, (AppSettings.Default with { RedlineOutput = input }).Validated().RedlineOutput);
    }

    [Theory]
    [InlineData(10, 96)]
    [InlineData(1000, 240)]
    [InlineData(150.4, 150)]
    [InlineData(148, 148)]
    [InlineData(double.NaN, 148)]
    [InlineData(double.PositiveInfinity, 148)]
    public void ValidatedClampsOrbSize(double input, double expected)
    {
        Assert.Equal(expected, (AppSettings.Default with { OrbSize = input }).Validated().OrbSize);
    }

    [Theory]
    [InlineData(0, 0.4)]
    [InlineData(-3, 0.4)]
    [InlineData(0.7, 0.7)]
    [InlineData(2, 1)]
    [InlineData(double.NaN, 1)]
    public void ValidatedClampsOpacity(double input, double expected)
    {
        Assert.Equal(expected, (AppSettings.Default with { OrbOpacity = input }).Validated().OrbOpacity);
    }

    [Theory]
    [InlineData(77, 120)]
    [InlineData(0, 120)]
    [InlineData(60, 60)]
    [InlineData(300, 300)]
    public void ValidatedNormalizesChartRange(int input, int expected)
    {
        Assert.Equal(expected, (AppSettings.Default with { ChartSeconds = input }).Validated().ChartSeconds);
    }

    [Fact]
    public void ValidatedReplacesUndefinedEnumsWithDefaults()
    {
        var validated = (AppSettings.Default with
        {
            Appearance = (AppearanceMode)42,
            Language = (AppLanguage)(-1),
            Metric = (SpeedMetric)9,
            BarMode = (BarDisplayMode)7,
            Effects = (EffectsIntensity)99,
        }).Validated();

        Assert.Equal(AppearanceMode.Auto, validated.Appearance);
        Assert.Equal(AppLanguage.System, validated.Language);
        Assert.Equal(SpeedMetric.Output, validated.Metric);
        Assert.Equal(BarDisplayMode.GlyphAndValue, validated.BarMode);
        Assert.Equal(EffectsIntensity.Standard, validated.Effects);
    }

    [Fact]
    public void ValidatedAcceptsOnlyFullyQualifiedPaths()
    {
        var full = Path.Combine(Path.GetTempPath(), "x");
        Assert.Equal(full, AppSettings.CleanPath(full));
        Assert.Equal(full, AppSettings.CleanPath("  " + full + "  "));
        Assert.Null(AppSettings.CleanPath("relative/path"));
        Assert.Null(AppSettings.CleanPath(Path.Combine("..", "x")));
        Assert.Null(AppSettings.CleanPath("   "));
        Assert.Null(AppSettings.CleanPath(null));
        Assert.Null(AppSettings.CleanPath(full + "\0evil"));
        Assert.Null(AppSettings.CleanPath(Path.Combine(Path.GetTempPath(), new string('a', AppSettings.MaxPathLength))));

        var validated = (AppSettings.Default with { CodexRoot = "relative", ClaudeRoot = full, DshRoot = Path.Combine("..", "dsh") }).Validated();
        Assert.Null(validated.CodexRoot);
        Assert.Equal(full, validated.ClaudeRoot);
        Assert.Null(validated.DshRoot);
        Assert.Equal(full, (AppSettings.Default with { DshRoot = "  " + full + " " }).Validated().DshRoot);
        Assert.Null(SettingsSerializer.Deserialize("""{ "sources": { "dshRoot": "relative/sessions" } }""").DshRoot);
    }

    [Fact]
    public void ValidatedCleansMonitorAndAnchors()
    {
        var anchors = ImmutableDictionary<string, OrbAnchor>.Empty
            .Add("ok", new OrbAnchor(2, -1))
            .Add("nan", new OrbAnchor(double.NaN, 0.5))
            .Add(" ", new OrbAnchor(0.5, 0.5))
            .Add(new string('k', 300), new OrbAnchor(0.5, 0.5));
        var validated = (AppSettings.Default with { OrbMonitor = "", OrbAnchors = anchors }).Validated();

        Assert.Null(validated.OrbMonitor);
        Assert.Equal(2, validated.OrbAnchors.Count);
        Assert.Equal(new OrbAnchor(1, 0), validated.OrbAnchors["OK"]);
        Assert.Equal(new OrbAnchor(1, 0.5), validated.OrbAnchors["nan"]);
        Assert.Null((AppSettings.Default with { OrbMonitor = new string('m', 257) }).Validated().OrbMonitor);
    }

    [Fact]
    public void ValidatedToleratesAnchorKeysThatDifferOnlyByCase()
    {
        var anchors = ImmutableDictionary<string, OrbAnchor>.Empty
            .Add("DISPLAY1", new OrbAnchor(0.1, 0.1))
            .Add("display1", new OrbAnchor(0.9, 0.9));
        var validated = (AppSettings.Default with { OrbAnchors = anchors }).Validated();
        Assert.Single(validated.OrbAnchors);
    }

    [Fact]
    public void WithOrbAnchorClampsAndKeepsAtMostSixteenMonitors()
    {
        var settings = AppSettings.Default;
        for (var i = 0; i < 20; i++)
        {
            settings = settings.WithOrbAnchor($"monitor-{i}", new OrbAnchor(i / 20.0, 1.5));
        }

        Assert.Equal(AppSettings.MaxStoredMonitors, settings.OrbAnchors.Count);
        Assert.Equal("monitor-19", settings.OrbMonitor);
        Assert.Equal(new OrbAnchor(19 / 20.0, 1), settings.OrbAnchors["MONITOR-19"]);
        Assert.Throws<ArgumentException>(() => settings.WithOrbAnchor("", new OrbAnchor(0, 0)));
    }

    [Fact]
    public void SourceToggles()
    {
        var settings = AppSettings.Default.WithSourceEnabled(SourceKind.ClaudeCode, false);
        Assert.False(settings.IsSourceEnabled(SourceKind.ClaudeCode));
        Assert.True(settings.IsSourceEnabled(SourceKind.Codex));
        Assert.True(settings.IsSourceEnabled(SourceKind.ClaudeDesktop));

        Assert.True(settings.IsSourceEnabled(SourceKind.DeepSeek));
        Assert.True(settings.IsSourceEnabled(SourceKind.OpenCode));
        Assert.True(settings.IsSourceEnabled(SourceKind.Gemini));
        Assert.True(settings.IsSourceEnabled(SourceKind.Omp));
        Assert.True(settings.IsSourceEnabled(SourceKind.Antigravity));
        Assert.True(settings.IsSourceEnabled(SourceKind.WorkBuddy));
        Assert.True(settings.IsSourceEnabled(SourceKind.WorkBuddyAi));

        var config = (settings with { CodexRoot = Path.Combine(Path.GetTempPath(), "c") }).ToMonitorConfiguration();
        Assert.True(config.EnabledSources.SetEquals(
        [
            SourceKind.Codex, SourceKind.ClaudeDesktop, SourceKind.DeepSeek, SourceKind.OpenCode, SourceKind.Gemini,
            SourceKind.Omp, SourceKind.Antigravity, SourceKind.WorkBuddy, SourceKind.WorkBuddyAi, SourceKind.ZCode, SourceKind.Kimi, SourceKind.GrokBuild,
            SourceKind.Pi, SourceKind.OpenClaw, SourceKind.Hermes, SourceKind.Droid, SourceKind.Trae,
        ]));
        Assert.Equal(Path.Combine(Path.GetTempPath(), "c"), config.CodexRoot);
        Assert.Null(config.ClaudeRoot);
        Assert.Null(config.DshRoot);
    }

    [Fact]
    public void DeepSeekToggleAndFolderReachTheMonitor()
    {
        var dsh = Path.Combine(Path.GetTempPath(), "dsh", "sessions");
        var settings = AppSettings.Default.WithSourceEnabled(SourceKind.DeepSeek, false) with { DshRoot = dsh };
        Assert.False(settings.DeepSeekEnabled);
        Assert.False(settings.IsSourceEnabled(SourceKind.DeepSeek));
        Assert.True(settings.WithSourceEnabled(SourceKind.DeepSeek, true).IsSourceEnabled(SourceKind.DeepSeek));

        var config = settings.ToMonitorConfiguration();
        Assert.True(config.EnabledSources.SetEquals(
        [
            SourceKind.Codex, SourceKind.ClaudeCode, SourceKind.ClaudeDesktop, SourceKind.OpenCode, SourceKind.Gemini,
            SourceKind.Omp, SourceKind.Antigravity, SourceKind.WorkBuddy, SourceKind.WorkBuddyAi, SourceKind.ZCode, SourceKind.Kimi, SourceKind.GrokBuild,
            SourceKind.Pi, SourceKind.OpenClaw, SourceKind.Hermes, SourceKind.Droid, SourceKind.Trae,
        ]));
        Assert.Equal(dsh, config.DshRoot);
        Assert.NotEqual(AppSettings.Default.ToMonitorConfiguration(), (AppSettings.Default with { DshRoot = dsh }).ToMonitorConfiguration());

        var back = SettingsSerializer.Deserialize(SettingsSerializer.Serialize(settings));
        Assert.False(back.DeepSeekEnabled);
        Assert.Equal(dsh, back.DshRoot);
    }

    [Theory]
    [InlineData("""{ "dashboard": { "agentsExpanded": true } }""", true)]
    [InlineData("""{ "dashboard": { "agentsExpanded": false } }""", false)]
    [InlineData("""{ "dashboard": { "chartSeconds": 60 } }""", false)]
    [InlineData("""{ "dashboard": { "agentsExpanded": "true" } }""", false)]
    [InlineData("""{ "dashboard": { "agentsExpanded": 1 } }""", false)]
    [InlineData("""{ "dashboard": { "agentsExpanded": null } }""", false)]
    [InlineData("""{ "dashboard": [] }""", false)]
    [InlineData("""{ "agentsExpanded": true }""", false)]
    [InlineData("{}", false)]
    public void AgentsExpandedIsReadTolerantly(string json, bool expected)
    {
        Assert.Equal(expected, SettingsSerializer.Deserialize(json).AgentsExpanded);
    }

    [Fact]
    public void AgentsExpandedDefaultsToCollapsedAndRoundTrips()
    {
        Assert.False(AppSettings.Default.AgentsExpanded);
        var back = SettingsSerializer.Deserialize(SettingsSerializer.Serialize(AppSettings.Default with { AgentsExpanded = true }));
        Assert.True(back.AgentsExpanded);
        Assert.Equal(120, back.ChartSeconds);
    }

    [Theory]
    [InlineData("{}", ExecutionModeKind.Effort)]
    [InlineData("{\"history\":{\"modeKind\":\"speed\"}}", ExecutionModeKind.Speed)]
    [InlineData("{\"history\":{\"modeKind\":\"workflow\"}}", ExecutionModeKind.Effort)]
    [InlineData("{\"history\":{\"modeKind\":1}}", ExecutionModeKind.Effort)]
    public void HistoryModeKindIsReadTolerantly(string json, ExecutionModeKind expected) =>
        Assert.Equal(expected, SettingsSerializer.Deserialize(json).HistoryModeKind);

    [Fact]
    public void TaskbarOffsetDefaultsToTheDefaultSpotAndRoundTrips()
    {
        Assert.Equal(0, AppSettings.Default.TaskbarOffset);
        using (var defaults = JsonDocument.Parse(SettingsSerializer.Serialize(AppSettings.Default)))
        {
            Assert.Equal(0, defaults.RootElement.GetProperty("taskbar").GetProperty("offset").GetDouble());
        }

        var json = SettingsSerializer.Serialize(AppSettings.Default with { TaskbarOffset = 382.67 });
        using (var document = JsonDocument.Parse(json))
        {
            Assert.Equal(382.67, document.RootElement.GetProperty("taskbar").GetProperty("offset").GetDouble());
        }

        Assert.Equal(382.67, SettingsSerializer.Deserialize(json).TaskbarOffset);
        Assert.Equal(0, SettingsSerializer.Deserialize(SettingsSerializer.Serialize(CustomSettings() with { TaskbarOffset = 0 })).TaskbarOffset);
    }

    [Theory]
    [InlineData("{}", 0)]
    [InlineData("""{ "taskbar": { "offset": 240 } }""", 240)]
    [InlineData("""{ "taskbar": { "offset": 12.3456 } }""", 12.35)]
    [InlineData("""{ "taskbar": { "offset": -12 } }""", 0)]
    [InlineData("""{ "taskbar": { "offset": 1e9 } }""", 10_000)]
    [InlineData("""{ "taskbar": { "offset": 1e400 } }""", 0)]
    [InlineData("""{ "taskbar": { "offset": "120" } }""", 0)]
    [InlineData("""{ "taskbar": { "offset": null } }""", 0)]
    [InlineData("""{ "taskbar": { "offset": true } }""", 0)]
    [InlineData("""{ "taskbar": [] }""", 0)]
    [InlineData("""{ "offset": 120 }""", 0)]
    public void TaskbarOffsetIsReadTolerantly(string json, double expected)
    {
        var settings = SettingsSerializer.Deserialize(json);
        Assert.Equal(expected, settings.TaskbarOffset);
        Assert.True(settings.ShowOnTaskbar);
    }

    [Theory]
    [InlineData(double.NaN, 0)]
    [InlineData(double.PositiveInfinity, 0)]
    [InlineData(-0.5, 0)]
    [InlineData(99.999, 100)]
    [InlineData(20_000, 10_000)]
    public void ValidatedNormalizesTheTaskbarOffset(double input, double expected)
    {
        Assert.Equal(expected, (AppSettings.Default with { TaskbarOffset = input }).Validated().TaskbarOffset);
    }

    [Fact]
    public void HistoryModeKindRoundTrips()
    {
        var back = SettingsSerializer.Deserialize(SettingsSerializer.Serialize(AppSettings.Default with { HistoryModeKind = ExecutionModeKind.Speed }));
        Assert.Equal(ExecutionModeKind.Speed, back.HistoryModeKind);
        Assert.Equal(ExecutionModeKind.Effort, (AppSettings.Default with { HistoryModeKind = ExecutionModeKind.Workflow }).Validated().HistoryModeKind);
    }

    [Fact]
    public void FilesWithoutDeepSeekKeysKeepItOnWithTheDefaultFolder()
    {
        const string json = """
            {
              "version": 1,
              "sources": { "codex": true, "claudeCode": false, "claudeDesktop": true, "codexRoot": null, "claudeRoot": null }
            }
            """;
        var s = SettingsSerializer.Deserialize(json);
        Assert.True(s.DeepSeekEnabled);
        Assert.Null(s.DshRoot);
        Assert.False(s.ClaudeCodeEnabled);
        Assert.True(SettingsSerializer.Deserialize("{}").DeepSeekEnabled);
        Assert.True(s.ToMonitorConfiguration().EnabledSources.Contains(SourceKind.DeepSeek));
    }

    [Fact]
    public void WrongTypesFallBackPerField()
    {
        const string json = """
            {
              "appearance": 5,
              "language": "ZhHant",
              "redline": "fast",
              "metric": "total",
              "taskbar": { "show": "yes", "mode": "ValueOnly", "showUnit": false },
              "orb": { "size": 200, "opacity": "x", "monitor": 12, "anchors": [] },
              "effects": [],
              "sources": { "codex": false, "codexRoot": 3, "deepseek": "no", "dshRoot": false },
              "dashboard": { "chartSeconds": "60" }
            }
            """;
        var s = SettingsSerializer.Deserialize(json);

        Assert.Equal(AppearanceMode.Auto, s.Appearance);
        Assert.Equal(AppLanguage.ZhHant, s.Language);
        Assert.Equal(2000, s.RedlineOutput);
        Assert.Equal(SpeedMetric.Total, s.Metric);
        Assert.True(s.ShowOnTaskbar);
        Assert.Equal(BarDisplayMode.ValueOnly, s.BarMode);
        Assert.False(s.BarShowUnit);
        Assert.Equal(200, s.OrbSize);
        Assert.Equal(1, s.OrbOpacity);
        Assert.Null(s.OrbMonitor);
        Assert.Empty(s.OrbAnchors);
        Assert.Equal(EffectsIntensity.Standard, s.Effects);
        Assert.False(s.CodexEnabled);
        Assert.Null(s.CodexRoot);
        Assert.True(s.DeepSeekEnabled);
        Assert.Null(s.DshRoot);
        Assert.Equal(120, s.ChartSeconds);
    }

    [Fact]
    public void UnknownPropertiesCommentsAndTrailingCommasAreTolerated()
    {
        const string json = """
            {
              // written by a future version
              "version": 99,
              "futureFeature": { "nested": [1, 2, 3] },
              "appearance": "light",
              "orb": { "size": 112, "sparkle": true, },
            }
            """;
        var s = SettingsSerializer.Deserialize(json);
        Assert.Equal(AppearanceMode.Light, s.Appearance);
        Assert.Equal(112, s.OrbSize);
    }

    [Theory]
    [InlineData("""{ "appearance": 2 }""")]
    [InlineData("""{ "appearance": "2" }""")]
    [InlineData("""{ "appearance": "-1" }""")]
    [InlineData("""{ "appearance": "Light, Dark" }""")]
    [InlineData("""{ "appearance": "Sepia" }""")]
    [InlineData("""{ "appearance": "" }""")]
    [InlineData("""{ "appearance": null }""")]
    public void EnumsMustBeDefinedNames(string json)
    {
        Assert.Equal(AppearanceMode.Auto, SettingsSerializer.Deserialize(json).Appearance);
    }

    [Fact]
    public void AnchorsSkipInvalidEntriesAndCapCount()
    {
        var entries = new List<string>
        {
            "\"bad1\": 5",
            "\"bad2\": { \"x\": 0.5 }",
            "\"bad3\": { \"x\": \"0.5\", \"y\": 0.5 }",
            "\"clamped\": { \"x\": -4, \"y\": 9 }",
        };
        entries.AddRange(Enumerable.Range(0, 30).Select(i => $"\"m{i}\": {{ \"x\": 0.5, \"y\": 0.5 }}"));
        var json = "{ \"orb\": { \"anchors\": { " + string.Join(", ", entries) + " } } }";

        var anchors = SettingsSerializer.Deserialize(json).OrbAnchors;
        Assert.Equal(AppSettings.MaxStoredMonitors, anchors.Count);
        Assert.Equal(new OrbAnchor(0, 1), anchors["clamped"]);
        Assert.False(anchors.ContainsKey("bad1"));
        Assert.False(anchors.ContainsKey("bad2"));
        Assert.False(anchors.ContainsKey("bad3"));
    }

    [Theory]
    [InlineData("{not json")]
    [InlineData("[]")]
    [InlineData("\"settings\"")]
    [InlineData("")]
    [InlineData("{} {}")]
    public void InvalidDocumentsThrowJsonException(string json)
    {
        Assert.ThrowsAny<JsonException>(() => SettingsSerializer.Deserialize(json));
    }

    [Fact]
    public void ExcessiveNestingIsRejected()
    {
        var json = "{ \"x\": " + string.Concat(Enumerable.Repeat("{ \"a\": ", 40)) + "1" + new string('}', 40) + " }";
        Assert.ThrowsAny<JsonException>(() => SettingsSerializer.Deserialize(json));
    }

    [Fact]
    public void NullInputThrows()
    {
        Assert.Throws<ArgumentNullException>(() => SettingsSerializer.Deserialize(null!));
        Assert.Throws<ArgumentNullException>(() => SettingsSerializer.Serialize(null!));
    }

    [Fact]
    public void EachMetricKeepsItsOwnRedline()
    {
        var settings = AppSettings.Default
            .WithRedline(SpeedMetric.Output, 433)
            .WithRedline(SpeedMetric.Total, 345_678);
        Assert.Equal(450, settings.RedlineOutput);
        Assert.Equal(350_000, settings.RedlineTotal);
        Assert.Equal(450, settings.Redline);
        Assert.Equal(350_000, (settings with { Metric = SpeedMetric.Total }).Redline);

        var back = SettingsSerializer.Deserialize(SettingsSerializer.Serialize(settings));
        Assert.Equal(450, back.RedlineOutput);
        Assert.Equal(350_000, back.RedlineTotal);
        Assert.Equal(2_000_000, AppSettings.Default.RedlineTotal);
    }

    [Fact]
    public void LegacyRedlineKeyMapsToOutputRedline()
    {
        var settings = SettingsSerializer.Deserialize("{ \"redline\": 800 }");
        Assert.Equal(800, settings.RedlineOutput);
        Assert.Equal(HeatMath.DefaultRedlineTotal, settings.RedlineTotal);
    }

    [Theory]
    [InlineData("{ \"redlineOutput\": 400, \"redlineTotal\": 200000 }", 2000, 2_000_000)]
    [InlineData("{ \"version\": 1, \"redlineOutput\": 400, \"redlineTotal\": 200000 }", 2000, 2_000_000)]
    [InlineData("{ \"version\": 1, \"redline\": 400 }", 2000, 2_000_000)]
    [InlineData("{ \"version\": 1, \"redlineOutput\": 450, \"redlineTotal\": 300000 }", 450, 300_000)]
    [InlineData("{ \"version\": 1, \"redlineOutput\": 400, \"redlineTotal\": 250000 }", 2000, 250_000)]
    [InlineData("{ \"version\": 2, \"redlineOutput\": 400, \"redlineTotal\": 200000 }", 400, 200_000)]
    [InlineData("{ \"version\": 1 }", 2000, 2_000_000)]
    public void OldDefaultRedlinesMoveToTheNewDefaultOnce(string json, double output, double total)
    {
        var settings = SettingsSerializer.Deserialize(json);
        Assert.Equal(output, settings.RedlineOutput);
        Assert.Equal(total, settings.RedlineTotal);
    }

    [Fact]
    public void RedlineMigrationNeverRepeats()
    {
        var migrated = SettingsSerializer.Deserialize("{ \"version\": 1, \"redlineOutput\": 400 }");
        Assert.Equal(2000, migrated.RedlineOutput);

        // The user deliberately picks 400 again: version 2 files keep it.
        var json = SettingsSerializer.Serialize(migrated with { RedlineOutput = 400, RedlineTotal = 200_000 });
        Assert.Contains("\"version\": 2", json, StringComparison.Ordinal);
        var back = SettingsSerializer.Deserialize(json);
        Assert.Equal(400, back.RedlineOutput);
        Assert.Equal(200_000, back.RedlineTotal);
    }
}
