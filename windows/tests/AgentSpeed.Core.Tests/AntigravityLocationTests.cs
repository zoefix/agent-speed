using System.Security.AccessControl;
using System.Security.Principal;
using AgentSpeed.Core.Antigravity;
using AgentSpeed.Core.History;
using AgentSpeed.Core.Locations;
using AgentSpeed.Core.Monitoring;

namespace AgentSpeed.Core.Tests;

public sealed class AntigravityLocationTests : IDisposable
{
    private const string ConversationId = "11111111-2222-4333-8444-555555555555";
    private readonly TempDirectory _temp = new();
    private readonly long _now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    private static readonly HashSet<SourceKind> Enabled = [SourceKind.Antigravity];

    public void Dispose() => _temp.Dispose();

    private string Store(string relative, string responseId, long output)
    {
        var store = _temp.Combine(relative);
        using var writer = new AgWriter(AgWriter.ConversationPath(Path.Combine(store, "conversations"), ConversationId));
        writer.Trajectory(AgBlobs.Trajectory(_now - 60_000, "/synthetic"))
            .Step(0, AgWriter.Planner, AgWriter.Done,
                AgBlobs.Step(_now - 2000, _now - 1000, usage: AgBlobs.Usage(100, output, responseId: responseId)));
        return store;
    }

    private static SourceLocations Resolve(params string[] stores) => SourceLocations.Resolve(new MonitorConfiguration(Enabled)
    {
        Locations = LocationLists.Automatic.With(LocationFolder.Antigravity, stores.Select(DataLocation.At)),
    });

    [Fact]
    public void ExplicitLocationsReadNativeAndT3TogetherWithoutCountingCopiesTwice()
    {
        var native = Store("home/.gemini/antigravity-cli", "native", 10);
        var first = Store("home/.t3/userdata/providers/antigravity/hash-a/antigravity-acp", "t3-a", 20);
        var second = Store("home/.t3/userdata/providers/antigravity/hash-b/antigravity-acp", "t3-b", 30);
        var copy = _temp.Combine("copy", "conversations");
        Directory.CreateDirectory(copy);
        File.Copy(Path.Combine(first, "conversations", ConversationId + ".db"), Path.Combine(copy, ConversationId + ".db"));
        var locations = Resolve(native, first, second, copy, Path.Combine(first, "conversations"), _temp.Combine("missing"));
        using var core = new MonitorCore(new MonitorConfiguration(Enabled), locations, _now - 3_600_000);
        core.Seed(_now, _now - 3_600_000, CancellationToken.None);
        var source = core.BuildSnapshot(_now, _now - 3_600_000, MonitorState.Running, false).Snapshot.Sources.Single(s => s.Kind == SourceKind.Antigravity);
        Assert.Equal((3L, 60L), (source.Today.Requests, source.Today.Output));

        using var store = HistoryStore.Open(_temp.Combine("history", "history.sqlite"));
        var analyzer = new HistoryAnalyzer(store);
        analyzer.Run(locations, Enabled, TimeZoneInfo.Utc, CancellationToken.None);
        Assert.Equal(60, analyzer.Days().Sum(day => day.Totals.Output));
        analyzer.Run(locations, Enabled, TimeZoneInfo.Utc, CancellationToken.None);
        Assert.Equal(60, analyzer.Days().Sum(day => day.Totals.Output));
    }

    [Fact]
    public void AutomaticStoresIncludeT3InstancesWhenRootsAreResolvedAgainOnWindows()
    {
        var native = Store("home/.gemini/antigravity-cli", "native", 10);
        var first = Store("home/.t3/userdata/providers/antigravity/hash-a/antigravity-acp", "t3-a", 20);
        var second = Store("home/.t3/userdata/providers/antigravity/hash-b/antigravity-acp", "t3-b", 30);
        Directory.CreateDirectory(_temp.Combine("home/.t3/userdata/providers/antigravity/not-a-store"));
        var gemini = _temp.Combine("home/.gemini");
        var roots = AntigravityPaths.Stores(gemini, null);
        var expected = OperatingSystem.IsWindows() ? 3 : 1;
        using var core = new MonitorCore(new MonitorConfiguration(Enabled), Resolve([.. roots]), _now - 3_600_000);
        Assert.Equal(expected, core.Seed(_now, _now - 3_600_000, CancellationToken.None).FilesDiscovered);
        Assert.Equal(OperatingSystem.IsWindows() ? 60 : 10,
            core.BuildSnapshot(_now, _now - 3_600_000, MonitorState.Running, false).Snapshot.Sources.Single(s => s.Kind == SourceKind.Antigravity).Today.Output);
        Assert.Equal([native], AntigravityPaths.Stores(gemini, native));
        Assert.Equal([Path.Combine(first, "conversations")], AntigravityPaths.Stores(gemini, Path.Combine(first, "conversations")));

        var third = Store("home/.t3/userdata/providers/antigravity/hash-c/antigravity-acp", "t3-c", 40);
        core.Rediscover(_now, _now - 3_600_000, CancellationToken.None);
        Assert.Equal(expected, core.TrackedFileCount);
        var refreshed = AntigravityPaths.Stores(gemini, null);
        Assert.Equal(OperatingSystem.IsWindows(), refreshed.Select(Path.GetFullPath).Contains(Path.GetFullPath(third)));
        using var restarted = new MonitorCore(new MonitorConfiguration(Enabled), Resolve([.. refreshed]), _now - 3_600_000);
        Assert.Equal(OperatingSystem.IsWindows() ? 4 : 1,
            restarted.Seed(_now, _now - 3_600_000, CancellationToken.None).FilesDiscovered);
    }

    [Fact]
    public void MissingInvalidAndInaccessibleProvidersKeepTheNativeDefaults()
    {
        var gemini = _temp.Combine("home", ".gemini");
        var expected = AntigravityPaths.StoreNames.Select(name => Path.Combine(gemini, name)).ToArray();
        Assert.Equal(expected, AntigravityPaths.DefaultStores(gemini));
        var provider = _temp.Combine("home", ".t3", "userdata", "providers", "antigravity");
        Directory.CreateDirectory(Path.GetDirectoryName(provider)!);
        File.WriteAllText(provider, "not a directory");
        Assert.Equal(expected, AntigravityPaths.DefaultStores(gemini));
        File.Delete(provider);
        var directory = Directory.CreateDirectory(provider);
        if (OperatingSystem.IsWindows())
        {
            var original = directory.GetAccessControl();
            var denied = directory.GetAccessControl();
            denied.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.ListDirectory, AccessControlType.Deny));
            try
            {
                directory.SetAccessControl(denied);
                Assert.Equal(expected, AntigravityPaths.DefaultStores(gemini));
            }
            finally
            {
                directory.SetAccessControl(original);
            }
        }

        Assert.Equal(expected, AntigravityPaths.DefaultStores(gemini));
    }

    [Fact]
    public void AProviderParentOrWildcardDoesNotReadItsInstances()
    {
        Store("home/.t3/userdata/providers/antigravity/hash-a/antigravity-acp", "a", 20);
        var provider = _temp.Combine("home/.t3/userdata/providers/antigravity");
        foreach (var root in new[] { provider, Path.Combine(provider, "*", "antigravity-acp") })
        {
            var locations = Resolve(root);
            using var core = new MonitorCore(new MonitorConfiguration(Enabled), locations, _now - 3_600_000);
            Assert.Equal(0, core.Seed(_now, _now - 3_600_000, CancellationToken.None).FilesDiscovered);
        }
    }
}