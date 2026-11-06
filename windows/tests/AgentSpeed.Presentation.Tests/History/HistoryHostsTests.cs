using AgentSpeed.Core;
using AgentSpeed.Core.Remote;
using AgentSpeed.Core.Traffic;
using AgentSpeed.Presentation.History;
using static AgentSpeed.Presentation.Tests.TestSupport.HistoryData;

namespace AgentSpeed.Presentation.Tests.History;

/// <summary>The Usage History window's host picker and filter, and host rows in every view of the days (DESIGN 13, REMOTE 8.3).</summary>
public class HistoryHostsTests
{
    private const string Devbox = "8C0D6B0A-1111-4222-8333-944455556666";
    private const string Gpu = "1F2E3D4C-5555-4666-8777-988899990000";

    private static readonly IReadOnlyDictionary<string, string> Names = new Dictionary<string, string> { [Devbox] = "devbox", [Gpu] = "Alpha GPU" };

    private static DailyUsage On(string host, DailyUsage usage) => usage with { Host = host };

    private static IReadOnlyList<DailyUsage> Mixed() =>
    [
        Usage("2026-10-20", SourceKind.Codex, output: 100, requests: 1, peak: 50, models: [new ModelUsage("gpt-5.5", 1, 100)]),
        On(Devbox, Usage("2026-10-20", SourceKind.Codex, output: 300, requests: 3, peak: 50, models: [new ModelUsage("gpt-5.5", 3, 300)])),
        On(Gpu, Usage("2026-10-20", SourceKind.ClaudeCode, output: 50, requests: 2, peak: 50)),
        On(Devbox, Usage("2026-10-21", SourceKind.Codex, output: 70, requests: 1)),
    ];

    [Fact]
    public void NothingAboutHostsShowsWithoutRemoteHistory()
    {
        IReadOnlyList<DailyUsage> local = [Usage("2026-10-20"), Usage("2026-10-21", SourceKind.ClaudeCode)];
        Assert.Empty(HistoryHosts.WithHistory(local, Names));
        // A remote row without usage does not count either.
        Assert.Empty(HistoryHosts.WithHistory([.. local, On(Devbox, Usage("2026-10-20", output: 0, requests: 0, input: 0))], Names));

        var view = HistoryHosts.Select(local, Names, RemoteHost.LocalId);
        Assert.Null(view.Host);
        Assert.Same(local, view.Usage);
        Assert.Null(view.CsvHost);
        Assert.True(view.ShowsPeak && view.HasTraffic);
    }

    [Fact]
    public void ThePickerListsThisComputerThenTheHostsByName()
    {
        var hosts = HistoryHosts.WithHistory(Mixed(), Names);
        Assert.Equal([RemoteHost.LocalId, Gpu, Devbox], hosts.Select(h => h.Id));
        Assert.Equal([null, "Alpha GPU", "devbox"], hosts.Select(h => h.Name));
        Assert.True(hosts[0].IsLocal);

        // A removed host without a stored name shows its id; a selected host without history is listed too.
        const string Fresh = "00000000-AAAA-4BBB-8CCC-DDDDDDDDDDDD";
        var listed = HistoryHosts.WithHistory(Mixed(), new Dictionary<string, string> { [Gpu] = "Alpha GPU" }, Fresh);
        Assert.Equal([RemoteHost.LocalId, Fresh, Devbox, Gpu], listed.Select(h => h.Id));
        Assert.Equal([null, Fresh, Devbox, "Alpha GPU"], listed.Select(h => h.Name));
        Assert.Equal([RemoteHost.LocalId, Fresh], HistoryHosts.WithHistory([Usage("2026-10-20")], Names, Fresh).Select(h => h.Id));
    }

    [Fact]
    public void SettingsNamesWinOverStoredOnes()
    {
        var names = HistoryHosts.Names(Names, new Dictionary<string, string> { [Devbox] = "build" });
        Assert.Equal("build", names[Devbox]);
        Assert.Equal("Alpha GPU", names[Gpu]);
        Assert.Same(Names, HistoryHosts.Names(Names, null));
    }

    [Fact]
    public void AllHostsAddUpPerDayAndSource()
    {
        var days = HistoryAggregator.Daily(Mixed(), null);
        Assert.Equal(2, days.Count);
        var first = days[0];
        Assert.Equal((450L, 6L), (first.Output, first.Requests));
        Assert.Equal([(SourceKind.Codex, 400L), (SourceKind.ClaudeCode, 50L)],
            first.Sources.Select(s => (s.Source, s.Output)).OrderBy(s => s.Source));
        Assert.Equal((4L, 400L), (first.Models.Single(m => m.Model == "gpt-5.5").Requests, first.Models.Single(m => m.Model == "gpt-5.5").Output));
        Assert.Equal(50, first.Peak);

        var codex = HistoryAggregator.Daily(Mixed(), SourceKind.Codex);
        Assert.Equal([400L, 70L], codex.Select(d => d.Output));
    }

