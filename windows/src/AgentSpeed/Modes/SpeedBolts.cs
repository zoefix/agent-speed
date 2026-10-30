using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AgentSpeed.Theme;
using Capsule = AgentSpeed.Presentation.Modes.EffortCapsule;

namespace AgentSpeed.Modes;

/// <summary>
/// The speed's bolts (DESIGN 4, Agents): one for <c>fast</c> and <c>highspeed</c>, two overlapping by 3 DIP for
/// <c>ultrafast</c> with the rear one at 60 %. Bolts pop in (scale 0.6 → 1) and fade out in 0.15 s.
/// </summary>
internal sealed class SpeedBolts : StackPanel
{
    private const string Lightning = "\uE945";

    private readonly TextBlock[] _glyphs = new TextBlock[2];
    private readonly ScaleTransform _scale = new(1, 1);
    private int _count;

    public SpeedBolts()
    {
        Orientation = Orientation.Horizontal;
        VerticalAlignment = VerticalAlignment.Center;
        HorizontalAlignment = HorizontalAlignment.Right;
        IsHitTestVisible = false;
        RenderTransformOrigin = new Point(0.5, 0.5);
        RenderTransform = _scale;
        Opacity = 0;
        for (var i = 0; i < _glyphs.Length; i++)
        {
            _glyphs[i] = new TextBlock
            {
                Text = Lightning,
                FontSize = Capsule.BoltSize,
                Width = Capsule.BoltSize,
                TextAlignment = TextAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(i == 0 ? 0 : -Capsule.BoltOverlap, 0, 0, 0),
                Visibility = Visibility.Collapsed,
            };
            _glyphs[i].SetResourceReference(TextBlock.FontFamilyProperty, AppFonts.SymbolFontKey);
            Children.Add(_glyphs[i]);
        }
    }

    /// <param name="configured">A configured speed: the same glyph in the tertiary color, never amber.</param>
    public void Show(int count, CapsuleBrushes brushes, bool animate, bool reduceMotion, bool configured = false)
    {
        foreach (var glyph in _glyphs)
        {
            if (!configured)
            {
                glyph.Foreground = brushes.Bolt;
            }
            else if (brushes.ConfiguredBolt is { } contrast)
            {
                glyph.Foreground = contrast;
            }
            else
            {
                glyph.SetResourceReference(TextBlock.ForegroundProperty, CapsuleBrushes.TertiaryKey);
            }
        }

        var previous = _count;
        _count = count;
        if (count == 0)
        {
            // The glyphs stay in place while they fade.
            CapsuleMotion.To(this, OpacityProperty, 0, reduceMotion ? CapsuleMotion.Crossfade : CapsuleMotion.BoltFade,
                CapsuleMotion.Smooth, animate);
            return;
        }

        for (var i = 0; i < _glyphs.Length; i++)
        {
            _glyphs[i].Visibility = i < count ? Visibility.Visible : Visibility.Collapsed;
            _glyphs[i].Opacity = count > 1 && i < count - 1 ? Capsule.RearBoltOpacity : 1;
        }

        if (count == previous)
        {
            return;
        }

        CapsuleMotion.To(this, OpacityProperty, 1, reduceMotion ? CapsuleMotion.Crossfade : CapsuleMotion.BoltPop, CapsuleMotion.Smooth, animate);
        if (animate && !reduceMotion)
        {
            _scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            _scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            _scale.ScaleX = _scale.ScaleY = 0.6;
        }

        CapsuleMotion.To(_scale, ScaleTransform.ScaleXProperty, 1, CapsuleMotion.BoltPop, CapsuleMotion.Spring, animate && !reduceMotion);
        CapsuleMotion.To(_scale, ScaleTransform.ScaleYProperty, 1, CapsuleMotion.BoltPop, CapsuleMotion.Spring, animate && !reduceMotion);
    }
}
