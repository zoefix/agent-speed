using System.Globalization;
using System.Text.Json;
using AgentSpeed.Presentation.Settings;

namespace AgentSpeed.Presentation.Tests;

public class UpdateSettingsTests
{
    private static readonly DateTimeOffset Checked = new DateTimeOffset(2026, 10, 21, 8, 30, 15, TimeSpan.Zero).AddTicks(1_234_567);

    private static AppSettings UpdateSettings() => AppSettings.Default with
    {
        AutoCheckUpdates = false,
        LastUpdateCheck = Checked,
        SkippedVersion = "v1.2.0",
        NotifiedVersion = "1.3.0-rc.1",
    };

    private static AppSettings Deserialize(string updatesJson) => SettingsSerializer.Deserialize("{ \"updates\": " + updatesJson + " }");

    [Fact]
    public void DefaultsMatchSpecification()
    {
        var d = AppSettings.Default;
        Assert.True(d.AutoCheckUpdates);
        Assert.Null(d.LastUpdateCheck);
        Assert.Null(d.SkippedVersion);
        Assert.Null(d.NotifiedVersion);
    }

    [Fact]
    public void UpdateFieldsRoundTrip()
    {
        var settings = UpdateSettings();

        var back = SettingsSerializer.Deserialize(SettingsSerializer.Serialize(settings));

        SettingsSerializerTests.AssertEquivalent(settings, back);
        Assert.False(back.AutoCheckUpdates);
        Assert.Equal(Checked.UtcTicks, back.LastUpdateCheck!.Value.UtcTicks);
        Assert.Equal(TimeSpan.Zero, back.LastUpdateCheck.Value.Offset);
        Assert.Equal("v1.2.0", back.SkippedVersion);
        Assert.Equal("1.3.0-rc.1", back.NotifiedVersion);
    }

    [Fact]
    public void DefaultsRoundTrip()
    {
        var back = SettingsSerializer.Deserialize(SettingsSerializer.Serialize(AppSettings.Default));
        Assert.True(back.AutoCheckUpdates);
        Assert.Null(back.LastUpdateCheck);
        Assert.Null(back.SkippedVersion);
        Assert.Null(back.NotifiedVersion);
    }

    [Fact]
    public void JsonShapeUnderUpdates()
    {
        using var document = JsonDocument.Parse(SettingsSerializer.Serialize(UpdateSettings()));
        var updates = document.RootElement.GetProperty("updates");

        Assert.Equal(JsonValueKind.False, updates.GetProperty("autoCheck").ValueKind);
        Assert.Equal(Checked.ToString("O", CultureInfo.InvariantCulture), updates.GetProperty("lastCheck").GetString());
        Assert.Equal("2026-10-21T08:30:15.1234567+00:00", updates.GetProperty("lastCheck").GetString());
        Assert.Equal("v1.2.0", updates.GetProperty("skippedVersion").GetString());
        Assert.Equal("1.3.0-rc.1", updates.GetProperty("notifiedVersion").GetString());
        Assert.Equal(["autoCheck", "lastCheck", "skippedVersion", "notifiedVersion"], updates.EnumerateObject().Select(p => p.Name));

        using var defaults = JsonDocument.Parse(SettingsSerializer.Serialize(AppSettings.Default));
        var empty = defaults.RootElement.GetProperty("updates");
        Assert.Equal(JsonValueKind.True, empty.GetProperty("autoCheck").ValueKind);
        Assert.Equal(JsonValueKind.Null, empty.GetProperty("lastCheck").ValueKind);
        Assert.Equal(JsonValueKind.Null, empty.GetProperty("skippedVersion").ValueKind);
        Assert.Equal(JsonValueKind.Null, empty.GetProperty("notifiedVersion").ValueKind);
    }

