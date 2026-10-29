using System.Diagnostics;
using AgentSpeed.Core;
using AgentSpeed.Core.Pricing;

namespace AgentSpeed.Probe;

/// <summary><c>--dump-history</c>: analyses every log completely and prints one line per day and source (METRICS 8, 9.10).</summary>
internal static class DumpHistory
{
    public static int Run(ProbeOptions options, ProbeOutput output)
    {
        if (options.PriceTable(out var tableError) is not { } table)
        {
            Console.Error.WriteLine(tableError);
            return 2;
        }

        var zone = options.Utc ? TimeZoneInfo.Utc : TimeZoneInfo.Local;
        var process = Process.GetCurrentProcess();
        var (report, days, perf, storeBytes) = HistoryRun.Analyze(options, table, zone);

        foreach (var day in days)
        {
            var models = new JsonLine();
            foreach (var model in day.Models.OrderBy(m => m.Model, StringComparer.Ordinal))
            {
                models.Raw(model.Model, new JsonLine()
                    .Add("requests", model.Requests)
                    .Add("output", model.Output)
                    .Add("measuredOutput", model.MeasuredOutput)
                    .Add("measuredMs", model.MeasuredMs)
                    .Add("costMicroUsd", model.Cost?.CostMicroUsd)
                    .Add("unpricedRequests", model.Cost?.UnpricedRequests)
                    .Text);
            }

            output.Line(new JsonLine()
                .Add("date", day.Day)
                .Add("source", JsonLine.SourceName(day.Source))
                .Add("requests", day.Totals.Requests)
                .Add("input", day.Totals.Input)
                .Add("cachedInput", day.Totals.CachedInput)
                .Add("cacheWriteInput", day.Totals.CacheWriteInput)
                .Add("output", day.Totals.Output)
                .Add("reasoning", day.Totals.Reasoning)
                .Add("measuredOutput", day.MeasuredOutput)
                .Add("measuredMs", day.MeasuredMs)
                .Add("unfinalized", day.Unfinalized)
                .Add("costMicroUsd", day.Cost?.CostMicroUsd)
                .Add("unpricedRequests", day.Cost?.UnpricedRequests)
                .Add("priceVersion", day.PriceVersion)
                .Add("agents", day.Agents)
                .Raw("models", models.Text)
                .Raw("unpriced", Unpriced(day))
                .Text);
        }

        foreach (var record in perf)
        {
            output.Line(JsonLine.Of("perf", w => PerfDumpJson.WriteRecord(w, record)));
        }

        output.Flush();
        process.Refresh();
        Console.Error.WriteLine(
            $"history: discovered {report.FilesDiscovered}, analysed {report.FilesAnalyzed} files ({report.BytesAnalyzed} bytes) " +
            $"and {report.SessionsAnalyzed} OpenCode sessions in {report.ElapsedMs} ms; unreadable {report.FilesUnreadable}; " +
            $"store {storeBytes} bytes; working set {process.WorkingSet64 / 1048576.0:F1} MB; " +
            $"allocated {GC.GetTotalAllocatedBytes() / 1048576.0:F0} MB; {days.Count} rows, {perf.Count} performance records");
        return 0;
    }

    /// <summary>The day's unpriced facts (METRICS 9.10), in the fact order.</summary>
    private static string Unpriced(DailyUsage day) => JsonLine.Array(day.Unpriced.Facts.Select(f => new JsonLine()
        .Add("model", f.Fact.Model)
        .Add("provider", f.Fact.Provider)
        .Add("fast", f.Fact.Fast)
        .Add("requests", f.Requests)
        .Text));
}
