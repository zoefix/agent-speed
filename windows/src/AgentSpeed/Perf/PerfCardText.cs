using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace AgentSpeed.Perf;

/// <summary>Text helpers of the score card's drawn elements.</summary>
internal static class PerfCardText
{
    /// <summary>The culture of the element's language (the app's, set on the card window), for line breaking and fonts.</summary>
    public static CultureInfo Culture(FrameworkElement element)
    {
        ArgumentNullException.ThrowIfNull(element);
        try
        {
            return element.Language.GetSpecificCulture();
        }
        catch (InvalidOperationException)
        {
            return CultureInfo.CurrentUICulture;
        }
    }

    /// <summary>One line of text in <paramref name="family"/> for a drawn element.</summary>
    public static FormattedText Format(FrameworkElement element, string text, FontFamily family, double size, FontWeight weight, Brush brush) =>
        new(text, Culture(element), FlowDirection.LeftToRight, new Typeface(family, FontStyles.Normal, weight, FontStretches.Normal), size, brush,
            VisualTreeHelper.GetDpi(element).PixelsPerDip);
}
