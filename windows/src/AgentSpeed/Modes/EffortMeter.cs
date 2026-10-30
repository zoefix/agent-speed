using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using Path = System.Windows.Shapes.Path;
using AgentSpeed.Presentation.Modes;
using Capsule = AgentSpeed.Presentation.Modes.EffortCapsule;

namespace AgentSpeed.Modes;

/// <summary>
/// The effort meter (DESIGN 4, Agents): five bottom-aligned bars, the level lit from the left. Bars fill or empty one
/// after another; the top rung glows and a light band crosses it every 2.6 s while effects and motion allow.
/// </summary>
internal sealed class EffortMeter : Grid
{
    private static readonly TimeSpan ShimmerSweep = TimeSpan.FromMilliseconds(900);
    private static readonly TimeSpan ShimmerPeriod = TimeSpan.FromMilliseconds(2600);
    private const double BandWidth = 5;

    private readonly Rectangle[] _unlit = new Rectangle[5];
    private readonly Rectangle[] _lit = new Rectangle[5];
    private readonly ScaleTransform[] _scales = new ScaleTransform[5];
    private readonly Canvas _litLayer = new();
    private readonly Path _shimmer = new() { IsHitTestVisible = false };
    private readonly TranslateTransform _band = new();
    private int _level;
    private bool _shimmering;
    private Capsule _capsule = new(null, null);
    private CapsuleBrushes _brushes;
    private bool _reduceMotion;
    private bool _shimmerAllowed;

    public EffortMeter()
    {
        Width = Capsule.MeterWidth;
        Height = Capsule.BarHeights[^1];
        VerticalAlignment = VerticalAlignment.Center;
        IsHitTestVisible = false;
        var unlitLayer = new Canvas();
        for (var i = 0; i < 5; i++)
        {
            _unlit[i] = Bar(i);
            unlitLayer.Children.Add(_unlit[i]);
            _scales[i] = new ScaleTransform(1, 0);
            _lit[i] = Bar(i);
            _lit[i].RenderTransformOrigin = new Point(0.5, 1);
            _lit[i].RenderTransform = _scales[i];
            _lit[i].Opacity = 0;
            _litLayer.Children.Add(_lit[i]);
        }

        var band = new LinearGradientBrush
        {
            MappingMode = BrushMappingMode.Absolute,
            StartPoint = new Point(0, 0),
            // A band tilted 30° from the vertical: its gradient runs across it, at 30° below the horizontal.
            EndPoint = new Point(BandWidth * Math.Cos(Math.PI / 6), BandWidth * Math.Sin(Math.PI / 6)),
            Transform = _band,
        };
        band.GradientStops.Add(new GradientStop(Color.FromArgb(0, 255, 255, 255), 0));
        band.GradientStops.Add(new GradientStop(Color.FromArgb(150, 255, 255, 255), 0.5));
        band.GradientStops.Add(new GradientStop(Color.FromArgb(0, 255, 255, 255), 1));
        _shimmer.Fill = band;
        _shimmer.Opacity = 0;
        Children.Add(unlitLayer);
        Children.Add(_litLayer);
        Children.Add(_shimmer);
        IsVisibleChanged += (_, _) => UpdateShimmer();
    }

