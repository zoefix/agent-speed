using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json.Nodes;
using AgentSpeed.Core.Quota.Local;
using AgentSpeed.Core.Tests.Quota.Routes;

namespace AgentSpeed.Core.Tests.Quota.Local;

/// <summary>
/// The stand-in <c>agy.exe</c> (tests/AgentSpeed.FakeAgy) installed in a temporary home's <c>.local\bin</c>, its
/// behaviour in <c>fake-agy.json</c>, every start it recorded, and a usage report that runs it. Never a real agy.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class FakeAgy : IDisposable
{
    /// <summary>The tests that start stand-ins run one after another, so none sees another's agy.</summary>
    public const string Collection = "fake agy";

    public FakeAgy(AgyLimits? limits = null)
    {
        Root = Path.Combine(Path.GetTempPath(), "agentspeed-fakeagy-" + Guid.NewGuid().ToString("N"));
        Home = Path.Combine(Root, "home");
        Bin = Path.Combine(Home, ".local", "bin");
        Install(Bin);
        Log = Path.Combine(Root, "calls.jsonl");
        Configure(version: "1.5.9\n", report: Observed);
        // A generous version limit: the stand-in records each start (two cmd.exe runs) before it answers, on a busy runner.
        Report = new AgyUsageReport(Home, name => name == "PATH" ? PathVariable : null, ChildEnvironment, limits ?? new AgyLimits(VersionTimeoutMs: 20_000));
    }

    /// <summary>The report agy 1.5.9 printed on 2026-11-14 (spec/fixtures/quota-cases.json <c>antigravityCli</c>).</summary>
    public static string Observed => CliCase("observed").GetProperty("stdout").GetString()!;

    public static System.Text.Json.JsonElement ObservedWindows => CliCase("observed").GetProperty("expected");

    public string Root { get; }

    public string Home { get; }

    public string Bin { get; }

    public string Log { get; }

    /// <summary>The app's <c>PATH</c> as the report sees it.</summary>
    public string? PathVariable { get; set; }

    public AgyUsageReport Report { get; }

    public string Executable => Path.Combine(Bin, "agy.exe");

    public string LingerFile => Path.Combine(Root, "linger.pid");

    /// <summary>Copies the stand-in (its apphost, assembly and runtime files) into <paramref name="folder"/>.</summary>
    public static void Install(string folder)
    {
        Directory.CreateDirectory(folder);
        var source = Path.Combine(AppContext.BaseDirectory, "fake-agy");
        foreach (var file in Directory.EnumerateFiles(source, "agy.*"))
        {
            File.Copy(file, Path.Combine(folder, Path.GetFileName(file)), overwrite: true);
        }
    }

    public void Configure(string? version = "1.5.9\n", string? report = null, int reportBytes = 0, int reportSleepMs = 0, int versionSleepMs = 0,
        bool linger = false, string? folder = null)
    {
        var config = new JsonObject
        {
            ["log"] = Log,
            ["version"] = version,
            ["report"] = report,
            ["reportBytes"] = reportBytes,
            ["reportSleepMs"] = reportSleepMs,
            ["versionSleepMs"] = versionSleepMs,
            ["linger"] = linger ? LingerFile : null,
        };
        File.WriteAllText(Path.Combine(folder ?? Bin, "fake-agy.json"), config.ToJsonString());
    }

    /// <summary>Every start, oldest first.</summary>
    public IReadOnlyList<JsonObject> Calls() =>
        File.Exists(Log) ? [.. File.ReadAllLines(Log).Where(l => l.Length > 0).Select(l => JsonNode.Parse(l)!.AsObject())] : [];

    public IReadOnlyList<string[]> Arguments() => [.. Calls().Select(c => c["args"]!.AsArray().Select(a => a!.GetValue<string>()).ToArray())];

    public int LingerPid() => int.Parse(File.ReadAllText(LingerFile), System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// Starts <paramref name="executable"/> (the stand-in under any name) as this process's child that writes its pid to
    /// <paramref name="pidFile"/> and sleeps; the extra arguments are only there to be read.
    /// </summary>
    public static Process Linger(string executable, string pidFile, params string[] arguments)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var (name, value) in ChildEnvironment())
        {
            start.Environment[name] = value;
        }

        string[] all = ["--linger", pidFile, .. arguments];
        foreach (var argument in all)
        {
            start.ArgumentList.Add(argument);
        }

        var process = Process.Start(start)!;
        var deadline = Environment.TickCount64 + 30_000;
        while (!File.Exists(pidFile) || new FileInfo(pidFile).Length == 0)
        {
            Assert.True(Environment.TickCount64 < deadline, "the stand-in did not start");
            Thread.Sleep(50);
        }

        return process;
    }

    /// <summary>The process has ended (or ends within a few seconds).</summary>
    public static bool Gone(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return process.WaitForExit(5_000);
        }
        catch (ArgumentException)
        {
            return true;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
    }

    /// <summary>
    /// This process's environment; the stand-in is a framework-dependent apphost, so it also gets <c>DOTNET_ROOT</c> of
    /// the runtime running the tests.
    /// </summary>
    private static IReadOnlyDictionary<string, string> ChildEnvironment()
    {
        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (System.Collections.DictionaryEntry entry in System.Environment.GetEnvironmentVariables())
        {
            if (entry is { Key: string name, Value: string value })
            {
                environment[name] = value;
            }
        }

        if (!environment.ContainsKey("DOTNET_ROOT"))
        {
            var runtime = new DirectoryInfo(RuntimeEnvironment.GetRuntimeDirectory());
            if (runtime.Parent?.Parent?.Parent is { } root)
            {
                environment["DOTNET_ROOT"] = root.FullName;
            }
        }

        return environment;
    }

    private static System.Text.Json.JsonElement CliCase(string name) =>
        QuotaVendorHarness.Vendor("antigravityCli").EnumerateArray().Single(c => c.GetProperty("name").GetString() == name);

    public void Dispose()
    {
        if (File.Exists(LingerFile) && !Gone(LingerPid()))
        {
            using var stray = Process.GetProcessById(LingerPid());
            stray.Kill();
        }

        for (var attempt = 0; attempt < 20; attempt++)
        {
            try
            {
                Directory.Delete(Root, recursive: true);
                return;
            }
            catch (DirectoryNotFoundException)
            {
                return;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(100);
            }
        }
    }
}
