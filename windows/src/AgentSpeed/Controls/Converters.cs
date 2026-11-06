using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace AgentSpeed.Controls;

[ValueConversion(typeof(bool), typeof(Visibility))]
internal sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is Visibility.Collapsed;
}

/// <summary>Collapses an element whose bound text is null or empty; with the parameter <c>invert</c>, one whose text is not.</summary>
[ValueConversion(typeof(string), typeof(Visibility))]
internal sealed class TextToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        (value is string { Length: > 0 }) != (parameter is "invert") ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

[ValueConversion(typeof(bool), typeof(double))]
internal sealed class DimOpacityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? 0.5 : 1.0;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

/// <summary>An enabled value at full opacity, a disabled one dimmed like a disabled settings row.</summary>
[ValueConversion(typeof(bool), typeof(double))]
internal sealed class EnabledOpacityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is false ? 0.55 : 1.0;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        Binding.DoNothing;
}
