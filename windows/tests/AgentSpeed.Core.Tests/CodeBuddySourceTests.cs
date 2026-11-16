using AgentSpeed.Core.IO.Sqlite;
using AgentSpeed.Core.Locations;
using AgentSpeed.Core.Monitoring;
using AgentSpeed.Core.WorkBuddy;

namespace AgentSpeed.Core.Tests;

/// <summary>
/// CodeBuddy Code in the monitor and the location lists (METRICS 2, 2.1, 4.20, 6.7): detection by the root, main sessions only,
/// sub-agents under their main session, the config folder's configured effort and no session database.
/// </summary>
public sealed class CodeBuddySourceTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly long _now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    private readonly long _todayStart;

    public CodeBuddySourceTests()
    {
        _todayStart = _now - 3_600_000;
    }

    private string Config => _temp.Combine(".codebuddy");

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void SessionsSubAgentsAndTheConfiguredEffortOfTheConfigFolder()
    {
        var t = _now - 200_000;
        var main = Write("projects/Users-me-nova/s1.jsonl",
            WorkBuddyLines.User(t, "u1"),
            WorkBuddyLines.ToolCall(t + 2000, "c1", "m1", "call_task", WorkBuddyLines.Usage(4000, 70)));
        var sub = Write("projects/Users-me-nova/s1/subagents/agent-1.jsonl",
            WorkBuddyLines.User(t + 2100, "s1"),
            WorkBuddyLines.Say(t + 5000, "s2", "m2", WorkBuddyLines.Usage(3000, 120), model: "glm-5.3-flash"));
        Write("projects/Users-me-nova/s1/stdio/2026-10-23T09-00-00-000Z-4242.jsonl", WorkBuddyLines.Say(t, "x", "m9", WorkBuddyLines.Usage(1, 1)));
        _temp.WriteFile(".codebuddy/settings.json", """{"reasoningEffort":"high","cleanupPeriodDays":30}""");

        // A session database there is WorkBuddy's alone: CodeBuddy Code keeps a session's effort in memory.
        using (var writer = SqliteConnection.Open(Path.Combine(Config, "workbuddy.db"), SqliteOpenMode.ReadWriteCreate))
        {
            writer.Execute("CREATE TABLE sessions (id TEXT PRIMARY KEY, thought_level TEXT); INSERT INTO sessions VALUES ('s1', 'max')");
        }

        using var core = Core();
        Assert.Equal(2, core.Seed(_now, _todayStart, CancellationToken.None).FilesDiscovered);
        var snapshot = Build(core);
        var agents = snapshot.Agents.ToDictionary(a => a.Id);

        Assert.Equal((main, (string?)null), (agents[sub].ParentId, agents[main].ParentId));
        Assert.Equal((SourceKind.CodeBuddyCode, false, true), (agents[main].Source, agents[main].IsSubagent, agents[sub].IsSubagent));
        Assert.All(agents.Values, agent => Assert.Equal(("high", "configured"), Effort(agent)));
        var status = snapshot.Sources.Single(s => s.Kind == SourceKind.CodeBuddyCode);
        Assert.Equal((true, 1, 190L, Path.Combine(Config, "projects")), (status.Available, status.SessionCount, status.Today.Output, status.RootPath));
    }

    /// <summary>The CodeBuddy IDE and its plugins keep settings in <c>~/.codebuddy</c> without transcripts: only the root detects the source.</summary>
    [Fact]
    public void AConfigFolderWithoutProjectsIsNotFound()
    {
        _temp.WriteFile(".codebuddy/settings.json", "{}");
        using var core = Core();
        core.Seed(_now, _todayStart, CancellationToken.None);

        Assert.False(Build(core).Sources.Single(s => s.Kind == SourceKind.CodeBuddyCode).Available);
        Assert.False(core.AnyEnabledRootAvailable);
        Assert.False(AutomaticLocations.IsReadable(LocationFolder.CodeBuddyCode, Config));
        Assert.True(AutomaticLocations.Exists(LocationFolder.CodeBuddyCode, Config));

        Write("projects/Users-me-nova/s1.jsonl", WorkBuddyLines.User(_now - 6000, "u1"), WorkBuddyLines.Say(_now - 2000, "a1", "m1", WorkBuddyLines.Usage(800, 64)));
        Assert.True(core.Rediscover(_now, _todayStart, CancellationToken.None));
        var after = Build(core).Sources.Single(s => s.Kind == SourceKind.CodeBuddyCode);
        Assert.Equal((true, 1, 64L), (after.Available, after.SessionCount, after.Today.Output));
        Assert.True(AutomaticLocations.IsReadable(LocationFolder.CodeBuddyCode, Config));
        Assert.True(AutomaticLocations.IsReadable(LocationFolder.CodeBuddyCode, Path.Combine(Config, "projects")));
    }

    [Fact]
    public void ADisabledSourceIsNotRead()
    {
        Write("projects/Users-me-nova/s1.jsonl", WorkBuddyLines.User(_now - 6000, "u1"), WorkBuddyLines.Say(_now - 2000, "a1", "m1", WorkBuddyLines.Usage(800, 64)));
        using var core = new MonitorCore(new MonitorConfiguration(new HashSet<SourceKind> { SourceKind.WorkBuddy }),
            new SourceLocations("", "", "", "", []) { CodeBuddyCodeRoots = [Path.Combine(Config, "projects")] }, _todayStart);
        core.Seed(_now, _todayStart, CancellationToken.None);

        var status = Build(core).Sources.Single(s => s.Kind == SourceKind.CodeBuddyCode);
        Assert.Equal((false, 0L, 0), (status.Enabled, status.Today.Output, core.TrackedFileCount));
    }

    [Fact]
    public void AConfigFolderAndItsProjectsFolderAreOneLocation()
    {
        string Key(string relative) => LocationLists.LocationKey(_temp.Combine(relative.Split('/')), LocationFolder.CodeBuddyCode);
        Assert.Equal(Key(".codebuddy"), Key(".codebuddy/projects"));
        Assert.Equal(Key(".codebuddy"), Key(".codebuddy/"));
        Assert.NotEqual(Key(".codebuddy"), Key(".workbuddy"));
        Assert.Equal([SourceKind.CodeBuddyCode], LocationFolder.CodeBuddyCode.Sources());
        Assert.Equal((LocationFolder.CodeBuddyCode, "codebuddyCode"), (LocationFolders.Of(SourceKind.CodeBuddyCode), LocationFolder.CodeBuddyCode.Key()));
        Assert.False(LocationFolder.CodeBuddyCode.AutomaticIsWiderThanDefault());
        Assert.False(AutomaticLocations.AcceptsFile(LocationFolder.CodeBuddyCode));

        var automatic = new[] { LocationLists.LocationKey(_temp.Combine(".codebuddy"), LocationFolder.CodeBuddyCode) };
        var lists = LocationLists.Automatic;
        Assert.Equal(LocationAddOutcome.AlreadyAutomatic, lists.Add(_temp.Combine(".codebuddy", "projects"), LocationFolder.CodeBuddyCode, automatic).Outcome);
        (lists, var added) = lists.Add(_temp.Combine("work", "cb"), LocationFolder.CodeBuddyCode, automatic);
        Assert.Equal(LocationAddOutcome.Added, added);
        Assert.Equal(LocationAddOutcome.AlreadyListed, lists.Add(_temp.Combine("work", "cb", "projects"), LocationFolder.CodeBuddyCode, automatic).Outcome);

        var configuration = new MonitorConfiguration(new HashSet<SourceKind> { SourceKind.CodeBuddyCode })
        {
            Locations = LocationLists.Automatic.With(LocationFolder.CodeBuddyCode,
                [DataLocation.Automatic, DataLocation.At(_temp.Combine("work", "cb")), DataLocation.At(_temp.Combine("cli", "projects"))]),
        };
        var resolved = SourceLocations.Resolve(configuration);
        Assert.Equal([LiveMonitor.DefaultCodeBuddyCodeRoot, _temp.Combine("work", "cb", "projects"), _temp.Combine("cli", "projects")], resolved.CodeBuddyCodeRoots);
        Assert.Equal(resolved.CodeBuddyCodeRoots, resolved.FileRoots().Where(r => r.Family == RootFamily.CodeBuddyCode).Select(r => r.Path));
        Assert.Equal([Path.Combine(Config, "projects")],
            SourceLocations.Resolve(new MonitorConfiguration(new HashSet<SourceKind>(), CodeBuddyCodeRoot: Config)).CodeBuddyCodeRoots);
    }

    [Fact]
    public void TheAutomaticRootFollowsThisProcesssEnvironment()
    {
        Assert.Equal([LiveMonitor.DefaultCodeBuddyCodeRoot], AutomaticLocations.All(LocationFolder.CodeBuddyCode));
        Assert.Equal("projects", Path.GetFileName(LiveMonitor.DefaultCodeBuddyCodeRoot));
        if (Environment.GetEnvironmentVariable(CodeBuddyCodePaths.ConfigVariable) is not { Length: > 0 })
        {
            Assert.Equal(".codebuddy", Path.GetFileName(Path.GetDirectoryName(LiveMonitor.DefaultCodeBuddyCodeRoot)));
        }
    }

    private static (string, string) Effort(AgentStatus agent) => agent.Current(ExecutionModeKind.Effort) is var value ? (value.Mode, value.Evidence) : default;

    private MonitorCore Core() => new(
        new MonitorConfiguration(new HashSet<SourceKind> { SourceKind.CodeBuddyCode }),
        new SourceLocations("", "", "", "", []) { CodeBuddyCodeRoots = [SourceLocations.Normalize(CodeBuddyCodePaths.RootOf(Config))] },
        _todayStart);

    private SpeedSnapshot Build(MonitorCore core) => core.BuildSnapshot(_now, _todayStart, MonitorState.Running, observeLive: false).Snapshot;

    private string Write(string relative, params string[] lines)
    {
        var path = Path.Combine(Config, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, string.Concat(lines.Select(l => l + "\n")));
        return Path.GetFullPath(path);
    }
}
