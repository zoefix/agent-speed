using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using AgentSpeed.Dashboard.Quota;
using AgentSpeed.Presentation.Localization;
using AgentSpeed.Theme;

namespace AgentSpeed.Windows.Tests;

/// <summary>
/// DESIGN 4, Quota lines, measured with the Windows fonts: lines of one section share the label column, the bar's ends and
/// the percent cell however long a phase 2 label is; the reset keeps its room before the bar's comfort, and an age shows only
/// where it fits beside a comfortable bar (otherwise it goes to the tooltip).
/// </summary>
public class QuotaLinePanelTests
{
    /// <summary>A Sources row's line width: the 368 DIP panel less its padding and the dot column.</summary>
    private const double SourcesWidth = 368 - 2 * 20 - 18;

    /// <summary>A host's source line, 12 DIP deeper (DESIGN 13, Host quotas).</summary>
    private const double HostsWidth = SourcesWidth - 12;

    private sealed record Placed(double LabelRight, double BarLeft, double BarWidth, double PercentRight, double ResetLeft, double ResetWidth,
        double ResetNatural, bool AgeShown, double AgeOpacity, double AgeLeft);

    private static readonly FontFamily Ui = AppFonts.Ui(LanguageCodes.ZhHant);

    private static TextBlock Text(string text, double size, FontWeight weight) =>
        new() { Text = text, FontSize = size, FontWeight = weight, FontFamily = Ui, TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis };

    private static QuotaLinePanel Line(QuotaColumns columns, string label, string reset, string age)
    {
        var panel = new QuotaLinePanel { Columns = columns, Height = 16 };
        panel.Children.Add(Text(label, 11, FontWeights.Medium));
        panel.Children.Add(new QuotaBar { Value = 0.4 });
        var cell = new Grid();
        cell.Children.Add(Text("100%", 11.5, FontWeights.SemiBold));
        panel.Children.Add(cell);
        panel.Children.Add(Text(reset, 11, FontWeights.Normal));
        panel.Children.Add(Text(age, 11, FontWeights.Normal));
        return panel;
    }

    private static Placed[] Lay(double width, params (string Label, string Reset, string Age)[] lines)
    {
        var columns = new QuotaColumns();
        var stack = new StackPanel { Width = width };
        foreach (var (label, reset, age) in lines)
        {
            stack.Children.Add(Line(columns, label, reset, age));
        }

        for (var pass = 0; pass < 3; pass++)
        {
            stack.Measure(new Size(width, double.PositiveInfinity));
            stack.Arrange(new Rect(0, 0, width, stack.DesiredSize.Height));
            stack.UpdateLayout();
        }

        return [.. stack.Children.OfType<QuotaLinePanel>().Select(p =>
        {
            Rect Box(int i) => LayoutInformation.GetLayoutSlot((FrameworkElement)p.Children[i]);
            var reset = (TextBlock)p.Children[3];
            var natural = Text(reset.Text, reset.FontSize, reset.FontWeight);
            natural.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            return new Placed(Box(0).Right, Box(1).Left, Box(1).Width, Box(2).Right, Box(3).Left, Box(3).Width, natural.DesiredSize.Width,
                p.AgeShown, p.Children[4].Opacity, Box(4).Left);
        })];
    }

    [Fact]
    public void ALongGroupLabelKeepsEveryColumnOfTheSectionOnOneLine()
    {
        var placed = OnStaThread(() => Lay(SourcesWidth,
            ("Gemini 5h", "2 小時 12 分後重置", string.Empty),
            ("Claude/GPT 7d", "週六 16:07 重置", string.Empty),
            ("MCP 月", "週一 14:07 重置", string.Empty),
            ("點數", "週一 14:07 重置", string.Empty),
            ("Pro 1d", "9 小時後重置", string.Empty)));
        Assert.All(placed, p => Assert.Equal(placed[0].BarLeft, p.BarLeft, 3));
        Assert.All(placed, p => Assert.Equal(placed[0].BarWidth, p.BarWidth, 3));
        Assert.All(placed, p => Assert.Equal(placed[0].PercentRight, p.PercentRight, 3));
        Assert.All(placed, p => Assert.Equal(placed[0].ResetLeft, p.ResetLeft, 3));
        Assert.All(placed, p => Assert.True(p.LabelRight <= p.BarLeft - QuotaLinePanel.Gap + 0.01));
        Assert.True(placed[0].BarWidth >= QuotaLinePanel.CompactBar);

        // Every reset is whole: the bar gives way first.
        Assert.All(placed, p => Assert.True(p.ResetWidth >= p.ResetNatural - 0.01, $"{p.ResetWidth} for {p.ResetNatural}"));
    }

    [Fact]
    public void TheEnglishResetsStayWholeInTheHostsSectionToo()
    {
        var placed = OnStaThread(() => Lay(HostsWidth,
            ("Claude/GPT 7d", "Resets in 2 hr 12 min", string.Empty),
            ("Flash 1d", "Resets Sat 16:07", string.Empty)));
        Assert.Equal(placed[0].BarLeft, placed[1].BarLeft, 3);
        Assert.True(placed[0].BarWidth >= QuotaLinePanel.CompactBar);
        Assert.All(placed, p => Assert.True(p.ResetWidth >= p.ResetNatural - 0.01, $"{p.ResetWidth} for {p.ResetNatural}"));
    }

    [Fact]
    public void AnAgeShowsOnlyWhereItFitsBesideAComfortableBar()
    {
        var placed = OnStaThread(() => Lay(SourcesWidth,
            ("5h", "3 小時後重置", " · 2 小時前更新"),
            ("Claude/GPT 7d", "週日 14:07 重置", " · 3 小時前更新")));
        var shortLabel = OnStaThread(() => Lay(SourcesWidth, ("月", "3 小時後重置", " · 3 小時前更新")));

        Assert.True(shortLabel[0].AgeShown);
        Assert.Equal(1.0, shortLabel[0].AgeOpacity, 3);
        Assert.Equal(shortLabel[0].ResetLeft + shortLabel[0].ResetNatural, shortLabel[0].AgeLeft, 1);
        Assert.True(shortLabel[0].BarWidth >= QuotaLinePanel.MinBar - 0.01);

        // With the long label the section's bar would fall under its comfort: the age goes, the reset stays whole.
        Assert.All(placed, p => Assert.False(p.AgeShown));
        Assert.All(placed, p => Assert.Equal(0.0, p.AgeOpacity, 3));
        Assert.All(placed, p => Assert.True(p.ResetWidth >= p.ResetNatural - 0.01));
    }

    private static T OnStaThread<T>(Func<T> work)
    {
        T result = default!;
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                result = work();
            }
            catch (Exception ex)
            {
                error = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        return error is null ? result : throw new InvalidOperationException("Layout failed.", error);
    }
}
