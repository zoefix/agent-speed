using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Presenters;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Styling;

namespace AgentSpeed.Linux.UI;

/// <summary>Roles of the labels drawn on the desktop dashboard.</summary>
internal enum TextRole
{
    Primary,
    Secondary,
    Muted,
    Accent,
    Success,
    Warning,
}

internal enum DesktopButtonKind
{
    Secondary,
    Ghost,
    Nav,
    Tool,
    Source,
}

/// <summary>
/// The desktop surfaces use the original project's dark, blue-black instrument palette rather than the
/// generic Fluent greys. Every colour has a light and a system-high-contrast counterpart.
/// </summary>
internal sealed record DesktopPalette(
    bool Dark,
    bool HighContrast,
    Color Window,
    Color Sidebar,
    Color Card,
    Color CardLow,
    Color CardInset,
    Color Border,
    Color SidebarBorder,
    Color TextPrimary,
    Color TextSecondary,
    Color TextMuted,
    Color Accent,
    Color AccentSoft,
    Color NavHover,
    Color NavSelected,
    Color ButtonHover,
    Color ButtonPressed,
    Color WarningBackground,
    Color WarningBorder,
    Color WarningText,
    Color Success,
    Color ChartGrid)
{
    public IBrush Brush(Color color) => new ImmutableSolidColorBrush(color);

    public IBrush PrimaryBrush => Brush(TextPrimary);

    public IBrush SecondaryBrush => Brush(TextSecondary);

    public IBrush MutedBrush => Brush(TextMuted);

    public IBrush AccentBrush => Brush(Accent);

    public IBrush BorderBrush => Brush(Border);

    public IBrush WarningBrush => Brush(WarningText);

    public Color TextFor(TextRole role) => role switch
    {
        TextRole.Primary => TextPrimary,
        TextRole.Secondary => TextSecondary,
        TextRole.Muted => TextMuted,
        TextRole.Accent => Accent,
        TextRole.Success => Success,
        TextRole.Warning => WarningText,
        _ => TextPrimary,
    };
}

internal static class DesktopStyles
{
    public static Color Parse(string hex) => Color.Parse(hex);

    public static Color Alpha(Color color, byte alpha) => Color.FromArgb(alpha, color.R, color.G, color.B);

    public static DesktopPalette PaletteFor(bool dark, bool highContrast) => highContrast
        ? dark ? HighContrastDark : HighContrastLight
        : dark ? Dark : Light;

    private static DesktopPalette Dark { get; } = new(
        true, false,
        Parse("#0D111A"), Parse("#101620"), Parse("#151D2A"), Parse("#111925"), Parse("#111925"),
        Parse("#253144"), Parse("#1C2533"), Parse("#E7EDF7"), Parse("#93A0B4"), Parse("#7C8BA1"),
        Parse("#2DD4BF"), Parse("#17323A"), Parse("#182431"), Parse("#1A2E38"), Parse("#1C2635"), Parse("#223044"),
        Parse("#2B2114"), Parse("#5C4520"), Parse("#F5C66A"), Parse("#34D399"), Parse("#263246"));

    private static DesktopPalette Light { get; } = new(
        false, false,
        Parse("#F3F6FB"), Parse("#EAF0F7"), Parse("#FFFFFF"), Parse("#F7FAFD"), Parse("#F3F7FC"),
        Parse("#D6E0EC"), Parse("#DCE5EF"), Parse("#152033"), Parse("#4F5E72"), Parse("#5F6E83"),
        Parse("#0F766E"), Parse("#DDF3EF"), Parse("#EDF3F9"), Parse("#DDF3EF"), Parse("#EAF1F8"), Parse("#DDE7F2"),
        Parse("#FFF6E5"), Parse("#F0D29A"), Parse("#8A5A00"), Parse("#047857"), Parse("#DFE7F1"));

    private static DesktopPalette HighContrastDark { get; } = new(
        true, true,
        Parse("#000000"), Parse("#000000"), Parse("#050505"), Parse("#050505"), Parse("#0A0A0A"),
        Parse("#FFFFFF"), Parse("#FFFFFF"), Parse("#FFFFFF"), Parse("#EDEDED"), Parse("#D8D8D8"),
        Parse("#00E5FF"), Parse("#003840"), Parse("#1A1A1A"), Parse("#003840"), Parse("#1A1A1A"), Parse("#2E2E2E"),
        Parse("#241C00"), Parse("#FFD400"), Parse("#FFE680"), Parse("#00FF9C"), Parse("#666666"));

