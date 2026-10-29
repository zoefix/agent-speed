using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using AgentSpeed.Presentation.History.Perf;
using AgentSpeed.Presentation.Perf;
using AgentSpeed.Theme;

namespace AgentSpeed.History.Perf;

/// <summary>
/// The 性能表現 table and narrow list; its data context is the <see cref="PerfHistoryViewModel"/>. Builds the table's
/// columns from <see cref="PerfHistoryViewModel.Headers"/>, keeps the sort glyph and its words current, fits the columns to
/// the width, keeps the selection (the chart's series) in step, and copies the selected row as text (context menu and Ctrl+C).
/// </summary>
internal sealed partial class PerfTableView : UserControl
{
    private const string SortDescendingGlyph = "";
    private const string SortAscendingGlyph = "";
    private const double CellSize = PerfHistoryLayout.CellSize;
    private const double HeaderSize = 12;

    private readonly List<(PerfHeader Header, GridViewColumn Column, GridViewColumnHeader Button, TextBlock Label, TextBlock Glyph)> _columns = [];
    private PerfHistoryViewModel? _model;
    private IReadOnlyList<PerfHeader> _built = [];
    private bool _fitQueued;
    private bool _syncing;

    public PerfTableView()
    {
        InitializeComponent();
        DataContextChanged += (_, e) => Attach(e.NewValue as PerfHistoryViewModel);
        // Fitted inside the same layout pass, so a new width never shows a frame with the old columns.
        Table.SizeChanged += (_, e) =>
        {
            if (Math.Abs(e.NewSize.Width - e.PreviousSize.Width) > 0.5)
            {
                Fit();
            }
        };
        foreach (var list in new ListBox[] { Table, List })
        {
            list.PreviewKeyDown += OnListKeyDown;
            list.ContextMenu = CopyMenu(list);
            list.SelectionChanged += (_, _) =>
            {
                if (!_syncing && list.SelectedItem is PerfRowView row)
                {
                    _model?.Select(row);
                }
            };
        }
    }

