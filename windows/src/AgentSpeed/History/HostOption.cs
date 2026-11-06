using System.Windows;
using System.Windows.Controls;
using AgentSpeed.Controls;

namespace AgentSpeed.History;

/// <summary>
/// One entry of the Usage History window's host picker (DESIGN 13): every host, this computer, or a remote host (removed
/// hosts under their last name), each with a monochrome symbol.
/// </summary>
internal sealed class HostOption(string? id, string glyph) : ObservableObject
{
    /// <summary>Every host's stacked layers, this computer, a server on the network.</summary>
    public const string AllGlyph = "";
    public const string LocalGlyph = "";
    public const string RemoteGlyph = "";

    private string _name = string.Empty;
    private bool _compact;

    /// <summary>Null for every host, else <c>local</c> or a remote host's id.</summary>
    public string? Id { get; } = id;

    public string Glyph { get; } = glyph;

    public string Name { get => _name; set => Set(ref _name, value); }

    /// <summary>The closed picker shows only the symbol (the toolbar is at its narrowest); the list keeps the names.</summary>
    public bool Compact { get => _compact; set => Set(ref _compact, value); }
}

/// <summary>
/// The pickers' templates: one for the closed box (which narrows to a symbol or dot with the toolbar) and one for the open
/// list. The box's presenter belongs to the ComboBox's own template; a list item's presenter to a ComboBoxItem's.
/// </summary>
internal sealed class PickerTemplateSelector : DataTemplateSelector
{
    public DataTemplate? BoxTemplate { get; set; }

    public DataTemplate? ItemTemplate { get; set; }

    public override DataTemplate? SelectTemplate(object item, DependencyObject container) =>
        container is ComboBox or FrameworkElement { TemplatedParent: ComboBox } ? BoxTemplate ?? ItemTemplate : ItemTemplate;
}
