using System.ComponentModel;
using System.Windows;
using System.Windows.Media.Animation;

namespace AgentSpeed.Settings.Pages;

/// <summary>
/// Settings → API (DESIGN 11, 14): a segmented control above the pane switches between live reporting and the local
/// network feed, crossfading the groups in 0.2 s (at once with Reduce Motion). The choice lives in the settings model
/// while the app runs.
/// </summary>
internal sealed partial class ApiPage
{
    private static readonly Duration Fade = new(TimeSpan.FromSeconds(0.2));
    private SettingsViewModel? _model;

    public ApiPage()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Attach(DataContext as SettingsViewModel);
    }

    // Group-driven unchecking and binding initialization must never write back to the model.
    // Click also covers keyboard activation, without reacting to Checked during binding transfer.
    private void OnReportSegmentClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsViewModel model)
        {
            model.ApiShowsLan = false;
        }
    }

    private void OnLanSegmentClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsViewModel model)
        {
            model.ApiShowsLan = true;
        }
    }

    private void Attach(SettingsViewModel? model)
    {
        if (_model is not null)
        {
            _model.PropertyChanged -= OnModelChanged;
        }

        _model = model;
        if (model is not null)
        {
            model.PropertyChanged += OnModelChanged;
        }

        Show(animate: false);
    }

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SettingsViewModel.ApiShowsLan))
        {
            Show(animate: IsVisible && _model?.ReduceMotion != true);
        }
    }

    private void Show(bool animate)
    {
        var lan = _model?.ApiShowsLan == true;
        FrameworkElement shown = lan ? LanSegment : ReportSegment;
        FrameworkElement hidden = lan ? ReportSegment : LanSegment;
        if (shown.Visibility == Visibility.Visible && shown.IsHitTestVisible && hidden.Visibility == Visibility.Collapsed)
        {
            return;
        }

        shown.Visibility = Visibility.Visible;
        shown.IsHitTestVisible = true;
        if (!animate || hidden.Visibility != Visibility.Visible)
        {
            foreach (var element in new[] { shown, hidden })
            {
                element.BeginAnimation(OpacityProperty, null);
                element.Opacity = 1;
            }

            hidden.Visibility = Visibility.Collapsed;
            return;
        }

        var ease = new CubicEase { EasingMode = EasingMode.EaseInOut };
        hidden.IsHitTestVisible = false;
        var fadeOut = new DoubleAnimation(0, Fade) { EasingFunction = ease };
        fadeOut.Completed += (_, _) =>
        {
            if (!hidden.IsHitTestVisible)
            {
                hidden.Visibility = Visibility.Collapsed;
                hidden.BeginAnimation(OpacityProperty, null);
                hidden.Opacity = 1;
            }
        };
        hidden.BeginAnimation(OpacityProperty, fadeOut);
        shown.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, Fade) { EasingFunction = ease });
    }
}
