using System.Globalization;
using System.Windows;
using System.Windows.Media;
using AgentSpeed.Presentation.Modes;
using AgentSpeed.Theme;

namespace AgentSpeed.Modes;

/// <summary>Widths of capsule words in their style: the UI face at 11 DIP medium, the configured mark at 9.5 DIP medium.</summary>
internal static class CapsuleWordMeasure
{
    private static readonly Dictionary<(string Word, string Language, double Size), double> Cache = new();

    public static Func<string, double> For(string languageCode) => word => Width(word, languageCode);

    public static double Mark(string mark, string languageCode) => Width(mark, languageCode, EffortCapsule.MarkFontSize);

    public static double Width(string word, string languageCode, double size = EffortCapsule.WordFontSize)
    {
        ArgumentNullException.ThrowIfNull(word);
        if (Cache.TryGetValue((word, languageCode, size), out var cached))
        {
            return cached;
        }

        var face = new Typeface(AppFonts.Ui(languageCode), FontStyles.Normal, FontWeights.Medium, FontStretches.Normal);
        var width = new FormattedText(word, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face, size, Brushes.Black, 1.0)
            .WidthIncludingTrailingWhitespace;
        if (Cache.Count > 256)
        {
            Cache.Clear();
        }

        Cache[(word, languageCode, size)] = width;
        return width;
    }
}
