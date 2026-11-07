using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace AgentSpeed.Settings.TitleThemes;

/// <summary>
/// Shows or hides its child with a height spring (0.35 s), the content laid out at its full height and clipped while
/// it grows or shrinks; with Reduce Motion it fades instead (DESIGN 5, Title themes editor).
/// </summary>
internal sealed class RevealDecorator : Decorator
{
    public static readonly DependencyProperty ProgressProperty = DependencyProperty.Register(nameof(Progress), typeof(double),
        typeof(RevealDecorator), new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    private static readonly Duration FadeDuration = new(TimeSpan.FromSeconds(0.2));

    private bool _shown = true;

    /// <summary>A fixed grow time with an ease-out curve instead of the spring (DESIGN 14: the server rows expand in 0.2 s).</summary>
    public TimeSpan? GrowDuration { get; set; }

    /// <summary>How much of the child's height is revealed, 0–1.</summary>
    public double Progress
    {
        get => (double)GetValue(ProgressProperty);
        set => SetValue(ProgressProperty, value);
    }

    public bool IsShown => _shown;

    /// <summary>Shows or hides the child; <paramref name="animate"/> false jumps to the end state.</summary>
    public void Reveal(bool show, bool animate, bool reduceMotion)
    {
        if (show == _shown && (!show || Visibility == Visibility.Visible))
        {
            return;
        }

        _shown = show;
        var fromProgress = Visibility == Visibility.Visible ? Progress : 0;
        var fromOpacity = Visibility == Visibility.Visible ? Opacity : 0;
        BeginAnimation(ProgressProperty, null);
        BeginAnimation(OpacityProperty, null);
        if (show)
        {
            Visibility = Visibility.Visible;
        }

        if (!animate || !IsLoaded)
        {
            Progress = show ? 1 : 0;
            Opacity = 1;
            Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            return;
        }

        AnimationTimeline animation;
        DependencyProperty property;
        if (reduceMotion)
        {
            Progress = 1;
            property = OpacityProperty;
            animation = new DoubleAnimation(fromOpacity, show ? 1 : 0, FadeDuration)
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            };
        }
        else
        {
            Opacity = 1;
            property = ProgressProperty;
            animation = GrowDuration is { } grow
                ? new DoubleAnimation(fromProgress, show ? 1 : 0, grow) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } }
                : new DoubleAnimation(fromProgress, show ? 1 : 0, SpringEase.Duration) { EasingFunction = new SpringEase() };
        }

        animation.FillBehavior = FillBehavior.Stop;
        animation.Completed += (_, _) =>
        {
            if (_shown != show)
            {
                return;
            }

            Progress = show ? 1 : 0;
            Opacity = 1;
            if (!show)
            {
                Visibility = Visibility.Collapsed;
            }
        };
        if (property == ProgressProperty)
        {
            Progress = show ? 1 : 0;
        }
        else
        {
            Opacity = show ? 1 : 0;
        }

        BeginAnimation(property, animation);
    }

    protected override Size MeasureOverride(Size constraint)
    {
        if (Child is not { } child)
        {
            return default;
        }

        child.Measure(new Size(constraint.Width, double.PositiveInfinity));
        var share = Math.Clamp(Progress, 0, 1);
        return new Size(child.DesiredSize.Width, child.DesiredSize.Height * share);
    }

    protected override Size ArrangeOverride(Size arrangeSize)
    {
        Child?.Arrange(new Rect(0, 0, arrangeSize.Width, Math.Max(arrangeSize.Height, Child.DesiredSize.Height)));
        return arrangeSize;
    }

    protected override Geometry? GetLayoutClip(Size layoutSlotSize) =>
        Progress < 0.999 ? new RectangleGeometry(new Rect(0, -8, RenderSize.Width, RenderSize.Height + 8)) : base.GetLayoutClip(layoutSlotSize);
}
