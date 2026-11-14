using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Principal;
using AgentSpeed.Core.Quota.Local;
using AgentSpeed.Core.Tests.Quota.Cookies;
using AgentSpeed.Core.Tests.Quota.Vendors;

namespace AgentSpeed.Core.Tests.Quota.Local;

/// <summary>QUOTAS 9.4 <i>CLI usage report</i> on Windows, run against the stand-in agy (never a real one).</summary>
[SupportedOSPlatform("windows")]
[Collection(FakeAgy.Collection)]
public sealed class AgyUsageReportTests : IDisposable
{
    private static readonly string[] ReportArguments = ["-p", "/usage", "--output-format", "json", "--print-timeout", "90s"];

    private readonly FakeAgy _agy = new();

    public void Dispose() => _agy.Dispose();

    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    [WindowsFact]
    public async Task TheReportRunsWithItsArgumentsInAPrivateFolderAndGivesItsWindows()
    {
        var before = Now();
        var report = await _agy.Report.RunAsync(Now);
        var after = Now();

        QuotaVendorFixtureTests.AssertWindows(FakeAgy.ObservedWindows, report.Windows);
        Assert.InRange(report.EndedMs, before, after);
        Assert.Equal(new[] { new[] { "--version" }, ReportArguments }, _agy.Arguments());
        string? user;
        using (var identity = WindowsIdentity.GetCurrent())
        {
            user = identity.User!.Value;
        }

        var folders = _agy.Calls().Select(c => c["cwd"]!.GetValue<string>()).Distinct().ToList();
        var folder = Assert.Single(folders);
        Assert.Equal(Path.GetFullPath(Path.GetTempPath()), Path.GetFullPath(Path.GetDirectoryName(folder)! + Path.DirectorySeparatorChar));
        Assert.StartsWith("agentspeed-agy-", Path.GetFileName(folder), StringComparison.Ordinal);
        Assert.False(Directory.Exists(folder), "the private folder is removed afterwards");
        foreach (var call in _agy.Calls())
        {
            var access = call["access"]!.GetValue<string>();
            Assert.StartsWith("D:P", access, StringComparison.Ordinal);
            Assert.EndsWith($"(A;OICI;FA;;;{user})", access, StringComparison.Ordinal);
            Assert.Equal(1, access.Count(c => c == '('));
            Assert.Equal(2u, call["stdin"]!["type"]!.GetValue<uint>());
            Assert.False(call["stdin"]!["console"]!.GetValue<bool>());
            Assert.Equal(0, call["stdin"]!["read"]!.GetValue<int>());
            Assert.Equal(2u, call["stderr"]!["type"]!.GetValue<uint>());
            Assert.False(call["stderr"]!["console"]!.GetValue<bool>());
            Assert.Equal("true", call["autoUpdate"]!.GetValue<string>());
            Assert.Equal(Path.Combine(folder, "open.cmd"), call["browser"]!.GetValue<string>());
            Assert.Equal(folder, call["pathFirst"]!.GetValue<string>());
            Assert.Equal("@exit /b 1\r\n", call["openStub"]!.GetValue<string>());
            Assert.Equal("@exit /b 1\r\n", call["xdgOpenStub"]!.GetValue<string>());
            Assert.Equal(1, call["browserExit"]!.GetValue<int>());
            Assert.Equal(1, call["openExit"]!.GetValue<int>());
        }
    }

    [WindowsFact]
    public async Task AnOlderOrUnknownVersionRunsNoReport()
    {
        _agy.Configure(version: "1.1.10\n", report: FakeAgy.Observed);
        Assert.Null((await _agy.Report.RunAsync(Now)).Windows);
        Assert.Equal(new[] { new[] { "--version" } }, _agy.Arguments());

        // An older version is kept like any other; an unknown one is asked again next time.
        Assert.Null((await _agy.Report.RunAsync(Now)).Windows);
        Assert.Equal(1, _agy.Report.VersionRuns);

        _agy.Configure(version: "agy 1.5.9", report: FakeAgy.Observed);
        File.SetLastWriteTimeUtc(_agy.Executable, File.GetLastWriteTimeUtc(_agy.Executable).AddSeconds(1));
        Assert.Null((await _agy.Report.RunAsync(Now)).Windows);
        Assert.Null((await _agy.Report.RunAsync(Now)).Windows);
        Assert.Equal(3, _agy.Report.VersionRuns);
        Assert.All(_agy.Arguments(), a => Assert.Equal(new[] { "--version" }, a));
    }

    [WindowsFact]
    public async Task TheVersionIsAskedOncePerBinary()
    {
        Assert.NotNull((await _agy.Report.RunAsync(Now)).Windows);
        Assert.NotNull((await _agy.Report.RunAsync(Now)).Windows);
        Assert.Equal(1, _agy.Report.VersionRuns);

        // A new modification time.
        var written = File.GetLastWriteTimeUtc(_agy.Executable).AddMinutes(1);
        File.SetLastWriteTimeUtc(_agy.Executable, written);
        Assert.NotNull((await _agy.Report.RunAsync(Now)).Windows);
        Assert.Equal(2, _agy.Report.VersionRuns);

        // Another file with the same path, size and time: only its file id differs.
        var copy = Path.Combine(_agy.Root, "agy-copy.exe");
        File.Copy(_agy.Executable, copy);
        File.SetLastWriteTimeUtc(copy, written);
        File.Move(copy, _agy.Executable, overwrite: true);
        Assert.NotNull((await _agy.Report.RunAsync(Now)).Windows);
        Assert.Equal(3, _agy.Report.VersionRuns);
        Assert.Equal(3, _agy.Arguments().Count(a => a is ["--version"]));
        Assert.Equal(4, _agy.Arguments().Count(a => a.SequenceEqual(ReportArguments)));
    }

