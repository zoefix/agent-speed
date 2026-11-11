using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using AgentSpeed.Controls;
using AgentSpeed.Presentation.Quota;

namespace AgentSpeed.Dashboard.Quota;

/// <summary>One quota line of a Sources row (DESIGN 4): kept across updates so its bar and texts move instead of jumping.</summary>
internal sealed class QuotaLineRow : ObservableObject
{
    private static readonly Brush AmberDark = Frozen(Color.FromRgb(0xF5, 0xA5, 0x24));
    private static readonly Brush AmberLight = Frozen(Color.FromRgb(0xB8, 0x6E, 0x00));

    private string _label = string.Empty;
    private string _percent = string.Empty;
    private double _bar;
    private double _pace = double.NaN;
    private bool _passed;
    private string _reset = string.Empty;
    private string _age = string.Empty;
    private string _tooltip = string.Empty;
    private string _tooltipWithAge = string.Empty;
    private Brush _fill = Brushes.Gray;
    private bool _critical;

    public string Label { get => _label; private set => Set(ref _label, value); }

    public string PercentText { get => _percent; private set => Set(ref _percent, value); }

    /// <summary>The bar's fill, 0…1.</summary>
    public double Bar { get => _bar; private set => Set(ref _bar, value); }

    /// <summary>The pace mark 0…1, NaN while unknown.</summary>
    public double Pace { get => _pace; private set => Set(ref _pace, value); }

    public bool Passed { get => _passed; private set => Set(ref _passed, value); }

    public string ResetText { get => _reset; private set => Set(ref _reset, value); }

    /// <summary><c> · 2 小時前更新</c> in tertiary while the snapshot is over an hour old (without the separator after no reset).</summary>
    public string AgeText { get => _age; private set => Set(ref _age, value); }

    public string Tooltip { get => _tooltip; private set => Set(ref _tooltip, value); }

    /// <summary>The tooltip of a line with no room for its age: the age after the snapshot's time.</summary>
    public string TooltipWithAge { get => _tooltipWithAge; private set => Set(ref _tooltipWithAge, value); }

    public Brush Fill { get => _fill; private set => Set(ref _fill, value); }

    /// <summary>From 95 % the percent takes the bar's red.</summary>
    public bool Critical { get => _critical; private set => Set(ref _critical, value); }

    /// <summary>Takes a line's values; <paramref name="source"/> is the source color, the tone's colors follow the theme.</summary>
    public void Apply(QuotaLine line, Brush source, bool dark, bool highContrast)
    {
        // The passed flag goes first, so the bar empties over the reset's longer duration.
        Passed = line.Passed;
        Label = line.Label;
        PercentText = line.PercentText;
        Bar = line.Bar;
        Pace = line.Pace ?? double.NaN;
        ResetText = line.ResetLong;
        var age = line.Age.StartsWith(Separator, StringComparison.Ordinal) ? line.Age[Separator.Length..] : line.Age;
        AgeText = line.ResetLong.Length > 0 || age.Length == 0 ? line.Age : age;
        Tooltip = line.Tooltip;
        TooltipWithAge = age.Length == 0 ? line.Tooltip : WithAge(line.Tooltip, age);
        Critical = line.Tone == QuotaTone.Critical && !highContrast;
        Fill = highContrast ? SystemColors.WindowTextBrush : line.Tone switch
        {
            QuotaTone.Critical => CriticalBrush(),
            QuotaTone.Warning => dark ? AmberDark : AmberLight,
            _ => source,
        };
    }

    private const string Separator = " · ";

    /// <summary><c>quota.updated</c>, the age, then the pace while known.</summary>
    private static string WithAge(string tooltip, string age)
    {
        var lineBreak = tooltip.IndexOf('\n', StringComparison.Ordinal);
        return lineBreak < 0 ? tooltip + "\n" + age : tooltip[..lineBreak] + "\n" + age + tooltip[lineBreak..];
    }

    private static Brush CriticalBrush() => Application.Current?.TryFindResource("SystemFillColorCriticalBrush") as Brush ?? Brushes.IndianRed;

    /// <summary>Updates <paramref name="rows"/> in place to <paramref name="lines"/>: kept rows animate, extra ones go.</summary>
    public static void Sync(ObservableCollection<QuotaLineRow> rows, IReadOnlyList<QuotaLine> lines, Brush source, bool dark, bool highContrast)
    {
        for (var i = 0; i < lines.Count; i++)
        {
            var row = rows.FirstOrDefault(r => r.Label == lines[i].Label);
            if (row is null)
            {
                row = new QuotaLineRow();
                rows.Insert(Math.Min(i, rows.Count), row);
            }
            else if (rows.IndexOf(row) is var at && at != i)
            {
                rows.Move(at, i);
            }

            row.Apply(lines[i], source, dark, highContrast);
        }

        while (rows.Count > lines.Count)
        {
            rows.RemoveAt(rows.Count - 1);
        }
    }

    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