    [Fact]
    public void AHostFilterKeepsOnlyItsRows()
    {
        var view = HistoryHosts.Select(Mixed(), Names, Devbox);
        Assert.Equal(Devbox, view.Host);
        Assert.All(view.Usage, r => Assert.Equal(Devbox, r.Host));
        Assert.Equal([300L, 70L], HistoryAggregator.Daily(view.Usage, null).Select(d => d.Output));
        Assert.Equal([300L, 70L], HistoryAggregator.Daily(Mixed(), null, host: Devbox).Select(d => d.Output));
        Assert.Equal([100L], HistoryAggregator.Daily(Mixed(), null, host: RemoteHost.LocalId).Select(d => d.Output));
        Assert.False(view.ShowsPeak);
        Assert.False(view.HasTraffic);
        Assert.Equal("devbox", view.Name("This PC"));

        var local = HistoryHosts.Select(Mixed(), Names, RemoteHost.LocalId);
        Assert.Equal([100L], local.Usage.Select(r => r.Totals.Output));
        Assert.True(local.HasTraffic);
        Assert.Equal("This PC", local.Name("This PC"));
        Assert.Null(HistoryHosts.Select(Mixed(), Names, null).Name("This PC"));
        Assert.Equal<DayRange?>(new DayRange(D(2026, 10, 20), D(2026, 10, 21)), HistoryAggregator.Bounds(view.Usage));
    }

    [Fact]
    public void TheBreakdownAndTheUnpricedViewAddUpHosts()
    {
        var bySource = DayBreakdown.BySource(Mixed(), D(2026, 10, 20));
        Assert.Equal([(SourceKind.Codex, 400L), (SourceKind.ClaudeCode, 50L)], bySource.Rows.Select(r => (r.Source, r.Output)));
        var byModel = DayBreakdown.ByModel(Mixed(), D(2026, 10, 20), SourceKind.Codex);
        Assert.Equal(400, Assert.Single(byModel.Rows).Output);

        var unpriced = UnpricedPopover.ViewOf(Mixed(), new DayRange(D(2026, 10, 20), D(2026, 10, 21)), null, default,
            Core.Pricing.PriceBook.Bundled);
        Assert.Equal(7, unpriced.Requests);
    }

    [Fact]
    public void TheCsvHostValueNamesTheFilter()
    {
        var hosts = HistoryHosts.WithHistory(Mixed(), Names);
        Assert.Equal("all", HistoryHosts.CsvValue(null, hosts));
        Assert.Equal("local", HistoryHosts.CsvValue(RemoteHost.LocalId, hosts));
        Assert.Equal("devbox", HistoryHosts.CsvValue(Devbox, hosts));
        Assert.Equal("unknown-id", HistoryHosts.CsvValue("unknown-id", hosts));
        Assert.Equal("all", HistoryHosts.Select(Mixed(), Names, null).CsvHost);
        Assert.Equal("Alpha GPU", HistoryHosts.Select(Mixed(), Names, Gpu).CsvHost);
    }

    [Fact]
    public void TheCsvAddsATrailingHostColumnOnlyWhileHostHistoryExists()
    {
        var rows = HistoryAggregator.Daily(Mixed(), null);
        var plain = HistoryCsv.Build(rows, null).Split("\r\n");
        Assert.Equal(string.Join(',', HistoryCsv.Columns), plain[0]);

        var lines = HistoryCsv.Build(rows, null, host: "devbox").Split("\r\n");
        Assert.Equal(string.Join(',', HistoryCsv.Columns) + ",host", lines[0]);
        Assert.EndsWith(",devbox", lines[1], StringComparison.Ordinal);
        Assert.Equal(plain[1] + ",devbox", lines[1]);
        Assert.Equal(HistoryCsv.Columns.Count + 1, lines[1].Split(',').Length);

        // A host name that a spreadsheet would read as a formula is quoted like any other text.
        Assert.EndsWith(",'=cmd", HistoryCsv.Build(rows, null, host: "=cmd").Split("\r\n")[1], StringComparison.Ordinal);
    }

    [Fact]
    public void TheMachineTrafficRowIsThisComputers()
    {
        var traffic = new DayTraffic(new TrafficBytes(10, 20), new TrafficBytes(1, 2), new Dictionary<SourceKind, TrafficBytes>(),
            new TrafficBytes(500, 600), 1000, false, true);
        var row = HistoryAggregator.Daily(Mixed(), null)[0] with { Traffic = traffic };
        var lines = HistoryCsv.Build([row], null, traffic: true, host: "all").Split("\r\n");
        var header = lines[0].Split(',');
        Assert.Equal("host", header[^1]);
        Assert.Equal("traffic_tx_tools_bytes", header[^2]);
        var machine = lines[2].Split(',');
        Assert.Equal(header.Length, machine.Length);
        Assert.Equal(("machine", "500", "600", "local"), (machine[1], machine[^5], machine[^4], machine[^1]));
        Assert.Equal("all", lines[1].Split(',')[^1]);
    }
}
