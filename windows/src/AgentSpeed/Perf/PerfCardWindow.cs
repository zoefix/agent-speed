using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using AgentSpeed.Interop;
using AgentSpeed.Presentation.Layout;
using AgentSpeed.Presentation.Perf;
using AgentSpeed.Theme;

namespace AgentSpeed.Perf;

/// <summary>What a score card is shown for: the card, the panel or board it sits beside and the row's top, in screen pixels.</summary>
/// <param name="CardKey">The agent the card is for: a new agent under the same row crossfades rather than counts.</param>
/// <param name="Anchor">The dashboard flyout's or the board's frame.</param>
/// <param name="RowTop">The row's top (the hero: the dial's), which the card's top is level with.</param>
internal sealed record PerfCardRequest(PerfCard Card, object CardKey, RECT Anchor, int RowTop, PerfCardHost Host);

/// <summary>
/// The score card's window (DESIGN 12, <i>Window</i>): 320 DIP wide and as tall as its content, in the flyout style of
/// the unknown-prices popover — the flyout fill (solid with transparency effects off), radius 8, a 1 px stroke and a soft
/// shadow. A tooltip: it never takes focus and the pointer passes through it. It appears with a 0.15 s fade and a 2 DIP
/// slide away from the row (Reduce Motion: the fade only) and switches rows with a 0.12 s crossfade.
/// </summary>
internal sealed class PerfCardWindow : Window
{
    private const double HideSeconds = 0.1;

    /// <summary>Room for the shadow (blur 24, 6 DIP down) around the card, so it is never cut.</summary>
    private static readonly Thickness ShadowMargin = new(24, 20, 24, 32);

    private readonly Border _card;
    private readonly Grid _stack = new();
    private readonly TranslateTransform _slide = new();
    private PerfCardView _front = new();
    private PerfCardView _back = new() { Opacity = 0, Visibility = Visibility.Collapsed };
    private PerfCardRequest? _request;
    private PerfCardAppearance _appearance;
    private PerfCardSpot? _spot;
    private int _compact;

    public PerfCardWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        Focusable = false;
        IsHitTestVisible = false;
        Style = new Style(typeof(Window));
        Left = -32000;
        Top = -32000;
        Title = "Agent Speed";
        UseLayoutRounding = true;
        SetResourceReference(FontFamilyProperty, AppFonts.UiFontKey);

        _stack.Children.Add(_back);
        _stack.Children.Add(_front);
        _card = new Border
        {
            Width = PerfCardPlacement.Width,
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(PerfCardView.CardPadding - 1),
            Margin = ShadowMargin,
            Child = _stack,
            RenderTransform = _slide,
            Effect = new DropShadowEffect { BlurRadius = 24, ShadowDepth = 6, Direction = 270, Opacity = 0.2, Color = Colors.Black },
        };
        Content = _card;