    [WindowsFact]
    public async Task TheBinaryIsTheHomeFoldersElseTheFirstRegularFileOnPath()
    {
        var other = Path.Combine(_agy.Root, "other bin");
        FakeAgy.Install(other);
        _agy.Configure(folder: other, report: FakeAgy.Observed);
        _agy.PathVariable = string.Join(';', "relative", @"C:\does-not-exist", "\"" + other + "\"");
        Assert.NotNull((await _agy.Report.RunAsync(Now)).Windows);
        Assert.Equal(2, _agy.Calls().Count);
        Assert.All(_agy.Calls(), c => Assert.Equal(_agy.Executable, c["exe"]!.GetValue<string>(), ignoreCase: true));

        // Without the home folder's agy (a folder of that name is no file), PATH's runs.
        foreach (var file in Directory.GetFiles(_agy.Bin))
        {
            File.Delete(file);
        }

        Directory.CreateDirectory(_agy.Executable);
        _agy.Configure(folder: other, version: "1.5.8", report: FakeAgy.Observed);
        Assert.NotNull((await _agy.Report.RunAsync(Now)).Windows);
        Assert.Equal(4, _agy.Calls().Count);
        Assert.All(_agy.Calls().Skip(2), c => Assert.Equal(Path.Combine(other, "agy.exe"), c["exe"]!.GetValue<string>(), ignoreCase: true));

        // Nowhere: no process, a failure.
        _agy.PathVariable = @"C:\does-not-exist";
        var started = Now();
        var none = await _agy.Report.RunAsync(Now);
        Assert.Null(none.Windows);
        Assert.InRange(none.EndedMs, started, Now());
        Assert.Equal(4, _agy.Calls().Count);
    }

    [WindowsFact]
    public async Task ReportsWithoutAWindowFail()
    {
        foreach (var stdout in new[] { "Please sign in to Antigravity: https://accounts.google.com/...\n", "", """{"status":"ERROR"}""" })
        {
            _agy.Configure(report: stdout);
            Assert.Null((await _agy.Report.RunAsync(Now)).Windows);
        }
    }

    [WindowsFact]
    public async Task ADescendantDoesNotOutliveTheReport()
    {
        _agy.Configure(report: FakeAgy.Observed, linger: true);
        var watch = Stopwatch.StartNew();
        var report = await _agy.Report.RunAsync(Now);
        QuotaVendorFixtureTests.AssertWindows(FakeAgy.ObservedWindows, report.Windows);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(30), $"took {watch.Elapsed}");
        Assert.True(FakeAgy.Gone(_agy.LingerPid()), "the child agy left behind was killed with its job");
    }

    [WindowsFact]
    public async Task MoreThanOneMebibyteOfOutputKillsAgyAndFails()
    {
        _agy.Configure(reportBytes: 2 << 20, reportSleepMs: 60_000, linger: true);
        var watch = Stopwatch.StartNew();
        var report = await _agy.Report.RunAsync(Now);
        Assert.Null(report.Windows);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(30), $"took {watch.Elapsed}");
        Assert.All(_agy.Calls(), c => Assert.True(FakeAgy.Gone(c["pid"]!.GetValue<int>())));
        Assert.True(FakeAgy.Gone(_agy.LingerPid()));
    }

    [WindowsFact]
    public async Task TheTimeLimitKillsAgyAndItsDescendants()
    {
        using var agy = new FakeAgy(new AgyLimits(VersionTimeoutMs: 20_000, ReportTimeoutMs: 3_000));
        agy.Configure(report: FakeAgy.Observed, reportSleepMs: 60_000, linger: true);
        var watch = Stopwatch.StartNew();
        var report = await agy.Report.RunAsync(Now);
        Assert.Null(report.Windows);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(30), $"took {watch.Elapsed}");
        Assert.True(watch.Elapsed >= TimeSpan.FromSeconds(3), $"took {watch.Elapsed}");
        Assert.All(agy.Calls(), c => Assert.True(FakeAgy.Gone(c["pid"]!.GetValue<int>())));
        Assert.True(FakeAgy.Gone(agy.LingerPid()));
        Assert.False(Directory.Exists(agy.Calls()[^1]["cwd"]!.GetValue<string>()));
    }

    [WindowsFact]
    public async Task ASlowVersionIsKilledAfterItsLimit()
    {
        using var agy = new FakeAgy(new AgyLimits(VersionTimeoutMs: 2_000));
        agy.Configure(versionSleepMs: 60_000, report: FakeAgy.Observed);
        var watch = Stopwatch.StartNew();
        Assert.Null((await agy.Report.RunAsync(Now)).Windows);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(20), $"took {watch.Elapsed}");
        Assert.Equal(new[] { new[] { "--version" } }, agy.Arguments());
        Assert.True(FakeAgy.Gone(agy.Calls()[0]["pid"]!.GetValue<int>()));
    }

    [Fact]
    public void TheLimitsAreTheSpecs()
    {
        Assert.Equal(new AgyLimits(3_000, 4096, 100_000, 1_048_576), new AgyLimits());
        Assert.Equal(ReportArguments, AgyUsageReport.Arguments);
    }

    [WindowsFact]
    public async Task OneReportAtATime()
    {
        _agy.Configure(report: FakeAgy.Observed, reportSleepMs: 2_000);
        var first = _agy.Report.RunAsync(Now);
        var second = await _agy.Report.RunAsync(Now);
        Assert.Null(second.Windows);
        Assert.NotNull((await first).Windows);
    }
}
