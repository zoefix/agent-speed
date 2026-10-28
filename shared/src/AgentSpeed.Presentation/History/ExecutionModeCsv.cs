using System.Globalization;
using System.Text;
using AgentSpeed.Core;
using AgentSpeed.Core.Reporting;

namespace AgentSpeed.Presentation.History;

public static class ExecutionModeCsv
{
    public static string Build(IEnumerable<DaySummary> days)
    {
        var builder = new StringBuilder("date,source,model,kind,mode,evidence,provider,requests,output_tokens,measured_output_tokens,measured_ms,avg_speed_tok_s\r\n");
        foreach (var day in days)
        foreach (var row in ExecutionModeUsage.Combine(day.Modes))
        {
            var values = new[]
            {
                HistoryRanges.FormatDay(day.Day), ReportBody.SourceId(row.Source), row.Model, ExecutionModes.Name(row.Kind), row.Mode,
                row.Evidence, row.Provider ?? string.Empty, Number(row.Requests), Number(row.Output), Number(row.MeasuredOutput),
                Number(row.MeasuredMs), row.AverageSpeed?.ToString("0.0", CultureInfo.InvariantCulture) ?? string.Empty,
            };
            builder.AppendJoin(',', values.Select(Quote)).Append("\r\n");
        }
        return builder.ToString();
    }

    public static string FileName(DayRange range, SourceKind? source) =>
        $"agent-speed-modes-{HistoryCsv.SourceId(source)}-{HistoryRanges.FormatDay(range.Start)}-{HistoryRanges.FormatDay(range.End)}.csv";
    private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);
    private static string Quote(string value)
    {
        if (value.Length > 0 && value[0] is '=' or '+' or '-' or '@' or '\t' or '\r') value = "'" + value;
        return value.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : value;
    }
}
