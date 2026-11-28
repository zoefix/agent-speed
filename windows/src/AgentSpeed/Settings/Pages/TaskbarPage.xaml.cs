using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;

namespace AgentSpeed.Settings.Pages;

internal sealed partial class TaskbarPage : UserControl
{
    private BarQuotaViewModel? _attached;

    public TaskbarPage()
    {
        InitializeComponent();
        Loaded += (_, _) => BuildQuotaChoices();
        DataContextChanged += (_, _) => BuildQuotaChoices();
    }

    /// <summary>
    /// The <c>settings.bar.quota</c> popup's items (DESIGN 10, <i>Quota control</i>), built here so they keep the Fluent item style
    /// while each binds its name and whether its source's quota is read; the list itself never changes.
    /// </summary>
    private void BuildQuotaChoices()
    {
        var model = (DataContext as SettingsViewModel)?.BarQuota;
        if (ReferenceEquals(model, _attached))
        {
            return;
        }

        _attached = model;
        QuotaCombo.Items.Clear();
        foreach (var choice in model?.Choices ?? [])
        {
            var item = new ComboBoxItem { DataContext = choice };
            item.SetBinding(ContentControl.ContentProperty, new Binding(nameof(BarQuotaChoice.Name)));
            item.SetBinding(UIElement.IsEnabledProperty, new Binding(nameof(BarQuotaChoice.IsEnabled)));
            QuotaCombo.Items.Add(item);
        }

        // An index bound while the list was empty stays coerced to -1: set it and coerce it anew (the binding stays in place).
        if (model is not null)
        {
            QuotaCombo.SetCurrentValue(Selector.SelectedIndexProperty, model.Index);
            QuotaCombo.CoerceValue(Selector.SelectedIndexProperty);
        }
    }
}
