using System.Globalization;
using System.Text;
using AgentSpeed.Core;

namespace AgentSpeed.Presentation.History;

/// <summary>
/// CSV export of the rows the Usage History table shows. Columns are locale independent: English snake_case
/// headers, ISO dates, plain integers, a dot as the decimal separator, RFC 4180 quoting and CRLF line ends.
/// </summary>
public static class HistoryCsv
{
    public static IReadOnlyList<string> Columns { get; } =
    [
        "date", "source", "requests", "input_tokens", "cached_input_tokens", "cache_write_input_tokens", "output_tokens",
        "reasoning_tokens", "cache_hit_percent", "measured_output_tokens", "measured_ms", "avg_speed_tok_s", "peak_tok_s",
        "agents", "unfinalized", "models",
    ];

    /// <summary>Stable identifier of a source (or <c>all</c>) for the <c>source</c> column and file names.</summary>
    public static string SourceId(SourceKind? source) => source switch
    {
        null => "all",
        SourceKind.Codex => "codex",
        SourceKind.ClaudeCode => "claude-code",
        SourceKind.ClaudeDesktop => "claude-desktop",
        SourceKind.DeepSeek => "deepseek",
        SourceKind.OpenCode => "opencode",
        SourceKind.Gemini => "gemini",
        SourceKind.Antigravity => "antigravity",
        SourceKind.AntigravityCli => "antigravityCli",
        _ => "unknown",
    };

    public static string Build(IEnumerable<DaySummary> rows, SourceKind? source)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var builder = new StringBuilder();
        builder.AppendJoin(',', Columns).Append("\r\n");
        var id = SourceId(source);
        foreach (var row in rows)
        {
            Field(builder, HistoryRanges.FormatDay(row.Day), first: true);
            Field(builder, id);
            Field(builder, Integer(row.Requests));
            Field(builder, Integer(row.Input));
            Field(builder, Integer(row.CachedInput));
            Field(builder, Integer(row.CacheWriteInput));
            Field(builder, Integer(row.Output));
            Field(builder, Integer(row.Reasoning));
            Field(builder, Decimal(row.CacheHitRatio * 100));
            Field(builder, Integer(row.MeasuredOutput));
            Field(builder, Integer(row.MeasuredMs));
            Field(builder, Decimal(row.AverageSpeed));
            Field(builder, Decimal(row.Peak));
            Field(builder, Integer(row.Agents));
            Field(builder, Integer(row.Unfinalized));
            Field(builder, string.Join("; ", row.Models.Select(m => m.Model)));
            builder.Append("\r\n");
        }

        return builder.ToString();
    }

    /// <summary>UTF-8 with a byte order mark, so Excel opens non-ASCII model names correctly.</summary>
    public static byte[] Encode(string csv)
    {
        ArgumentNullException.ThrowIfNull(csv);
        var encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
        var preamble = encoding.GetPreamble();
        var body = encoding.GetBytes(csv);
        var bytes = new byte[preamble.Length + body.Length];
        preamble.CopyTo(bytes, 0);
        body.CopyTo(bytes, preamble.Length);
        return bytes;
    }

    /// <summary><c>agent-speed-history-all-2026-09-22-2026-10-21.csv</c></summary>
    public static string FileName(DayRange range, SourceKind? source) =>
        $"agent-speed-history-{SourceId(source)}-{HistoryRanges.FormatDay(range.Start)}-{HistoryRanges.FormatDay(range.End)}.csv";

    private static string Integer(long value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Decimal(double? value) =>
        value is { } v && double.IsFinite(v) ? Math.Round(v, 1, MidpointRounding.AwayFromZero).ToString("0.0", CultureInfo.InvariantCulture) : string.Empty;

    private static void Field(StringBuilder builder, string value, bool first = false)
    {
        if (!first)
        {
            builder.Append(',');
        }

        // Text that a spreadsheet would read as a formula is prefixed with an apostrophe.
        if (value.Length > 0 && value[0] is '=' or '+' or '-' or '@' or '\t' or '\r')
        {
            value = "'" + value;
        }

        if (value.IndexOfAny([',', '"', '\r', '\n']) >= 0)
        {
            builder.Append('"').Append(value.Replace("\"", "\"\"", StringComparison.Ordinal)).Append('"');
        }
        else
        {
            builder.Append(value);
        }
    }
}
