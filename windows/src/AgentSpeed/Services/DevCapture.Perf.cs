using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AgentSpeed.History.Perf;
using AgentSpeed.Localization;
using AgentSpeed.Perf;
using AgentSpeed.Presentation.History.Perf;
using AgentSpeed.Presentation.Localization;
using AgentSpeed.Presentation.Perf;
using AgentSpeed.Theme;

namespace AgentSpeed.Services;

/// <summary>
/// The performance score's capture sheets (DESIGN 12), rendered in-process from sample data in the app's current appearance
/// and language: <c>perfcard name [#backdrop] [2x]</c> — every score card state side by side — and
/// <c>perfhistory name [#backdrop] [2x]</c> — the Usage History 性能表現 section at its wide, medium and narrow widths, a
/// single-day range, then its empty and analyzing states.
/// </summary>
internal sealed partial class DevCapture
{
    private static readonly double[] HistoryWidths = [900, 680, 420];

    private void CapturePerf(string kind, string name, string options)
    {
        var parts = options.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var backdrop = parts.FirstOrDefault(o => o.StartsWith('#')) ?? "#202020";
        var scale = parts.Contains("2x") ? 2.0 : 1.0;
        var loc = LocalizationSource.Instance.Localizer;
        var dark = Application.Current?.ThemeMode == ThemeMode.Dark;
        var appearance = new PerfCardAppearance(dark, SystemParameters.HighContrast, ReduceMotion: true);
        var models = new List<PerfHistoryViewModel>();
        var sheet = kind == "perfcard" ? CardSheet(loc, appearance) : HistorySheet(loc, appearance, models);
        sheet.SetResourceReference(TextElement.FontFamilyProperty, AppFonts.UiFontKey);
        sheet.SetResourceReference(TextElement.ForegroundProperty, PerfCardAppearance.PrimaryKey);

        // Two layout passes a moment apart: the history section picks its layout from the width it was given.
        Lay(sheet);
        var delay = new DispatcherTimer(TimeSpan.FromMilliseconds(400), DispatcherPriority.Background, (sender, _) =>
        {
            ((DispatcherTimer)sender!).Stop();
            Lay(sheet);
            var width = (int)Math.Ceiling(sheet.ActualWidth * scale);
            var height = (int)Math.Ceiling(sheet.ActualHeight * scale);
            if (width > 0 && height > 0)
            {
                var content = new RenderTargetBitmap(width, height, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
                content.Render(sheet);
                Save(OnBackdrop(content, backdrop), name);
            }

            foreach (var model in models)
            {
                model.Dispose();
            }
        }, Dispatcher.CurrentDispatcher);
        delay.Start();
    }

    private static void Lay(FrameworkElement sheet)
    {
        sheet.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        sheet.Arrange(new Rect(sheet.DesiredSize));
        sheet.UpdateLayout();
    }

    private static FrameworkElement CardSheet(Localizer loc, PerfCardAppearance appearance)
    {
        var sheet = new WrapPanel { Width = 4 * (PerfCardPlacement.Width + 24) + 24, Margin = new Thickness(0) };
        foreach (var (caption, subject) in PerfCardSamples.Cards(loc))
        {
            var view = new PerfCardView();
            view.Apply(PerfCard.Build(subject, loc), appearance, sameRow: false);
            var card = new Border
            {
                Width = PerfCardPlacement.Width,
                CornerRadius = new CornerRadius(8),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(PerfCardView.CardPadding - 1),
                Child = view,
            };
            if (appearance.Solid)
            {
                card.Background = appearance.SolidFill;
            }
            else
            {
                card.SetResourceReference(Border.BackgroundProperty, "FlyoutBackground");
            }

            card.SetResourceReference(Border.BorderBrushProperty, "FlyoutBorderBrush");
            var label = new TextBlock { Text = caption, FontSize = 11, FontWeight = FontWeights.SemiBold, Margin = new Thickness(2, 0, 0, 6) };
            label.SetResourceReference(TextBlock.ForegroundProperty, PerfCardAppearance.SecondaryKey);
            sheet.Children.Add(new StackPanel { Margin = new Thickness(24, 24, 0, 0), VerticalAlignment = VerticalAlignment.Top, Children = { label, card } });
        }

        return sheet;
    }

    private static FrameworkElement HistorySheet(Localizer loc, PerfCardAppearance appearance, List<PerfHistoryViewModel> models)
    {
        var sheet = new StackPanel { Margin = new Thickness(24) };
        var today = DateOnly.FromDateTime(DateTime.Today);
        var records = PerfCardSamples.Records(today);
        void Add(string caption, double width, IReadOnlyList<Core.Perf.PerfRecord> data, DateOnly first, bool analyzing = false)
        {
            var model = new PerfHistoryViewModel(loc);
            models.Add(model);
            model.SetAppearance(appearance.Dark, appearance.HighContrast, reduceMotion: true);
            model.Update(data, first, today, today, null, analyzing);
            model.SetContentWidth(width);
            var section = new PerfPerformanceSection { DataContext = model, Width = width };
            var card = new Border
            {
                Padding = new Thickness(16, 14, 16, 12),
                CornerRadius = new CornerRadius(8),
                BorderThickness = new Thickness(1),
                HorizontalAlignment = HorizontalAlignment.Left,
                Child = section,
            };
            card.SetResourceReference(Border.BackgroundProperty, "CardBackgroundFillColorDefaultBrush");
            card.SetResourceReference(Border.BorderBrushProperty, "CardStrokeColorDefaultBrush");
            var label = new TextBlock { Text = caption, FontSize = 11, FontWeight = FontWeights.SemiBold, Margin = new Thickness(2, 18, 0, 6) };
            label.SetResourceReference(TextBlock.ForegroundProperty, PerfCardAppearance.SecondaryKey);
            sheet.Children.Add(label);
            sheet.Children.Add(card);
        }

        foreach (var width in HistoryWidths)
        {
            Add($"{PerfHistoryLayout.WidthOf(width).ToString().ToUpperInvariant()} · {width}", width, records, today.AddDays(-29));
        }

        Add("ONE DAY", HistoryWidths[1], records, today);
        Add("EMPTY", HistoryWidths[1], [], today.AddDays(-6));
        Add("ANALYZING", HistoryWidths[1], [], today.AddDays(-6), analyzing: true);
        return sheet;
    }
}
