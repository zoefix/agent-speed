using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using AgentSpeed.Theme;

namespace AgentSpeed.Settings.Lan;

/// <summary>
/// Add / Edit a target (DESIGN 14) as a Fluent dialog laid out like the host sheet: Return saves while the address is valid,
/// Esc cancels and stops a running test. The address has focus (its text selected while editing).
/// </summary>
internal sealed partial class LanTargetDialog : Window
{
    private readonly LanTargetDialogViewModel _model;

    public LanTargetDialog(LanTargetDialogViewModel model)
    {
        InitializeComponent();
        _model = model;
        DataContext = model;
        if (AppIconSource.Frame(32) is { } icon)
        {
            Icon = icon;
        }

        model.PropertyChanged += OnModelChanged;
        Loaded += (_, _) =>
        {
            ShowTestResult();
            AddressBox.Focus();
            AddressBox.SelectAll();
        };
        Closed += (_, _) =>
        {
            model.PropertyChanged -= OnModelChanged;
            model.CancelTest();
        };
    }

    /// <summary>Shows the dialog modally over <paramref name="owner"/>; true when the target was saved.</summary>
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
        if (e.PropertyName == nameof(LanTargetDialogViewModel.TestState))
        {
            ShowTestResult();
        }
    }

    /// <summary>A check in the success color, or a warning in the color of the dot the target would show.</summary>
    private void ShowTestResult()
    {
        var (glyph, brush) = _model.TestState == LanTestState.Succeeded
            ? ("", "SystemFillColorSuccessBrush")
            : ("", "SystemFillColorCautionBrush");
        TestMark.Text = glyph;
        TestMark.SetResourceReference(TextBlock.ForegroundProperty, brush);
    }
}