    private static DesktopPalette HighContrastLight { get; } = new(
        false, true,
        Parse("#FFFFFF"), Parse("#FFFFFF"), Parse("#FFFFFF"), Parse("#FFFFFF"), Parse("#F2F2F2"),
        Parse("#000000"), Parse("#000000"), Parse("#000000"), Parse("#000000"), Parse("#333333"),
        Parse("#005FCC"), Parse("#DCEEFF"), Parse("#EEEEEE"), Parse("#DCEEFF"), Parse("#EEEEEE"), Parse("#D8D8D8"),
        Parse("#FFF6D0"), Parse("#806000"), Parse("#4A3500"), Parse("#006B45"), Parse("#9A9A9A"));

    public static void ApplyWindow(Window window, DesktopPalette palette)
    {
        window.Background = palette.Brush(palette.Window);
        window.Foreground = palette.PrimaryBrush;
    }

    public static IBrush CardBrush(DesktopPalette palette) => new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
        GradientStops =
        {
            new GradientStop(palette.Card, 0),
            new GradientStop(palette.CardLow, 1),
        },
    };
}

/// <summary>
/// Registers palette-dependent setters so a light/dark or high-contrast change is applied in place. Long-lived shell
/// controls use the root registration; rebuilt settings and dynamic rows own a scope that is disposed with the controls,
/// so a long-running window does not retain every page or row it has ever shown.
/// </summary>
internal sealed class ThemeBinder
{
    private readonly List<ThemeScope> _scopes = [];
    private readonly ThemeScope _root;
    private ThemeScope? _current;

    public ThemeBinder(DesktopPalette palette)
    {
        Palette = palette;
        _root = new ThemeScope(this);
        _scopes.Add(_root);
    }

    public DesktopPalette Palette { get; private set; }

    /// <summary>All live registrations; exposed for the bounded-lifecycle smoke check.</summary>
    public int BindingCount => _scopes.Sum(scope => scope.Count);

    /// <summary>All live scopes, including the root scope; exposed for the bounded-lifecycle smoke check.</summary>
    public int ScopeCount => _scopes.Count;

    public void On(Action apply) => (_current ?? _root).Add(apply);

    internal ThemeScope BeginScope()
    {
        var scope = new ThemeScope(this);
        _scopes.Add(scope);
        return scope;
    }

    /// <summary>Registers subsequently created controls in <paramref name="scope"/> until the returned handle is disposed.</summary>
    internal ScopeActivation UseScope(ThemeScope scope)
    {
        if (scope.IsDisposed) throw new ObjectDisposedException(nameof(scope));
        var previous = _current;
        _current = scope;
        return new ScopeActivation(this, previous);
    }

    public void Apply(DesktopPalette next)
    {
        Palette = next;
        foreach (var scope in _scopes.ToArray()) scope.Apply();
    }

    internal void Remove(ThemeScope scope) => _scopes.Remove(scope);

    internal readonly struct ScopeActivation : IDisposable
    {
        private readonly ThemeBinder _binder;
        private readonly ThemeScope? _previous;

        internal ScopeActivation(ThemeBinder binder, ThemeScope? previous)
        {
            _binder = binder;
            _previous = previous;
        }

        public void Dispose() => _binder._current = _previous;
    }
}

/// <summary>One releasable set of theme registrations: a settings page, a source row or an agent row.</summary>
internal sealed class ThemeScope(ThemeBinder binder) : IDisposable
{
    private readonly List<Action> _bindings = [];
    private bool _disposed;

    internal int Count => _bindings.Count;

    internal bool IsDisposed => _disposed;

    internal void Add(Action apply)
    {
        if (_disposed) return;
        _bindings.Add(apply);
        apply();
    }

    internal void Apply()
    {
        if (_disposed) return;
        foreach (var apply in _bindings.ToArray()) apply();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _bindings.Clear();
        binder.Remove(this);
    }
}

/// <summary>
/// A flat, rounded button with the desktop palette's own hover, pressed and keyboard-focus states. The template is
/// deliberately simple: the Fluent grey plate never appears, while Enter/Space and the normal Button automation stay.
/// </summary>
internal sealed class DesktopButton : Button
{
    private readonly DesktopButtonKind _kind;
    private DesktopPalette _palette;
    private bool _hover;
    private bool _pressed;
    private bool _focused;

