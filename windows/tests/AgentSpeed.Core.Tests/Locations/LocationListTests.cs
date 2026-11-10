using System.Text.Json;
using AgentSpeed.Core.Locations;
using AgentSpeed.Core.Monitoring;

namespace AgentSpeed.Core.Tests.Locations;

/// <summary>METRICS 2.1: the location lists, their stored form, the editing rules and the roots they name (issue #45).</summary>
public sealed class LocationListTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    /// <summary>An absolute path on this system's first drive (<c>/w/…</c>, <c>C:\w\…</c>).</summary>
    private static string P(params string[] parts) =>
        Path.Combine([Path.GetPathRoot(Path.GetTempPath())!, "w", .. parts]);

    private static DataLocation At(string path) => DataLocation.At(path);

    private static readonly DataLocation Auto = DataLocation.Automatic;

    private static LocationLists Lists(LocationFolder folder, params DataLocation[] list) => LocationLists.Automatic.With(folder, list);

    /// <summary>A configuration whose OpenClaw list is an empty scratch folder, so Codex's automatic entry is its sessions folder alone.</summary>
    private MonitorConfiguration Isolated(LocationLists lists) =>
        new(new HashSet<SourceKind>(Enum.GetValues<SourceKind>()))
        {
            Locations = lists.With(LocationFolder.OpenClaw, [At(Directory.CreateDirectory(_temp.Combine("no-claw")).FullName)]),
        };

    // Lists

    [Fact]
    public void EveryFolderStartsAutomaticOnly()
    {
        var lists = LocationLists.Automatic;
        Assert.All(LocationFolders.All, folder => Assert.Equal([Auto], lists[folder]));
        Assert.True(lists.IsAutomaticOnly);
        Assert.Equal([SourceKind.ClaudeCode, SourceKind.ClaudeDesktop], LocationFolder.Claude.Sources());
        Assert.All(Enum.GetValues<SourceKind>(), kind => Assert.Contains(kind, LocationFolders.Of(kind).Sources()));
        Assert.Equal(19, LocationFolders.All.Count);
        Assert.Equal(["codex", "claude", "dsh", "opencode", "gemini", "omp", "antigravity", "workbuddy", "workbuddyAi", "zcode", "kimi",
            "grokBuild", "pi", "openclaw", "hermes", "droid", "devin", "qoder", "qoderCli"], LocationFolders.All.Select(f => f.Key()));
    }

    [Fact]
    public void ListsAreNormalized()
    {
        var sep = Path.DirectorySeparatorChar;
        var lists = Lists(LocationFolder.Codex, At(P("codex", "sessions") + sep), Auto, At(P("codex", ".", "sessions")), At("relative"),
            At(P("x", "..", "y")));
        Assert.Equal([Auto, At(P("codex", "sessions")), At(P("y"))], lists[LocationFolder.Codex]);
        Assert.Equal(LocationLists.MaxEntries, Lists(LocationFolder.Codex, [.. Enumerable.Range(0, 12).Select(i => At(P($"{i}")))])[LocationFolder.Codex].Count);
        var empty = Lists(LocationFolder.Codex, At("relative"));
        Assert.Equal([Auto], empty[LocationFolder.Codex]);
        Assert.True(empty.IsAutomaticOnly);
    }

    [Fact]
    public void ASingleFolderOfAnEarlierVersionBecomesTheWholeList()
    {
        var database = P("agent", "openclaw-agent.sqlite");
        var lists = LocationLists.FromLegacy([new(LocationFolder.Codex, P("codex", "sessions")), new(LocationFolder.OpenClaw, database),
            new(LocationFolder.Dsh, "relative"), new(LocationFolder.Kimi, "")]);
        Assert.Equal([At(P("codex", "sessions"))], lists[LocationFolder.Codex]);
        Assert.Equal([At(database)], lists[LocationFolder.OpenClaw]);
        Assert.Equal([Auto], lists[LocationFolder.Claude]);
        Assert.Equal([Auto], lists[LocationFolder.Dsh]);
        Assert.Equal([Auto], lists[LocationFolder.Kimi]);
        Assert.Equal("droidRoot", LocationFolder.Droid.LegacyKey());
        Assert.Equal("workbuddyAiRoot", LocationFolder.WorkBuddyAi.LegacyKey());
    }

    // Document

    [Fact]
    public void TheDocumentRoundTripsAndLeavesAutomaticOnlyListsOut()
    {
        var work = P("me", "work", "codex", "sessions");
        var hermes = P("srv", "hermes");
        var lists = Lists(LocationFolder.Codex, Auto, At(work)).With(LocationFolder.Hermes, [At(hermes)]);
        var json = lists.ToJson();
        Assert.Equal(
            $$$"""{"version":1,"lists":{"codex":[{"automatic":true},{"path":{{{JsonSerializer.Serialize(work)}}}}],"hermes":[{"path":{{{JsonSerializer.Serialize(hermes)}}}}]}}""",
            json);
        Assert.Equal(lists, LocationLists.Parse(json));
        Assert.True(LocationLists.Parse(LocationLists.Automatic.ToJson())!.IsAutomaticOnly);
        Assert.Equal("""{"version":1,"lists":{}}""", LocationLists.Automatic.ToJson());
    }

    [Fact]
    public void DecodingIsTolerant()
    {
        var a = P("a");
        var json = $$$"""
            {"version":7,"future":true,"lists":{
              "codex":[{"path":"relative"},{"kind":"other"},{"automatic":false},{"path":{{{JsonSerializer.Serialize(a)}}}},
                       {"path":{{{JsonSerializer.Serialize(a + Path.DirectorySeparatorChar)}}}},{"automatic":true},7,null],
              "claude":"not a list",
              "newSource":[{"path":"/n"}]
            }}
            """;
        var decoded = LocationLists.Parse(json);
        Assert.NotNull(decoded);
        Assert.Equal([Auto, At(a)], decoded[LocationFolder.Codex]);
        Assert.Equal([Auto], decoded[LocationFolder.Claude]);
        Assert.Contains("\"newSource\":[{\"path\":\"/n\"}]", decoded.ToJson(), StringComparison.Ordinal);
        Assert.Null(LocationLists.Parse("[1]"));
        Assert.Null(LocationLists.Parse("not json"));
        Assert.True(LocationLists.Parse("{}")!.IsAutomaticOnly);
        Assert.True(LocationLists.Parse("""{"lists":[1]}""")!.IsAutomaticOnly);
    }

    // Editing

    private static readonly string[] CodexKeys = [P("u", ".codex", "sessions"), P("u", ".openclaw", "agents", "a", "agent", "codex-home", "sessions")];

    [Fact]
    public void AddingAppendsAndRefusesWhatIsAlreadyRead()
    {
        var lists = LocationLists.Automatic;
        (lists, var outcome) = lists.Add(P("codex", "sessions"), LocationFolder.Codex, CodexKeys);
        Assert.Equal(LocationAddOutcome.Added, outcome);
        Assert.Equal([Auto, At(P("codex", "sessions"))], lists[LocationFolder.Codex]);
        Assert.Equal(LocationAddOutcome.AlreadyListed, lists.Add(P("codex", "sessions") + Path.DirectorySeparatorChar, LocationFolder.Codex, CodexKeys).Outcome);
        Assert.Equal(LocationAddOutcome.AlreadyAutomatic, lists.Add(CodexKeys[0], LocationFolder.Codex, CodexKeys).Outcome);
        Assert.Equal(LocationAddOutcome.AlreadyAutomatic, lists.Add(CodexKeys[1], LocationFolder.Codex, CodexKeys).Outcome);
        Assert.Equal(LocationAddOutcome.Invalid, lists.Add("relative", LocationFolder.Codex, CodexKeys).Outcome);
        Assert.Equal(LocationAddOutcome.Invalid, lists.Add(null, LocationFolder.Codex, CodexKeys).Outcome);
        Assert.Equal(2, lists[LocationFolder.Codex].Count);
    }

    [Fact]
    public void OpenClawsCodexHomesAreAutomaticCodexKeys()
    {
        var claw = _temp.Combine("claw");
        _temp.WriteFile("claw/agents/main/agent/openclaw-agent.sqlite", string.Empty);
        var lists = Lists(LocationFolder.OpenClaw, At(claw));
        var home = Path.Combine(claw, "agents", "main", "agent", "codex-home", "sessions");
        Assert.Contains(LocationPaths.Resolved(home), lists.AutomaticKeys(LocationFolder.Codex));
        Assert.Equal(LocationPaths.Resolved(LiveMonitor.DefaultCodexRoot), lists.AutomaticKeys(LocationFolder.Codex)[0]);
        Assert.Equal(LocationAddOutcome.AlreadyAutomatic, lists.Add(home, LocationFolder.Codex).Outcome);
        Assert.Equal(LocationAddOutcome.Added, lists.With(LocationFolder.Codex, [At(P("x"))]).Add(home, LocationFolder.Codex).Outcome);
    }

    [Fact]
    public void TheDefaultLocationBringsTheAutomaticEntryBack()
    {
        var lists = LocationLists.FromLegacy([new(LocationFolder.Codex, P("codex", "sessions"))]);
        var (next, outcome) = lists.Add(CodexKeys[0], LocationFolder.Codex, CodexKeys);
        Assert.Equal(LocationAddOutcome.RestoredAutomatic, outcome);
        Assert.Equal([Auto, At(P("codex", "sessions"))], next[LocationFolder.Codex]);
        Assert.Equal(LocationAddOutcome.RestoredAutomatic, LocationLists.Automatic.With(LocationFolder.Claude, [At(P("c"))])
            .Add(LiveMonitor.DefaultClaudeRoot, LocationFolder.Claude).Outcome);
    }

    [Fact]
    public void WhereTheAutomaticSetIsWiderTheDefaultIsAddedAsAFolder()
    {
        foreach (var folder in new[] { LocationFolder.Omp, LocationFolder.Antigravity, LocationFolder.Pi, LocationFolder.OpenClaw })
        {
            var lists = LocationLists.FromLegacy([new(folder, P("other"))]);
            var (next, outcome) = lists.Add(P("u", "default"), folder, [P("u", "default"), P("u", "profile")]);
            Assert.Equal(LocationAddOutcome.Added, outcome);
            Assert.Equal([At(P("other")), At(P("u", "default"))], next[folder]);
        }

        Assert.Equal([LocationFolder.Omp, LocationFolder.Antigravity, LocationFolder.Pi, LocationFolder.OpenClaw],
            LocationFolders.All.Where(f => f.AutomaticIsWiderThanDefault()));
    }

    [Fact]
    public void AtMostEightEntriesAndAtLeastOne()
    {
        string[] keys = [P("u", ".dsh", "sessions")];
        var lists = LocationLists.Automatic;
        for (var index = 1; index < LocationLists.MaxEntries; index++)
        {
            (lists, var outcome) = lists.Add(P($"{index}"), LocationFolder.Dsh, keys);
            Assert.Equal(LocationAddOutcome.Added, outcome);
        }

        Assert.Equal(8, lists[LocationFolder.Dsh].Count);
        Assert.False(lists.CanAdd(LocationFolder.Dsh));
        Assert.Equal(LocationAddOutcome.Full, lists.Add(P("9"), LocationFolder.Dsh, keys).Outcome);
        lists = lists.Remove(Auto, LocationFolder.Dsh);
        Assert.Equal(LocationAddOutcome.RestoredAutomatic, lists.Add(keys[0], LocationFolder.Dsh, keys).Outcome);
        Assert.True(lists.CanReset(LocationFolder.Dsh));
        lists = lists.Reset(LocationFolder.Dsh);
        Assert.Equal([Auto], lists[LocationFolder.Dsh]);
        Assert.False(lists.CanReset(LocationFolder.Dsh));
        Assert.False(lists.CanRemove(LocationFolder.Dsh));
        Assert.Equal([Auto], lists.Remove(Auto, LocationFolder.Dsh)[LocationFolder.Dsh]);
    }

    [Fact]
    public void RemovingTheAutomaticEntryKeepsTheFolders()
    {
        var lists = Lists(LocationFolder.Claude, Auto, At(P("a")), At(P("b")));
        lists = lists.Remove(At(P("a")), LocationFolder.Claude).Remove(Auto, LocationFolder.Claude);
        Assert.Equal([At(P("b"))], lists[LocationFolder.Claude]);
        Assert.False(lists.CanRemove(LocationFolder.Claude));
    }

    [Fact]
    public void LocationsThatNameTheSameRootAreTheSame()
    {
        _temp.WriteFile(".factory/sessions/keep", string.Empty);
        _temp.WriteFile("oc/opencode.db", string.Empty);
        _temp.WriteFile("z/.zcode/cli/db/db.sqlite", string.Empty);
        _temp.WriteFile("claw/agents/main/agent/openclaw-agent.sqlite", string.Empty);
        (LocationFolder, string, string)[] cases =
        [
            (LocationFolder.WorkBuddy, P(".workbuddy"), P(".workbuddy", "projects")),
            (LocationFolder.WorkBuddyAi, P(".workbuddy-ai") + Path.DirectorySeparatorChar, P(".workbuddy-ai", "projects")),
            (LocationFolder.Kimi, P(".kimi-code"), P(".kimi-code", "sessions")),
            (LocationFolder.GrokBuild, P(".grok"), P(".grok", "sessions")),
            (LocationFolder.Antigravity, P(".gemini", "antigravity-cli"), P(".gemini", "antigravity-cli", "conversations")),
            (LocationFolder.Droid, _temp.Combine(".factory"), _temp.Combine(".factory", "sessions")),
            (LocationFolder.OpenCode, _temp.Combine("oc"), _temp.Combine("oc", "opencode.db")),
            (LocationFolder.ZCode, _temp.Combine("z"), _temp.Combine("z", ".zcode", "cli", "db", "db.sqlite")),
            (LocationFolder.ZCode, _temp.Combine("z", ".zcode", "cli", "db"), _temp.Combine("z", ".zcode", "cli", "db", "db.sqlite")),
            (LocationFolder.OpenClaw, _temp.Combine("claw", "agents", "main", "agent"),
                _temp.Combine("claw", "agents", "main", "agent", "openclaw-agent.sqlite")),
        ];
        foreach (var (folder, first, second) in cases)
        {
            var (lists, outcome) = LocationLists.Automatic.Add(first, folder, [P("elsewhere")]);
            Assert.True(outcome == LocationAddOutcome.Added, $"{folder} {first}");
            Assert.True(lists.Add(second, folder, [P("elsewhere")]).Outcome == LocationAddOutcome.AlreadyListed, $"{folder} {second}");
        }

        var (hermes, _) = LocationLists.Automatic.Add(P(".hermes"), LocationFolder.Hermes, [P("elsewhere")]);
        Assert.Equal(LocationAddOutcome.Added, hermes.Add(P(".hermes", "logs"), LocationFolder.Hermes, [P("elsewhere")]).Outcome);
    }

    [Fact]
    public void ASymlinkToAListedFolderIsTheSameLocation()
    {
        Directory.CreateDirectory(_temp.Combine("real", "sessions"));
        Directory.CreateSymbolicLink(_temp.Combine("link"), _temp.Combine("real"));
        var (lists, outcome) = LocationLists.Automatic.Add(_temp.Combine("real", "sessions"), LocationFolder.Codex, [P("elsewhere")]);
        Assert.Equal(LocationAddOutcome.Added, outcome);
        Assert.Equal(LocationAddOutcome.AlreadyListed, lists.Add(_temp.Combine("link", "sessions"), LocationFolder.Codex, [P("elsewhere")]).Outcome);
        Assert.Equal(LocationPaths.Resolved(_temp.Combine("real", "sessions")), LocationPaths.Resolved(_temp.Combine("link", "sessions")));
    }

    [Fact]
    public void AutomaticKeysFollowTheEnvironment()
    {
        var lists = Lists(LocationFolder.OpenClaw, At(_temp.Combine("no-claw")));
        Assert.Equal([LocationPaths.Resolved(LiveMonitor.DefaultCodexRoot)], lists.AutomaticKeys(LocationFolder.Codex));
        Assert.Equal(LocationPaths.Resolved(LiveMonitor.DefaultHermesRoot), lists.AutomaticKeys(LocationFolder.Hermes)[0]);
        Assert.Equal(LocationPaths.Resolved(Path.Combine(LiveMonitor.DefaultOpenCodeRoot, "opencode.db")),
            lists.AutomaticKeys(LocationFolder.OpenCode)[0]);
        Assert.All(lists.AutomaticKeys(LocationFolder.Antigravity), key => Assert.EndsWith("conversations", key, StringComparison.Ordinal));
        Assert.Equal(LocationPaths.Resolved(Path.Combine(LiveMonitor.DefaultWorkBuddyFolder, "projects")),
            lists.AutomaticKeys(LocationFolder.WorkBuddy)[0]);
    }

    // Roots

    [Fact]
    public void ASourceReadsTheUnionOfItsEntriesInListOrder()
    {
        var automatic = LocationPaths.Normalized(LiveMonitor.DefaultCodexRoot);
        var configuration = Isolated(Lists(LocationFolder.Codex, Auto, At(P("codex", "sessions")), At(automatic), At(P("b"))));
        var locations = SourceLocations.Resolve(configuration);
        Assert.Equal([automatic, P("codex", "sessions"), P("b")], locations.FileRoots().Where(r => r.Family == RootFamily.Codex).Select(r => r.Path));
        var without = SourceLocations.Resolve(Isolated(Lists(LocationFolder.Codex, At(P("a")), At(P("b")))));
        Assert.Equal([P("a"), P("b")], without.FileRoots().Where(r => r.Family == RootFamily.Codex).Select(r => r.Path));
        Assert.NotEqual(without.Signature(), locations.Signature());
        Assert.Equal(SourceLocations.Resolve(Isolated(LocationLists.Automatic)).Signature(),
            SourceLocations.Resolve(Isolated(Lists(LocationFolder.Claude, At(LocationPaths.Normalized(LiveMonitor.DefaultClaudeRoot))))).Signature());
        Assert.Equal(SourceLocations.Resolve(Isolated(LocationLists.Automatic)).Signature(),
            SourceLocations.Resolve(Isolated(Lists(LocationFolder.Codex, Auto, At(automatic)))).Signature());
    }

    [Fact]
    public void EachUserLocationNamesItsSourcesRoot()
    {
        var locations = SourceLocations.Resolve(Isolated(LocationLists.Automatic
            .With(LocationFolder.WorkBuddy, [Auto, At(P(".workbuddy"))])
            .With(LocationFolder.Kimi, [At(P(".kimi-code")), At(P("other", "sessions"))])
            .With(LocationFolder.Antigravity, [At(P(".gemini", "antigravity-ide", "conversations")), At(P(".gemini", "antigravity-backup"))])));
        IEnumerable<string> RootsOf(RootFamily family) => locations.FileRoots().Where(r => r.Family == family).Select(r => r.Path);
        Assert.Equal([LocationPaths.Normalized(Path.Combine(LiveMonitor.DefaultWorkBuddyFolder, "projects")), P(".workbuddy", "projects")],
            RootsOf(RootFamily.WorkBuddy));
        Assert.Equal([P(".kimi-code", "sessions"), P("other", "sessions")], RootsOf(RootFamily.Kimi));
        Assert.Equal([P(".gemini", "antigravity-ide", "conversations")], RootsOf(RootFamily.Antigravity));
    }

    [Fact]
    public void CodexReadsOpenClawsCodexHomesOnlyWithTheAutomaticEntry()
    {
        var claw = _temp.Combine("claw");
        _temp.WriteFile("claw/agents/main/agent/openclaw-agent.sqlite", string.Empty);
        var home = Path.Combine(claw, "agents", "main", "agent", "codex-home", "sessions");
        var withAutomatic = new MonitorConfiguration(new HashSet<SourceKind> { SourceKind.Codex })
        {
            Locations = Lists(LocationFolder.OpenClaw, At(claw)).With(LocationFolder.Codex, [Auto, At(P("codex"))]),
        };
        Assert.Equal([LocationPaths.Normalized(LiveMonitor.DefaultCodexRoot), home, P("codex")],
            SourceLocations.Resolve(withAutomatic).FileRoots().Where(r => r.Family == RootFamily.Codex).Select(r => LocationPaths.Normalized(r.Path)));
        var userOnly = withAutomatic with { Locations = withAutomatic.Locations.With(LocationFolder.Codex, [At(P("codex"))]) };
        Assert.Equal([P("codex")], SourceLocations.Resolve(userOnly).FileRoots().Where(r => r.Family == RootFamily.Codex).Select(r => r.Path));
    }

    [Fact]
    public void GeminiSettingsFollowTheRootTheyBelongTo()
    {
        var locations = SourceLocations.Resolve(Isolated(Lists(LocationFolder.Gemini, Auto, At(P(".gemini", "tmp")), At(P("plain")))));
        var automatic = LiveMonitor.DefaultGeminiRoots.Select(LocationPaths.Normalized).ToList();
        Assert.Equal([.. automatic, P(".gemini", "tmp"), P("plain")], locations.GeminiRoots);
        var shared = Path.Combine(Path.GetDirectoryName(LocationPaths.Normalized(LiveMonitor.DefaultGeminiRoot))!, "settings.json");
        Assert.All(automatic, root => Assert.Equal(shared, locations.GeminiSettingsOf(root)));
        Assert.Equal(P(".gemini", "settings.json"), locations.GeminiSettingsOf(P(".gemini", "tmp")));
        Assert.Equal(P("plain", "settings.json"), locations.GeminiSettingsOf(P("plain")));
    }

    [Fact]
    public void DatabaseSourcesReadEachDatabaseOnce()
    {
        _temp.WriteFile("a/opencode.db", string.Empty);
        _temp.WriteFile("b/opencode.db", string.Empty);
        Directory.CreateSymbolicLink(_temp.Combine("c"), _temp.Combine("a"));
        var locations = SourceLocations.Resolve(Isolated(Lists(LocationFolder.OpenCode,
            At(_temp.Combine("a")), At(_temp.Combine("a", "opencode.db")), At(_temp.Combine("b")), At(_temp.Combine("c")))));
        Assert.Equal([_temp.Combine("a"), _temp.Combine("b")], locations.OpenCodeRoots);
        var zcode = SourceLocations.Resolve(Isolated(Lists(LocationFolder.ZCode, Auto, At(P("zcode")))));
        Assert.Equal([LocationPaths.Normalized(LiveMonitor.DefaultZCodeRoot), P("zcode")], zcode.ZCodeRoots);
    }

    [Fact]
    public void OpenClawAndHermesReadEveryEntry()
    {
        var database = P("agent", "openclaw-agent.sqlite");
        var configuration = Isolated(LocationLists.Automatic) with
        {
            Locations = LocationLists.Automatic
                .With(LocationFolder.OpenClaw, [Auto, At(database), At(LocationPaths.Normalized(LiveMonitor.DefaultOpenClawRoot))])
                .With(LocationFolder.Hermes, [Auto, At(P("hermes", "logs")), At(LocationPaths.Normalized(LiveMonitor.DefaultHermesRoot))]),
        };
        var locations = SourceLocations.Resolve(configuration);
        var automatic = LiveMonitor.DefaultOpenClawRoots.Select(LocationPaths.Normalized).ToList();
        Assert.Equal([.. automatic, database], locations.OpenClawRoots);
        Assert.True(locations.OpenClawAutomatic);
        Assert.Equal([database, LocationPaths.Normalized(LiveMonitor.DefaultOpenClawRoot)], locations.OpenClawUserRoots);
        Assert.Equal([(LocationPaths.Normalized(LiveMonitor.DefaultHermesRoot), false), (P("hermes", "logs"), true)], locations.HermesRoots);
    }
}