    /// <param name="animate">Bars fill or empty in turn (crossfade only with <paramref name="reduceMotion"/>).</param>
    /// <param name="shimmer">Effects are on: the top rung may shimmer.</param>
    public void Show(Capsule capsule, CapsuleBrushes brushes, bool animate, bool reduceMotion, bool shimmer)
    {
        var level = capsule.Level ?? 0;
        var restyle = capsule.Effort != _capsule.Effort || capsule.Tone != _capsule.Tone || capsule.IsConfigured != _capsule.IsConfigured || brushes != _brushes;
        _capsule = capsule;
        _brushes = brushes;
        _reduceMotion = reduceMotion;
        _shimmerAllowed = shimmer;
        if (restyle)
        {
            for (var i = 0; i < 5; i++)
            {
                _unlit[i].Fill = capsule.IsConfigured ? brushes.ConfiguredUnlit : brushes.Unlit;
                if (brushes.LitBar(capsule.Tone, Capsule.BarHeights[i]) is { } fill)
                {
                    _lit[i].Fill = fill;
                }
                else
                {
                    _lit[i].SetResourceReference(Shape.FillProperty, CapsuleBrushes.TextKey(capsule.Tone));
                }
            }

            var top = capsule.Tone == EffortTone.TopRung && !brushes.HighContrast;
            _litLayer.Effect = top
                ? new DropShadowEffect { ShadowDepth = 0, BlurRadius = EffortCapsulePalette.GlowRadius * 2, Color = brushes.Glow, Opacity = EffortCapsulePalette.GlowOpacity }
                : null;
        }

        animate &= IsLoaded && IsVisible;
        var previous = _level;
        _level = level;
        for (var i = 0; i < 5; i++)
        {
            var lit = i < level;
            if (reduceMotion || !animate)
            {
                _scales[i].BeginAnimation(ScaleTransform.ScaleYProperty, null);
                _scales[i].ScaleY = 1;
                CapsuleMotion.To(_lit[i], OpacityProperty, lit ? 1 : 0, CapsuleMotion.Crossfade, CapsuleMotion.Smooth, animate);
                continue;
            }

            var delay = lit ? i - previous : previous - 1 - i;
            var stagger = TimeSpan.FromTicks(CapsuleMotion.BarStagger.Ticks * Math.Max(0, delay));
            CapsuleMotion.To(_scales[i], ScaleTransform.ScaleYProperty, lit ? 1 : 0, CapsuleMotion.BarSpring,
                lit ? CapsuleMotion.Spring : CapsuleMotion.Smooth, animate: true, stagger);
            CapsuleMotion.To(_lit[i], OpacityProperty, lit ? 1 : 0, CapsuleMotion.BarSpring, CapsuleMotion.Smooth, animate: true, stagger);
        }

        _shimmer.Data = Bars(level);
        UpdateShimmer();
    }

    private void UpdateShimmer()
    {
        var run = _capsule.IsTopRung && _shimmerAllowed && !_reduceMotion && !_brushes.HighContrast && IsVisible;
        if (run == _shimmering)
        {
            return;
        }

        _shimmering = run;
        // A repeating animation keeps WPF rendering even in a hidden window, so the band runs only while shown.
        if (!run)
        {
            _band.BeginAnimation(TranslateTransform.XProperty, null);
            _shimmer.Opacity = 0;
            return;
        }

        _shimmer.Opacity = 1;
        var sweep = new DoubleAnimationUsingKeyFrames { Duration = ShimmerPeriod, RepeatBehavior = RepeatBehavior.Forever };
        sweep.KeyFrames.Add(new DiscreteDoubleKeyFrame(-BandWidth * 2, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        sweep.KeyFrames.Add(new EasingDoubleKeyFrame(Capsule.MeterWidth + BandWidth, KeyTime.FromTimeSpan(ShimmerSweep),
            new SineEase { EasingMode = EasingMode.EaseInOut }));
        sweep.KeyFrames.Add(new DiscreteDoubleKeyFrame(Capsule.MeterWidth + BandWidth, KeyTime.FromTimeSpan(ShimmerPeriod)));
        _band.BeginAnimation(TranslateTransform.XProperty, sweep);
    }

    private static Rectangle Bar(int index)
    {
        var height = Capsule.BarHeights[index];
        var bar = new Rectangle
        {
            Width = Capsule.BarWidth,
            Height = height,
            RadiusX = Capsule.BarCornerRadius,
            RadiusY = Capsule.BarCornerRadius,
        };
        Canvas.SetLeft(bar, X(index));
        Canvas.SetTop(bar, Capsule.BarHeights[^1] - height);
        return bar;
    }

    private static double X(int index) => index * (Capsule.BarWidth + Capsule.BarSpacing);

    /// <summary>The lit bars as one geometry, so the band crosses them as one shape.</summary>
    private static Geometry Bars(int level)
    {
        var group = new GeometryGroup();
        for (var i = 0; i < level; i++)
        {
            var height = Capsule.BarHeights[i];
            group.Children.Add(new RectangleGeometry(new Rect(X(i), Capsule.BarHeights[^1] - height, Capsule.BarWidth, height),
                Capsule.BarCornerRadius, Capsule.BarCornerRadius));
        }

        group.Freeze();
        return group;
    }
}