    public DesktopButton(DesktopPalette palette, DesktopButtonKind kind, object? content = null)
    {
        _palette = palette;
        _kind = kind;
        Content = content;
        Focusable = true;
        Cursor = new Cursor(StandardCursorType.Hand);
        HorizontalAlignment = HorizontalAlignment.Stretch;
        VerticalAlignment = VerticalAlignment.Center;
        HorizontalContentAlignment = HorizontalAlignment.Left;
        VerticalContentAlignment = VerticalAlignment.Center;
        BorderThickness = new Thickness(1);
        Background = Brushes.Transparent;
        BorderBrush = Brushes.Transparent;
        CornerRadius = new CornerRadius(8);
        if (content is string)
        {
            Padding = kind == DesktopButtonKind.Nav ? new Thickness(13, 0) : new Thickness(12, 7);
        }

        Template = new Avalonia.Controls.Templates.FuncControlTemplate<DesktopButton>((button, _) =>
        {
            var border = new Border { Name = "PART_Border" };
            border.Bind(Border.BackgroundProperty, button.GetObservable(BackgroundProperty));
            border.Bind(Border.BorderBrushProperty, button.GetObservable(BorderBrushProperty));
            border.Bind(Border.BorderThicknessProperty, button.GetObservable(BorderThicknessProperty));
            border.Bind(Border.CornerRadiusProperty, button.GetObservable(CornerRadiusProperty));
            border.Bind(Border.PaddingProperty, button.GetObservable(PaddingProperty));
            var presenter = new ContentPresenter
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
            };
            presenter.Bind(ContentPresenter.ContentProperty, button.GetObservable(ContentProperty));
            presenter.Bind(ContentPresenter.ContentTemplateProperty, button.GetObservable(ContentTemplateProperty));
            presenter.Bind(ContentPresenter.HorizontalContentAlignmentProperty,
                button.GetObservable(HorizontalContentAlignmentProperty));
            presenter.Bind(ContentPresenter.VerticalContentAlignmentProperty,
                button.GetObservable(VerticalContentAlignmentProperty));
            presenter.Bind(TextElement.ForegroundProperty, button.GetObservable(ForegroundProperty));
            border.Child = presenter;
            return border;
        });

        PointerEntered += (_, _) => { _hover = true; UpdateVisual(); };
        PointerExited += (_, _) => { _hover = false; UpdateVisual(); };
        PropertyChanged += (_, e) =>
        {
            if (e.Property == IsPressedProperty)
            {
                _pressed = IsPressed;
                UpdateVisual();
            }
        };
        GotFocus += (_, _) => { _focused = true; UpdateVisual(); };
        LostFocus += (_, _) => { _focused = false; UpdateVisual(); };
        UpdateVisual();
    }

    private bool _selected;
    public bool Selected
    {
        get => _selected;
        set
        {
            if (_selected == value) return;
            _selected = value;
            UpdateVisual();
        }
    }

    public void SetPalette(DesktopPalette palette)
    {
        _palette = palette;
        UpdateVisual();
    }

    private void UpdateVisual()
    {
        var p = _palette;
        var foreground = p.TextSecondary;
        var background = Colors.Transparent;
        var border = Colors.Transparent;
        var thickness = _kind == DesktopButtonKind.Nav || _kind == DesktopButtonKind.Tool
            ? new Thickness(0) : new Thickness(1);

        switch (_kind)
        {
            case DesktopButtonKind.Nav when Selected:
                background = p.NavSelected;
                foreground = p.TextPrimary;
                border = p.Accent;
                thickness = new Thickness(1);
                break;
            case DesktopButtonKind.Nav:
                background = _hover ? p.NavHover : Colors.Transparent;
                foreground = _hover ? p.TextPrimary : p.TextSecondary;
                break;
            case DesktopButtonKind.Tool:
                background = Selected ? p.NavSelected : _hover ? p.ButtonHover : Colors.Transparent;
                foreground = Selected || _hover ? p.TextPrimary : p.TextMuted;
                if (Selected) { border = p.Accent; thickness = new Thickness(1); }
                break;
            case DesktopButtonKind.Ghost:
                background = _hover ? p.ButtonHover : Colors.Transparent;
                foreground = _hover ? p.TextPrimary : p.TextSecondary;
                break;
            case DesktopButtonKind.Source:
                background = _pressed ? p.ButtonPressed : _hover ? p.ButtonHover : Colors.Transparent;
                foreground = p.TextPrimary;
                break;
            case DesktopButtonKind.Secondary:
                background = _pressed ? p.ButtonPressed : _hover ? p.ButtonHover : Colors.Transparent;
                border = _hover || _focused ? p.Accent : p.Border;
                foreground = _hover ? p.TextPrimary : p.TextSecondary;
                break;
        }

        if (_pressed && _kind is not DesktopButtonKind.Source)
        {
            background = p.ButtonPressed;
            foreground = p.TextPrimary;
        }

        if (_focused)
        {
            border = p.Accent;
            thickness = new Thickness(1);
        }

        Background = p.Brush(background);
        BorderBrush = p.Brush(border);
        BorderThickness = thickness;
        Foreground = p.Brush(foreground);
    }
}
