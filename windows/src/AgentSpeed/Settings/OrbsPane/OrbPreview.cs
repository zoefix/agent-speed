using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using AgentSpeed.Orb.Render;
using AgentSpeed.Presentation.Colors;
using AgentSpeed.Presentation.Heat;
using AgentSpeed.Presentation.Motion;
using AgentSpeed.Presentation.Orb;
using AgentSpeed.Presentation.Settings;

namespace AgentSpeed.Settings.OrbsPane;

/// <summary>
/// A live orb in Settings (DESIGN 5): the real renderer, effects capped at Soft. Thumbnails (<see cref="Card"/> false)
/// draw the value only at 38 pt and at most 15 fps from the orb's live data; style cards draw a 64 pt preview that
/// loops cruise → redline → cruise every 8 s (each card 1.6 s after the previous one) at most 24 fps, or a still frame
/// at the redline with Reduce Motion. Both render only while visible. To keep the pane under 3 % of a core, only the
/// selected style's card and the card under the pointer loop; the others settle on their redline still and stop drawing
/// (five looping cards cost about 8 % of a core in WPF).
/// </summary>
internal sealed class OrbPreview : FrameworkElement
{
    public static readonly DependencyProperty StyleKindProperty = DependencyProperty.Register(nameof(StyleKind), typeof(OrbStyle),
        typeof(OrbPreview), new PropertyMetadata(OrbStyle.Led, (d, _) => ((OrbPreview)d).Reconfigure()));

    public static readonly DependencyProperty DiameterProperty = DependencyProperty.Register(nameof(Diameter), typeof(double),
        typeof(OrbPreview), new PropertyMetadata(38.0, (d, _) => ((OrbPreview)d).Reconfigure()));

    public static readonly DependencyProperty CardProperty = DependencyProperty.Register(nameof(Card), typeof(bool),
        typeof(OrbPreview), new PropertyMetadata(false, (d, _) => ((OrbPreview)d).Reconfigure()));

    public static readonly DependencyProperty CardIndexProperty = DependencyProperty.Register(nameof(CardIndex), typeof(int),
        typeof(OrbPreview), new PropertyMetadata(0));

    public static readonly DependencyProperty DarkProperty = DependencyProperty.Register(nameof(Dark), typeof(bool),
        typeof(OrbPreview), new PropertyMetadata(true, (d, _) => ((OrbPreview)d).Reconfigure()));

    public static readonly DependencyProperty ReduceMotionProperty = DependencyProperty.Register(nameof(ReduceMotion), typeof(bool),
        typeof(OrbPreview), new PropertyMetadata(false, (d, _) => ((OrbPreview)d).Reconfigure()));

    public static readonly DependencyProperty LabelsProperty = DependencyProperty.Register(nameof(Labels), typeof(IReadOnlyList<string>),
        typeof(OrbPreview), new PropertyMetadata(null, (d, _) => ((OrbPreview)d).Reconfigure()));

    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(nameof(Value), typeof(double),
        typeof(OrbPreview), new PropertyMetadata(0.0, (d, _) => ((OrbPreview)d).PushLive()));

    public static readonly DependencyProperty TierProperty = DependencyProperty.Register(nameof(Tier), typeof(SpeedTier),
        typeof(OrbPreview), new PropertyMetadata(SpeedTier.Idle, (d, _) => ((OrbPreview)d).PushLive()));

    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(nameof(Title), typeof(SpeedTitle),
        typeof(OrbPreview), new PropertyMetadata(SpeedTitle.Idle, (d, _) => ((OrbPreview)d).PushLive()));

    private const double LoopSeconds = 8;
    private const double CardRedline = 400;

    private readonly OrbSurface? _surface;
    private readonly DispatcherTimer _loop;
    private readonly DispatcherTimer _settle;
    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    public static readonly DependencyProperty CharacterIdProperty = DependencyProperty.Register(nameof(CharacterId), typeof(string),
        typeof(OrbPreview), new PropertyMetadata(null, (d, _) => ((OrbPreview)d).InvalidateVisual()));

    /// <summary>
    /// The portrait of a <c>character</c> orb (DESIGN 5: the locked, else the first installed character's
    /// <c>preview.png</c>), or null for a quiet silhouette; set by the app.
    /// </summary>
    public static Func<string?, ImageSource?>? CharacterPortrait { get; set; }

    /// <summary>Installed characters changed: every character preview redraws.</summary>
    public static event EventHandler? CharacterPortraitsChanged;

    public static void NotifyCharacterPortraits() => CharacterPortraitsChanged?.Invoke(null, EventArgs.Empty);

    /// <summary>The orb's locked character (null: Auto) for the <c>character</c> style's portrait.</summary>
    public string? CharacterId { get => (string?)GetValue(CharacterIdProperty); set => SetValue(CharacterIdProperty, value); }

