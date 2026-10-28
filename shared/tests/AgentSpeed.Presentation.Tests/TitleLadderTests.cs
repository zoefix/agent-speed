using AgentSpeed.Core;
using AgentSpeed.Presentation.Formatting;
using AgentSpeed.Presentation.Heat;
using AgentSpeed.Presentation.Layout;
using AgentSpeed.Presentation.Localization;

namespace AgentSpeed.Presentation.Tests;

public class TitleLadderTests
{
    private static readonly StringTable Embedded = Localizer.LoadEmbeddedTable();

    private static Localizer For(string language) => new(Embedded, language);

    [Theory]
    [InlineData(SpeedTitle.Bicycle, "Bicycle")]
    [InlineData(SpeedTitle.EBike, "E-Bike")]
    [InlineData(SpeedTitle.SportsCar, "Sports Car")]
    [InlineData(SpeedTitle.BulletTrain, "Bullet Train")]
    [InlineData(SpeedTitle.FighterJet, "Fighter Jet")]
    [InlineData(SpeedTitle.Idle, "Idle")]
    public void LatinNamesAreTitleCased(SpeedTitle title, string expected)
    {
        Assert.Equal(expected, TitleLadderText.Name(title, For(LanguageCodes.En)));
    }

    [Theory]
    [InlineData(LanguageCodes.ZhHant, SpeedTitle.Hypersonic, "極音速飛機")]
    [InlineData(LanguageCodes.ZhHans, SpeedTitle.Maglev, "磁悬浮列车")]
    [InlineData(LanguageCodes.Ja, SpeedTitle.SportsCar, "スポーツカー")]
    public void CjkNamesAreUnchanged(string language, SpeedTitle title, string expected)
    {
        Assert.Equal(expected, TitleLadderText.Name(title, For(language)));
    }

    [Fact]
    public void OutputSliderRedlinesStepBy50()
    {
        var redlines = TitleLadderText.SliderRedlines(SpeedMetric.Output);
        Assert.Equal(40, redlines.Count);
        Assert.Equal(50, redlines[0]);
        Assert.Equal(2000, redlines[^1]);
        Assert.All(redlines.Zip(redlines.Skip(1)), pair => Assert.Equal(50, pair.Second - pair.First, 6));
    }

    [Fact]
    public void TotalSliderRedlinesCoverTheLogRangeWithoutDuplicates()
    {
        var redlines = TitleLadderText.SliderRedlines(SpeedMetric.Total);
        Assert.Equal(HeatMath.MinRedlineTotal, redlines[0]);
        Assert.Equal(HeatMath.MaxRedlineTotal, redlines[^1]);
        Assert.Equal(redlines.Count, redlines.Distinct().Count());
        Assert.True(redlines.SequenceEqual(redlines.Order()));
    }

    [Fact]
    public void ShapesCoverEveryThresholdOverTheSlider()
    {
        foreach (var metric in new[] { SpeedMetric.Output, SpeedMetric.Total })
        {
            foreach (var step in SpeedTitles.Scaled)
            {
                var shapes = TitleLadderText.Shapes(step.Title, metric);
                Assert.NotEmpty(shapes);
                foreach (var redline in TitleLadderText.SliderRedlines(metric))
                {
                    Assert.Contains(TitleLadderText.Pattern(DisplayText.TitleThreshold(step.Title, redline)), shapes);
                }
            }
        }

        Assert.Equal(["0", "00"], TitleLadderText.Shapes(SpeedTitle.Bicycle, SpeedMetric.Output));
        Assert.Contains("0.00k", TitleLadderText.Shapes(SpeedTitle.Rocket, SpeedMetric.Output));
    }

    [Theory]
    [InlineData("1.28k", "0.00k")]
    [InlineData("48", "00")]
    [InlineData("2.5", "0.0")]
    [InlineData("1.20M", "0.00M")]
    public void PatternReplacesDigits(string text, string expected)
    {
        Assert.Equal(expected, TitleLadderText.Pattern(text));
    }

    [Fact]
    public void GridTakesTheWidestColumnCountThatFits()
    {
        double[] widths = [80, 100, 90, 70, 60, 120, 85, 75, 95, 65, 110, 70];
        var four = AlignedGrid.For(widths, 4, 20);
        Assert.Equal([95, 120, 110, 75], four.Widths);
        Assert.Equal(95 + 120 + 110 + 75 + 60, four.Width);

        Assert.Equal(4, AlignedGrid.Choose(widths, 1000, [4, 3, 2], 20).Columns);
        Assert.Equal(4, AlignedGrid.Choose(widths, four.Width, [4, 3, 2], 20).Columns);
        var three = AlignedGrid.For(widths, 3, 20);
        Assert.Equal(3, AlignedGrid.Choose(widths, three.Width, [4, 3, 2], 20).Columns);
        Assert.Equal(2, AlignedGrid.Choose(widths, 10, [4, 3, 2], 20).Columns);
    }

    [Fact]
    public void GridHandlesFewItemsAndBadWidths()
    {
        var layout = AlignedGrid.For([50, double.NaN, -3], 4, 10);
        Assert.Equal(3, layout.Columns);
        Assert.Equal([50, 0, 0], layout.Widths);
        Assert.Throws<ArgumentException>(() => AlignedGrid.Choose([1], 10, [], 0));
    }
}
