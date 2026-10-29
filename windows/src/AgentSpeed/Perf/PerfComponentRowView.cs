using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using AgentSpeed.Presentation.Perf;

namespace AgentSpeed.Perf;

/// <summary>
/// One component row of the card (DESIGN 12, <i>Score card</i>, 3): line 1 the label (12.5 DIP semibold, the rest of the
/// width), the measured value (12 DIP medium, tabular, right-aligned in a column as wide as the widest value shown, at least
/// 64), the 44 × 4 mini meter 10 after it and the sub-score (12 DIP semibold, tabular, right-aligned, 8 after the meter, at
/// least 34); the meaning 3 under it (10.5 DIP, caption color, wrapping). An absent component shows its reason in 11.5 DIP
/// tertiary, a dotted track, a tertiary <c>–</c> and why instead of the meaning. The value and sub-score columns are shared
/// by every row of the card.
/// </summary>
internal sealed class PerfComponentRowView : StackPanel
{
    public const string ValueGroup = "PerfValue";
    public const string SubScoreGroup = "PerfSubScore";

    private readonly TextBlock _label = Text(12.5, FontWeights.SemiBold);
    private readonly TextBlock _value = Text(12, FontWeights.Medium);
    private readonly PerfMiniMeter _meter = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _subScore = Text(12, FontWeights.SemiBold);
    private readonly TextBlock _meaning = Text(10.5, FontWeights.Normal);

    public PerfComponentRowView()
    {
        var line = new Grid();
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, SharedSizeGroup = ValueGroup, MinWidth = 64 });
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(44) });
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, SharedSizeGroup = SubScoreGroup, MinWidth = 34 });
        _label.TextTrimming = TextTrimming.CharacterEllipsis;
        _label.Margin = new Thickness(0, 0, 8, 0);
        foreach (var number in new[] { _value, _subScore })
        {
            number.HorizontalAlignment = HorizontalAlignment.Right;
            Typography.SetNumeralAlignment(number, FontNumeralAlignment.Tabular);
        }

        Grid.SetColumn(_value, 1);
        Grid.SetColumn(_meter, 3);
        Grid.SetColumn(_subScore, 5);
        line.Children.Add(_label);
        line.Children.Add(_value);
        line.Children.Add(_meter);
        line.Children.Add(_subScore);
        _meaning.Margin = new Thickness(0, 3, 0, 0);
        _meaning.TextWrapping = TextWrapping.Wrap;
        _meaning.LineHeight = 14.5;
        _meaning.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
        Children.Add(line);
        Children.Add(_meaning);
    }

    public void ShowMeaning(bool shown) => _meaning.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;

    public void Apply(PerfComponentRow row, PerfCardView card, bool animate)
    {
        ArgumentNullException.ThrowIfNull(row);
        card.Paint(_label, PerfCardView.Role.Primary);
        _label.Text = row.Label;
        _value.Text = row.Value;
        _value.FontSize = row.Present ? 12 : 11.5;
        _value.FontWeight = row.Present ? FontWeights.Medium : FontWeights.Normal;
        card.Paint(_value, row.Present ? PerfCardView.Role.Primary : PerfCardView.Role.Tertiary);
        _subScore.Text = row.SubScore;
        card.Paint(_subScore, row.Present ? PerfCardView.Role.Primary : PerfCardView.Role.Tertiary);
        _meter.Apply(row.Present, row.Fill, card.Appearance, animate);
        _meaning.Text = row.Meaning;
        card.Paint(_meaning, PerfCardView.Role.Caption);
    }

    private static TextBlock Text(double size, FontWeight weight) => new() { FontSize = size, FontWeight = weight, VerticalAlignment = VerticalAlignment.Center };
}
