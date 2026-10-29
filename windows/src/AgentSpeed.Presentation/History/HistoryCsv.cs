using System.Globalization;
using System.Text;
using AgentSpeed.Core;
using AgentSpeed.Core.Money;

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
        "agents", "unfinalized", "models", "cost_usd", "unpriced_requests", "price_version",
    ];

    /// <summary>Traffic bytes (DESIGN 10): own and tools per source row, empty before measuring began.</summary>
    public static IReadOnlyList<string> TrafficColumns { get; } =
        ["traffic_rx_bytes", "traffic_tx_bytes", "traffic_rx_tools_bytes", "traffic_tx_tools_bytes"];

    /// <summary>The <c>source</c> of the row that carries this computer's traffic of a date.</summary>
    public const string MachineSource = "machine";

    /// <summary>
    /// The header: <see cref="Columns"/>, then <c>cost_&lt;code&gt;</c> (lower-case) for a non-USD display currency (DESIGN 9),
    /// then <see cref="TrafficColumns"/> once traffic was measured.
    /// </summary>
    public static IReadOnlyList<string> Header(HistoryMoney? money, bool traffic = false)
    {
        IReadOnlyList<string> header = money?.CsvColumn is { } column ? [.. Columns, column] : Columns;
        return traffic ? [.. header, .. TrafficColumns] : header;
    }

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
        SourceKind.Omp => "omp",
        SourceKind.Antigravity => "antigravity",
        SourceKind.WorkBuddy => "workbuddy",
        SourceKind.WorkBuddyAi => "workbuddy-ai",
        SourceKind.ZCode => "zcode",
        SourceKind.Kimi => "kimi",
        SourceKind.GrokBuild => "grokBuild",
        SourceKind.Pi => "pi",
        SourceKind.OpenClaw => "openclaw",
        SourceKind.Hermes => "hermes",
        SourceKind.Droid => "droid",
        SourceKind.Trae => "trae",
        _ => "unknown",
    };

    /// <summary>
    /// The rows as CSV. Costs are every source's (subscription exclusion does not apply to the CSV): <c>cost_usd</c> exact
    /// with 6 decimals, empty for a day without cost data; <c>cost_&lt;code&gt;</c> with the currency's digits + 2 at the rate
    /// in use (<paramref name="money"/> null or USD: no such column). With <paramref name="traffic"/> every row adds its
    /// traffic bytes (empty before measuring began), and each date with this computer's traffic adds a row of its own,
    /// <c>source = machine</c>, carrying only <c>traffic_rx_bytes</c> and <c>traffic_tx_bytes</c>.
    /// </summary>
    public static string Build(IEnumerable<DaySummary> rows, SourceKind? source, HistoryMoney? money = null, bool traffic = false)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var converted = money is { IsUsd: false } ? money : null;
        var header = Header(converted, traffic);
        var builder = new StringBuilder();
        builder.AppendJoin(',', header).Append("\r\n");
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
            var cost = row.TotalCost;
            Field(builder, cost is { } usd ? MoneyFormat.CsvUsd(usd.CostMicroUsd) : string.Empty);
            Field(builder, cost is { } unpriced ? Integer(unpriced.UnpricedRequests) : string.Empty);
            Field(builder, row.PriceVersion is { } version ? Integer(version) : string.Empty);
            if (converted is not null)
            {
                Field(builder, cost is { } local ? converted.CsvAmount(local.CostMicroUsd) : string.Empty);
            }

            if (traffic)
            {
                var day = row.Traffic;
                Field(builder, day is null ? string.Empty : Integer(day.Own.Rx));
                Field(builder, day is null ? string.Empty : Integer(day.Own.Tx));
                Field(builder, day is null ? string.Empty : Integer(day.Tools.Rx));
                Field(builder, day is null ? string.Empty : Integer(day.Tools.Tx));
            }

            builder.Append("\r\n");
            if (traffic && row.Traffic?.Machine is { } machine)
            {
                Field(builder, HistoryRanges.FormatDay(row.Day), first: true);
                Field(builder, MachineSource);
                for (var i = 2; i < header.Count; i++)
                {
                    var at = i - (header.Count - TrafficColumns.Count);
                    Field(builder, at == 0 ? Integer(machine.Rx) : at == 1 ? Integer(machine.Tx) : string.Empty);
                }

                builder.Append("\r\n");
            }
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
