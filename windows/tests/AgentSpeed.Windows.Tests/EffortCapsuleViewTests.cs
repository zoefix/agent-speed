using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using AgentSpeed.Dashboard;
using AgentSpeed.Modes;
using AgentSpeed.Presentation.Localization;
using AgentSpeed.Presentation.Modes;
using AgentSpeed.Theme;
using Xunit.Abstractions;

namespace AgentSpeed.Windows.Tests;

/// <summary>
/// DESIGN 4, Agents, Effort capsule: measured with the Windows fonts, capsules in one column start their meters on one
/// vertical line and end their bolts on another, and the widest capsule leaves a top-level project name its 72 DIP.
/// </summary>
public class EffortCapsuleViewTests(ITestOutputHelper output)
{
    /// <summary>A top-level row's name room in English without a capsule: the intelligence cell and its gap come off too.</summary>
    private static double DashboardNameRoom => NameRoom(LanguageCodes.En);

    [Fact]
    public void TheViewTakesTheModelsWidthAndTheColumnAddsItsGap()
    {
        var capsule = new EffortCapsule("ultracode", "ultrafast");
        var (own, column, empty) = OnStaThread(() =>
        {
            var view = View(capsule, double.NaN, gap: 0);
            var inColumn = View(capsule, 120, gap: 8);
            var none = View(new EffortCapsule(null, "standard"), 0, gap: 8);
            return (view.Width, inColumn.Width, none.Width);
        });
        var expected = OnStaThread(() => capsule.Width(compact: false, CapsuleWordMeasure.For(LanguageCodes.En)));
        Assert.Equal(expected, own, 3);
        Assert.Equal(128, column, 3);
        Assert.Equal(0, empty);
    }

    [Fact]
    public void MetersStartAndBoltsEndOnTheColumnsLines()
    {
        var capsules = new[] { new EffortCapsule("xhigh", "fast"), new EffortCapsule("ultracode", "ultrafast"), new EffortCapsule("low", "ultrafast") };
        var edges = OnStaThread(() =>
        {
            var column = EffortCapsuleColumn.For(capsules, DashboardNameRoom, CapsuleWordMeasure.For(LanguageCodes.En));
            return capsules.Select(c =>
            {
                var view = View(c, column.Width, gap: 0);
                view.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                view.Arrange(new Rect(view.DesiredSize));
                view.UpdateLayout();
                var meter = Find<EffortMeter>(view);
                var bolts = Find<SpeedBolts>(view);
                var meterLeft = meter.TranslatePoint(default, view).X;
                var boltsRight = bolts.TranslatePoint(new Point(bolts.ActualWidth, 0), view).X;
                return (column.Width, meterLeft, boltsRight, bolts.ActualWidth);
            }).ToArray();
        });
        foreach (var (width, meterLeft, boltsRight, boltsWidth) in edges)
        {
            output.WriteLine($"column {width:0.0}: meter at {meterLeft:0.0}, bolts {boltsWidth:0.0} end at {boltsRight:0.0}");
            Assert.Equal(EffortCapsule.Padding, meterLeft, 1);
            Assert.Equal(width - EffortCapsule.Padding, boltsRight, 1);
        }
    }

    [Fact]
    public void TheWidestCapsuleKeepsItsWordBesideATopLevelNameInEveryLanguage()
    {
        var widest = new[] { new EffortCapsule("ultracode", "ultrafast") };
        var columns = OnStaThread(() => LanguageCodes.All
            .Select(language => (language, NameRoom(language), EffortCapsuleColumn.For(widest, NameRoom(language), CapsuleWordMeasure.For(language))))
            .ToArray());
        foreach (var (language, room, column) in columns)
        {
            output.WriteLine($"{language}: name room {room:0.0}, capsule {column.Width:0.0} DIP, compact {column.Compact}");
            Assert.False(column.Compact, language);
            Assert.True(room - column.Footprint >= EffortCapsuleColumn.MinimumName, language);
        }
    }

    [Fact]
    public void TheConfiguredFormKeepsTheColumnsLinesAndItsWidth()
    {
        var capsule = new EffortCapsule("high", "fast", true, true);
        var (measured, width, meterLeft, boltsRight, dashes, markLeft, wordRight, baselines) = OnStaThread(() =>
        {
            var markWidth = CapsuleWordMeasure.Mark("設定值", LanguageCodes.En);
            var own = capsule.Width(compact: false, CapsuleWordMeasure.For(LanguageCodes.En), markWidth);
            var view = View(capsule, double.NaN, gap: 0);
            view.Mark = "設定值";
            Layout(view);
            var meter = Find<EffortMeter>(view);
            var bolts = Find<SpeedBolts>(view);
            var texts = Texts(view);
            var word = texts.Single(t => t.Text == "high");
            var mark = texts.Single(t => t.Text == "設定值");
            return (own, view.Width, meter.TranslatePoint(default, view).X, bolts.TranslatePoint(new Point(bolts.ActualWidth, 0), view).X,
                Dashes(view), mark.TranslatePoint(default, view).X, word.TranslatePoint(new Point(word.ActualWidth, 0), view).X,
                Math.Abs(Baseline(word, view) - Baseline(mark, view)));
        });
        output.WriteLine($"width {width:0.0}, meter {meterLeft:0.0}, bolts end {boltsRight:0.0}, mark at {markLeft:0.0} after word end {wordRight:0.0}, baselines {baselines:0.00} apart");
        Assert.Equal(measured, width, 3);
        Assert.Equal(EffortCapsule.Padding, meterLeft, 1);
        Assert.Equal(width - EffortCapsule.Padding, boltsRight, 1);
        Assert.Equal(EffortCapsule.PartSpacing, markLeft - wordRight, 0);
        Assert.True(baselines < 0.5, $"{baselines}");
        Assert.NotNull(dashes);
    }

