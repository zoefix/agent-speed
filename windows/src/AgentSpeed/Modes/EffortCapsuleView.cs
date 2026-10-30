using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using AgentSpeed.Presentation.Localization;
using AgentSpeed.Presentation.Modes;
using AgentSpeed.Theme;
using Capsule = AgentSpeed.Presentation.Modes.EffortCapsule;

namespace AgentSpeed.Modes;

/// <summary>
/// The effort capsule (DESIGN 4, Agents): meter, word and bolts in a fully rounded capsule. Given a column width it
/// stretches to fill it, so meters start on one vertical line and bolts end on another; width changes animate 0.2 s.
/// A value only a config file holds takes the quiet dashed form with its mark, in the same column, height and part sizes,
/// and a real value arriving crossfades the whole form at once: an opacity change, so Reduce Motion keeps it.
/// </summary>
internal sealed class EffortCapsuleView : Grid
{
    public static readonly DependencyProperty CapsuleProperty = Register(nameof(Capsule), typeof(Capsule), null);

    public static readonly DependencyProperty CompactProperty = Register(nameof(Compact), typeof(bool), false);

    /// <summary>The shared column's width; NaN draws the capsule at its own width.</summary>
    public static readonly DependencyProperty ColumnWidthProperty = Register(nameof(ColumnWidth), typeof(double), double.NaN);

    /// <summary>Space after the capsule while the column shows (the gap before the live value).</summary>
    public static readonly DependencyProperty GapProperty = Register(nameof(Gap), typeof(double), 0.0);

    public static readonly DependencyProperty AppearanceProperty = Register(nameof(Appearance), typeof(CapsuleAppearance), default(CapsuleAppearance));

    /// <summary>The language whose UI face measures the word.</summary>
    public static readonly DependencyProperty LanguageCodeProperty = Register(nameof(LanguageCode), typeof(string), LanguageCodes.ZhHant);

    /// <summary><c>mode.configured.mark</c> in the UI language.</summary>
    public static readonly DependencyProperty MarkProperty = Register(nameof(Mark), typeof(string), string.Empty);

    private readonly Grid _frame = new();
    private readonly Border _shell;
    private readonly Rectangle _dashes = new() { RadiusX = Capsule.Height / 2, RadiusY = Capsule.Height / 2, IsHitTestVisible = false, StrokeDashCap = PenLineCap.Round };
    private readonly Image _previousForm = new() { HorizontalAlignment = HorizontalAlignment.Left, Stretch = Stretch.None, IsHitTestVisible = false, Visibility = Visibility.Collapsed };
    private readonly EffortMeter _meter = new();
    private readonly TextBlock _word = Word();
    private readonly TextBlock _fadingWord = Word();
    private readonly Grid _words = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _mark = MarkText();
    private readonly SpeedBolts _bolts = new();
    private string? _shownWord;
    private Capsule _shown = new(null, null);
    private bool _drawn;

    public EffortCapsuleView()
    {
        HorizontalAlignment = HorizontalAlignment.Left;
        VerticalAlignment = VerticalAlignment.Center;
        IsHitTestVisible = false;
        Focusable = false;
        // The fading word sits on a canvas so it never holds the capsule at the old word's width.
        var fading = new Canvas { ClipToBounds = false };
        fading.Children.Add(_fadingWord);
        _words.Children.Add(fading);
        _words.Children.Add(_word);
        var content = new Grid();
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        SetColumn(_words, 1);
        SetColumn(_mark, 2);
        SetColumn(_bolts, 4);
        content.Children.Add(_meter);
        content.Children.Add(_words);
        content.Children.Add(_mark);
        content.Children.Add(_bolts);
        _shell = new Border
        {
            Height = Capsule.Height,
            CornerRadius = new CornerRadius(Capsule.Height / 2),
            Child = content,
            SnapsToDevicePixels = false,
        };
        _frame.Height = Capsule.Height;
        _frame.Opacity = 0;
        _frame.Children.Add(_shell);
        _frame.Children.Add(_dashes);
        Children.Add(_frame);
        Children.Add(_previousForm);
        Width = 0;
        Loaded += (_, _) => Refresh(animate: false);
    }

