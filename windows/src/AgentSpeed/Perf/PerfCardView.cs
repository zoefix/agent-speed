using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Shapes;
using AgentSpeed.Presentation.Colors;
using AgentSpeed.Presentation.Perf;
using AgentSpeed.Theme;

namespace AgentSpeed.Perf;

/// <summary>
/// The score card's content (性能分析卡, DESIGN 12, <i>Score card</i>, Layout): the header, the score with its eyebrow, number,
/// usual chip, scale and summary, the 速度 and 效率 component groups, 行為, the facts and the footnote — 320 DIP wide with
/// 14 DIP padding and blocks 12 apart. Built once and updated in place, so the number counts and the meters ease when the
/// engine tick changes them.
/// </summary>
internal sealed class PerfCardView : StackPanel
{
    public const double CardWidth = PerfCardPlacement.Width;
    public const double CardPadding = 14;
    public const double BlockGap = 12;
    public const double GroupGap = 14;
    public const double RowGap = 9;

    private const double SourceDot = 7;
    private const double DotGap = 7;

    private readonly Ellipse _dot = new() { Width = SourceDot, Height = SourceDot, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _project = Text(13, FontWeights.SemiBold, trim: true);
    private readonly PerfChipView _provisional = new() { Margin = new Thickness(8, 0, 0, 0) };
    private readonly TextBlock _meta = Text(11, FontWeights.Normal, trim: true);
    private readonly Rectangle _headerRule = Hairline();

    private readonly TextBlock _eyebrow = Text(11, FontWeights.Normal, trim: true);
    private readonly PerfNumberView _number = new();
    private readonly PerfChipView _usual = new();
    private readonly PerfScaleView _scale = new();
    private readonly PerfScoreBlock _scoreRow;
    private readonly TextBlock _summary = Text(12, FontWeights.Normal, wrap: true);
    private readonly TextBlock _hint = Text(11, FontWeights.Normal, wrap: true);

    private readonly StackPanel _groups = new() { Margin = new Thickness(0, BlockGap, 0, 0) };
    private readonly (StackPanel Panel, PerfTrackedText Title, PerfComponentRowView[] Rows)[] _groupViews;

    private readonly StackPanel _signals = new() { Margin = new Thickness(0, BlockGap, 0, 0) };
    private readonly PerfTrackedText _signalsTitle = new() { Margin = new Thickness(0, 0, 0, 8) };
    private readonly StackPanel _signalLines = new();

    private readonly Rectangle _factsRule = Hairline();
    private readonly StackPanel _facts = new();
    private readonly TextBlock _footnote = Text(10.5, FontWeights.Normal, wrap: true);

    private PerfCard? _card;
    private int _compact;

    public PerfCardView()
    {
        Width = CardWidth - 2 * CardPadding;
        UseLayoutRounding = true;
        Grid.SetIsSharedSizeScope(this, true);

        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(DotGap) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(_project, 2);
        Grid.SetColumn(_provisional, 3);
        header.Children.Add(_dot);
        header.Children.Add(_project);
        header.Children.Add(_provisional);
        _meta.Margin = new Thickness(SourceDot + DotGap, 2, 0, 0);
        Children.Add(header);
        Children.Add(_meta);
        Children.Add(_headerRule);

        _number.SetResourceReference(PerfNumberView.FontFamilyProperty, AppFonts.DisplayFontKey);
        _scale.Number = _number;
        _scoreRow = new PerfScoreBlock(_number, _usual, _scale);
        _eyebrow.Margin = new Thickness(0, 0, 0, 2);
        _summary.Margin = new Thickness(0, 8, 0, 0);
        _summary.LineHeight = 17;
        _summary.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
        _summary.MaxHeight = 2 * 17 + 1;
        _summary.TextTrimming = TextTrimming.CharacterEllipsis;
        _hint.Margin = new Thickness(0, 4, 0, 0);
        Children.Add(_eyebrow);
        Children.Add(_scoreRow);
        Children.Add(_summary);
        Children.Add(_hint);

        _groupViews = new (StackPanel, PerfTrackedText, PerfComponentRowView[])[2];
        for (var g = 0; g < _groupViews.Length; g++)
        {
            var panel = new StackPanel { Margin = new Thickness(0, g == 0 ? 0 : GroupGap, 0, 0) };
            var title = new PerfTrackedText { Margin = new Thickness(0, 0, 0, 8) };
            var rows = Enumerable.Range(0, g == 0 ? PerfText.SpeedRows.Count : PerfText.EfficiencyRows.Count)
                .Select(i => new PerfComponentRowView { Margin = new Thickness(0, i == 0 ? 0 : RowGap, 0, 0) }).ToArray();
            panel.Children.Add(title);
            foreach (var row in rows)
            {
                panel.Children.Add(row);
            }

            _groups.Children.Add(panel);
            _groupViews[g] = (panel, title, rows);
        }

        Children.Add(_groups);

        _signals.Children.Add(_signalsTitle);
        _signals.Children.Add(_signalLines);
        Children.Add(_signals);

        Children.Add(_factsRule);
        Children.Add(_facts);
        _footnote.Margin = new Thickness(0, 10, 0, 0);
        _footnote.LineHeight = 14.5;
        _footnote.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
        Children.Add(_footnote);
    }

    internal enum Role
    {
        Primary,
        Secondary,
        Tertiary,
        Caption,
    }

    internal PerfCardAppearance Appearance { get; private set; }

    /// <summary>
    /// How much a card taller than the screen leaves out: 1 drops the components' meanings, 2 the footnote too. Both stay
    /// in the row's help text.
    /// </summary>
    public int Compact
    {
        get => _compact;
        set
        {
            if (value == _compact)
            {
                return;
            }

            _compact = value;
            foreach (var (_, _, rows) in _groupViews)
            {
                foreach (var row in rows)
                {
                    row.ShowMeaning(value < 1);
                }
            }

            _footnote.Visibility = value < 2 ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    /// <summary>
    /// Shows <paramref name="card"/>. While <paramref name="sameRow"/> (the open card's row on a new engine tick) the number
    /// follows DESIGN 12's motion and the meters ease; a newly opened or switched card shows its values as they are.
    /// </summary>
    public void Apply(PerfCard card, PerfCardAppearance appearance, bool sameRow)
    {
        ArgumentNullException.ThrowIfNull(card);
        Appearance = appearance;
        _card = card;

        _dot.Fill = appearance.HighContrast ? SystemColors.WindowTextBrush : HeatPalette.SourceColor(card.Source).ToBrush();
        Set(_project, card.Project, Role.Primary);
        _provisional.Apply(card.Provisional ? new PerfChip(card.ProvisionalChip, PerfChipTone.Outlined) : null, appearance, dotRing: true);
        Set(_meta, card.Meta, Role.Secondary);
        _headerRule.Margin = new Thickness(0, BlockGap, 0, BlockGap);
        PaintRule(_headerRule);

        ApplyScore(card, sameRow);
        ApplyGroups(card, sameRow);
        ApplySignals(card);
        ApplyFacts(card);
        Set(_footnote, card.Footnote, Role.Caption);
        _footnote.Visibility = _compact >= 2 ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>The card window shows what the row already says: one element named with the row's help text, without children.</summary>
    protected override AutomationPeer OnCreateAutomationPeer() => new CardPeer(this);

    internal void Paint(TextBlock block, Role role)
    {
        if (role != Role.Caption && !Appearance.HighContrast)
        {
            block.SetResourceReference(TextBlock.ForegroundProperty, Key(role));
            return;
        }

        block.Foreground = BrushFor(role);
    }

    private void ApplyScore(PerfCard card, bool sameRow)
    {
        _eyebrow.Visibility = card.HasEyebrow ? Visibility.Visible : Visibility.Collapsed;
        Set(_eyebrow, card.Eyebrow ?? string.Empty, Role.Secondary);

        _number.ReduceMotion = Appearance.ReduceMotion;
        _number.HighContrast = Appearance.HighContrast;
        _number.Apply(card.Shown, card.NumberTone, card.Provisional, animate: sameRow);
        _usual.Apply(card.Usual, Appearance);
        _scale.Apply(card.NumberTone, card.Provisional, Appearance);

        Set(_summary, card.Summary, Role.Secondary);
        _summary.ToolTip = null;
        _hint.Visibility = string.IsNullOrEmpty(card.ProvisionalHint) ? Visibility.Collapsed : Visibility.Visible;
        Set(_hint, card.ProvisionalHint, Role.Tertiary);
    }

    private void ApplyGroups(PerfCard card, bool sameRow)
    {
        _groups.Visibility = card.Groups.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        for (var g = 0; g < _groupViews.Length; g++)
        {
            var (panel, title, rows) = _groupViews[g];
            var visible = g < card.Groups.Count;
            panel.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            if (!visible)
            {
                continue;
            }

            var group = card.Groups[g];
            title.SetTitle(group.Title);
            PaintTitle(title);
            for (var i = 0; i < rows.Length; i++)
            {
                var shown = i < group.Rows.Count;
                rows[i].Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
                if (shown)
                {
                    rows[i].Apply(group.Rows[i], this, sameRow);
                    rows[i].ShowMeaning(_compact < 1);
                }
            }
        }
    }

    private void ApplySignals(PerfCard card)
    {
        _signals.Visibility = card.HasSignals ? Visibility.Visible : Visibility.Collapsed;
        _signalsTitle.SetTitle(card.SignalsTitle);
        PaintTitle(_signalsTitle);
        _signalLines.Children.Clear();
        for (var i = 0; i < card.Signals.Count; i++)
        {
            var signal = card.Signals[i];
            var line = new DockPanel { Margin = new Thickness(0, i == 0 ? 0 : 6, 0, 0) };
            var glyph = new TextBlock
            {
                Text = SignalGlyph(signal.Glyph),
                FontSize = 13,
                FontFamily = AppFonts.Symbols,
                Width = 16,
                Margin = new Thickness(0, 1, 6, 0),
                VerticalAlignment = VerticalAlignment.Top,
                Foreground = signal.Glyph == PerfSignalGlyph.Loop ? Appearance.LoopGlyph : BrushFor(Role.Secondary),
            };
            DockPanel.SetDock(glyph, Dock.Left);
            var text = Text(12, FontWeights.Normal, wrap: true);
            Set(text, signal.Text, signal.IsWarning ? Role.Primary : Role.Secondary);
            line.Children.Add(glyph);
            line.Children.Add(text);
            _signalLines.Children.Add(line);
        }
    }

    private void ApplyFacts(PerfCard card)
    {
        _factsRule.Visibility = card.Facts.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        _factsRule.Margin = new Thickness(0, BlockGap, 0, 9);
        PaintRule(_factsRule);
        _facts.Children.Clear();
        foreach (var fact in card.Facts)
        {
            var line = Text(11, FontWeights.Normal, wrap: true);
            line.LineHeight = 16.5;
            line.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
            Typography.SetNumeralAlignment(line, FontNumeralAlignment.Tabular);
            Set(line, fact, Role.Secondary);
            _facts.Children.Add(line);
        }
    }

    private void PaintTitle(PerfTrackedText title)
    {
        if (Appearance.HighContrast)
        {
            title.Foreground = SystemColors.GrayTextBrush;
        }
        else
        {
            title.SetResourceReference(PerfTrackedText.ForegroundProperty, PerfCardAppearance.TertiaryKey);
        }
    }

    private void PaintRule(Rectangle rule)
    {
        if (Appearance.HighContrast)
        {
            rule.Fill = SystemColors.GrayTextBrush;
        }
        else
        {
            rule.SetResourceReference(Shape.FillProperty, "DividerStrokeColorDefaultBrush");
        }
    }

    private static string Key(Role role) => role switch
    {
        Role.Primary => PerfCardAppearance.PrimaryKey,
        Role.Secondary => PerfCardAppearance.SecondaryKey,
        _ => PerfCardAppearance.TertiaryKey,
    };

    private Brush BrushFor(Role role)
    {
        if (role == Role.Caption)
        {
            return Appearance.Caption;
        }

        if (Appearance.HighContrast)
        {
            return role == Role.Tertiary ? SystemColors.GrayTextBrush : SystemColors.WindowTextBrush;
        }

        return TryFindResource(Key(role)) as Brush ?? Brushes.Gray;
    }

    private void Set(TextBlock block, string text, Role role)
    {
        block.Text = text;
        Paint(block, role);
    }

    private static string SignalGlyph(PerfSignalGlyph glyph) => glyph switch
    {
        PerfSignalGlyph.Loop => "",
        PerfSignalGlyph.Repeat => "",
        _ => "",
    };

    private static TextBlock Text(double size, FontWeight weight, bool trim = false, bool wrap = false)
    {
        var block = new TextBlock { FontSize = size, FontWeight = weight };
        if (trim)
        {
            block.TextTrimming = TextTrimming.CharacterEllipsis;
        }

        if (wrap)
        {
            block.TextWrapping = TextWrapping.Wrap;
        }

        return block;
    }

    private static Rectangle Hairline() => new() { Height = 1, SnapsToDevicePixels = true };

    private sealed class CardPeer(PerfCardView owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Group;

        protected override string GetClassNameCore() => nameof(PerfCardView);

        protected override string GetNameCore() => ((PerfCardView)Owner)._card?.Project ?? string.Empty;

        protected override string GetHelpTextCore() => ((PerfCardView)Owner)._card?.Description ?? string.Empty;

        protected override List<AutomationPeer> GetChildrenCore() => [];
    }
}