    [Fact]
    public void ARealValueBesideAConfiguredOneStaysSolidAndWithoutAMark()
    {
        var (dashes, marks) = OnStaThread(() =>
        {
            var view = View(new EffortCapsule("high", "fast", SpeedConfigured: true), double.NaN, gap: 0);
            view.Mark = "config";
            Layout(view);
            return (Dashes(view).Visibility, Texts(view).Count(t => t.Text == "config" && t.Visibility == Visibility.Visible));
        });
        Assert.Equal(Visibility.Collapsed, dashes);
        Assert.Equal(0, marks);
    }

    [Fact]
    public void HighContrastDrawsTheSameDashesAtOneDip()
    {
        var (thickness, dashes) = OnStaThread(() =>
        {
            var view = View(new EffortCapsule("high", null, EffortConfigured: true), double.NaN, gap: 0);
            view.Appearance = new CapsuleAppearance(Dark: true, HighContrast: true, ReduceMotion: false, Shimmer: false);
            var rectangle = Dashes(view);
            return (rectangle.StrokeThickness, rectangle.StrokeDashArray.ToArray());
        });
        Assert.Equal(1, thickness);
        Assert.Equal([EffortCapsule.DashLength, EffortCapsule.DashGap], dashes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ARealValueArrivingCrossfadesTheFormAndNeverRefillsTheBars(bool reduceMotion)
    {
        var (during, after, scale) = OnStaThread(() =>
        {
            var window = new Window { Width = 200, Height = 60, WindowStyle = WindowStyle.None, ShowInTaskbar = false, ShowActivated = false, Left = -4000, Top = -4000 };
            var view = View(new EffortCapsule("high", null, EffortConfigured: true), double.NaN, gap: 0);
            view.Appearance = new CapsuleAppearance(Dark: false, HighContrast: false, reduceMotion, Shimmer: false);
            window.Content = view;
            window.Show();
            Pump(TimeSpan.FromMilliseconds(100));
            view.Capsule = new EffortCapsule("high", null);
            var image = Find<Image>(view);
            var visible = image.Visibility;
            var bar = FindAll<Rectangle>(view).Select(r => r.RenderTransform).OfType<ScaleTransform>().Min(t => t.ScaleY);
            Pump(TimeSpan.FromMilliseconds(400));
            var gone = image.Visibility;
            window.Close();
            return (visible, gone, bar);
        });
        Assert.Equal(Visibility.Visible, during);
        Assert.Equal(Visibility.Collapsed, after);
        Assert.Equal(1, scale);
    }

    private static void Pump(TimeSpan time)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = time };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            frame.Continue = false;
        };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    private static void Layout(FrameworkElement view)
    {
        view.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        view.Arrange(new Rect(view.DesiredSize));
        view.UpdateLayout();
    }

    private static List<TextBlock> Texts(DependencyObject parent) => FindAll<TextBlock>(parent);

    private static Rectangle Dashes(DependencyObject parent) => FindAll<Rectangle>(parent).Single(r => r.RadiusX == EffortCapsule.Height / 2);

    private static double Baseline(TextBlock text, Visual view) => text.TranslatePoint(new Point(0, text.BaselineOffset), (UIElement)view).Y;

    private static List<T> FindAll<T>(DependencyObject parent)
        where T : DependencyObject
    {
        var found = new List<T>();
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match)
            {
                found.Add(match);
            }

            if (child is Visual)
            {
                found.AddRange(FindAll<T>(child));
            }
        }

        return found;
    }

    private static double NameRoom(string language) => DashboardViewModel.NameRoom(0,
        IntelligenceColumnMeasure.Width(language, new Localizer(Localizer.LoadEmbeddedTable(), language)["intel.glyph"]));

    private static EffortCapsuleView View(EffortCapsule capsule, double columnWidth, double gap)
    {
        var view = new EffortCapsuleView();
        view.Resources[AppFonts.UiFontKey] = AppFonts.Ui(LanguageCodes.En);
        view.Resources[AppFonts.SymbolFontKey] = AppFonts.Symbols;
        view.LanguageCode = LanguageCodes.En;
        view.Gap = gap;
        view.ColumnWidth = columnWidth;
        view.Capsule = capsule;
        return view;
    }

    private static T Find<T>(DependencyObject parent)
        where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match)
            {
                return match;
            }

            if (child is Visual && Find<T>(child) is { } nested)
            {
                return nested;
            }
        }

        return null!;
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