        SourceInitialized += (_, _) => WindowEffects.AddExtendedStyle(new WindowInteropHelper(this).Handle,
            WS_EX.TOOLWINDOW | WS_EX.NOACTIVATE | WS_EX.TRANSPARENT);
        SizeChanged += (_, _) => Reposition();
    }

    /// <summary>Carries out <paramref name="action"/> for <paramref name="request"/>; <see cref="PerfCardAction.None"/> updates the open card in place.</summary>
    public void Present(PerfCardAction action, PerfCardRequest request, PerfCardAppearance appearance, XmlLanguage language)
    {
        ArgumentNullException.ThrowIfNull(request);
        var restyle = appearance != _appearance || !IsVisible;
        var sameAgent = Equals(_request?.CardKey, request.CardKey);
        _appearance = appearance;
        _request = request;
        if (!Equals(Language, language))
        {
            Language = language;
        }

        if (restyle)
        {
            Paint();
        }

        if (action == PerfCardAction.None && !sameAgent)
        {
            action = PerfCardAction.Switch;
        }

        switch (action)
        {
            case PerfCardAction.Switch when IsVisible:
                (_front, _back) = (_back, _front);
                SetCompact(0);
                _front.Apply(request.Card, appearance, sameRow: false);
                Crossfade();
                break;
            case PerfCardAction.Show or PerfCardAction.Switch:
                _back.Visibility = Visibility.Collapsed;
                SetCompact(0);
                _front.Apply(request.Card, appearance, sameRow: false);
                Appear();
                break;
            default:
                _front.Apply(request.Card, appearance, sameRow: true);
                break;
        }

        Reposition();
    }

    public void Dismiss()
    {
        _request = null;
        if (!IsVisible)
        {
            return;
        }

        if (_appearance.ReduceMotion)
        {
            HideNow();
            return;
        }

        var fade = new DoubleAnimation { To = 0, Duration = Duration(HideSeconds), FillBehavior = FillBehavior.Stop };
        fade.Completed += (_, _) =>
        {
            if (_request is null)
            {
                HideNow();
            }
        };
        _card.BeginAnimation(OpacityProperty, fade);
        _card.Opacity = 0;
    }

    private static Duration Duration(double seconds) => new(TimeSpan.FromSeconds(seconds));

    private void HideNow()
    {
        _card.BeginAnimation(OpacityProperty, null);
        Hide();
    }

    private void Paint()
    {
        if (_appearance.Solid)
        {
            _card.Background = _appearance.SolidFill;
        }
        else
        {
            _card.SetResourceReference(Border.BackgroundProperty, "FlyoutBackground");
        }

        if (_appearance.HighContrast)
        {
            _card.BorderBrush = SystemColors.WindowTextBrush;
            _card.Effect = null;
        }
        else
        {
            _card.SetResourceReference(Border.BorderBrushProperty, "FlyoutBorderBrush");
            _card.Effect ??= new DropShadowEffect { BlurRadius = 24, ShadowDepth = 6, Direction = 270, Opacity = 0.2, Color = Colors.Black };
        }
    }

    private void Appear()
    {
        _front.Opacity = 1;
        _front.Visibility = Visibility.Visible;
        _back.Visibility = Visibility.Collapsed;
        if (!IsVisible)
        {
            _card.Opacity = 0;
            Show();
        }

        Reposition();
        var fade = _appearance.ReduceMotion ? 0.15 : PerfCardPlacement.FadeSeconds;
        _card.BeginAnimation(OpacityProperty, new DoubleAnimation { To = 1, Duration = Duration(fade), EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } });
        _slide.BeginAnimation(TranslateTransform.XProperty, null);
        _slide.X = 0;
        if (!_appearance.ReduceMotion && _spot is { } spot)
        {
            _slide.BeginAnimation(TranslateTransform.XProperty,
                new DoubleAnimation(spot.SlideFrom, 0, Duration(PerfCardPlacement.FadeSeconds)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        }
    }

    /// <summary>The old card fades out over the new one, which fades in, in 0.12 s; the window moves to the new row at once.</summary>
    private void Crossfade()
    {
        _card.BeginAnimation(OpacityProperty, new DoubleAnimation { To = 1, Duration = Duration(PerfCardPlacement.SwitchSeconds) });
        _front.Visibility = Visibility.Visible;
        _back.Visibility = Visibility.Visible;
        var duration = Duration(PerfCardPlacement.SwitchSeconds);
        var outgoing = _back;
        var fadeOut = new DoubleAnimation { To = 0, Duration = duration };
        fadeOut.Completed += (_, _) =>
        {
            if (!ReferenceEquals(outgoing, _front))
            {
                outgoing.Visibility = Visibility.Collapsed;
            }
        };
        outgoing.BeginAnimation(OpacityProperty, fadeOut);
        _front.BeginAnimation(OpacityProperty, new DoubleAnimation { To = 1, Duration = duration });
        Panel.SetZIndex(_front, 1);
        Panel.SetZIndex(_back, 0);
    }

    private void Reposition()
    {
        if (!IsVisible || _request is not { } request)
        {
            return;
        }

        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == 0)
        {
            return;
        }

        var monitor = Monitors.FromRect(request.Anchor);
        var scale = monitor?.Scale ?? 1;
        var work = monitor?.WorkArea ?? new RECT(0, 0, 10000, 10000);
        RectD Dip(RECT r) => new(r.Left / scale, r.Top / scale, r.Width / scale, r.Height / scale);
        var visible = Dip(work);
        var cardHeight = Math.Max(0, ActualHeight - ShadowMargin.Top - ShadowMargin.Bottom);

        // A card taller than the screen drops the components' meanings, then the footnote (both stay in the row's spoken text).
        if (cardHeight > visible.Height - 2 * PerfCardPlacement.Inset && _compact < MaxCompact)
        {
            SetCompact(_compact + 1);
        }

        var spot = PerfCardPlacement.Place(request.Host, Dip(request.Anchor), request.RowTop / scale, cardHeight, visible);
        _spot = spot;
        var x = (int)Math.Round((spot.X - ShadowMargin.Left) * scale);
        var y = (int)Math.Round((spot.Y - ShadowMargin.Top) * scale);
        User32.SetWindowPos(hwnd, HWND.TOPMOST, x, y, 0, 0, SWP.NOSIZE | SWP.NOACTIVATE);
    }

    private const int MaxCompact = 2;

    private void SetCompact(int level)
    {
        _compact = level;
        _front.Compact = level;
        _back.Compact = level;
    }
}
