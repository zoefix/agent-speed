using System.ComponentModel;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using AgentSpeed.Presentation.History.Perf;

namespace AgentSpeed.History.Perf;

/// <summary>
/// The Usage History window's 性能表現 section (DESIGN 12, <i>Usage History: performance</i>). Its data context is a
/// <see cref="PerfHistoryViewModel"/>; it reports its content width (which picks the wide, medium or narrow layout), draws
/// the selected row's trend with its title and legend, opens the ⓘ popover, and fades a new state in.
/// </summary>
internal sealed partial class PerfPerformanceSection : UserControl
{
    public static readonly DependencyProperty ReferenceInkProperty = DependencyProperty.Register(
        nameof(ReferenceInk), typeof(Brush), typeof(PerfPerformanceSection), new PropertyMetadata(Brushes.Gray));

    public static readonly DependencyProperty UsualInkProperty = DependencyProperty.Register(
        nameof(UsualInk), typeof(Brush), typeof(PerfPerformanceSection), new PropertyMetadata(Brushes.Gray));

    private PerfHistoryViewModel? _model;

    public PerfPerformanceSection()
    {
        InitializeComponent();
        DataContextChanged += (_, e) => Attach(e.NewValue as PerfHistoryViewModel);
        SizeChanged += (_, e) =>
        {
            if (Math.Abs(e.NewSize.Width - e.PreviousSize.Width) > 0.5)
            {
                _model?.SetContentWidth(e.NewSize.Width);
            }
        };
    }

    /// <summary>The legend's reference sample: the 1000 line's color.</summary>
    public Brush ReferenceInk
    {
        get => (Brush)GetValue(ReferenceInkProperty);
        set => SetValue(ReferenceInkProperty, value);
    }

    /// <summary>The legend's dashed sample: the selected row's source color at 60 %.</summary>
    public Brush UsualInk
    {
        get => (Brush)GetValue(UsualInkProperty);
        set => SetValue(UsualInkProperty, value);
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new SectionPeer(this);

    private void Attach(PerfHistoryViewModel? model)
    {
        if (_model is not null)
        {
            _model.PropertyChanged -= OnModelChanged;
        }

        _model = model;
        if (model is null)
        {
            return;
        }

        model.PropertyChanged += OnModelChanged;
        if (ActualWidth > 0)
        {
            model.SetContentWidth(ActualWidth);
        }

        ShowChart();
    }

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(PerfHistoryViewModel.Chart):
            case nameof(PerfHistoryViewModel.Selected):
            case nameof(PerfHistoryViewModel.TrendTitle):
            case nameof(PerfHistoryViewModel.AppearanceGeneration):
                ShowChart();
                break;
            case nameof(PerfHistoryViewModel.State):
                FadeIn();
                if (_model is { ShowsAnalyzing: true })
                {
                    Dispatcher.BeginInvoke(AnnounceAnalyzing, DispatcherPriority.Loaded);
                }

                break;
        }
    }

    /// <summary>The chart in the selected row's source color, and the title line with the model in the label color, medium.</summary>
    private void ShowChart()
    {
        if (_model is not { } model)
        {
            return;
        }

        var source = model.Selected is { } row ? PerfHistoryAppearance.SourceColor(row.Source) : Colors.Gray;
        Chart.Apply(model.Chart, source, model.Loc, model.Today, model.Chart?.IsEmpty == true ? model.ChartEmpty : model.ChartSpoken);
        ReferenceInk = PerfHistoryAppearance.Brush(PerfHistoryAppearance.Label(this, PerfHistoryLayout.ReferenceOpacity));
        UsualInk = PerfHistoryAppearance.Brush(model.HighContrast ? SystemColors.HighlightColor : PerfHistoryAppearance.WithOpacity(source, PerfHistoryLayout.UsualOpacity));

        TrendTitle.Inlines.Clear();
        var title = model.TrendTitle;
        var modelText = model.Selected?.ModelText;
        var at = modelText is { Length: > 0 } ? title.IndexOf(modelText, StringComparison.Ordinal) : -1;
        if (at < 0)
        {
            TrendTitle.Inlines.Add(new Run(title));
            return;
        }

        TrendTitle.Inlines.Add(new Run(title[..at]));
        var name = new Run(modelText) { FontWeight = FontWeights.Medium };
        name.SetResourceReference(TextElement.ForegroundProperty, "TextFillColorPrimaryBrush");
        TrendTitle.Inlines.Add(name);
        TrendTitle.Inlines.Add(new Run(title[(at + modelText!.Length)..]));
    }

    // ---- State ----

    /// <summary>A new state — records after re-analysing, or none any more — fades in (0.2 s), also with Reduce Motion.</summary>
    private void FadeIn()
    {
        if (!IsLoaded || !IsVisible)
        {
            return;
        }

        Body.BeginAnimation(OpacityProperty, PerfHistoryMotion.FadeTo(0, 1));
    }

    /// <summary>WPF raises no live-region event by itself: screen readers hear the re-analysing line once it shows.</summary>
    private void AnnounceAnalyzing()
    {
        if (AnalyzingLine.IsVisible && AutomationPeer.ListenerExists(AutomationEvents.LiveRegionChanged)
            && UIElementAutomationPeer.CreatePeerForElement(AnalyzingLine) is { } peer)
        {
            peer.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        }
    }

    // ---- Info ----

    private void OnInfo(object sender, RoutedEventArgs e)
    {
        if (InfoFlyout.IsOpen)
        {
            InfoFlyout.IsOpen = false;
            return;
        }

        InfoScroller.ScrollToTop();
        InfoFlyout.IsOpen = true;
        Dispatcher.BeginInvoke(() => InfoScroller.Focus(), DispatcherPriority.Input);
    }

    private void OnInfoClosed(object? sender, EventArgs e)
    {
        if (Window.GetWindow(this) is { IsActive: true } window && !window.IsKeyboardFocusWithin && InfoButton.IsVisible)
        {
            InfoButton.Focus();
        }
    }

    private void OnInfoKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            InfoFlyout.IsOpen = false;
            InfoButton.Focus();
            e.Handled = true;
        }
    }

    /// <summary>The section is a group named <c>history.perf.title</c>.</summary>
    private sealed class SectionPeer(PerfPerformanceSection owner) : UserControlAutomationPeer(owner)
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Group;

        protected override string GetClassNameCore() => nameof(PerfPerformanceSection);
    }
}
