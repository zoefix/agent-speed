using System.Windows.Media;
using AgentSpeed.Controls;
using AgentSpeed.Core;
using AgentSpeed.Core.Perf;
using AgentSpeed.Presentation.History.Perf;
using AgentSpeed.Presentation.Localization;
using AgentSpeed.Presentation.Perf;

namespace AgentSpeed.History.Perf;

/// <summary>One component cell of a row as the table shows it: the value, or a tertiary <c>–</c> with why.</summary>
internal sealed class PerfCellView : ObservableObject
{
    private string _text = string.Empty;
    private bool _present;
    private string _tooltip = string.Empty;
    private double _fill;
    private string _spoken = string.Empty;

    public PerfCellView(PerfComponentId id)
    {
        Id = id;
    }

    public PerfComponentId Id { get; }

    public string Text { get => _text; private set => Set(ref _text, value); }

    public bool Present { get => _present; private set => Set(ref _present, value); }

    public bool Absent => !Present;

    /// <summary>The component's meaning while present, else why it is absent.</summary>
    public string Tooltip { get => _tooltip; private set => Set(ref _tooltip, value); }

    /// <summary>The sub-score on the log scale, for the medium layout's bar.</summary>
    public double Fill { get => _fill; private set => Set(ref _fill, value); }

    /// <summary>The value, or the tooltip of a <c>–</c> (DESIGN 12: <c>–</c> speaks its tooltip).</summary>
    public string Spoken { get => _spoken; private set => Set(ref _spoken, value); }

    public void Apply(PerfHistoryCell cell)
    {
        Text = cell.Text;
        if (Present != cell.Present)
        {
            Present = cell.Present;
            OnPropertyChanged(nameof(Absent));
        }

        Tooltip = cell.Tooltip;
        Fill = cell.Fill ?? 0;
        Spoken = cell.Present ? cell.Text : cell.Tooltip;
    }
}

/// <summary>
/// One row of 性能表現 (DESIGN 12, <i>Table</i>) as the table and the narrow list bind it, kept per source and model so a
/// new range updates it in place and its values crossfade.
/// </summary>
internal sealed class PerfRowView : ObservableObject
{
    private string _modelText = string.Empty;
    private bool _isUnknown;
    private string _sourceName = string.Empty;
    private bool _hasScore;
    private string _scoreText = string.Empty;
    private string _scoreTooltip = string.Empty;
    private double _scoreFill;
    private string _usualText = string.Empty;
    private string _usualTooltip = string.Empty;
    private bool _hasUsual;
    private string _timedText = string.Empty;
    private string _sampleLow = string.Empty;
    private string _partsSpoken = string.Empty;
    private string _line2 = string.Empty;
    private string _presentLine = string.Empty;
    private string _spoken = string.Empty;
    private string _copyText = string.Empty;
    private IReadOnlyList<PerfHistoryCell> _parts = [];

    public PerfRowView(SourceKind source, string model)
    {
        Source = source;
        Model = model;
        Cells = [.. PerfText.CardOrder.Select(id => new PerfCellView(id))];
        SourceColors = [PerfHistoryAppearance.SourceColor(source)];
    }

    public SourceKind Source { get; }

    public string Model { get; }

    public SourceKindModel Key => new(Source, Model);

    /// <summary>The row's source dot.</summary>
    public IReadOnlyList<Color> SourceColors { get; }

    public string ModelText { get => _modelText; private set => Set(ref _modelText, value); }

    /// <summary>The model is unknown (<c>cost.noModelName</c>): secondary italic.</summary>
    public bool IsUnknown { get => _isUnknown; private set => Set(ref _isUnknown, value); }

    public string SourceName { get => _sourceName; private set => Set(ref _sourceName, value); }

    public bool HasScore
    {
        get => _hasScore;
        private set
        {
            if (Set(ref _hasScore, value))
            {
                OnPropertyChanged(nameof(LowSample));
            }
        }
    }

    /// <summary>No score over the range: the values read in secondary, and the chip follows the id.</summary>
    public bool LowSample => !HasScore;