    public Capsule? Capsule
    {
        get => (Capsule?)GetValue(CapsuleProperty);
        set => SetValue(CapsuleProperty, value);
    }

    public bool Compact
    {
        get => (bool)GetValue(CompactProperty);
        set => SetValue(CompactProperty, value);
    }

    public double ColumnWidth
    {
        get => (double)GetValue(ColumnWidthProperty);
        set => SetValue(ColumnWidthProperty, value);
    }

    public double Gap
    {
        get => (double)GetValue(GapProperty);
        set => SetValue(GapProperty, value);
    }

    public CapsuleAppearance Appearance
    {
        get => (CapsuleAppearance)GetValue(AppearanceProperty);
        set => SetValue(AppearanceProperty, value);
    }

    public string LanguageCode
    {
        get => (string)GetValue(LanguageCodeProperty);
        set => SetValue(LanguageCodeProperty, value);
    }

    public string Mark
    {
        get => (string)GetValue(MarkProperty);
        set => SetValue(MarkProperty, value);
    }

    private static DependencyProperty Register(string name, Type type, object? fallback) =>
        DependencyProperty.Register(name, type, typeof(EffortCapsuleView),
            new PropertyMetadata(fallback, (d, _) => ((EffortCapsuleView)d).Refresh(animate: true)));

    private static TextBlock Word()
    {
        var word = new TextBlock
        {
            FontSize = Capsule.WordFontSize,
            FontWeight = FontWeights.Medium,
            VerticalAlignment = VerticalAlignment.Center,
            Opacity = 0,
        };
        word.SetResourceReference(TextBlock.FontFamilyProperty, AppFonts.UiFontKey);
        return word;
    }

    // The mark shares the word's baseline: the two sizes centered differ by this much.
    private static TextBlock MarkText()
    {
        var mark = new TextBlock
        {
            FontSize = Capsule.MarkFontSize,
            FontWeight = FontWeights.Medium,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(Capsule.PartSpacing, 1.2, 0, 0),
            Visibility = Visibility.Collapsed,
        };
        mark.SetResourceReference(TextBlock.FontFamilyProperty, AppFonts.UiFontKey);
        return mark;
    }

    private void Refresh(bool animate)
    {
        var capsule = Capsule ?? new Capsule(null, null);
        var appearance = Appearance;
        var brushes = appearance.Brushes;
        animate &= IsLoaded && IsVisible && _drawn;
        _drawn = IsLoaded;
        var newForm = animate && capsule.IsVisible && _shown.IsVisible
            && (_shown.EffortConfigured != capsule.EffortConfigured || _shown.SpeedConfigured != capsule.SpeedConfigured);
        _shown = capsule;
        if (newForm)
        {
            CapturePreviousForm();
        }

        ShowForm(capsule, brushes);
        _frame.Margin = new Thickness(0, 0, Gap, 0);
        if (newForm)
        {
            _frame.BeginAnimation(OpacityProperty, null);
            _frame.Opacity = 0;
        }

        CapsuleMotion.To(_frame, OpacityProperty, capsule.IsVisible ? 1 : 0, CapsuleMotion.Crossfade, CapsuleMotion.Smooth, animate);

        // A capsule that disappears keeps its last parts while it fades; a new form changes them under the crossfade.
        if (capsule.IsVisible)
        {
            var parts = animate && !newForm;
            _meter.Visibility = capsule.Level is null ? Visibility.Collapsed : Visibility.Visible;
            _meter.Show(capsule, brushes, parts, appearance.ReduceMotion, appearance.Shimmers);
            ShowWord(capsule.ShowsWord(Compact) ? capsule.Effort : null, capsule, brushes, parts);
            _words.Margin = new Thickness(capsule.Level is not null && _shownWord is not null ? Capsule.PartSpacing : 0, 0, 0, 0);
            ShowMark(capsule, brushes);
            _bolts.Show(capsule.Bolts, brushes, parts, appearance.ReduceMotion, capsule.SpeedConfigured);
        }

        var own = capsule.Width(Compact, CapsuleWordMeasure.For(LanguageCode), CapsuleWordMeasure.Mark(Mark, LanguageCode));
        var width = double.IsNaN(ColumnWidth) ? own : ColumnWidth;
        CapsuleMotion.To(this, WidthProperty, width > 0 ? width + Gap : 0, CapsuleMotion.Crossfade, CapsuleMotion.Smooth, animate);
    }

