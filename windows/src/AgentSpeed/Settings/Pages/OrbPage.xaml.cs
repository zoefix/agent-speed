using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using AgentSpeed.Settings.OrbsPane;

namespace AgentSpeed.Settings.Pages;

internal partial class OrbPage : UserControl
{
    private Point _dragStart;
    private OrbRowViewModel? _dragRow;
    private OrbsPaneViewModel? _attached;

    public OrbPage()
    {
        InitializeComponent();
        IsVisibleChanged += (_, _) =>
        {
            Highlight(IsVisible ? Model?.SelectedRow : null);
            if (IsVisible)
            {
                Model?.CharacterSectionShown();
            }
        };
        OrbList.SelectionChanged += (_, _) => Highlight(Model?.SelectedRow);
        OrbList.GotKeyboardFocus += (_, _) => Highlight(Model?.SelectedRow);
        Loaded += (_, _) => AttachModel();
        DataContextChanged += (_, _) => AttachModel();
        Unloaded += (_, _) => DetachModel();
    }

    private OrbsPaneViewModel? Model => (DataContext as SettingsViewModel)?.Orbs;

    private System.Windows.Controls.Primitives.UniformGrid? _cardsPanel;

    private void AttachModel()
    {
        var model = Model;
        if (ReferenceEquals(model, _attached))
        {
            return;
        }

        if (_attached is not null)
        {
            _attached.PropertyChanged -= OnModelPropertyChanged;
        }

        _attached = model;
        if (model is not null)
        {
            model.PropertyChanged += OnModelPropertyChanged;
        }

        BuildThemeChoices();
    }

    /// <summary>The view model outlives the settings window.</summary>
    private void DetachModel()
    {
        if (_attached is not null)
        {
            _attached.PropertyChanged -= OnModelPropertyChanged;
            _attached = null;
        }
    }

    private void OnModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(OrbsPaneViewModel.TitleThemeChoices))
        {
            BuildThemeChoices();
        }
    }

    /// <summary>
    /// The Advanced <c>titles.theme</c> popup (General, presets, custom sets with separators between them). The items are
    /// built here so they keep the Fluent item style while the separators stay unselectable.
    /// </summary>
    private void BuildThemeChoices()
    {
        ThemeCombo.Items.Clear();
        foreach (var choice in _attached?.TitleThemeChoices ?? [])
        {
            ThemeCombo.Items.Add(choice.IsSeparator
                ? new ComboBoxItem
                {
                    IsEnabled = false,
                    Focusable = false,
                    IsHitTestVisible = false,
                    MinHeight = 0,
                    Padding = new Thickness(0),
                    HorizontalContentAlignment = HorizontalAlignment.Stretch,
                    Content = new Separator { Margin = new Thickness(0, 3, 0, 3) },
                }
                : new ComboBoxItem { Content = choice.Text });
        }

        // Clearing the items dropped the selection, and an index bound while the list was empty stays coerced to -1 even when
        // the same index arrives again: set it and coerce it anew (the binding stays in place).
        if (_attached is { } model)
        {
            ThemeCombo.SetCurrentValue(Selector.SelectedIndexProperty, model.TitleThemeIndex);
            ThemeCombo.CoerceValue(Selector.SelectedIndexProperty);
        }
    }

    private void OnCardsPanelLoaded(object sender, RoutedEventArgs e)
    {
        _cardsPanel = sender as System.Windows.Controls.Primitives.UniformGrid;
        UpdateCardColumns();
    }

    /// <summary>Six cards in one row; 3 + 3 below 560 pt of width (DESIGN 5).</summary>
    private void OnCardsSizeChanged(object sender, SizeChangedEventArgs e) => UpdateCardColumns();

    private void UpdateCardColumns()
    {
        if (_cardsPanel is { } panel)
        {
            var columns = StyleCards.ActualWidth >= 560 ? 6 : 3;
            if (panel.Columns != columns)
            {
                panel.Columns = columns;
            }
        }
    }

    private void Highlight(OrbRowViewModel? row) => Model?.Highlight(IsVisible ? row?.Id : null);

    private void OnRowEnter(object sender, MouseEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: OrbRowViewModel row })
        {
            Highlight(row);
        }
    }

    private void OnListMouseLeave(object sender, MouseEventArgs e) => Highlight(Model?.SelectedRow);

    private void OnEyeClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: OrbRowViewModel row })
        {
            Model?.ToggleVisibility(row);
        }

        e.Handled = true;
    }

    private void OnSourceClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: OrbSourceRowViewModel row })
        {
            Model?.ToggleSource(row);
        }
    }

    /// <summary>Space toggles visibility, Ctrl+D duplicates, Delete removes (the removal can be undone).</summary>
    private void OnListKeyDown(object sender, KeyEventArgs e)
    {
        if (Model is not { } model || model.SelectedRow is not { } row)
        {
            return;
        }

        if (e.Key == Key.Space)
        {
            model.ToggleVisibility(row);
            e.Handled = true;
        }
        else if (e.Key == Key.D && Keyboard.Modifiers == ModifierKeys.Control)
        {
            if (model.DuplicateCommand.CanExecute(null))
            {
                model.DuplicateCommand.Execute(null);
            }

            e.Handled = true;
        }
        else if (e.Key is Key.Delete or Key.Back)
        {
            model.RemoveSelected();
            e.Handled = true;
        }
    }

    private void OnListMouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(OrbList);
        _dragRow = (e.OriginalSource as DependencyObject) is { } source && ItemsControl.ContainerFromElement(OrbList, source) is ListBoxItem item
            ? item.DataContext as OrbRowViewModel
            : null;
    }

    /// <summary>Rows reorder by drag (list order only).</summary>
    private void OnListMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _dragRow is null || e.OriginalSource is ButtonBase)
        {
            return;
        }

        var position = e.GetPosition(OrbList);
        if (Math.Abs(position.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        var row = _dragRow;
        _dragRow = null;
        DragDrop.DoDragDrop(OrbList, new DataObject(typeof(OrbRowViewModel), row), DragDropEffects.Move);
    }

    private void OnListDrop(object sender, DragEventArgs e)
    {
        if (Model is not { } model || e.Data.GetData(typeof(OrbRowViewModel)) is not OrbRowViewModel row)
        {
            return;
        }

        var target = (e.OriginalSource as DependencyObject) is { } source && ItemsControl.ContainerFromElement(OrbList, source) is ListBoxItem item
            ? item.DataContext as OrbRowViewModel
            : null;
        var index = target is null ? model.Rows.Count - 1 : model.Rows.IndexOf(target);
        model.Move(row.Id, index);
        model.SelectedRow = model.Rows.FirstOrDefault(r => r.Id == row.Id);
        _ = VisualTreeHelper.GetParent(this);
    }
}
