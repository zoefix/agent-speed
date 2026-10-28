using System.Text.Json;
using System.Text.Json.Nodes;
using AgentSpeed.Presentation.Heat;
using AgentSpeed.Presentation.Hero;
using AgentSpeed.Presentation.Settings;

namespace AgentSpeed.Presentation.Tests;

/// <summary><c>heroLastTitle</c> (DESIGN 4c "Idle"): the parked vehicle survives relaunches.</summary>
public class HeroSettingsTests
{
    [Fact]
    public void DefaultHasNoLastTitle()
    {
        Assert.Null(AppSettings.Default.HeroLastTitle);
        using var document = JsonDocument.Parse(SettingsSerializer.Serialize(AppSettings.Default));
        Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("dashboard").GetProperty("heroLastTitle").ValueKind);
    }

    [Fact]
    public void LastTitleIsWrittenUnderDashboard()
    {
        var settings = AppSettings.Default with { HeroLastTitle = "sportsCar" };
        var json = SettingsSerializer.Serialize(settings);

        using var document = JsonDocument.Parse(json);
        Assert.Equal("sportsCar", document.RootElement.GetProperty("dashboard").GetProperty("heroLastTitle").GetString());

        var back = SettingsSerializer.Deserialize(json);
        Assert.Equal("sportsCar", back.HeroLastTitle);
        SettingsSerializerTests.AssertEquivalent(settings, back);
    }

    [Fact]
    public void EveryVehicleKeyRoundTrips()
    {
        foreach (var step in SpeedTitles.Ladder)
        {
            var key = VehicleArt.KeyFor(step.Title);
            var back = SettingsSerializer.Deserialize(SettingsSerializer.Serialize(AppSettings.Default with { HeroLastTitle = key }));
            Assert.Equal(key, back.HeroLastTitle);
        }
    }

    [Fact]
    public void RoundTripsAlongsideOtherDashboardSettings()
    {
        var settings = SettingsSerializerTests.CustomSettings() with { HeroLastTitle = "maglev" };
        var back = SettingsSerializer.Deserialize(SettingsSerializer.Serialize(settings));
        SettingsSerializerTests.AssertEquivalent(settings, back);
        Assert.Equal(300, back.ChartSeconds);
        Assert.True(back.AgentsExpanded);
    }

    [Theory]
    [InlineData("\"plane\"")]
    [InlineData("\"\"")]
    [InlineData("\"   \"")]
    [InlineData("123")]
    [InlineData("null")]
    [InlineData("true")]
    [InlineData("\"SportsCar\"")]
    [InlineData("\"title.sportsCar\"")]
    [InlineData("\"tier.idle\"")]
    [InlineData("[\"walk\"]")]
    [InlineData("{\"key\":\"walk\"}")]
    public void UnknownLastTitleReadsAsNull(string raw)
    {
        var root = JsonNode.Parse(SettingsSerializer.Serialize(AppSettings.Default with { HeroLastTitle = "car", ChartSeconds = 300 }))!;
        root["dashboard"]!["heroLastTitle"] = JsonNode.Parse(raw);

        var back = SettingsSerializer.Deserialize(root.ToJsonString());

        Assert.Null(back.HeroLastTitle);
        Assert.Equal(300, back.ChartSeconds);
    }

    [Fact]
    public void LongLastTitleReadsAsNull()
    {
        var root = JsonNode.Parse(SettingsSerializer.Serialize(AppSettings.Default))!;
        root["dashboard"]!["heroLastTitle"] = new string('w', 10_000);
        Assert.Null(SettingsSerializer.Deserialize(root.ToJsonString()).HeroLastTitle);
    }

    [Fact]
    public void MissingLastTitleReadsAsNull()
    {
        var root = JsonNode.Parse(SettingsSerializer.Serialize(AppSettings.Default with { HeroLastTitle = "car" }))!;
        root["dashboard"]!.AsObject().Remove("heroLastTitle");
        Assert.Null(SettingsSerializer.Deserialize(root.ToJsonString()).HeroLastTitle);

        root.AsObject().Remove("dashboard");
        Assert.Null(SettingsSerializer.Deserialize(root.ToJsonString()).HeroLastTitle);

        Assert.Null(SettingsSerializer.Deserialize("{\"version\": 1}").HeroLastTitle);
    }

    [Fact]
    public void ValidatedDropsUnknownKeys()
    {
        Assert.Null((AppSettings.Default with { HeroLastTitle = "plane" }).Validated().HeroLastTitle);
        Assert.Null((AppSettings.Default with { HeroLastTitle = "Walk" }).Validated().HeroLastTitle);
        Assert.Null((AppSettings.Default with { HeroLastTitle = "" }).Validated().HeroLastTitle);
        Assert.Equal("walk", (AppSettings.Default with { HeroLastTitle = "walk" }).Validated().HeroLastTitle);
        Assert.Equal("rocket", (AppSettings.Default with { HeroLastTitle = "rocket" }).Validated().HeroLastTitle);
    }

    [Fact]
    public void SerializingAnUnknownKeyWritesNull()
    {
        var json = SettingsSerializer.Serialize(AppSettings.Default with { HeroLastTitle = "plane" });
        using var document = JsonDocument.Parse(json);
        Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("dashboard").GetProperty("heroLastTitle").ValueKind);
    }

    [Fact]
    public void PersistedKeyParksThatVehicle()
    {
        var json = SettingsSerializer.Serialize(AppSettings.Default with { HeroLastTitle = "sportsCar" });
        var stage = new HeroStage(VehicleArt.Shared!, SettingsSerializer.Deserialize(json).HeroLastTitle);
        Assert.Equal("sportsCar", stage.CurrentKey);
        Assert.True(stage.Parked);
    }
}
