using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace AgentSpeed.Dashboard.Quota;

/// <summary>
/// Reports its child's height eased over <see cref="Duration"/> (cubic ease-out) instead of jumping, clipping the child
/// meanwhile; the dashboard follows the reported height each frame. At once before it is shown and with Reduce Motion.
/// </summary>
internal sealed class EasedHeight : Decorator
{
    public static readonly DependencyProperty ReduceMotionProperty = DependencyProperty.Register(
        nameof(ReduceMotion), typeof(bool), typeof(EasedHeight), new PropertyMetadata(false));

    private static readonly TimeSpan Duration = TimeSpan.FromMilliseconds(250);
    private static readonly CubicEase Ease = new() { EasingMode = EasingMode.EaseOut };

    private double _from;
    private double _to = double.NaN;
    private double _shown;
    private TimeSpan _started;
    private bool _running;

    public EasedHeight()
    {
        ClipToBounds = true;
        Unloaded += (_, _) => Stop();
    }

    public bool ReduceMotion { get => (bool)GetValue(ReduceMotionProperty); set => SetValue(ReduceMotionProperty, value); }

    protected override Size MeasureOverride(Size constraint)
    {
        var child = Child;
        if (child is null)
        {
            return default;
        }

        child.Measure(new Size(constraint.Width, double.PositiveInfinity));
        var target = child.DesiredSize.Height;
        if (double.IsNaN(_to) || !IsVisible || ReduceMotion)
        {
            Stop();
            _to = _shown = target;
        }
        else if (Math.Abs(target - _to) > 0.25)
        {
            _from = _shown;
            _to = target;
            _started = TimeSpan.MinValue;
            if (!_running)
            {
                _running = true;
                CompositionTarget.Rendering += OnRendering;
            }
        }

        return new Size(child.DesiredSize.Width, _shown);
    }

    protected override Size ArrangeOverride(Size arrangeSize)
    {
        Child?.Arrange(new Rect(0, 0, arrangeSize.Width, Math.Max(double.IsFinite(_to) ? _to : 0, arrangeSize.Height)));
        return new Size(arrangeSize.Width, _shown);
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        var now = ((RenderingEventArgs)e).RenderingTime;
        if (_started == TimeSpan.MinValue)
        {
            _started = now;
        }

        var t = Math.Clamp((now - _started).TotalMilliseconds / Duration.TotalMilliseconds, 0, 1);
        _shown = _from + (_to - _from) * Ease.Ease(t);
        if (t >= 1)
        {
            _shown = _to;
            Stop();
        }

        InvalidateMeasure();
    }

    private void Stop()
    {
        if (_running)
        {
            _running = false;
            CompositionTarget.Rendering -= OnRendering;
        }
    }
}
