using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using AgentSpeed.Core.Intelligence;
using AgentSpeed.Core.Perf;
using AgentSpeed.Dashboard;
using AgentSpeed.Presentation.Formatting;
using AgentSpeed.Presentation.Localization;
using AgentSpeed.Theme;
using Xunit.Abstractions;

namespace AgentSpeed.Windows.Tests;

/// <summary>
/// DESIGN 4, Intelligence column, measured with the Windows fonts: the cell is the wider of <c>888</c> and the caption in
/// whole DIPs, and its 12 DIP number sits on the 13 DIP live value's baseline.
/// </summary>
public class IntelligenceColumnTests(ITestOutputHelper output)
{
    [Fact]
    public void TheCellIsTheWiderOf888AndTheCaptionInEveryLanguage()
    {
        var widths = OnStaThread(() => LanguageCodes.All.Select(language =>
        {
            var caption = new Localizer(Localizer.LoadEmbeddedTable(), language)["intel.glyph"];
            var family = AppFonts.Ui(language);
            return (language, IntelligenceColumnMeasure.Width(language, caption),
                Measure(AgentIntelligenceColumn.WidestNumber, family, FontWeights.SemiBold, AgentIntelligenceColumn.NumberSize),
                Measure(caption, family, FontWeights.Medium, AgentIntelligenceColumn.CaptionSize));
        }).ToArray());
        foreach (var (language, width, number, caption) in widths)
        {
            output.WriteLine($"{language}: cell {width} DIP (8,888 {number:0.0}, caption {caption:0.0})");
            Assert.Equal(Math.Ceiling(width), width);
            Assert.True(width >= number && width >= caption, language);
            Assert.True(width < Math.Max(number, caption) + 1, language);
        }
    }

    [Theory]
    [InlineData(PerfForm.Live)]
    [InlineData(PerfForm.Resting)]
    [InlineData(PerfForm.None)]
    public void TheNumberSharesTheLiveValuesBaseline(PerfForm form)
    {
        var (cell, live) = OnStaThread(() =>
        {
            var family = AppFonts.Ui(LanguageCodes.En);
            var row = new Grid { Height = 40, Width = 120 };
            TextElement.SetFontFamily(row, family);
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(56) });
            var intelligence = new IntelligenceCell { Width = 24, Text = form == PerfForm.None ? "–" : "1,286", Form = form };
            var value = new LiveValue { Text = "88.8", Kind = LiveValueKind.Number, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
            TextElement.SetFontSize(value, IntelligenceCell.LiveValueSize);
            TextElement.SetFontWeight(value, FontWeights.SemiBold);
            Grid.SetColumn(value, 1);
            row.Children.Add(intelligence);
            row.Children.Add(value);
            row.Measure(new Size(120, 40));
            row.Arrange(new Rect(0, 0, 120, 40));
            row.UpdateLayout();
            return (Baseline(intelligence, row, family, AgentIntelligenceColumn.NumberSize), Baseline(value, row, family, IntelligenceCell.LiveValueSize));
        });
        output.WriteLine($"{form}: cell baseline {cell:0.00}, live value baseline {live:0.00}");
        Assert.Equal(live, cell, 1);
    }

    /// <summary>The first visible text's baseline in <paramref name="root"/>: its top plus the face's baseline at that size.</summary>
    private static double Baseline(DependencyObject element, Visual root, FontFamily family, double size)
    {
        var text = Texts(element).First(t => t.Opacity > 0 && !string.IsNullOrEmpty(t.Text));
        var top = text.TranslatePoint(default, (UIElement)root).Y;
        return top + new FormattedText(text.Text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(family, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal), size, Brushes.Black, 1.0).Baseline;
    }

    private static IEnumerable<TextBlock> Texts(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is TextBlock text)
            {
                yield return text;
            }

            foreach (var nested in Texts(child))
            {
                yield return nested;
            }
        }
    }

    private static double Measure(string text, FontFamily family, FontWeight weight, double size) =>
        new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(family, FontStyles.Normal, weight, FontStretches.Normal), size, Brushes.Black, 1.0).WidthIncludingTrailingWhitespace;

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