    private void Attach(PerfHistoryViewModel? model)
    {
        if (_model is not null)
        {
            _model.Rebuilt -= OnRebuilt;
            _model.PropertyChanged -= OnModelChanged;
        }

        _model = model;
        if (model is not null)
        {
            model.Rebuilt += OnRebuilt;
            model.PropertyChanged += OnModelChanged;
            OnRebuilt(model, EventArgs.Empty);
        }
    }

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PerfHistoryViewModel.ShowsTable) && _model is { ShowsTable: true })
        {
            QueueFit();
        }
        else if (e.PropertyName == nameof(PerfHistoryViewModel.Selected))
        {
            SyncSelection();
        }
    }

    private void OnRebuilt(object? sender, EventArgs e)
    {
        if (_model is not { } model)
        {
            return;
        }

        if (!_built.SequenceEqual(model.Headers))
        {
            BuildColumns(model.Headers);
        }

        UpdateHeaders(model);
        SyncSelection();
        if (Table.Visibility == Visibility.Visible && Table.ActualWidth > 0)
        {
            Fit();
        }
        else
        {
            QueueFit();
        }
    }

    // ---- Columns ----

    private void BuildColumns(IReadOnlyList<PerfHeader> headers)
    {
        _built = headers;
        _columns.Clear();
        TableView.Columns.Clear();
        var style = (Style)FindResource("HeaderStyle");
        foreach (var header in headers)
        {
            var label = new TextBlock
            {
                TextWrapping = TextWrapping.WrapWithOverflow,
                TextAlignment = header.Numeric ? TextAlignment.Right : TextAlignment.Left,
                LineHeight = PerfTableColumns.HeaderLineHeight,
                LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
                VerticalAlignment = VerticalAlignment.Bottom,
            };
            var glyph = new TextBlock
            {
                FontSize = 9,
                Width = PerfTableColumns.GlyphRoom,
                TextAlignment = header.Numeric ? TextAlignment.Left : TextAlignment.Right,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, 0, 3),
                Visibility = Visibility.Hidden,
            };
            glyph.SetResourceReference(TextBlock.FontFamilyProperty, AppFonts.SymbolFontKey);
            var dock = new DockPanel { LastChildFill = true };
            DockPanel.SetDock(glyph, header.Numeric ? Dock.Left : Dock.Right);
            dock.Children.Add(glyph);
            dock.Children.Add(label);
            var button = new GridViewColumnHeader
            {
                Style = style,
                Content = dock,
                HorizontalContentAlignment = header.Numeric ? HorizontalAlignment.Right : HorizontalAlignment.Stretch,
            };
            if (header.Column is { } column)
            {
                button.Click += (_, _) => _model?.SortBy(column);
            }
            else
            {
                button.Cursor = null;
                button.Focusable = false;
                button.IsHitTestVisible = false;
            }

            var gridColumn = new GridViewColumn { Header = button, CellTemplate = (DataTemplate)FindResource(CellKey(header)) };
            TableView.Columns.Add(gridColumn);
            _columns.Add((header, gridColumn, button, label, glyph));
        }
    }

    private static string CellKey(PerfHeader header) => header.Column switch
    {
        null => "PartsCell",
        PerfSortColumn.Model => "ModelCell",
        PerfSortColumn.Score => "ScoreCell",
        PerfSortColumn.Usual => "UsualCell",
        PerfSortColumn.Timed => "TimedCell",
        PerfSortColumn.Throughput => "ThroughputCell",
        PerfSortColumn.Latency => "LatencyCell",
        PerfSortColumn.Reasoning => "ReasoningCell",
        PerfSortColumn.Pace => "PaceCell",
        PerfSortColumn.ToolTime => "ToolTimeCell",
        PerfSortColumn.ToolSuccess => "ToolSuccessCell",
        _ => "CacheCell",
    };

    /// <summary>
    /// Short names with the full name as tooltip and accessible name (a component's also says what it measures); the sorted
    /// column in the label color with its glyph.
    /// </summary>
    private void UpdateHeaders(PerfHistoryViewModel model)
    {
        var loc = model.Loc;
        foreach (var (header, _, button, label, glyph) in _columns)
        {
            label.Text = header.Text;
            var active = header.Column == model.SortColumn && model.Rows.Count > 0;
            glyph.Text = model.SortDescending ? SortDescendingGlyph : SortAscendingGlyph;
            glyph.Visibility = active ? Visibility.Visible : Visibility.Hidden;
            if (active)
            {
                button.SetResourceReference(ForegroundProperty, "TextFillColorPrimaryBrush");
            }
            else
            {
                button.ClearValue(ForegroundProperty);
            }

            button.ToolTip = header.Hint is { } hint ? header.FullName + "\n" + hint : header.FullName;
            AutomationProperties.SetName(button, header.FullName);
            AutomationProperties.SetHelpText(button,
                active ? loc[model.SortDescending ? "win.history.sorted.desc" : "win.history.sorted.asc"] : string.Empty);
        }
    }

    // ---- Widths ----

    private void QueueFit()
    {
        if (_fitQueued)
        {
            return;
        }

        _fitQueued = true;
        Dispatcher.BeginInvoke(() =>
        {
            _fitQueued = false;
            Fit();
        }, DispatcherPriority.Loaded);
    }

    private void Fit()
    {
        var available = Table.ActualWidth;
        if (_model is not { } model || _columns.Count == 0 || available <= 0 || Table.Visibility != Visibility.Visible)
        {
            return;
        }

        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var family = Table.FontFamily;
        var cell = new Typeface(family, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        var heading = new Typeface(family, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
        var rows = model.Rows;
        var needs = new List<PerfColumnNeed>();
        var modelWant = PerfTableColumns.TextWidth(_columns[0].Header.Text, heading, HeaderSize, dpi) + PerfTableColumns.GlyphRoom;
        var idFace = new Typeface(family, FontStyles.Normal, FontWeights.Medium, FontStretches.Normal);
        foreach (var row in rows)
        {
            var id = PerfTableColumns.TextWidth(row.ModelText, idFace, PerfHistoryLayout.ModelSize, dpi);
            var source = PerfTableColumns.TextWidth(row.SourceName, cell, PerfHistoryLayout.SourceSize, dpi) + 7;
            var chip = row.LowSample ? PerfTableColumns.TextWidth(row.SampleLow, idFace, 10, dpi) + 10 + 7 : 0;
            modelWant = Math.Max(modelWant, PerfHistoryLayout.SourceDot + 7 + id + chip + source);
        }

        var scoreFace = new Typeface(family, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
        foreach (var (header, _, _, _, _) in _columns.Skip(1))
        {
            var content = 0.0;
            foreach (var row in rows)
            {
                var width = header.Column switch
                {
                    null => 7 * PerfHistoryLayout.PartWidth + 6 * PerfHistoryLayout.PartGap,
                    PerfSortColumn.Score => PerfTableColumns.TextWidth(PerfNumber.WidestFull, scoreFace, PerfHistoryLayout.ScoreSize, dpi) + 8
                        + PerfHistoryLayout.MeterWidth,
                    _ => PerfTableColumns.TextWidth(CellText(row, header), cell, CellSize, dpi),
                };
                content = Math.Max(content, width);
            }

            needs.Add(new PerfColumnNeed(content,
                PerfTableColumns.TextWidth(header.Text, heading, HeaderSize, dpi),
                PerfTableColumns.TextWidth(header.Text, heading, HeaderSize, dpi, lines: 2),
                PerfTableColumns.TextWidth(header.Text, heading, HeaderSize, dpi, lines: 3)));
        }

        var widths = PerfTableColumns.Fit(Math.Floor(available), modelWant + 2 * PerfTableColumns.CellPadding, needs,
            model.Width == PerfHistoryWidth.Wide);
        for (var i = 0; i < _columns.Count && i < widths.Length; i++)
        {
            if (Math.Abs(_columns[i].Column.Width - widths[i]) > 0.5 || double.IsNaN(_columns[i].Column.Width))
            {
                _columns[i].Column.Width = widths[i];
            }
        }
    }

    private static string CellText(PerfRowView row, PerfHeader header) => header.Column switch
    {
        PerfSortColumn.Usual => row.UsualText,
        PerfSortColumn.Timed => row.TimedText,
        { } column when column >= PerfSortColumn.Throughput => row.Cell(PerfSort.Component(column)).Text,
        _ => string.Empty,
    };

    // ---- Selection ----

    /// <summary>The selected row is the chart's series; both lists show it.</summary>
    private void SyncSelection()
    {
        if (_model is not { } model)
        {
            return;
        }

        _syncing = true;
        try
        {
            Table.SelectedItem = model.Selected;
            List.SelectedItem = model.Selected;
        }
        finally
        {
            _syncing = false;
        }
    }

    // ---- Copy ----

    private ContextMenu CopyMenu(ListBox list)
    {
        var item = new MenuItem { InputGestureText = "Ctrl+C" };
        item.SetBinding(HeaderedItemsControl.HeaderProperty, new System.Windows.Data.Binding("DataContext." + nameof(PerfHistoryViewModel.CopyRow)) { Source = this });
        item.Click += (_, _) => Copy(list.SelectedItem);
        var menu = new ContextMenu { Items = { item } };
        menu.SetResourceReference(FontFamilyProperty, AppFonts.UiFontKey);
        menu.Opened += (_, _) => item.IsEnabled = list.SelectedItem is PerfRowView;
        return menu;
    }

    private void OnListKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control && sender is ListBox list)
        {
            e.Handled = Copy(list.SelectedItem);
        }
    }

    private static bool Copy(object? item)
    {
        if (item is not PerfRowView row)
        {
            return false;
        }

        try
        {
            Clipboard.SetText(row.CopyText);
            return true;
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            return false;
        }
    }
}
