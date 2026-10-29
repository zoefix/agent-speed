using System.Collections.ObjectModel;
using AgentSpeed.Controls;
using AgentSpeed.Core;
using AgentSpeed.Core.Perf;
using AgentSpeed.Presentation.History.Perf;
using AgentSpeed.Presentation.Localization;
using AgentSpeed.Presentation.Perf;

namespace AgentSpeed.History.Perf;

/// <summary>A term of the ⓘ popover's definition list: a component's name and what it measures.</summary>
internal sealed record PerfInfoTerm(string Term, string Hint);

/// <summary>
/// A table header: the column it sorts (null: the medium layout's 構成, which does not sort), its short name, its full name
/// (tooltip and accessible name) and what the column measures.
/// </summary>
internal sealed record PerfHeader(PerfSortColumn? Column, string Text, string FullName, string? Hint, bool Numeric)
{
    /// <summary>The medium layout's folded components column.</summary>
    public bool IsParts => Column is null;
}

/// <summary>
/// State of the Usage History window's 性能表現 section (DESIGN 12, <i>Usage History: performance</i>): the range's
/// performance records pooled per source and model (METRICS 6.11), the selected row's daily trend, the remembered sort and
/// the layout for the section's width. Rows are kept per source and model and updated in place, so values crossfade and
/// sorted rows move.
/// </summary>
internal sealed class PerfHistoryViewModel : ObservableObject, IDisposable
{
    private Localizer _loc;
    private IReadOnlyList<PerfRecord> _records = [];
    private DateOnly _first = DateOnly.FromDateTime(DateTime.Today);
    private DateOnly _last = DateOnly.FromDateTime(DateTime.Today);
    private DateOnly _today = DateOnly.FromDateTime(DateTime.Today);
    private SourceKind? _source;
    private bool _analyzing;
    private PerfSort _sort = PerfSort.Default;
    private Action<PerfSort>? _sortChanged;
    private SourceKindModel? _chosen;
    private double _contentWidth = double.PositiveInfinity;
    private PerfHistoryState _state = PerfHistoryState.Empty;
    private PerfHistoryWidth _width = PerfHistoryWidth.Wide;
    private PerfHistoryChart? _chart;
    private PerfRowView? _selected;
    private string _title = string.Empty;
    private string _info = string.Empty;
    private string _infoLabel = string.Empty;
    private IReadOnlyList<PerfInfoTerm> _infoTerms = [];
    private string _trendTitle = string.Empty;
    private string _referenceLegend = string.Empty;
    private string _usualLegend = string.Empty;
    private string _footnote = string.Empty;
    private string _analyzingText = string.Empty;
    private string _emptyText = string.Empty;
    private string _emptyHint = string.Empty;
    private string _copyRow = string.Empty;
    private string _chartSpoken = string.Empty;
    private string _chartEmpty = string.Empty;
    private IReadOnlyList<PerfHeader> _headers = [];
    private bool _dark;
    private bool _highContrast;
    private bool _reduceMotion;
    private int _appearanceGeneration;
    private bool _disposed;

    public PerfHistoryViewModel(Localizer loc)
    {
        ArgumentNullException.ThrowIfNull(loc);
        _loc = loc;
        _loc.LanguageChanged += OnLanguageChanged;
        Rebuild();
    }

    /// <summary>After every rebuild, once the rows hold the new words.</summary>
    public event EventHandler? Rebuilt;

    public Localizer Loc => _loc;

    public DateOnly Today => _today;

    public PerfHistoryState State
    {
        get => _state;
        private set
        {
            if (Set(ref _state, value))
            {
                RaiseShown();
            }
        }
    }

    /// <summary>The layout for the section's content width: wide from 860, medium from 600, narrow below.</summary>
    public PerfHistoryWidth Width
    {
        get => _width;
        private set
        {
            if (Set(ref _width, value))
            {
                OnPropertyChanged(nameof(ChartHeight));
                RaiseShown();
            }
        }
    }

    public double ChartHeight => PerfHistoryLayout.ChartHeight(Width);

    public bool ShowsAnalyzing => State == PerfHistoryState.Analyzing;

    public bool ShowsEmpty => State == PerfHistoryState.Empty;

    public bool ShowsData => State == PerfHistoryState.Rows;

    public bool ShowsTable => ShowsData && Width != PerfHistoryWidth.Narrow;

    public bool ShowsList => ShowsData && Width == PerfHistoryWidth.Narrow;

    /// <summary>The medium layout folds the seven components into one 構成 column.</summary>
    public bool ShowsParts => Width == PerfHistoryWidth.Medium;

    public PerfHistoryChart? Chart { get => _chart; private set => Set(ref _chart, value); }

    /// <summary>The row whose trend the chart shows.</summary>
    public PerfRowView? Selected { get => _selected; private set => Set(ref _selected, value); }

    public ObservableCollection<PerfRowView> Rows { get; } = [];

    public IReadOnlyList<PerfHeader> Headers { get => _headers; private set => Set(ref _headers, value); }

