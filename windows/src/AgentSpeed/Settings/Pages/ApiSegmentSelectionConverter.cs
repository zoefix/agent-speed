using System.Globalization;
using System.Windows.Data;

namespace AgentSpeed.Settings.Pages;

/// <summary>Only selecting an API segment changes the model; group-driven unchecking never does.</summary>
internal sealed class ApiSegmentSelectionConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is bool lan && lan == bool.Parse((string)parameter);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? bool.Parse((string)parameter) : Binding.DoNothing;
}
