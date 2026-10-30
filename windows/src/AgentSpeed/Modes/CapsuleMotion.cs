using System.Windows;
using System.Windows.Media.Animation;

namespace AgentSpeed.Modes;

/// <summary>The capsule's short transitions (DESIGN 4, Agents, Changes); each ends on its target value.</summary>
internal static class CapsuleMotion
{
    public static readonly TimeSpan Crossfade = TimeSpan.FromMilliseconds(200);
    public static readonly TimeSpan BarSpring = TimeSpan.FromMilliseconds(220);
    public static readonly TimeSpan BarStagger = TimeSpan.FromMilliseconds(30);
    public static readonly TimeSpan BoltPop = TimeSpan.FromMilliseconds(300);
    public static readonly TimeSpan BoltFade = TimeSpan.FromMilliseconds(150);

    /// <summary>A light spring: a small overshoot that settles within the duration.</summary>
    public static IEasingFunction Spring { get; } = Frozen(new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.35 });

    public static IEasingFunction Smooth { get; } = Frozen(new CubicEase { EasingMode = EasingMode.EaseOut });

    /// <summary>Sets <paramref name="property"/> to <paramref name="to"/>, animating from its current value when asked.</summary>
    /// <param name="completed">Runs when the value has arrived, at once when nothing animates.</param>
    public static void To<T>(T target, DependencyProperty property, double to, TimeSpan duration, IEasingFunction easing, bool animate,
        TimeSpan delay = default, Action? completed = null)
        where T : DependencyObject, IAnimatable
    {
        var from = (double)target.GetValue(property);
        target.BeginAnimation(property, null);
        target.SetValue(property, to);
        if (animate && Math.Abs(from - to) > 0.001)
        {
            var animation = Animation(from, to, duration, easing, delay);
            if (completed is not null)
            {
                animation.Completed += (_, _) => completed();
            }

            target.BeginAnimation(property, animation);
        }
        else
        {
            completed?.Invoke();
        }
    }

    /// <summary>Holds <paramref name="from"/> through the delay, so a staggered bar does not jump to its target first.</summary>
    private static DoubleAnimationUsingKeyFrames Animation(double from, double to, TimeSpan duration, IEasingFunction easing, TimeSpan delay)
    {
        var animation = new DoubleAnimationUsingKeyFrames { FillBehavior = FillBehavior.Stop };
        animation.KeyFrames.Add(new DiscreteDoubleKeyFrame(from, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        if (delay > TimeSpan.Zero)
        {
            animation.KeyFrames.Add(new DiscreteDoubleKeyFrame(from, KeyTime.FromTimeSpan(delay)));
        }

        animation.KeyFrames.Add(new EasingDoubleKeyFrame(to, KeyTime.FromTimeSpan(delay + duration), easing));
        return animation;
    }

    private static IEasingFunction Frozen(EasingFunctionBase easing)
    {
        easing.Freeze();
        return easing;
    }
}
