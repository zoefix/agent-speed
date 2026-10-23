using System.Globalization;
using System.Text;
using AgentSpeed.Core;

namespace AgentSpeed.Probe;

/// <summary>
/// One compact JSON object with keys in insertion order. Mirrors the Swift probe's JSONLine byte for byte
/// (quoting rules and three-decimal numbers with trailing zeros trimmed) so dumps can be diffed as text.
/// </summary>
internal sealed class JsonLine
{
    private readonly List<string> _parts = [];

    public string Text => "{" + string.Join(',', _parts) + "}";

    public JsonLine Add(string key, string? value) => Raw(key, value is null ? "null" : Quote(value));

    public JsonLine Add(string key, long? value) =>
        Raw(key, value is { } v ? v.ToString(CultureInfo.InvariantCulture) : "null");

    public JsonLine Add(string key, double? value) => Raw(key, value is { } v ? Number(v) : "null");

    public JsonLine Add(string key, bool value) => Raw(key, value ? "true" : "false");

    public JsonLine Raw(string key, string rawJson)
    {
        _parts.Add(Quote(key) + ":" + rawJson);
        return this;
    }

    public static string Array(IEnumerable<string> items) => "[" + string.Join(',', items) + "]";

    public static string Number(double value)
    {
        if (!double.IsFinite(value))
        {
            return "null";
        }

        var text = value.ToString("F3", CultureInfo.InvariantCulture).TrimEnd('0').TrimEnd('.');
        return text == "-0" ? "0" : text;
    }

    public static string Quote(string text)
    {
        var builder = new StringBuilder(text.Length + 2).Append('"');
        foreach (var rune in text.EnumerateRunes())
        {
            switch (rune.Value)
            {
                case '"':
                    builder.Append("\\\"");
                    break;
                case '\\':
                    builder.Append("\\\\");
                    break;
                case '\n':
                    builder.Append("\\n");
                    break;
                case '\r':
                    builder.Append("\\r");
                    break;
                case '\t':
                    builder.Append("\\t");
                    break;
                case < 0x20:
                    builder.Append("\\u").Append(rune.Value.ToString("x4", CultureInfo.InvariantCulture));
                    break;
                default:
                    builder.Append(rune.ToString());
                    break;
            }
        }

        return builder.Append('"').ToString();
    }

    public static string SourceName(SourceKind kind) => kind switch
    {
        SourceKind.Codex => "codex",
        SourceKind.ClaudeCode => "claudeCode",
        SourceKind.ClaudeDesktop => "claudeDesktop",
        SourceKind.DeepSeek => "deepseek",
        SourceKind.OpenCode => "opencode",
        SourceKind.Gemini => "gemini",
        SourceKind.Antigravity => "antigravity",
        SourceKind.AntigravityCli => "antigravityCli",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    public static string PhaseName(AgentPhase phase) => phase switch
    {
        AgentPhase.Generating => "generating",
        AgentPhase.Tools => "tools",
        _ => "idle",
    };

    public static string Totals(TokenTotals totals) => new JsonLine()
        .Add("requests", totals.Requests)
        .Add("input", totals.Input)
        .Add("cachedInput", totals.CachedInput)
        .Add("cacheWriteInput", totals.CacheWriteInput)
        .Add("output", totals.Output)
        .Add("reasoning", totals.Reasoning)
        .Text;

    public static string Rate(Rate rate) => new JsonLine().Add("output", rate.Output).Add("total", rate.Total).Text;
}

/// <summary>Buffered UTF-8 stdout writer, one line per call.</summary>
internal sealed class ProbeOutput : IDisposable
{
    private readonly StreamWriter _writer = new(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = false };

    public void Line(string text)
    {
        _writer.Write(text);
        _writer.Write('\n');
    }

    public void Flush() => _writer.Flush();

    public void Dispose() => _writer.Dispose();
}
