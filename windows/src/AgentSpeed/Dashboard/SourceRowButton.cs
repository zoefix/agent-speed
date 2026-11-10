using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace AgentSpeed.Dashboard;

/// <summary>
/// A dashboard source row that opens the usage history (DESIGN section 4, item 3). Hovering lights a plate behind the
/// row; hovering or keyboard focus slides the trailing values aside and fades in a chevron in the space they leave.
/// Everything moves by opacity and render transforms only, so the row never changes its measured size.
/// </summary>
internal sealed class SourceRowButton : Button
{
    /// <summary>How far the trailing values move to make room for the chevron.</summary>
    public const double RevealShift = 14;

    private const double ChevronLead = 4;

    public static readonly DependencyProperty TrailingProperty = DependencyProperty.Register(
        nameof(Trailing), typeof(object), typeof(SourceRowButton), new PropertyMetadata(null));

    /// <summary>Content under the row's line (the quota lines, DESIGN 4), inside the row's plate.</summary>
    public static readonly DependencyProperty BelowProperty = DependencyProperty.Register(
        nameof(Below), typeof(object), typeof(SourceRowButton), new PropertyMetadata(null));

    public static readonly DependencyProperty ShowChevronProperty = DependencyProperty.Register(
        nameof(ShowChevron), typeof(bool), typeof(SourceRowButton),
        new PropertyMetadata(true, (d, _) => ((SourceRowButton)d).Update(animate: true)));

    public static readonly DependencyProperty ReduceMotionProperty = DependencyProperty.Register(
        nameof(ReduceMotion), typeof(bool), typeof(SourceRowButton), new PropertyMetadata(false));

    private static readonly TimeSpan RevealDuration = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan PlateInDuration = TimeSpan.FromMilliseconds(120);
    private static readonly TimeSpan PlateOutDuration = TimeSpan.FromMilliseconds(200);

    private readonly TranslateTransform _trailingShift = new();
    private readonly TranslateTransform _chevronShift = new() { X = -ChevronLead };
    private UIElement? _plate;
    private UIElement? _chevron;
    private bool _revealed;
    private bool _lit;

    public SourceRowButton()
    {
        IsKeyboardFocusedChanged += (_, _) => Update(animate: true);
        IsVisibleChanged += (_, _) =>
        {
            if (!IsVisible)
            {
                Update(animate: false);
            }
        };
    }

    /// <summary>Content laid out at the trailing edge (the values that make room for the chevron).</summary>
    public object? Trailing
    {
        get => GetValue(TrailingProperty);
        set => SetValue(TrailingProperty, value);
    }

    public object? Below
    {
        get => GetValue(BelowProperty);
        set => SetValue(BelowProperty, value);
    }

    /// <summary>False when something else (a settings link) occupies the trailing edge.</summary>
    public bool ShowChevron
    {
        get => (bool)GetValue(ShowChevronProperty);
        set => SetValue(ShowChevronProperty, value);
    }

    public bool ReduceMotion
    {
        get => (bool)GetValue(ReduceMotionProperty);
        set => SetValue(ReduceMotionProperty, value);
    }

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        _plate = GetTemplateChild("PART_Plate") as UIElement;
        _chevron = GetTemplateChild("PART_Chevron") as UIElement;
        if (GetTemplateChild("PART_Trailing") is UIElement trailing)
        {
            trailing.RenderTransform = _trailingShift;
        }

        if (_chevron is not null)
        {
            _chevron.RenderTransform = _chevronShift;
        }

        _revealed = false;
        _lit = false;
        Snap(_plate, OpacityProperty, 0);
        Snap(_chevron, OpacityProperty, 0);
        Snap(_trailingShift, TranslateTransform.XProperty, 0);
        Snap(_chevronShift, TranslateTransform.XProperty, -ChevronLead);
        Update(animate: false);
    }

    protected override void OnMouseEnter(MouseEventArgs e)
    {
        base.OnMouseEnter(e);
        Update(animate: true);
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        Update(animate: true);
    }

    private void Update(bool animate)
    {
        var visible = IsVisible;
        var lit = visible && IsMouseOver && IsEnabled;
        var revealed = visible && ShowChevron && (IsMouseOver || IsKeyboardFocused) && IsEnabled;
        animate &= !ReduceMotion && visible;

        if (lit != _lit)
        {
            _lit = lit;
            Fade(_plate, OpacityProperty, lit ? 1 : 0, lit ? PlateInDuration : PlateOutDuration, animate);
        }

        if (revealed != _revealed)
        {
            _revealed = revealed;
            Fade(_chevron, OpacityProperty, revealed ? 1 : 0, RevealDuration, animate || (visible && ReduceMotion));
            Fade(_trailingShift, TranslateTransform.XProperty, revealed ? -RevealShift : 0, RevealDuration, animate);
            Fade(_chevronShift, TranslateTransform.XProperty, revealed ? 0 : -ChevronLead, RevealDuration, animate);
        }
    }

    private static void Fade(IAnimatable? target, DependencyProperty property, double to, TimeSpan duration, bool animate)
    {
        if (target is null)
        {
            return;
        }

        if (!animate)
        {
            Snap(target, property, to);
            return;
        }

        target.BeginAnimation(property, new DoubleAnimation(to, duration)
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        });
    }

    private static void Snap(IAnimatable? target, DependencyProperty property, double value)
    {
        if (target is null)
        {
            return;
        }

        target.BeginAnimation(property, null);
        ((DependencyObject)target).SetValue(property, value);
    }
}
