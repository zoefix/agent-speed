using System.Globalization;
using AgentSpeed.Core.Monitoring;
using AgentSpeed.Core.Parsing;

namespace AgentSpeed.Core.Tests;

/// <summary>omp roots in the monitor (METRICS 4.7, 5): profiles, sub-agents and older files that grow.</summary>
public sealed class OmpMonitorTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly long _now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    private readonly long _todayStart;

    public OmpMonitorTests()
    {
        _todayStart = _now - 3_600_000;
    }

    private string DefaultRoot => _temp.Combine("home", ".omp", "agent", "sessions");

    private string ProfileRoot => _temp.Combine("home", ".omp", "profiles", "work", "agent", "sessions");

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void EveryRootIsSeededAndSubAgentsAttachToTheSessionThatStartedThem()
    {
        var t = _now - 200_000;
        var main = Write(DefaultRoot, "-w-nova/2026-10-20T10-00-00-000Z_main.jsonl",
            Header(t, "/w/nova"), User("u1", t + 1000), Assistant("a1", t + 4000, t + 1010, 3000, 120, "toolUse", "call_1"));
        var sub = Write(DefaultRoot, "-w-nova/2026-10-20T10-00-00-000Z_main/Scan.jsonl",
            Header(t + 2000, "/w/nova"), User("s1", t + 2100), Assistant("s2", t + 3500, t + 2110, 1300, 60, "stop"));
        var nested = Write(DefaultRoot, "-w-nova/2026-10-20T10-00-00-000Z_main/Scan/Verify.jsonl",
            Header(t + 2200, "/w/nova"), User("v1", t + 2300), Assistant("v2", t + 3300, t + 2310, 900, 30, "stop"));
        var profile = Write(ProfileRoot, "-w-orbit/2026-10-20T10-05-00-000Z_other.jsonl",
            Header(t, "/w/orbit"), User("p1", t + 1000), Assistant("p2", t + 3000, t + 1005, 1900, 75, "stop"));
        Write(DefaultRoot, "stray.jsonl", Header(t, "/w/stray"), Assistant("x", t + 3000, t + 1005, 1900, 75, "stop"));
        using var core = Core();

        var report = core.Seed(_now, _todayStart, CancellationToken.None);

        Assert.Equal(4, report.FilesDiscovered);
        var agents = Build(core).Agents.ToDictionary(a => a.Id);
        Assert.Equal((main, sub), (agents[sub].ParentId, agents[nested].ParentId));
        Assert.Null(agents[main].ParentId);
        Assert.Equal((false, true, true, false), (agents[main].IsSubagent, agents[sub].IsSubagent, agents[nested].IsSubagent, agents[profile].IsSubagent));
        Assert.Equal(("nova", "orbit", AgentPhase.Tools), (agents[main].Project, agents[profile].Project, agents[main].Phase));
        var source = Build(core).Sources.Single(s => s.Kind == SourceKind.Omp);
        Assert.Equal((4, 285L, true), (source.SessionCount, source.Today.Output, source.Available));
    }

    [Fact]
    public void AnOlderFileThatGrowsIsReadFromItsEndWithItsHeaderFirst()
    {
        var t = _todayStart - 7_200_000;
        var path = Write(DefaultRoot, "-w-nova/2026-10-20T08-00-00-000Z_s.jsonl",
            Title(), Header(t, "/w/nova"), User("u1", t + 1000), Assistant("a1", t + 3000, t + 1010, 1900, 40, "stop"));
        File.SetLastWriteTimeUtc(path, DateTime.UnixEpoch.AddMilliseconds(t + 3000));
        var observer = new Observer();
        using var core = Core(observer);
        core.Seed(_now, _todayStart, CancellationToken.None);
        Assert.Empty(observer.Seen);

        File.AppendAllText(path, string.Concat(new[]
        {
            Assistant("copied", t - 5000, t - 6000, 900, 99, "stop"),
            User("u2", _now - 5000),
            Assistant("a2", _now - 1000, _now - 4990, 3900, 80, "stop"),
        }.Select(l => l + "\n")));
        Assert.True(core.ProcessPaths([path], _now, _todayStart));

        var response = Assert.Single(observer.Seen).Response;
        Assert.Equal(("omp:a2:" + (_now - 4990).ToString(CultureInfo.InvariantCulture), (long?)(_now - 4990), _now - 1090),
            (response.Key, response.StartMs, response.EndMs));
        var agent = Assert.Single(Build(core).Agents);
        Assert.Equal(("nova", AgentPhase.Idle, (string?)"claude-opus-5-5"), (agent.Project, agent.Phase, agent.Model));
    }

    [Fact]
    public void AChosenFolderReplacesEveryAutomaticRoot()
    {
        var configuration = new MonitorConfiguration(new HashSet<SourceKind> { SourceKind.Omp }, OmpRoot: ProfileRoot);
        var locations = SourceLocations.Resolve(configuration);

        Assert.Equal([Path.GetFullPath(ProfileRoot)], locations.OmpRoots);
        Assert.False(locations.OmpAutomatic);
        Assert.Equal([Path.GetFullPath(ProfileRoot)], locations.FileRoots().Where(r => r.Family == RootFamily.Omp).Select(r => r.Path));
        Assert.True(SourceLocations.Resolve(MonitorConfiguration.Default).OmpAutomatic);
    }

    private MonitorCore Core(IResponseObserver? observer = null) => new(
        new MonitorConfiguration(new HashSet<SourceKind> { SourceKind.Omp }),
        new SourceLocations(string.Empty, string.Empty, string.Empty, string.Empty, [], OmpRoots: [DefaultRoot, ProfileRoot]),
        _todayStart,
        observer);

    private SpeedSnapshot Build(MonitorCore core) => core.BuildSnapshot(_now, _todayStart, MonitorState.Running, observeLive: false).Snapshot;

    private static string Write(string root, string relative, params string[] lines)
    {
        var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, string.Concat(lines.Select(l => l + "\n")));
        return Path.GetFullPath(path);
    }

    private static string Title() => """{"type":"title","v":1,"title":"Fix the build","updatedAt":"2026-10-20T08:00:05.000Z","pad":"     "}""";

    private static string Header(long ms, string cwd) =>
        "{\"type\":\"session\",\"version\":3,\"id\":\"01a1\",\"timestamp\":\"" + Ts.Iso(ms) + "\",\"cwd\":\"" + cwd + "\"}";

    private static string User(string id, long ms) =>
        "{\"type\":\"message\",\"id\":\"" + id + "\",\"timestamp\":\"" + Ts.Iso(ms)
        + "\",\"message\":{\"role\":\"user\",\"content\":[{\"type\":\"text\",\"text\":\"go\"}],\"timestamp\":" + Number(ms) + "}}";

    private static string Assistant(string id, long entryMs, long startedMs, double duration, long output, string stop, string? call = null)
    {
        var content = call is null
            ? "[{\"type\":\"text\",\"text\":\"done\"}]"
            : "[{\"type\":\"toolCall\",\"id\":\"" + call + "\",\"name\":\"read\",\"arguments\":{}}]";
        return "{\"type\":\"message\",\"id\":\"" + id + "\",\"timestamp\":\"" + Ts.Iso(entryMs)
            + "\",\"message\":{\"role\":\"assistant\",\"content\":" + content
            + ",\"api\":\"anthropic-messages\",\"provider\":\"anthropic\",\"model\":\"claude-opus-5-5\",\"usage\":{\"input\":10,\"output\":"
            + Number(output) + ",\"cacheRead\":100,\"cacheWrite\":0},\"stopReason\":\"" + stop + "\",\"timestamp\":" + Number(startedMs)
            + ",\"duration\":" + duration.ToString("0.###", CultureInfo.InvariantCulture) + "}}";
    }

    private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);

    private sealed class Observer : IResponseObserver
    {
        public List<(ParsedResponse Response, bool IsCompletion, bool Seeded)> Seen { get; } = [];

        public void OnResponse(ParsedResponse response, bool isCompletion, bool seeded) => Seen.Add((response, isCompletion, seeded));
    }
}
