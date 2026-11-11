using System.ComponentModel;
using System.Windows;
using System.Windows.Media.Animation;
using AgentSpeed.Presentation.Remote;
using AgentSpeed.Theme;

namespace AgentSpeed.Settings.Remote;

/// <summary>
/// Add / Edit a remote host (DESIGN 13) as a Fluent dialog: Return saves when every field is valid, Esc cancels and stops a
/// running test. The destination has focus (its text selected while editing).
/// </summary>
internal sealed partial class RemoteHostDialog : Window
{
    private readonly RemoteHostDialogViewModel _model;
    private readonly bool _reduceMotion;

    public RemoteHostDialog(RemoteHostDialogViewModel model, bool reduceMotion)
    {
        InitializeComponent();
        _model = model;
        _reduceMotion = reduceMotion;
        DataContext = model;
        if (AppIconSource.Frame(32) is { } icon)
        {
            Icon = icon;
        }

        model.PropertyChanged += OnModelChanged;
        Loaded += (_, _) =>
        {
            ShowTestResult();
            ShowBridgeMark();
            TurnChevron(animate: false);
            DestinationBox.Focus();
            DestinationBox.SelectAll();
        };
        Closed += (_, _) =>
        {
            model.PropertyChanged -= OnModelChanged;
            model.CancelTest();
            model.Detach();
        };
    }

    /// <summary>Shows the dialog modally over <paramref name="owner"/>; true when the host was saved.</summary>
    public bool ShowOver(Window? owner)
    {
        if (owner is { IsVisible: true })
        {
            Owner = owner;
            Language = owner.Language;
        }
        else
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        return ShowDialog() == true;
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        if (_model.Save())
        {
            DialogResult = true;
        }
    }

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(RemoteHostDialogViewModel.TestState):
                ShowTestResult();
                break;
            case nameof(RemoteHostDialogViewModel.BridgeMark):
                ShowBridgeMark();
                break;
            case nameof(RemoteHostDialogViewModel.AdvancedExpanded):
                TurnChevron(animate: !_reduceMotion);
                if (_model.AdvancedExpanded)
                {
                    Dispatcher.BeginInvoke(() => FolderBox.Focus());
                }

                break;
        }
    }

    /// <summary>A check in the success color, or a warning in the color of the dot the host would show.</summary>
    private void ShowTestResult()
    {
        var (glyph, brush) = _model.TestState switch
        {
            RemoteTestState.Reachable => ("", "SystemFillColorSuccessBrush"),
            RemoteTestState.NeedsUser => ("", "SystemFillColorCriticalBrush"),
            _ => ("", "SystemFillColorCautionBrush"),
        };
        TestMark.Text = glyph;
        TestMark.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, brush);
    }

    /// <summary>The bridge's line leads with the check while connected and the caution triangle on a problem (Settings' marks).</summary>
    private void ShowBridgeMark()
    {
        var mark = _model.BridgeMark;
        BridgeMark.Visibility = mark == RemoteBridgeMark.None ? Visibility.Collapsed : Visibility.Visible;
        BridgeMark.Text = mark == RemoteBridgeMark.Ok ? "\uE73E" : "\uE7BA";
        BridgeMark.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty,
            mark == RemoteBridgeMark.Ok ? "SystemFillColorSuccessBrush" : "SystemFillColorCautionBrush");
    }

    private void TurnChevron(bool animate)
    {
        var angle = _model.AdvancedExpanded ? 90 : 0;
        if (!animate)
        {
            ChevronTurn.BeginAnimation(System.Windows.Media.RotateTransform.AngleProperty, null);
            ChevronTurn.Angle = angle;
            return;
        }

        ChevronTurn.BeginAnimation(System.Windows.Media.RotateTransform.AngleProperty,
            new DoubleAnimation(angle, TimeSpan.FromMilliseconds(160)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
    }
}
