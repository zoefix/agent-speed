using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using AgentSpeed.Controls;
using AgentSpeed.Presentation.Quota;

namespace AgentSpeed.Dashboard.Quota;

/// <summary>
/// One quota line of a Sources or host row (DESIGN 4): kept across updates so its bar and texts move instead of jumping. The
/// row shows the short reset; its tooltip reads the long reset, the snapshot's time, the age when the line has no room for
/// it, the pace, then the estimate's details.
/// </summary>
internal sealed class QuotaLineRow : ObservableObject
{
    private static readonly Brush AmberDark = Frozen(Color.FromRgb(0xF5, 0xA5, 0x24));
    private static readonly Brush AmberLight = Frozen(Color.FromRgb(0xB8, 0x6E, 0x00));

    private string _label = string.Empty;
    private string _percent = string.Empty;
    private string _percentLead = string.Empty;
    private string _amount = string.Empty;
    private string _amountCap = string.Empty;
    private string _tipReset = string.Empty;
    private string _tipUpdated = string.Empty;
    private string _tipAge = string.Empty;
    private string _tipPace = string.Empty;
    private IReadOnlyList<string> _details = [];
    private string _note = string.Empty;
    private double _bar;
    private double _pace = double.NaN;
    private bool _passed;
    private string _reset = string.Empty;
    private string _age = string.Empty;
    private Brush _fill = Brushes.Gray;
    private bool _critical;

    public string Label { get => _label; private set => Set(ref _label, value); }

    public string PercentText { get => _percent; private set => Set(ref _percent, value); }

    /// <summary><c>≈</c> before an extrapolated percent (QUOTAS 10.7), in secondary; empty otherwise.</summary>
    public string PercentLead { get => _percentLead; private set => Set(ref _percentLead, value); }

    /// <summary>The used amount in the value color (DESIGN 4 <i>Amount</i>); empty without spend and estimate.</summary>
    public string AmountText { get => _amount; private set => Set(ref _amount, value); }

    /// <summary><c> / ≈$5,742</c> or <c> / ≥$737</c> after the amount, in tertiary.</summary>
    public string AmountCap { get => _amountCap; private set => Set(ref _amountCap, value); }

    /// <summary>The bar's fill, 0…1.</summary>
    public double Bar { get => _bar; private set => Set(ref _bar, value); }

    /// <summary>The pace mark 0…1, NaN while unknown.</summary>
    public double Pace { get => _pace; private set => Set(ref _pace, value); }

    public bool Passed { get => _passed; private set => Set(ref _passed, value); }

    public string ResetText { get => _reset; private set => Set(ref _reset, value); }

    /// <summary><c> · 2 小時前更新</c> in tertiary while the snapshot is over an hour old (without the separator after no reset).</summary>
    public string AgeText { get => _age; private set => Set(ref _age, value); }

    /// <summary>The tooltip's first line, the long reset (QUOTAS 7); empty while unknown or passed.</summary>
    public string TipReset { get => _tipReset; private set => Set(ref _tipReset, value); }

    /// <summary><c>quota.updated</c>.</summary>
    public string TipUpdated { get => _tipUpdated; private set => Set(ref _tipUpdated, value); }

    /// <summary>The age under it, shown only while the line has no room for it (<see cref="QuotaLinePanel.AgeShown"/>); empty without an age.</summary>
    public string TipAge { get => _tipAge; private set => Set(ref _tipAge, value); }

    /// <summary><c>quota.pace</c> while known; empty otherwise.</summary>
    public string TipPace { get => _tipPace; private set => Set(ref _tipPace, value); }

    /// <summary>The estimate's lines under a hairline, in secondary (DESIGN 4 <i>Tooltip</i>); empty without an estimate.</summary>
    public IReadOnlyList<string> Details
    {
        get => _details;
        private set
        {
            if (!_details.SequenceEqual(value))
            {
                _details = value;
                OnPropertyChanged();
            }
        }
    }

    /// <summary><c>quota.estimate.note</c>, last in tertiary; empty without an estimate.</summary>
    public string Note
    {
        get => _note;
        private set
        {
            if (Set(ref _note, value))
            {
                OnPropertyChanged(nameof(HasEstimate));
            }
        }
    }

    public bool HasEstimate => _note.Length > 0;

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
        PercentLead = line.Estimated ? QuotaEstimateText.Approx : string.Empty;
        AmountText = line.Amount;
        AmountCap = line.AmountCap;
        Bar = line.Bar;
        Pace = line.Pace ?? double.NaN;
        ResetText = line.ResetShort;
        TipReset = line.ResetLong;
        var age = line.Age.StartsWith(Separator, StringComparison.Ordinal) ? line.Age[Separator.Length..] : line.Age;
        AgeText = line.ResetShort.Length > 0 || age.Length == 0 ? line.Age : age;
        var lineBreak = line.Tooltip.IndexOf('\n', StringComparison.Ordinal);
        TipUpdated = lineBreak < 0 ? line.Tooltip : line.Tooltip[..lineBreak];
        TipPace = lineBreak < 0 ? string.Empty : line.Tooltip[(lineBreak + 1)..];
        TipAge = age;
        Details = line.Details;
        Note = line.Note;
        Critical = line.Tone == QuotaTone.Critical && !highContrast;
        Fill = highContrast ? SystemColors.WindowTextBrush : line.Tone switch
        {
            QuotaTone.Critical => CriticalBrush(),
            QuotaTone.Warning => dark ? AmberDark : AmberLight,
            _ => source,
        };
    }

    private const string Separator = " · ";

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
