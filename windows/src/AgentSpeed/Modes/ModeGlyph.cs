using System.Windows;
using System.Windows.Controls;
using System.Windows.Shapes;
using Capsule = AgentSpeed.Presentation.Modes.EffortCapsule;

namespace AgentSpeed.Modes;

/// <summary>
/// An effort or speed value's leading glyph in the history (DESIGN 4b, 3b): the capsule's meter for effort; for speed its
/// bolts, or a hollow 6 DIP ring for <c>standard</c> and tokens without bolts. Nothing for the unknown bucket.
/// </summary>
internal sealed class ModeGlyph : Grid
{
    /// <summary>An effort-only or speed-only capsule; null draws nothing.</summary>
    public static readonly DependencyProperty GlyphProperty = Register(nameof(Glyph), typeof(Capsule), null);

    public static readonly DependencyProperty AppearanceProperty = Register(nameof(Appearance), typeof(CapsuleAppearance), default(CapsuleAppearance));

    private readonly EffortMeter _meter = new() { HorizontalAlignment = HorizontalAlignment.Left };
    private readonly SpeedBolts _bolts = new() { HorizontalAlignment = HorizontalAlignment.Left };
    private readonly Ellipse _ring = new()
    {
        Width = 6,
        Height = 6,
        StrokeThickness = 1,
        Margin = new Thickness(1, 0, 0, 0),
        HorizontalAlignment = HorizontalAlignment.Left,
        VerticalAlignment = VerticalAlignment.Center,
    };

    public ModeGlyph()
    {
        IsHitTestVisible = false;
        _ring.SetResourceReference(Shape.StrokeProperty, CapsuleBrushes.SecondaryKey);
        Children.Add(_meter);
        Children.Add(_bolts);
        Children.Add(_ring);
        Loaded += (_, _) => Refresh(animate: false);
        Refresh(animate: false);
    }

    public Capsule? Glyph
    {
        get => (Capsule?)GetValue(GlyphProperty);
        set => SetValue(GlyphProperty, value);
    }

    public CapsuleAppearance Appearance
    {
        get => (CapsuleAppearance)GetValue(AppearanceProperty);
        set => SetValue(AppearanceProperty, value);
    }

    private static DependencyProperty Register(string name, Type type, object? fallback) =>
        DependencyProperty.Register(name, type, typeof(ModeGlyph),
            new PropertyMetadata(fallback, (d, _) => ((ModeGlyph)d).Refresh(animate: true)));

    private void Refresh(bool animate)
    {
        var glyph = Glyph;
        var appearance = Appearance;
        animate &= IsLoaded;
        var meter = glyph is { Level: not null };
        var bolts = glyph is { Effort: null, Speed: not null } && glyph.Bolts > 0;
        _meter.Visibility = meter ? Visibility.Visible : Visibility.Collapsed;
        _ring.Visibility = glyph is { Effort: null, Speed: not null } && !bolts ? Visibility.Visible : Visibility.Collapsed;
        if (meter)
        {
            _meter.Show(glyph!, appearance.Brushes, animate, appearance.ReduceMotion, appearance.Shimmers);
        }

        _bolts.Visibility = bolts ? Visibility.Visible : Visibility.Collapsed;
        _bolts.Show(bolts ? glyph!.Bolts : 0, appearance.Brushes, animate: false, appearance.ReduceMotion);
    }
}
