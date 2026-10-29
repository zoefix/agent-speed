using System.Globalization;
using System.Reflection;
using AgentSpeed.Core;
using AgentSpeed.Core.Monitoring;
using AgentSpeed.Core.Pricing;
using AgentSpeed.Core.Unpriced;

namespace AgentSpeed.Probe;

/// <summary>
/// <c>--dump-unpriced</c>: the copied diagnostics text of METRICS 9.10 over the daily history (analysed into a store and
/// read back) or, with <c>--live</c>, over today's engine; the same text as <c>reference.py --dump-unpriced</c> but for
/// the <c>app</c> line.
/// </summary>
internal static class DumpUnpriced
{
    public static int Run(ProbeOptions options, ProbeOutput output)
    {
        if (options.PriceTable(out var tableError) is not { } table)
        {
            Console.Error.WriteLine(tableError);
            return 2;
        }

        var zone = options.Utc ? TimeZoneInfo.Utc : TimeZoneInfo.Local;
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, zone).DateTime);
        var (view, first, last) = Range(options, today);
        IReadOnlyList<UnpricedPart> parts;
        if (options.Live)
        {
            parts = LiveParts(options, table, zone, Label(today));
        }
        else
        {
            var (report, days, _, _) = HistoryRun.Analyze(options, table, zone);
            parts = [.. days.Select(UnpricedPart.Of)];
            Console.Error.WriteLine($"history: analysed {report.FilesAnalyzed} files and {report.SessionsAnalyzed} OpenCode sessions " +
                $"in {report.ElapsedMs} ms; {days.Count} rows");
        }

        var built = UnpricedView.Build(parts, table, first, last, options.Source);
        output.Write(UnpricedText.Render(built, UnpricedApp.Windows(Version), table, new UnpricedRange(view, first, last, options.Source)));
        output.Flush();
        return 0;
    }

    /// <summary>(view, first, last): the History window's presets for <c>--days</c> (default 30), else a custom range.</summary>
    private static (string View, string First, string Last) Range(ProbeOptions options, DateOnly today)
    {
        if (options.Live)
        {
            return ("live", Label(today), Label(today));
        }

        if (options.First is { } first && options.Last is { } last)
        {
            return ("custom", first, last);
        }

        var count = options.Days ?? 30;
        var view = count switch
        {
            1 => "today",
            7 => "7d",
            30 => "30d",
            90 => "90d",
            _ => "custom",
        };
        return (view, Label(today.AddDays(1 - count)), Label(today));
    }

    /// <summary>Today's parts from the <c>--dump-today</c> seeding, priced by the engine with the one table.</summary>
    private static IReadOnlyList<UnpricedPart> LiveParts(ProbeOptions options, PriceTable table, TimeZoneInfo zone, string today)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var todayStart = options.SinceMs ?? LocalDay.StartMs(now, zone);
        using var core = new MonitorCore(MonitorConfiguration.Default, options.Locations, todayStart, pricing: new PriceBook([table]), zone: zone);
        var report = core.Seed(now, todayStart, CancellationToken.None);
        var (snapshot, _) = core.BuildSnapshot(now, todayStart, MonitorState.Running, observeLive: false);
        Console.Error.WriteLine($"seeded {report.FilesRead} files, {report.BytesRead} bytes in {report.ElapsedMs} ms");
        return UnpricedPart.Live(snapshot.Sources, today);
    }

    private static string Label(DateOnly day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string Version
    {
        get
        {
            var informational = typeof(DumpUnpriced).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            var plus = informational?.IndexOf('+') ?? -1;
            return string.IsNullOrWhiteSpace(informational) ? "0.0.0" : plus > 0 ? informational[..plus] : informational;
        }
    }
}
