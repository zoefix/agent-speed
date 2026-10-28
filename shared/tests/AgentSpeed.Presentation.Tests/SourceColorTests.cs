using AgentSpeed.Core;
using AgentSpeed.Presentation.Colors;
using AgentSpeed.Presentation.Orb.Names;

namespace AgentSpeed.Presentation.Tests;

/// <summary>The WorkBuddy source colours against DESIGN 1 "Source colors".</summary>
public class SourceColorTests
{
    private static readonly Rgba DarkSurface = Rgba.FromHex("#1E1E1E");

    private static readonly SourceKind[] WorkBuddyEditions = [SourceKind.WorkBuddy, SourceKind.WorkBuddyAi];

    private static double Distance(Rgba a, Rgba b)
    {
        var (x, y) = (OkLab.FromRgb(a), OkLab.FromRgb(b));
        return Math.Sqrt((x.L - y.L) * (x.L - y.L) + (x.A - y.A) * (x.A - y.A) + (x.B - y.B) * (x.B - y.B));
    }

    [Theory]
    [InlineData(SourceKind.WorkBuddy, "#11A68B", 5.4, 3.1)]
    [InlineData(SourceKind.WorkBuddyAi, "#007AB4", 3.5, 4.7)]
    public void WorkBuddyColorsReadOnBothAppearances(SourceKind source, string hex, double onDark, double onWhite)
    {
        var color = HeatPalette.SourceColor(source);
        Assert.Equal(hex, color.ToHex());
        Assert.Equal(onDark, Rgba.ContrastRatio(color, DarkSurface), 1);
        Assert.Equal(onWhite, Rgba.ContrastRatio(color, Rgba.White), 1);
    }

    [Fact]
    public void WorkBuddyColorsStayApartFromEveryOtherSourceAndEachOther()
    {
        foreach (var source in WorkBuddyEditions)
        {
            var color = HeatPalette.SourceColor(source);
            foreach (var other in Enum.GetValues<SourceKind>().Where(k => k != source))
            {
                var distance = Distance(color, HeatPalette.SourceColor(other));
                Assert.True(distance >= 0.12, $"{source} vs {other}: {distance:F3}");
            }
        }

        Assert.InRange(Distance(HeatPalette.SourceColor(SourceKind.WorkBuddy), HeatPalette.SourceColor(SourceKind.WorkBuddyAi)), 0.12, 0.2);
    }

    [Fact]
    public void WorkBuddyColorsStayApartFromTheNeutralSlate()
    {
        foreach (var source in WorkBuddyEditions)
        {
            var distance = Distance(HeatPalette.SourceColor(source), HeatPalette.Neutral);
            Assert.True(distance >= 0.12, $"{source} vs slate: {distance:F3}");
        }
    }

    [Theory]
    [InlineData(SourceKind.WorkBuddy)]
    [InlineData(SourceKind.WorkBuddyAi)]
    public void LightModeDarkeningRaisesContrastOnWhite(SourceKind source)
    {
        var color = HeatPalette.SourceColor(source);
        var plain = Rgba.ContrastRatio(color, Rgba.White);

        var plate = NamePlateMaterial.SourceColor(source, dark: false);
        Assert.Equal(OkLab.Darken(color, 0.1), plate);
        Assert.Equal(color, NamePlateMaterial.SourceColor(source, dark: true));
        Assert.True(Rgba.ContrastRatio(plate, Rgba.White) > plain);
        Assert.True(Rgba.ContrastRatio(plate, Rgba.White) >= 3, source.ToString());

        var readout = OkLab.Darken(color, 0.12);
        Assert.True(Rgba.ContrastRatio(readout, Rgba.White) > Rgba.ContrastRatio(plate, Rgba.White));
    }
}
