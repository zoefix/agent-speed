using AgentSpeed.Presentation.Layout;
using AgentSpeed.Presentation.Orb.Names;

namespace AgentSpeed.Presentation.Tests;

public class NamePlateMetricsTests
{
    private static readonly RectD Screen = new(0, 0, 1920, 1080);

    [Theory]
    [InlineData(112, 9, 16, 4, 5.4, 168, 118)]
    [InlineData(148, 10, 18, 5, 6, 222, 154)]
    [InlineData(196, 12, 21, 7, 7.2, 294, 202)]
    public void TheGeometryTableHolds(double size, double f, double h, double g, double d, double alone, double tightRow)
    {
        var m = new NamePlateMetrics(size);
        Assert.Equal(f, m.FontSize);
        Assert.Equal(h, m.Height);
        Assert.Equal(h / 2, m.CornerRadius);
        Assert.Equal(g, m.Gap);
        Assert.Equal(d, m.Dot, 9);
        Assert.Equal(alone, m.AloneBudget);

        // Two equal orbs at the 12 pt snap gap: S + 6 each, so their plates stay 6 pt apart.
        var row = new[]
        {
            new NamePlateSubject(new PointD(400, 300), size, Screen, true),
            new NamePlateSubject(new PointD(400 + size + 12, 300), size, Screen, true),
        };
        Assert.Equal(tightRow, NamePlatePlacement.RowBudget(row, 0), 9);
        Assert.Equal(tightRow, NamePlatePlacement.RowBudget(row, 1), 9);
        Assert.Equal(alone, NamePlatePlacement.RowBudget([row[0]], 0), 9);
    }

    [Theory]
    [InlineData(64, 9, 16, 4)]
    [InlineData(80, 9, 16, 4)]
    [InlineData(240, 12, 21, 7)]
    public void TextSizeAndGapAreClamped(double size, double f, double h, double g)
    {
        var m = new NamePlateMetrics(size);
        Assert.Equal(f, m.FontSize);
        Assert.Equal(h, m.Height);
        Assert.Equal(g, m.Gap);
    }

    [Fact]
    public void FontSizeRoundsToHalfPoints()
    {
        Assert.Equal(10.5, new NamePlateMetrics(160).FontSize);
        Assert.Equal(11, new NamePlateMetrics(167).FontSize);
        Assert.Equal(11.5, new NamePlateMetrics(174).FontSize);
    }

    [Fact]
    public void DotsAndPaddingFollowTheLayoutRules()
    {
        var m = new NamePlateMetrics(148);
        Assert.Equal(m.Height / 2, m.Leading + m.Dot / 2, 9);
        Assert.Equal(m.Dot * 2 / 3, m.DotPitch, 9);
        Assert.Equal(0, m.DotsWidth(0));
        Assert.Equal(6, m.DotsWidth(1), 9);
        Assert.Equal(6 + 4 + 4, m.DotsWidth(3), 9);
        Assert.Equal(4.2, m.TextGap, 9);
        Assert.Equal(9.36, m.Trailing, 9);
        Assert.Equal(9, m.ItemGap, 9);
        Assert.Equal(0.12, m.Tracking(false), 9);
        Assert.Equal(0.4, m.Tracking(true), 9);
        Assert.Equal(1.08, m.HollowStroke, 9);
        Assert.Equal(1, new NamePlateMetrics(112).HollowStroke);
        Assert.Equal(m.Leading + m.DotsWidth(2) + m.TextGap + 50 + m.Trailing, m.PlateWidth(2, 50), 9);
        Assert.Equal(50, m.TextBudget(2, m.PlateWidth(2, 50)), 9);
    }

    [Fact]
    public void UnusableSizesFallBackToTheDefault() => Assert.Equal(new NamePlateMetrics(148), new NamePlateMetrics(double.NaN));

    [Fact]
    public void LayoutPlacesDotsTextAndRuns()
    {
        var m = new NamePlateMetrics(148);
        var line = new NamePlateLine(
        [
            new NamePlateRun([new NamePlateDot(NamePlateDotKind.Source, Core.SourceKind.Codex)], "ab", NamePlateInk.Primary, false),
            new NamePlateRun([new NamePlateDot(NamePlateDotKind.Hollow, Core.SourceKind.Gemini)], "cd", NamePlateInk.Secondary, false),
        ]);
        var layout = NamePlateLayout.Of(line, m, (_, f) => f);
        var text = 10 + 0.12;
        Assert.Equal(m.Leading, layout.Runs[0].DotsX, 9);
        Assert.Equal(m.Leading + 6 + 4.2, layout.Runs[0].TextX, 9);
        Assert.Equal(text, layout.Runs[0].TextWidth, 9);
        Assert.Equal(layout.Runs[0].TextX + text + m.ItemGap, layout.Runs[1].DotsX, 9);
        Assert.Equal(layout.Runs[1].TextX + text + m.Trailing, layout.Width, 9);

        var textOnly = NamePlateLayout.Of(new NamePlateLine([new NamePlateRun([], "ab", NamePlateInk.Primary, false)]), m, (_, f) => f);
        Assert.Equal(m.Trailing, textOnly.Runs[0].TextX, 9);
        Assert.Equal(m.Trailing * 2 + text, textOnly.Width, 9);
    }
}