    private void ShowForm(Capsule capsule, CapsuleBrushes brushes)
    {
        var dashed = capsule.IsConfigured;
        var thickness = dashed ? 0 : brushes.StrokeThickness;
        _shell.Background = dashed ? null : brushes.Fill;
        _shell.BorderBrush = dashed ? null : brushes.Stroke;
        _shell.BorderThickness = new Thickness(thickness);
        // The stroke sits inside the 6 DIP padding, so meters and bolts keep their lines in high contrast too.
        var inset = Capsule.Padding - thickness;
        _shell.Padding = new Thickness(inset, 0, inset, 0);
        _dashes.Visibility = dashed ? Visibility.Visible : Visibility.Collapsed;
        if (dashed)
        {
            var stroke = brushes.DashedThickness;
            _dashes.Stroke = brushes.DashedStroke;
            _dashes.StrokeThickness = stroke;
            _dashes.StrokeDashArray = [Capsule.DashLength / stroke, Capsule.DashGap / stroke];
        }
    }

    // A picture of the form that goes: it fades out over the new one, so stroke, colors and parts change together.
    private void CapturePreviousForm()
    {
        var (width, height) = (_frame.ActualWidth, _frame.ActualHeight);
        if (width <= 0 || height <= 0)
        {
            return;
        }

        var dpi = VisualTreeHelper.GetDpi(this);
        var picture = new DrawingVisual();
        using (var drawing = picture.RenderOpen())
        {
            drawing.DrawRectangle(new VisualBrush(_frame), null, new Rect(0, 0, width, height));
        }

        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(width * dpi.DpiScaleX), (int)Math.Ceiling(height * dpi.DpiScaleY),
            dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        bitmap.Render(picture);
        bitmap.Freeze();
        _previousForm.Source = bitmap;
        _previousForm.Visibility = Visibility.Visible;
        _previousForm.BeginAnimation(OpacityProperty, null);
        _previousForm.Opacity = 1;
        CapsuleMotion.To(_previousForm, OpacityProperty, 0, CapsuleMotion.Crossfade, CapsuleMotion.Smooth, animate: true, completed: () =>
        {
            _previousForm.Visibility = Visibility.Collapsed;
            _previousForm.Source = null;
        });
    }

    private void ShowMark(Capsule capsule, CapsuleBrushes brushes)
    {
        _mark.Text = Mark;
        _mark.Visibility = capsule.ShowsMark(Compact) ? Visibility.Visible : Visibility.Collapsed;
        Paint(_mark, brushes.Word(EffortTone.Configured), EffortTone.Configured);
    }

    private void ShowWord(string? word, Capsule capsule, CapsuleBrushes brushes, bool animate)
    {
        Paint(_word, brushes.Word(capsule.Tone), capsule.Tone);
        if (word == _shownWord)
        {
            CapsuleMotion.To(_word, OpacityProperty, word is null ? 0 : 1, CapsuleMotion.Crossfade, CapsuleMotion.Smooth, animate);
            return;
        }

        // The old word crossfades out in place while the new one fades in.
        _fadingWord.Text = _word.Text;
        _fadingWord.Foreground = _word.Foreground;
        _fadingWord.Opacity = _word.Opacity;
        CapsuleMotion.To(_fadingWord, OpacityProperty, 0, CapsuleMotion.Crossfade, CapsuleMotion.Smooth, animate);
        _word.BeginAnimation(OpacityProperty, null);
        _word.Opacity = 0;
        _word.Text = word ?? string.Empty;
        _shownWord = word;
        CapsuleMotion.To(_word, OpacityProperty, word is null ? 0 : 1, CapsuleMotion.Crossfade, CapsuleMotion.Smooth, animate);
        if (!animate)
        {
            _fadingWord.Text = string.Empty;
        }
    }

    private static void Paint(TextBlock text, Brush? brush, EffortTone tone)
    {
        if (brush is null)
        {
            text.SetResourceReference(TextBlock.ForegroundProperty, CapsuleBrushes.TextKey(tone));
        }
        else
        {
            text.Foreground = brush;
        }
    }
}
