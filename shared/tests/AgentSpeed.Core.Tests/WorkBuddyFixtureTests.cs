using AgentSpeed.Core.IO;
using AgentSpeed.Core.Monitoring;
using AgentSpeed.Core.Parsing;
using AgentSpeed.Core.WorkBuddy;

namespace AgentSpeed.Core.Tests;

/// <summary>parser-expected.json <c>workbuddy</c>: traces, discovery and roots, like the reference's checks (METRICS 4.9).</summary>
public sealed class WorkBuddyFixtureTests
{
    public static TheoryData<string, int, string, long?> Traces()
    {
        var data = new TheoryData<string, int, string, long?>();
        foreach (var trace in ParserFixtureTests.Expected.GetProperty("workbuddy").GetProperty("traces").EnumerateArray())
        {
            data.Add(
                trace.GetProperty("file").GetString()!,
                trace.GetProperty("afterLine").GetInt32(),
                trace.GetProperty("phase").GetString()!,
                ParserFixtureTests.NullableLong(trace.GetProperty("inFlightStartMs")));
        }

        return data;
    }

    [Fact]
    public void EveryTraceIsListed() => Assert.Equal(96, Traces().Count);

    [Theory]
    [MemberData(nameof(Traces))]
    public void PhaseAfterLineMatchesTrace(string file, int afterLine, string phase, long? inFlightStartMs)
    {
        var deduper = new Deduper();
        foreach (var element in ParserFixtureTests.Expected.GetProperty("order").EnumerateArray())
        {
            var relative = element.GetString()!;
            if (relative == file)
            {
                break;
            }

            ParserFixtureTests.ParseFixture(relative, deduper, new RecordingSink());
        }

        var parser = ParserFixtureTests.ParseFixture(file, deduper, new RecordingSink(), afterLine);

        Assert.Equal((phase, inFlightStartMs), (ParserFixtureTests.PhaseName(parser.Phase), parser.InFlightStartMs));
    }

    [Fact]
    public void DiscoveryFindsMainSessionsAndTheirSubAgentsOnly()
    {
        var expected = ParserFixtureTests.Expected.GetProperty("workbuddy").GetProperty("discover").EnumerateArray()
            .Select(e => e.GetString()!).ToList();

        var found = new[] { SourceKind.WorkBuddy, SourceKind.WorkBuddyAi }
            .SelectMany(source => TranscriptDiscovery.Enumerate(WorkBuddyFixture.RootOf(source), TranscriptNaming.WorkBuddySession))
            .Select(f => Path.GetRelativePath(FixtureLocator.FixturesDirectory, f.Path).Replace(Path.DirectorySeparatorChar, '/'))
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.Equal(expected, found);
    }

    [Theory]
    [InlineData("Users-dev-projects-atlas/99aa739a-ed94-446a-a6e0-be45f96cc697.jsonl", true)]
    [InlineData("Users-dev-projects-atlas/b09191b3/subagents/agent-66bc0cda.jsonl", true)]
    [InlineData("C-Users-dev-projects-orbit/7321aebd.jsonl", true)]
    [InlineData("cloud/064ef5c3.jsonl", false)]
    [InlineData("cloud/user-064ef5c3/064ef5c3.jsonl", false)]
    [InlineData("Users-dev-projects-atlas/.e564b408.jsonl", false)]
    [InlineData("Users-dev-projects-atlas/b09191b3/tool-results/call_a1.jsonl", false)]
    [InlineData("Users-dev-projects-atlas/b09191b3/subagents/workflows/wf_1/journal.jsonl", false)]
    [InlineData("Users-dev-projects-atlas/b09191b3/subagents/nested/agent-deep.jsonl", false)]
    [InlineData("Users-dev-projects-atlas/.hidden/subagents/agent-x.jsonl", false)]
    [InlineData(".slug/session.jsonl", false)]
    [InlineData("Users-dev-projects-atlas/b09191b3.meta.json", false)]
    [InlineData("Users-dev-projects-atlas/notes.txt", false)]
    [InlineData("stray.jsonl", false)]
    public void WatcherPathsFollowTheDiscoveryRules(string relative, bool accepted)
    {
        using var temp = new TempDirectory();
        var root = temp.Combine("projects");
        var path = temp.WriteFile("projects/" + relative, "{}\n");

        Assert.Equal(accepted, TranscriptDiscovery.IsAcceptablePath(root, path, TranscriptNaming.WorkBuddySession));
        Assert.Equal(accepted, TranscriptDiscovery.Enumerate(root, TranscriptNaming.WorkBuddySession).Any(f => f.Path == path));
    }