    public PerfSortColumn SortColumn => _sort.Column;

    public bool SortDescending => _sort.Descending;

    public string Title { get => _title; private set => Set(ref _title, value); }

    public string Info { get => _info; private set => Set(ref _info, value); }

    /// <summary>The ⓘ button's name and tooltip (<c>history.perf.info.a11y</c>).</summary>
    public string InfoLabel { get => _infoLabel; private set => Set(ref _infoLabel, value); }

    public IReadOnlyList<PerfInfoTerm> InfoTerms { get => _infoTerms; private set => Set(ref _infoTerms, value); }

    public string TrendTitle { get => _trendTitle; private set => Set(ref _trendTitle, value); }

    /// <summary>The legend's reference sample: <c>1000</c> and <c>perf.reference</c>.</summary>
    public string ReferenceLegend { get => _referenceLegend; private set => Set(ref _referenceLegend, value); }

    /// <summary>The legend's dashed sample (<c>history.perf.usual.line</c>); empty while the usual line does not show.</summary>
    public string UsualLegend
    {
        get => _usualLegend;
        private set
        {
            if (Set(ref _usualLegend, value))
            {
                OnPropertyChanged(nameof(ShowsUsualLegend));
            }
        }
    }

    public bool ShowsUsualLegend => UsualLegend.Length > 0;

    public string ChartSpoken { get => _chartSpoken; private set => Set(ref _chartSpoken, value); }

    /// <summary>What the chart's area holds without a day with enough data.</summary>
    public string ChartEmpty { get => _chartEmpty; private set => Set(ref _chartEmpty, value); }

    public string Footnote { get => _footnote; private set => Set(ref _footnote, value); }

    public string AnalyzingText { get => _analyzingText; private set => Set(ref _analyzingText, value); }

    public string EmptyText { get => _emptyText; private set => Set(ref _emptyText, value); }

    public string EmptyHint { get => _emptyHint; private set => Set(ref _emptyHint, value); }

    /// <summary><c>win.history.copyRow</c>, the rows' context menu.</summary>
    public string CopyRow { get => _copyRow; private set => Set(ref _copyRow, value); }

    public bool Dark { get => _dark; private set => Set(ref _dark, value); }

    public bool HighContrast { get => _highContrast; private set => Set(ref _highContrast, value); }

    public bool ReduceMotion { get => _reduceMotion; private set => Set(ref _reduceMotion, value); }

    /// <summary>Counts theme notifications; the section redraws on each, for system colors that changed alone.</summary>
    public int AppearanceGeneration { get => _appearanceGeneration; private set => Set(ref _appearanceGeneration, value); }

    /// <summary>The range's records and the window's choices; call after every recomputation of the window.</summary>
    /// <param name="source">The window's source picker; null for all sources.</param>
    /// <param name="analyzing">The history is being re-analysed (<c>history.analyzing</c>).</param>
    public void Update(IReadOnlyList<PerfRecord> records, DateOnly first, DateOnly last, DateOnly today, SourceKind? source, bool analyzing)
    {
        ArgumentNullException.ThrowIfNull(records);
        _records = records;
        _first = first;
        _last = last;
        _today = today;
        _source = source;
        _analyzing = analyzing;
        Rebuild();
    }

    /// <param name="sort">The sort the window remembers.</param>
    /// <param name="changed">Persists a new sort.</param>
    public void RememberSort(PerfSort? sort, Action<PerfSort>? changed)
    {
        _sort = sort ?? PerfSort.Default;
        _sortChanged = changed;
        Rebuild();
    }

    public void SetAppearance(bool dark, bool highContrast, bool reduceMotion)
    {
        Dark = dark;
        HighContrast = highContrast;
        ReduceMotion = reduceMotion;
        AppearanceGeneration++;
    }

    /// <summary>Re-reads every word in <paramref name="loc"/> (the language changed).</summary>
    public void Relocalize(Localizer loc)
    {
        ArgumentNullException.ThrowIfNull(loc);
        if (!ReferenceEquals(loc, _loc))
        {
            _loc.LanguageChanged -= OnLanguageChanged;
            _loc = loc;
            _loc.LanguageChanged += OnLanguageChanged;
        }

        Rebuild();
    }

    /// <summary>The section's content width; a new layout rebuilds the headers.</summary>
    public void SetContentWidth(double width)
    {
        if (!double.IsFinite(width) || width <= 0)
        {
            return;
        }

        var before = PerfHistoryLayout.WidthOf(_contentWidth);
        _contentWidth = width;
        if (PerfHistoryLayout.WidthOf(width) != before)
        {
            Rebuild();
        }
    }

    /// <summary>A header click: the sorted column flips its direction, another column starts in its first direction.</summary>
    public void SortBy(PerfSortColumn column)
    {
        _sort = _sort.Toggle(column);
        _sortChanged?.Invoke(_sort);
        Rebuild();
    }