    private OrbStyleCardViewModel? _card;
    private UIElement? _hoverHost;
    private readonly TierTracker _tier = new(holdSeconds: 0.3);
    private readonly TitleTracker _title = new(holdSeconds: 0.3);

    public OrbPreview()
    {
        if (OrbClock.Current is { } clock)
        {
            _surface = new OrbSurface(clock, (uint)Environment.TickCount | 1) { Cap = FrameClass.Fps15, IsHitTestVisible = false };
            AddVisualChild(_surface);
        }

        _loop = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (_, _) => PushLoop(), Dispatcher);
        _loop.Stop();
        _settle = new DispatcherTimer(TimeSpan.FromMilliseconds(900), DispatcherPriority.Background, (sender, _) =>
        {
            ((DispatcherTimer)sender!).Stop();
            if (_surface is not null && Card && !Animating)
            {
                _surface.Paused = true;
            }
        }, Dispatcher);
        _settle.Stop();
        IsVisibleChanged += (_, _) => UpdateLoop();
        DataContextChanged += (_, _) => TrackCard();
        Loaded += (_, _) =>
        {
            CharacterPortraitsChanged += OnPortraitsChanged;
            TrackHover(true);
            TrackCard();
            Reconfigure();
            UpdateLoop();
        };
        Unloaded += (_, _) =>
        {
            CharacterPortraitsChanged -= OnPortraitsChanged;
            _loop.Stop();
            _settle.Stop();
            TrackHover(false);
            Track(null);
        };
        Focusable = false;
        ClipToBounds = false;
    }

    public OrbStyle StyleKind { get => (OrbStyle)GetValue(StyleKindProperty); set => SetValue(StyleKindProperty, value); }

    public double Diameter { get => (double)GetValue(DiameterProperty); set => SetValue(DiameterProperty, value); }

    public bool Card { get => (bool)GetValue(CardProperty); set => SetValue(CardProperty, value); }

    public int CardIndex { get => (int)GetValue(CardIndexProperty); set => SetValue(CardIndexProperty, value); }

    public bool Dark { get => (bool)GetValue(DarkProperty); set => SetValue(DarkProperty, value); }

    public bool ReduceMotion { get => (bool)GetValue(ReduceMotionProperty); set => SetValue(ReduceMotionProperty, value); }

    /// <summary>The titles as the readout draws them (the orb's title theme, idle first); null for Vehicles.</summary>
    public IReadOnlyList<string>? Labels { get => (IReadOnlyList<string>?)GetValue(LabelsProperty); set => SetValue(LabelsProperty, value); }

    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }

    public SpeedTier Tier { get => (SpeedTier)GetValue(TierProperty); set => SetValue(TierProperty, value); }

    public SpeedTitle Title { get => (SpeedTitle)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }

    private void OnPortraitsChanged(object? sender, EventArgs e) => InvalidateVisual();

    /// <summary>The <c>character</c> style shows her portrait, not a rendered orb (DESIGN 2a.6, LOD; DESIGN 5 style cards).</summary>
    protected override void OnRender(DrawingContext drawingContext)
    {
        ArgumentNullException.ThrowIfNull(drawingContext);
        if (StyleKind != OrbStyle.Character)
        {
            return;
        }

        var d = Diameter;
        if (CharacterPortrait?.Invoke(CharacterId) is { } portrait && portrait.Height > 0)
        {
            var h = d * 1.1;
            var w = h * portrait.Width / portrait.Height;
            RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.HighQuality);
            drawingContext.DrawImage(portrait, new Rect((d - w) / 2, d - h + d * 0.05, w, h));
            return;
        }

        var ink = new SolidColorBrush(Dark ? Color.FromArgb(0x60, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x50, 0, 0, 0));
        ink.Freeze();
        var glyph = new FormattedText("\uE77B", System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
            d * 0.6, ink, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        drawingContext.DrawText(glyph, new Point((d - glyph.Width) / 2, (d - glyph.Height) / 2));
    }

    protected override int VisualChildrenCount => _surface is null || StyleKind == OrbStyle.Character ? 0 : 1;

    protected override Visual GetVisualChild(int index) =>
        index == 0 && _surface is not null ? _surface : throw new ArgumentOutOfRangeException(nameof(index));

    protected override Size MeasureOverride(Size availableSize)
    {
        _surface?.Measure(new Size(Diameter * 1.5, Diameter * 1.5));
        return new Size(Diameter, Diameter);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        // The surface's window square is 1.5 S; center the disc in this element.
        _surface?.Arrange(new Rect(-Diameter * 0.25, -Diameter * 0.25, Diameter * 1.5, Diameter * 1.5));
        return finalSize;
    }

    private void Reconfigure()
    {
        InvalidateVisual();
        if (_surface is null || !IsLoaded)
        {
            return;
        }

        if (StyleKind == OrbStyle.Character)
        {
            _surface.Paused = true;
            _loop.Stop();
            return;
        }

        var effects = new EffectsPolicy(EffectsIntensity.Soft, ReduceMotion, SystemParameters.HighContrast);
        var loc = Localization.LocalizationSource.Instance.Localizer;
        var labels = Labels is { Count: > 0 } themed
            ? themed
            : Enum.GetValues<SpeedTitle>().Select(t => Presentation.Formatting.DisplayText.TitleLabel(t, loc)).ToArray();
        _surface.Cap = Card ? FrameClass.Fps24 : FrameClass.Fps15;
        _surface.Configure(new OrbSurfaceConfig(StyleKind, Diameter, Dark ? OrbAppearance.Dark : OrbAppearance.Light, effects,
            SystemParameters.HighContrast, false, 1, false, false, Card ? CardRedline : HeatMath.DefaultRedline,
            new ReadoutTexts(loc["unit.tokS"], string.Empty, labels, loc.UsesLatinScript), null, default));
        InvalidateMeasure();
        if (Card)
        {
            UpdateLoop();
        }
        else
        {
            PushLive();
        }
    }

    /// <summary>The card loops: it is the selected style or under the pointer, visible, and motion is allowed.</summary>
    private bool Animating => Card && IsVisible && !ReduceMotion && (_card?.IsSelected == true || _hoverHost?.IsMouseOver == true);

    private void UpdateLoop()
    {
        if (!Card || _surface is null)
        {
            _loop.Stop();
            return;
        }

        if (Animating)
        {
            _settle.Stop();
            _surface.Paused = false;
            if (!_loop.IsEnabled)
            {
                _loop.Start();
                PushLoop();
            }

            return;
        }

        _loop.Stop();
        _surface.Paused = false;
        PushStill();
        _settle.Stop();
        _settle.Start();
    }

    private void TrackCard() => Track(DataContext as OrbStyleCardViewModel);

    private void Track(OrbStyleCardViewModel? card)
    {
        if (ReferenceEquals(card, _card))
        {
            return;
        }

        if (_card is not null)
        {
            _card.PropertyChanged -= OnCardChanged;
        }

        _card = card;
        if (_card is not null)
        {
            _card.PropertyChanged += OnCardChanged;
        }

        UpdateLoop();
    }

    private void OnCardChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(OrbStyleCardViewModel.IsSelected))
        {
            UpdateLoop();
        }
    }

    /// <summary>The card's button (the nearest ancestor control) tells when the pointer is over the card.</summary>
    private void TrackHover(bool attach)
    {
        if (!Card)
        {
            return;
        }

        if (_hoverHost is not null)
        {
            _hoverHost.MouseEnter -= OnHoverChanged;
            _hoverHost.MouseLeave -= OnHoverChanged;
            _hoverHost = null;
        }

        if (!attach)
        {
            return;
        }

        DependencyObject? node = this;
        while (node is not null and not System.Windows.Controls.Control)
        {
            node = VisualTreeHelper.GetParent(node);
        }

        _hoverHost = node as UIElement;
        if (_hoverHost is not null)
        {
            _hoverHost.MouseEnter += OnHoverChanged;
            _hoverHost.MouseLeave += OnHoverChanged;
        }
    }

    private void OnHoverChanged(object sender, System.Windows.Input.MouseEventArgs e) => UpdateLoop();

    /// <summary>The still frame of a card that is not looping: the redline (as with Reduce Motion).</summary>
    private void PushStill()
    {
        if (_surface is null || !Card)
        {
            return;
        }

        var value = CardRedline * 1.1;
        _surface.SetData(value, HeatMath.RawTier(value, CardRedline), SpeedTitles.Raw(value, CardRedline), [], string.Empty, null);
    }

    private void PushLive()
    {
        if (_surface is null || Card || !IsLoaded)
        {
            return;
        }

        _surface.SetData(Value, Tier, Title, [], string.Empty, null);
    }

    /// <summary>The card loop: cruise (0.2 R) → redline/overdrive (1.1 R) → cruise every 8 s.</summary>
    private void PushLoop()
    {
        if (_surface is null || !Card)
        {
            return;
        }

        var t = _clock.Elapsed.TotalSeconds - CardIndex * 1.6;
        var phase = ReduceMotion ? 0.5 : (t % LoopSeconds + LoopSeconds) % LoopSeconds / LoopSeconds;
        var fraction = 0.2 + 0.9 * (0.5 - 0.5 * Math.Cos(2 * Math.PI * phase));
        var value = CardRedline * fraction;
        var now = _clock.Elapsed.TotalSeconds;
        _tier.Update(value, CardRedline, now);
        _title.Update(value, CardRedline, now);
        _surface.SetData(value, ReduceMotion ? HeatMath.RawTier(value, CardRedline) : _tier.Displayed, _title.Displayed, [], string.Empty, null);
    }
}