    public string ScoreText { get => _scoreText; private set => Set(ref _scoreText, value); }

    public string ScoreTooltip { get => _scoreTooltip; private set => Set(ref _scoreTooltip, value); }

    public double ScoreFill { get => _scoreFill; private set => Set(ref _scoreFill, value); }

    public string UsualText { get => _usualText; private set => Set(ref _usualText, value); }

    public string UsualTooltip { get => _usualTooltip; private set => Set(ref _usualTooltip, value); }

    public bool HasUsual
    {
        get => _hasUsual;
        private set
        {
            if (Set(ref _hasUsual, value))
            {
                OnPropertyChanged(nameof(NoUsual));
            }
        }
    }

    public bool NoUsual => !HasUsual;

    public string TimedText { get => _timedText; private set => Set(ref _timedText, value); }

    /// <summary><c>history.perf.sample.low</c>.</summary>
    public string SampleLow { get => _sampleLow; private set => Set(ref _sampleLow, value); }

    public IReadOnlyList<PerfCellView> Cells { get; }

    /// <summary>The medium layout's bars: every component's cell.</summary>
    public IReadOnlyList<PerfHistoryCell> Parts { get => _parts; private set => Set(ref _parts, value); }

    public string PartsSpoken { get => _partsSpoken; private set => Set(ref _partsSpoken, value); }

    /// <summary>The narrow list's line 2: the source · the timed requests · the usual value.</summary>
    public string Line2 { get => _line2; private set => Set(ref _line2, value); }

    /// <summary>The narrow list's line 3: the present components as <c>label value</c>.</summary>
    public string PresentLine { get => _presentLine; private set => Set(ref _presentLine, value); }

    /// <summary>The row as one element for screen readers: every column's words.</summary>
    public string Spoken { get => _spoken; private set => Set(ref _spoken, value); }

    /// <summary>The row as text (copy row): every column, tab separated.</summary>
    public string CopyText { get => _copyText; private set => Set(ref _copyText, value); }

    public PerfCellView Cell(PerfComponentId id) => Cells.First(c => c.Id == id);

    public void Apply(PerfHistoryRow row, Localizer loc)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(loc);
        ModelText = row.ModelText;
        IsUnknown = row.Model.Length == 0;
        SourceName = row.SourceName;
        HasScore = row.HasScore;
        ScoreText = row.ScoreText;
        ScoreTooltip = row.ScoreTooltip;
        ScoreFill = row.ScoreFill;
        UsualText = row.UsualText;
        UsualTooltip = row.UsualTooltip;
        HasUsual = row.Usual is not null;
        TimedText = row.TimedText;
        SampleLow = loc["history.perf.sample.low"];
        for (var i = 0; i < Cells.Count && i < row.Cells.Count; i++)
        {
            Cells[i].Apply(row.Cells[i]);
        }

        Parts = row.Cells;
        PartsSpoken = row.PartsSpoken;
        PresentLine = row.PresentLine;
        var requests = loc.Format("history.perf.requests", row.Timed);
        Line2 = string.Join(" · ", row.SourceName, requests, loc["history.perf.column.usual"] + " " + row.UsualText);

        var columns = new List<string>
        {
            row.ModelText + " · " + row.SourceName,
            loc["history.perf.column.score"] + " " + (row.HasScore ? row.ScoreText : row.ScoreTooltip),
            loc["history.perf.column.usual"] + " " + (row.Usual is null ? row.UsualTooltip : row.UsualText),
            loc["history.perf.column.timed"] + " " + row.TimedText,
        };
        columns.AddRange(row.Cells.Select(c => loc[PerfText.ColumnKey(c.Id)] + " " + (c.Present ? c.Text : c.Tooltip)));
        Spoken = string.Join(loc["list.separator"], columns);
        CopyText = string.Join('\t', new[] { row.ModelText, row.SourceName, row.ScoreText, row.UsualText, row.TimedText }
            .Concat(row.Cells.Select(c => c.Text)));
    }
}
