using AgentSpeed.Core.Intelligence;
using AgentSpeed.Core.Traffic;
using AgentSpeed.Presentation.Formatting;
using AgentSpeed.Presentation.Localization;
using AgentSpeed.Presentation.Settings;

namespace AgentSpeed.Presentation.Tests;

/// <summary>
/// The taskbar's two readout blocks (DESIGN 10, Menu bar and taskbar): the network choice and the 性能 switch, stored on
/// their own, an earlier build's intelligence choice migrated once, and the words in their order on the bar.
/// </summary>
public class BarIntelligenceSwitchTests
{
    private static readonly Localizer ZhHant = new(Localizer.LoadEmbeddedTable(), LanguageCodes.ZhHant);

    /// <summary>
    /// High contrast follows the orb readout for every part of the block (DESIGN 10, DESIGN 12 <i>Platform details</i>): live
    /// numbers in the text color; labels, the unit, a resting score, <c>–</c> and a zero rate in the gray-text color.
    /// </summary>
    [Fact]
    public void HighContrastColorsEveryPartLikeTheOrbReadout()
    {
        Assert.Equal(BarIntelligenceInk.Text, BarIntelligenceBlock.InkOf(number: true, dim: false, highContrast: true));
        Assert.Equal(BarIntelligenceInk.GrayText, BarIntelligenceBlock.InkOf(number: true, dim: true, highContrast: true));
        Assert.Equal(BarIntelligenceInk.GrayText, BarIntelligenceBlock.InkOf(number: false, dim: false, highContrast: true));
        Assert.Equal(BarIntelligenceInk.GrayText, BarIntelligenceBlock.InkOf(number: false, dim: true, highContrast: true));

        Assert.Equal(BarIntelligenceInk.Number, BarIntelligenceBlock.InkOf(number: true, dim: false, highContrast: false));
        Assert.Equal(BarIntelligenceInk.Secondary, BarIntelligenceBlock.InkOf(number: true, dim: true, highContrast: false));
        Assert.Equal(BarIntelligenceInk.Secondary, BarIntelligenceBlock.InkOf(number: false, dim: false, highContrast: false));
    }

    [Fact]
    public void TheNetworkControlKeepsItsThreeChoicesAndAnUnknownOneReadsAsOff()
    {
        foreach (var (key, bar) in new[] { ("off", BarTraffic.Off), ("machine", BarTraffic.Machine), ("agents", BarTraffic.Agents) })
        {
            Assert.Equal(key, TrafficSettings.Key(bar));
            Assert.Equal(bar, SettingsSerializer.Deserialize("{\"traffic\":{\"bar\":\"" + key + "\"}}").Traffic.Bar);
            var settings = AppSettings.Default with { Traffic = TrafficSettings.Default with { Bar = bar } };
            Assert.Equal(bar, SettingsSerializer.Deserialize(SettingsSerializer.Serialize(settings)).Traffic.Bar);
        }

        Assert.Equal(BarTraffic.Off, SettingsSerializer.Deserialize("""{"traffic":{"bar":"iq"}}""").Traffic.Bar);
        Assert.Equal(BarTraffic.Off, (TrafficSettings.Default with { Bar = (BarTraffic)3 }).Validated().Bar);
        Assert.Equal([BarTraffic.Off, BarTraffic.Machine, BarTraffic.Agents], Enum.GetValues<BarTraffic>());
        Assert.True(TrafficSettings.IsNetwork(BarTraffic.Machine) && TrafficSettings.IsNetwork(BarTraffic.Agents));
        Assert.False(TrafficSettings.IsNetwork(BarTraffic.Off));
    }

    [Fact]
    public void TheIntelligenceSwitchIsOffByDefaultAndStoredWithTheTaskbar()
    {
        Assert.False(AppSettings.Default.BarIntelligence);
        Assert.False(SettingsSerializer.Deserialize("{}").BarIntelligence);

        var both = AppSettings.Default with { BarIntelligence = true, Traffic = TrafficSettings.Default with { Bar = BarTraffic.Machine } };
        var json = SettingsSerializer.Serialize(both);
        Assert.Contains("\"intelligence\": true", json, StringComparison.Ordinal);
        Assert.Contains("\"bar\": \"machine\"", json, StringComparison.Ordinal);
        var read = SettingsSerializer.Deserialize(json);
        Assert.Equal((true, BarTraffic.Machine), (read.BarIntelligence, read.Traffic.Bar));
        Assert.False(SettingsSerializer.Deserialize(SettingsSerializer.Serialize(both with { BarIntelligence = false })).BarIntelligence);
    }

    [Fact]
    public void AnEarlierIntelligenceChoiceMigratesOnceToTheSwitch()
    {
        var migrated = SettingsSerializer.Deserialize("""{"version":2,"taskbar":{"show":true},"traffic":{"enabled":true,"bar":"intelligence"}}""");
        Assert.Equal((BarTraffic.Off, true), (migrated.Traffic.Bar, migrated.BarIntelligence));

        // Written back, the file holds the new keys only, so the next read migrates nothing...
        var json = SettingsSerializer.Serialize(migrated);
        Assert.DoesNotContain("\"bar\": \"intelligence\"", json, StringComparison.Ordinal);
        Assert.Contains("\"bar\": \"off\"", json, StringComparison.Ordinal);
        Assert.Equal(migrated.BarIntelligence, SettingsSerializer.Deserialize(json).BarIntelligence);

        // ...and a switch turned off afterwards stays off.
        var off = SettingsSerializer.Serialize(migrated with { BarIntelligence = false });
        Assert.False(SettingsSerializer.Deserialize(off).BarIntelligence);
        Assert.Equal(BarTraffic.Off, SettingsSerializer.Deserialize(off).Traffic.Bar);
    }

    [Fact]
    public void TheNetworkWordsComeFirstThenTheIntelligenceWords()
    {
        var traffic = new BarTrafficBlock(new TrafficFormat.Parts("1.28", "MB/s"), new TrafficFormat.Parts("64.0", "KB/s"), false,
            "下載每秒 1.28 MB，上傳每秒 64.0 KB");
        var intelligence = BarIntelligenceBlock.Of(TestSums.Of(300, 86, 30, 300), ZhHant);
        Assert.Equal("532 tok/s，下載每秒 1.28 MB，上傳每秒 64.0 KB，性能 86，思考每秒 10 個 token",
            BarReadouts.Append("532 tok/s", "，", traffic, intelligence));

        // The tooltip's first line adds both after ` · `.
        Assert.Equal("532 tok/s · 跑車 · 下載每秒 1.28 MB，上傳每秒 64.0 KB · 性能 86，思考每秒 10 個 token",
            BarReadouts.Append("532 tok/s · 跑車", " · ", traffic, intelligence));

        // Either alone, or neither; a network block without a sample has no words.
        Assert.Equal("v · 性能 86，思考每秒 10 個 token", BarReadouts.Append("v", " · ", null, intelligence));
        Assert.Equal("v · 下載每秒 1.28 MB，上傳每秒 64.0 KB", BarReadouts.Append("v", " · ", traffic, null));
        Assert.Equal("v", BarReadouts.Append("v", " · ", new BarTrafficBlock(null, null, false, null), null));
        Assert.Equal("v · 性能：尚無分數", BarReadouts.Append("v", " · ", null, BarIntelligenceBlock.Of(IntelligenceSums.Zero, ZhHant)));
    }
}