    /// <summary>A row was selected (click or arrow keys): the chart shows its trend, kept while the window is open.</summary>
    public void Select(PerfRowView? row)
    {
        if (row is null || Equals(row.Key, Selected?.Key))
        {
            return;
        }

        _chosen = row.Key;
        Rebuild();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _loc.LanguageChanged -= OnLanguageChanged;
    }

    private void OnLanguageChanged(object? sender, EventArgs e) => Rebuild();

    private void Rebuild()
    {
        var section = PerfHistorySection.Build(_records, _first, _last, _source, _today, _analyzing, _sort, _chosen, _loc);
        Title = _loc["history.perf.title"];
        Info = _loc["history.perf.info"];
        InfoLabel = _loc["history.perf.info.a11y"];
        var terms = PerfText.CardOrder.Select(id => new PerfInfoTerm(_loc[PerfText.ComponentKey(id)], _loc[PerfText.ComponentKey(id) + ".desc"])).ToList();
        if (!InfoTerms.SequenceEqual(terms))
        {
            InfoTerms = terms;
        }

        Footnote = _loc["history.perf.footnote"];
        AnalyzingText = _loc["history.perf.analyzing"];
        EmptyText = _loc["history.perf.empty"];
        EmptyHint = _loc["history.perf.empty.hint"];
        CopyRow = _loc["win.history.copyRow"];
        ReferenceLegend = "1000 " + _loc["perf.reference"];

        var width = PerfHistoryLayout.WidthOf(_contentWidth);
        var headers = HeadersFor(width);
        if (!Headers.SequenceEqual(headers))
        {
            Headers = headers;
        }

        OnPropertyChanged(nameof(SortColumn));
        OnPropertyChanged(nameof(SortDescending));
        ApplyRows(section.Rows);

        Selected = section.Selected is { } chosen ? Rows.FirstOrDefault(r => r.Source == chosen.Source && r.Model == chosen.Model) : null;
        Chart = section.Chart;
        TrendTitle = section.Chart?.Title ?? string.Empty;
        ChartSpoken = section.Chart?.Spoken ?? string.Empty;
        ChartEmpty = section.Selected is { } empty
            ? _loc.Format("a11y.history.perf.chart.empty", empty.ModelText + " · " + empty.SourceName)
            : string.Empty;
        UsualLegend = section.Chart?.Usual is { } usual ? _loc.Format("history.perf.usual.line", PerfNumber.Full(PerfFormula.Round(usual))) : string.Empty;

        Width = width;
        State = section.State;
        OnPropertyChanged(nameof(ShowsParts));
        Rebuilt?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Wide: every column; medium: the components folded into 構成; narrow: a list, no headers.</summary>
    private IReadOnlyList<PerfHeader> HeadersFor(PerfHistoryWidth width)
    {
        var headers = new List<PerfHeader>
        {
            new(PerfSortColumn.Model, _loc["history.perf.column.model"], _loc["history.perf.column.model"], null, false),
            new(PerfSortColumn.Score, _loc["history.perf.column.score"], _loc["history.perf.column.score"], null, true),
            new(PerfSortColumn.Usual, _loc["history.perf.column.usual"], _loc["history.perf.column.usual"], _loc["history.perf.usual.hint"], true),
            new(PerfSortColumn.Timed, _loc["history.perf.column.timed"], _loc["history.perf.column.timed.full"], null, true),
        };
        if (width == PerfHistoryWidth.Wide)
        {
            foreach (var id in PerfText.CardOrder)
            {
                var key = PerfText.ComponentKey(id);
                headers.Add(new PerfHeader(PerfSort.ColumnOf(id), _loc[PerfText.ColumnKey(id)], _loc[key], _loc[key + ".desc"], true));
            }
        }
        else if (width == PerfHistoryWidth.Medium)
        {
            headers.Add(new PerfHeader(null, _loc["history.perf.column.parts"], _loc["history.perf.column.parts"], null, false));
        }

        return headers;
    }

    /// <summary>Matches rows by source and model and moves them into the new order.</summary>
    private void ApplyRows(IReadOnlyList<PerfHistoryRow> rows)
    {
        var existing = Rows.ToDictionary(r => r.Key);
        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            var key = new SourceKindModel(row.Source, row.Model);
            if (!existing.Remove(key, out var view))
            {
                view = new PerfRowView(row.Source, row.Model);
                Rows.Insert(Math.Min(i, Rows.Count), view);
            }
            else if (Rows.IndexOf(view) is var at && at != i)
            {
                Rows.Move(at, i);
            }

            view.Apply(row, _loc);
        }

        foreach (var stale in existing.Values)
        {
            Rows.Remove(stale);
        }
    }

    private void RaiseShown()
    {
        OnPropertyChanged(nameof(ShowsAnalyzing));
        OnPropertyChanged(nameof(ShowsEmpty));
        OnPropertyChanged(nameof(ShowsData));
        OnPropertyChanged(nameof(ShowsTable));
        OnPropertyChanged(nameof(ShowsList));
        OnPropertyChanged(nameof(ShowsParts));
    }
}