    /// <summary>The cases of the reference's <c>check_workbuddy_roots</c>.</summary>
    [Fact]
    public void RootsAreTheProjectsFolderOfTheDataFolderOrTheChosenOne()
    {
        var home = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "home", "dev"));
        Assert.Equal(Path.Combine(home, ".workbuddy", "projects"),
            SourceLocations.WorkBuddyRootOf(WorkBuddyPaths.DefaultDataFolder(SourceKind.WorkBuddy, home)));
        Assert.Equal(Path.Combine(home, ".workbuddy-ai", "projects"),
            SourceLocations.WorkBuddyRootOf(WorkBuddyPaths.DefaultDataFolder(SourceKind.WorkBuddyAi, home)));

        var data = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "data"));
        var configuration = new MonitorConfiguration(
            new HashSet<SourceKind>(Enum.GetValues<SourceKind>()),
            WorkBuddyRoot: Path.Combine(data, "wb") + Path.DirectorySeparatorChar,
            WorkBuddyAiRoot: Path.Combine(data, "wb-ai", "projects"));
        var locations = SourceLocations.Resolve(configuration);

        Assert.Equal((Path.Combine(data, "wb", "projects"), Path.Combine(data, "wb-ai", "projects")),
            (locations.WorkBuddyRoot, locations.WorkBuddyAiRoot));
        Assert.Equal(
            [(RootFamily.WorkBuddy, locations.WorkBuddyRoot), (RootFamily.WorkBuddyAi, locations.WorkBuddyAiRoot)],
            locations.FileRoots().Where(r => r.Family is RootFamily.WorkBuddy or RootFamily.WorkBuddyAi));
    }

    /// <summary>The app sets these only on itself and its children; the CLI's own variable also moves the standalone CLI.</summary>
    [Fact]
    public void TheAppsConfigVariablesAreNotHonoured()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var elsewhere = Path.Combine(Path.GetTempPath(), "agent-speed-elsewhere");
        Environment.SetEnvironmentVariable("WORKBUDDY_CONFIG_DIR", elsewhere);
        Environment.SetEnvironmentVariable("CODEBUDDY_CONFIG_DIR", elsewhere);
        try
        {
            var locations = SourceLocations.Resolve(MonitorConfiguration.Default);

            Assert.Equal(Path.GetFullPath(Path.Combine(home, ".workbuddy", "projects")), locations.WorkBuddyRoot);
            Assert.Equal(Path.GetFullPath(Path.Combine(home, ".workbuddy-ai", "projects")), locations.WorkBuddyAiRoot);
        }
        finally
        {
            Environment.SetEnvironmentVariable("WORKBUDDY_CONFIG_DIR", null);
            Environment.SetEnvironmentVariable("CODEBUDDY_CONFIG_DIR", null);
        }
    }

    [Fact]
    public void TheDataFolderOfARootOrSettingIsWhatDetectsTheSource()
    {
        var data = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "data", "wb"));
        var projects = Path.Combine(data, "projects");

        Assert.Equal((data, data, data), (WorkBuddyPaths.DataFolderOf(projects), WorkBuddyPaths.DataFolderOf(data),
            WorkBuddyPaths.DataFolderOf(projects + Path.DirectorySeparatorChar)));
        Assert.Equal(data, new SourceRoot(RootFamily.WorkBuddy, projects, enabled: true).DetectionPath);
        Assert.Equal(projects, new SourceRoot(RootFamily.Claude, projects, enabled: true).DetectionPath);
        Assert.Equal(data, LiveMonitor.WorkBuddyDataFolder(SourceKind.WorkBuddyAi, projects));
        Assert.Equal(LiveMonitor.DefaultWorkBuddyAiFolder, LiveMonitor.WorkBuddyDataFolder(SourceKind.WorkBuddyAi, null));
        Assert.EndsWith(".workbuddy", LiveMonitor.DefaultWorkBuddyFolder, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryFixtureResponseBelongsToItsEdition()
    {
        foreach (var name in ParserFixtureTests.Expected.GetProperty("order").EnumerateArray().Select(e => e.GetString()!)
                     .Where(n => n.StartsWith("workbuddy/", StringComparison.Ordinal)))
        {
            var expected = WorkBuddyFixture.SourceOf(name) == SourceKind.WorkBuddy ? "workbuddy" : "workbuddyAi";
            Assert.Equal(expected, ParserFixtureTests.Expected.GetProperty("files").GetProperty(name).GetProperty("source").GetString());
        }
    }
}
