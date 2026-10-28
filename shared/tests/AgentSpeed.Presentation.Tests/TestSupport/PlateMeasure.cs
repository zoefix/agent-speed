using System.Globalization;
using AgentSpeed.Presentation.Orb.Names;

namespace AgentSpeed.Presentation.Tests.TestSupport;

/// <summary>
/// A deterministic stand-in for the system font at 600: 0.57 em per Latin grapheme (close to Segoe UI / SF semibold
/// for names like <c>Claude Code</c>), 1 em per CJK grapheme, 1.25 em per emoji.
/// </summary>
internal static class PlateMeasure
{
    public static readonly NamePlateMeasure Measure = (text, fontSize) =>
    {
        var width = 0.0;
        var elements = StringInfo.GetTextElementEnumerator(text);
        while (elements.MoveNext())
        {
            var element = (string)elements.Current;
            width += fontSize * (NamePlateScript.HasCjk(element) ? 1.0 : char.IsSurrogate(element[0]) ? 1.25 : 0.57);
        }

        return width;
    };
}