    [Fact]
    public void LastCheckIsStoredAsUtc()
    {
        var local = new DateTimeOffset(2026, 10, 21, 17, 30, 15, TimeSpan.FromHours(9));
        var json = SettingsSerializer.Serialize(AppSettings.Default with { LastUpdateCheck = local });

        using var document = JsonDocument.Parse(json);
        Assert.Equal("2026-10-21T08:30:15.0000000+00:00", document.RootElement.GetProperty("updates").GetProperty("lastCheck").GetString());

        var back = SettingsSerializer.Deserialize(json).LastUpdateCheck!.Value;
        Assert.Equal(local, back);
        Assert.Equal(TimeSpan.Zero, back.Offset);
    }

    [Theory]
    [InlineData("\"2026-10-21T17:30:15+09:00\"")]
    [InlineData("\"2026-10-21T08:30:15Z\"")]
    [InlineData("\"2026-10-21T08:30:15\"")]
    [InlineData("\"2026-10-21T03:30:15-05:00\"")]
    public void LastCheckOffsetsAreNormalizedToUtc(string value)
    {
        var last = Deserialize($"{{ \"lastCheck\": {value} }}").LastUpdateCheck;

        Assert.Equal(new DateTimeOffset(2026, 10, 21, 8, 30, 15, TimeSpan.Zero), last);
        Assert.Equal(TimeSpan.Zero, last!.Value.Offset);
    }

    [Theory]
    [InlineData("\"yes\"")]
    [InlineData("1")]
    [InlineData("null")]
    [InlineData("{}")]
    public void AutoCheckOfWrongTypeFallsBackToOn(string value)
    {
        Assert.True(Deserialize($"{{ \"autoCheck\": {value} }}").AutoCheckUpdates);
        Assert.False(Deserialize("{ \"autoCheck\": false }").AutoCheckUpdates);
    }

    [Theory]
    [InlineData("5")]
    [InlineData("\"garbage\"")]
    [InlineData("\"\"")]
    [InlineData("true")]
    [InlineData("[]")]
    [InlineData("\"2026-13-45T99:00:00Z\"")]
    [InlineData("\"0001-01-01T00:00:00+14:00\"")]
    [InlineData("\"9999-12-31T23:59:59-14:00\"")]
    public void InvalidLastCheckIsDropped(string value)
    {
        Assert.Null(Deserialize($"{{ \"lastCheck\": {value} }}").LastUpdateCheck);
    }

    [Fact]
    public void OverlongLastCheckIsDropped()
    {
        var padded = "2026-10-21T08:30:15" + new string('0', 60) + "Z";
        Assert.Null(Deserialize($"{{ \"lastCheck\": \"{padded}\" }}").LastUpdateCheck);
    }

    [Fact]
    public void WrongTypesFallBackPerField()
    {
        var settings = Deserialize("""
            {
              "autoCheck": "yes",
              "lastCheck": 5,
              "skippedVersion": 12,
              "notifiedVersion": "v1.4.0"
            }
            """);

        Assert.True(settings.AutoCheckUpdates);
        Assert.Null(settings.LastUpdateCheck);
        Assert.Null(settings.SkippedVersion);
        Assert.Equal("v1.4.0", settings.NotifiedVersion);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("\"on\"")]
    [InlineData("null")]
    [InlineData("42")]
    public void NonObjectUpdatesUsesDefaults(string value)
    {
        var settings = Deserialize(value);
        Assert.True(settings.AutoCheckUpdates);
        Assert.Null(settings.LastUpdateCheck);
        Assert.Null(settings.SkippedVersion);
        Assert.Null(settings.NotifiedVersion);
    }

    [Fact]
    public void OlderSettingsFileWithoutUpdatesLoadsDefaults()
    {
        const string json = """
            {
              "version": 1,
              "appearance": "Dark",
              "taskbar": { "show": false },
              "dashboard": { "chartSeconds": 300 }
            }
            """;

        var settings = SettingsSerializer.Deserialize(json);

        Assert.Equal(AppearanceMode.Dark, settings.Appearance);
        Assert.False(settings.ShowOnTaskbar);
        Assert.True(settings.AutoCheckUpdates);
        Assert.Null(settings.LastUpdateCheck);
        Assert.Null(settings.SkippedVersion);
        Assert.Null(settings.NotifiedVersion);
    }

