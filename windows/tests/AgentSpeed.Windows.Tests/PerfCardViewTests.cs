using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AgentSpeed.Presentation.Localization;
using AgentSpeed.Perf;
using AgentSpeed.Presentation.Perf;
using Xunit.Abstractions;

namespace AgentSpeed.Windows.Tests;

/// <summary>
/// The score card on Windows (DESIGN 12, <i>Score card</i>): every state lays out and renders at the card's width in both
/// appearances and in high contrast, in every language; an update of the open card keeps its width; and a card taller than
/// the screen drops the meanings, then the footnote.
/// </summary>
public class PerfCardViewTests(ITestOutputHelper output)
{
    private static readonly StringTable Table = Localizer.LoadEmbeddedTable();

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void EveryStateLaysOutAndRendersAtTheCardsWidth(bool dark, bool highContrast)
    {
        var sizes = OnStaThread(() => LanguageCodes.All.SelectMany(language =>
        {
            var loc = new Localizer(Table, language);
            return PerfCardSamples.Cards(loc).Select(sample =>
            {
                var view = new PerfCardView();
                view.Apply(PerfCard.Build(sample.Subject, loc), new PerfCardAppearance(dark, highContrast, ReduceMotion: true), sameRow: false);
                view.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                view.Arrange(new Rect(view.DesiredSize));
                view.UpdateLayout();
                var bitmap = new RenderTargetBitmap((int)Math.Ceiling(view.ActualWidth), (int)Math.Ceiling(view.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(view);
                return (language, sample.Caption, view.ActualWidth, view.ActualHeight);
            }).ToArray();
        }).ToArray());

        foreach (var (language, caption, width, height) in sizes)
        {
            output.WriteLine($"{language} {caption}: {width:0} × {height:0}");
            Assert.Equal(PerfCardView.CardWidth - 2 * PerfCardView.CardPadding, width, 1);
            Assert.InRange(height, 60, 900);
        }
    }

    [Fact]
    public void ANewTickUpdatesTheOpenCardInPlace()
    {
        var (before, after) = OnStaThread(() =>
        {
            var loc = new Localizer(Table, LanguageCodes.ZhHant);
            var samples = PerfCardSamples.Cards(loc);
            var view = new PerfCardView();
            var appearance = new PerfCardAppearance(Dark: true, HighContrast: false, ReduceMotion: false);
            view.Apply(PerfCard.Build(samples[0].Subject, loc), appearance, sameRow: false);
            view.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var first = view.DesiredSize;
            view.Apply(PerfCard.Build(samples[1].Subject, loc), appearance, sameRow: true);
            view.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            return (first, view.DesiredSize);
        });
        Assert.Equal(before.Width, after.Width, 1);
    }

    [Fact]
    public void ACompactCardDropsTheMeaningsThenTheFootnote()
    {
        var heights = OnStaThread(() =>
        {
            var loc = new Localizer(Table, LanguageCodes.En);
            var view = new PerfCardView();
            view.Apply(PerfCard.Build(PerfCardSamples.Cards(loc)[0].Subject, loc), new PerfCardAppearance(true, false, true), sameRow: false);
            var shown = new List<double>();
            for (var level = 0; level <= 2; level++)
            {
                view.Compact = level;
                view.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                view.Arrange(new Rect(view.DesiredSize));
                view.UpdateLayout();
                shown.Add(view.DesiredSize.Height);
            }

            return shown;
        });
        Assert.True(heights[0] > heights[1] && heights[1] > heights[2], string.Join(", ", heights));
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
