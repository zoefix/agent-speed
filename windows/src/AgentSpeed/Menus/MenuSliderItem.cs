using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using AgentSpeed.Presentation.Menus;

namespace AgentSpeed.Menus;

/// <summary>
/// A slider row of an app menu (<see cref="MenuSlider"/>, DESIGN 2b, Context menu): the slider starts in the label column
/// and the value ends the row in tabular secondary text. The row keeps the keyboard focus (the slider never takes it), so
/// ↑ / ↓ move through the menu as usual and ← / → step the value; the menu stays open while the value moves.
/// </summary>
internal sealed class MenuSliderItem : MenuItem
{
    private readonly MenuSlider _model;
    private readonly Action<double> _changed;
    private readonly Slider _slider;
    private readonly TextBlock _value;
    private double _current;
    private bool _syncing;

    public MenuSliderItem(MenuSlider model, Action<double> changed)
    {
        _model = model;
        _changed = changed;
        _current = model.Snap(model.Value);
        StaysOpenOnClick = true;

        _slider = new Slider
        {
            Minimum = model.Minimum,
            Maximum = model.Maximum,
            SmallChange = model.Step,
            LargeChange = model.Step * 6,
            TickFrequency = model.Step,
            IsSnapToTickEnabled = true,
            IsMoveToPointEnabled = true,
            Focusable = false,
            MinWidth = 150,
            VerticalAlignment = VerticalAlignment.Center,
            Value = _current,
        };
        AutomationProperties.SetName(_slider, model.Label);
        _slider.ValueChanged += (_, e) =>
        {
            if (!_syncing)
            {
                Apply(e.NewValue);
            }
        };

        _value = new TextBlock
        {
            MinWidth = 44,
            Margin = new Thickness(12, 0, 0, 0),
            TextAlignment = TextAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Typography.SetNumeralAlignment(_value, FontNumeralAlignment.Tabular);
        _value.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");

        var row = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(_value, Dock.Right);
        row.Children.Add(_value);
        row.Children.Add(_slider);
        Header = row;
        AutomationProperties.SetName(this, model.Label);
        ShowValue();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        var direction = e.Key switch
        {
            Key.Left => -1,
            Key.Right => 1,
            _ => 0,
        };
        if (direction == 0)
        {
            base.OnKeyDown(e);
            return;
        }

        Apply(_current + (FlowDirection == FlowDirection.RightToLeft ? -direction : direction) * _model.Step);
        e.Handled = true;
    }

    /// <summary>A click on the row outside the slider changes nothing and leaves the menu open.</summary>
    protected override void OnClick()
    {
    }

    private void Apply(double requested)
    {
        var next = _model.Snap(requested);
        if (_slider.Value != next)
        {
            _syncing = true;
            _slider.Value = next;
            _syncing = false;
        }

        if (next == _current)
        {
            return;
        }

        _current = next;
        ShowValue();
        _changed(next);
    }

    private void ShowValue()
    {
        var text = _model.ValueText(_current);
        _value.Text = text;
        AutomationProperties.SetItemStatus(this, text);
    }
}