    [Theory]
    [InlineData("v1.2.0", "v1.2.0")]
    [InlineData("1.2.0", "1.2.0")]
    [InlineData("  v1.2.0 ", "v1.2.0")]
    [InlineData("V2.0.0-beta.1+build", "V2.0.0-beta.1+build")]
    [InlineData("banana", null)]
    [InlineData("v1.2", null)]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData(null, null)]
    public void ValidatedKeepsOnlyVersionTags(string? tag, string? expected)
    {
        var validated = (AppSettings.Default with { SkippedVersion = tag, NotifiedVersion = tag }).Validated();
        Assert.Equal(expected, validated.SkippedVersion);
        Assert.Equal(expected, validated.NotifiedVersion);
        Assert.Equal(expected, AppSettings.CleanTag(tag));
    }

    [Fact]
    public void ValidatedDropsOverlongTags()
    {
        var overlong = "v1.2.0-" + new string('a', 100);
        Assert.Null((AppSettings.Default with { SkippedVersion = overlong }).Validated().SkippedVersion);
        Assert.Null(Deserialize($"{{ \"skippedVersion\": \"{overlong}\", \"notifiedVersion\": \"banana\" }}").SkippedVersion);
        Assert.Null(Deserialize("{ \"notifiedVersion\": \"banana\" }").NotifiedVersion);
    }

    [Theory]
    [InlineData(1999, 12, 31, false)]
    [InlineData(2000, 1, 1, true)]
    [InlineData(2026, 10, 21, true)]
    [InlineData(9000, 12, 31, true)]
    [InlineData(9001, 1, 1, false)]
    public void ValidatedKeepsTimestampsBetween2000And9000(int year, int month, int day, bool kept)
    {
        var value = new DateTimeOffset(year, month, day, 12, 0, 0, TimeSpan.Zero);
        var validated = (AppSettings.Default with { LastUpdateCheck = value }).Validated().LastUpdateCheck;
        Assert.Equal(kept ? value : (DateTimeOffset?)null, validated);

        var json = $"{{ \"lastCheck\": \"{value.ToString("O", CultureInfo.InvariantCulture)}\" }}";
        Assert.Equal(kept ? value : (DateTimeOffset?)null, Deserialize(json).LastUpdateCheck);
    }

    [Fact]
    public void ValidatedHandlesExtremeTimestamps()
    {
        Assert.Null(AppSettings.CleanTimestamp(DateTimeOffset.MinValue));
        Assert.Null(AppSettings.CleanTimestamp(DateTimeOffset.MaxValue));
        Assert.Null(AppSettings.CleanTimestamp(null));

        var cleaned = AppSettings.CleanTimestamp(new DateTimeOffset(2026, 10, 21, 17, 30, 0, TimeSpan.FromHours(9)));
        Assert.Equal(new DateTimeOffset(2026, 10, 21, 8, 30, 0, TimeSpan.Zero), cleaned);
        Assert.Equal(TimeSpan.Zero, cleaned!.Value.Offset);
    }

    [Fact]
    public void SerializeWritesValidatedUpdateValues()
    {
        var json = SettingsSerializer.Serialize(AppSettings.Default with
        {
            SkippedVersion = "banana",
            NotifiedVersion = " v1.2.0 ",
            LastUpdateCheck = new DateTimeOffset(1990, 1, 1, 0, 0, 0, TimeSpan.Zero),
        });

        using var document = JsonDocument.Parse(json);
        var updates = document.RootElement.GetProperty("updates");
        Assert.Equal(JsonValueKind.Null, updates.GetProperty("skippedVersion").ValueKind);
        Assert.Equal("v1.2.0", updates.GetProperty("notifiedVersion").GetString());
        Assert.Equal(JsonValueKind.Null, updates.GetProperty("lastCheck").ValueKind);
    }
}
