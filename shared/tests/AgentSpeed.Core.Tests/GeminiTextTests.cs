using System.Text;
using System.Text.Json;
using AgentSpeed.Core.Gemini;

namespace AgentSpeed.Core.Tests;

public sealed class GeminiTextTests
{
    // Python 3 str.isspace() over the whole code point range.
    private static readonly int[] PythonWhitespace =
    [
        0x09, 0x0A, 0x0B, 0x0C, 0x0D, 0x1C, 0x1D, 0x1E, 0x1F, 0x20, 0x85, 0xA0, 0x1680,
        0x2000, 0x2001, 0x2002, 0x2003, 0x2004, 0x2005, 0x2006, 0x2007, 0x2008, 0x2009, 0x200A,
        0x2028, 0x2029, 0x202F, 0x205F, 0x3000,
    ];

    [Fact]
    public void WhitespaceMatchesPython()
    {
        var expected = PythonWhitespace.ToHashSet();
        for (var c = 0; c <= 0x10FFFF; c++)
        {
            Assert.True(expected.Contains(c) == GeminiText.IsSpace(c), $"U+{c:X4}");
        }
    }

    [Theory]
    [InlineData("\"\"", false)]
    [InlineData("\" \\t\\n\\r\\f\"", false)]
    [InlineData("\"\\u000b\\u001c\\u001D\\u001e\\u001f\\u0085\\u00a0\\u1680\\u2000\\u200a\\u2028\\u2029\\u202f\\u205f\\u3000\"", false)]
    [InlineData("\"\u00a0\u3000\u2028\"", false)]
    [InlineData("\" x \"", true)]
    [InlineData("\"\\u0008\"", true)]
    [InlineData("\"\\\"\"", true)]
    [InlineData("\"\\\\\"", true)]
    [InlineData("\"\\/\"", true)]
    [InlineData("\"\\ud83d\\ude00\"", true)]
    [InlineData("\"\\u200b\"", true)]
    [InlineData("\"\\u180e\"", true)]
    [InlineData("\"\u00e9\"", true)]
    [InlineData("\"\U0001F600\"", true)]
    public void VisibleTextOfAJsonString(string json, bool expected)
    {
        var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(json));
        reader.Read();

        Assert.Equal(expected, GeminiText.HasVisibleText(ref reader));
        Assert.Equal(expected, GeminiText.Strip(reader.GetString()).Length > 0);
    }

    [Theory]
    [InlineData(" \u001c/a/b\u001f\n", "/a/b")]
    [InlineData("\u3000\u3000", "")]
    [InlineData("x", "x")]
    [InlineData("\u200bx\u200b", "\u200bx\u200b")]
    public void StripMatchesPython(string text, string expected) =>
        Assert.Equal(expected, GeminiText.Strip(text).ToString());
}
