using AgentSpeed.Core.Gemini;
using AgentSpeed.Core.IO;

namespace AgentSpeed.Core.Tests;

public sealed class GeminiDiscoveryTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly string _root;

    public GeminiDiscoveryTests()
    {
        _root = _temp.Combine("tmp");
        Directory.CreateDirectory(_root);
    }

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void FixtureRootListsExactlyTheExpectedSessions()
    {
        var expected = ParserFixtureTests.Expected.GetProperty("gemini").GetProperty("discover")
            .EnumerateArray().Select(e => e.GetString()!).ToList();

        var found = GeminiDiscovery.Enumerate(Path.Combine(FixtureLocator.FixturesDirectory, "gemini", "tmp"))
            .Select(f => Path.GetRelativePath(FixtureLocator.FixturesDirectory, f.Path).Replace('\\', '/'))
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.Equal(expected, found);
    }

    [Fact]
    public void OnlySessionFilesAtTheTwoChatsDepthsAreListed()
    {
        var main = Write("proj/chats/session-2026-10-20T10-00-abcdef12.jsonl");
        var legacy = Write("proj/chats/session-2026-10-20T09-00-abcdef12.json");
        var nested = Write("proj/chats/abcdef12-0000/sub-1.jsonl");
        var nestedLegacy = Write("other/chats/0f0f/sub-2.json");
        var folderNamedLikeASession = Write("proj/chats/odd.jsonl/inner.jsonl");
        Write("proj/logs.json");
        Write("proj/checkpoint-release.json");
        Write("proj/.project_root");
        Write("proj/session.jsonl");
        Write("proj/chats/session-2026-10-20T10-00-abcdef12.jsonl.tmp-4242");
        Write("proj/chats/session-2026-10-20T10-00-abcdef12.json.unreadable-1792491600000");
        Write("proj/chats/notes.txt");
        Write("proj/chats/abcdef12-0000/deeper/sub-3.jsonl");
        Write("proj/nested/chats/s.jsonl");
        Write("proj/tool-outputs/session-x/out.json");
        Write("root-level.jsonl");
        Write("chats/s.jsonl");

        var found = Relative(GeminiDiscovery.Enumerate(_root));

        Assert.Equal(Relative([main, legacy, nested, nestedLegacy, folderNamedLikeASession]), found);
    }

    [Fact]
    public void MissingRootYieldsNothing() => Assert.Empty(GeminiDiscovery.Enumerate(_temp.Combine("missing")));

    [Fact]
    public void ReparsePointsAreNeverListed()
    {
        var real = Write("proj/chats/s-1.jsonl");
        _temp.WriteFile("outside/proj/chats/linked-project.jsonl", "{}\n");
        _temp.WriteFile("outside/chats/linked-chats.jsonl", "{}\n");
        _temp.WriteFile("outside/sub/linked-folder.jsonl", "{}\n");
        var target = _temp.WriteFile("outside/target.jsonl", "{}\n");
        Directory.CreateDirectory(Path.Combine(_root, "other"));
        if (!TryLinkDirectory("tmp/linked", "outside/proj")
            || !TryLinkDirectory("tmp/other/chats", "outside/chats")
            || !TryLinkDirectory("tmp/proj/chats/abcdef12", "outside/sub")
            || !TryLinkFile("tmp/proj/chats/link.jsonl", target))
        {
            return;
        }

        Assert.Equal(Relative([real]), Relative(GeminiDiscovery.Enumerate(_root)));
        Assert.True(GeminiDiscovery.IsSessionPath(_root, real));
        Assert.False(GeminiDiscovery.IsSessionPath(_root, Path.Combine(_root, "linked", "chats", "linked-project.jsonl")));
        Assert.False(GeminiDiscovery.IsSessionPath(_root, Path.Combine(_root, "other", "chats", "linked-chats.jsonl")));
        Assert.False(GeminiDiscovery.IsSessionPath(_root, Path.Combine(_root, "proj", "chats", "abcdef12", "linked-folder.jsonl")));
        Assert.False(GeminiDiscovery.IsSessionPath(_root, Path.Combine(_root, "proj", "chats", "link.jsonl")));
    }

    [Fact]
    public void SessionPathAcceptsOnlyTheSessionLayout()
    {
        var main = Write("proj/chats/s.jsonl");
        var nested = Write("proj/chats/abcdef12/sub.json");
        var shallow = Write("proj/s.jsonl");
        var deep = Write("proj/chats/a/b/s.jsonl");
        var wrongFolder = Write("proj/chat/s.jsonl");
        var text = Write("proj/chats/s.txt");
        var temporary = Write("proj/chats/s.jsonl.tmp-1");
        var outside = Write("../outside/proj/chats/s.jsonl");
        Directory.CreateDirectory(Path.Combine(_root, "proj", "chats", "folder.jsonl"));

        Assert.True(GeminiDiscovery.IsSessionPath(_root, main));
        Assert.True(GeminiDiscovery.IsSessionPath(_root, nested));
        Assert.True(GeminiDiscovery.IsSessionPath(_root + Path.DirectorySeparatorChar, main));
        Assert.False(GeminiDiscovery.IsSessionPath(_root, shallow));
        Assert.False(GeminiDiscovery.IsSessionPath(_root, deep));
        Assert.False(GeminiDiscovery.IsSessionPath(_root, wrongFolder));
        Assert.False(GeminiDiscovery.IsSessionPath(_root, text));
        Assert.False(GeminiDiscovery.IsSessionPath(_root, temporary));
        Assert.False(GeminiDiscovery.IsSessionPath(_root, outside));
        Assert.False(GeminiDiscovery.IsSessionPath(_root, Path.Combine(_root, "..", "outside", "proj", "chats", "s.jsonl")));
        Assert.False(GeminiDiscovery.IsSessionPath(_root, Path.Combine(_root, "proj", "chats", "missing.jsonl")));
        Assert.False(GeminiDiscovery.IsSessionPath(_root, Path.Combine(_root, "proj", "chats", "folder.jsonl")));
        Assert.False(GeminiDiscovery.IsSessionPath(_root, _root));
    }

    [Fact]
    public void LimitKeepsTheNewestFiles()
    {
        var stamp = new DateTime(2026, 10, 20, 10, 0, 0, DateTimeKind.Utc);
        var paths = new List<string>();
        for (var i = 0; i < 5; i++)
        {
            var path = Write(i % 2 == 0 ? $"proj/chats/s-{i}.jsonl" : $"proj/chats/parent/s-{i}.jsonl");
            File.SetLastWriteTimeUtc(path, stamp.AddMinutes(i));
            paths.Add(path);
        }

        var found = GeminiDiscovery.Enumerate(_root, limit: 2);

        Assert.Equal(Relative([paths[3], paths[4]]), Relative(found));
        Assert.Equal(stamp.AddMinutes(4), found.Single(f => f.Path.EndsWith("s-4.jsonl", StringComparison.Ordinal)).LastWriteUtc);
        Assert.Empty(GeminiDiscovery.Enumerate(_root, limit: 0));
        Assert.Equal(5, GeminiDiscovery.Enumerate(_root).Count);
    }

    [Fact]
    public void ReportsLengthAndModificationTime()
    {
        var path = Write("proj/chats/s.jsonl", "12345\n");
        var stamp = new DateTime(2026, 10, 20, 10, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(path, stamp);

        var file = Assert.Single(GeminiDiscovery.Enumerate(_root));

        Assert.Equal(Path.GetFullPath(path), file.Path);
        Assert.Equal(6, file.Length);
        Assert.Equal(stamp, file.LastWriteUtc);
    }

    [Fact]
    public void CancellationStopsEnumeration()
    {
        Write("proj/chats/s.jsonl");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        Assert.Throws<OperationCanceledException>(() => GeminiDiscovery.Enumerate(_root, cancellationToken: cancelled.Token));
    }

    [Theory]
    [InlineData("session-2026-10-20T10-00-abcdef12.jsonl", true)]
    [InlineData("session-2026-10-20T10-00-abcdef12.json", true)]
    [InlineData("0b8e6d2c-3f4a-4b5c-8d6e-7f8091a2b3c4.jsonl", true)]
    [InlineData("SESSION.JSONL", true)]
    [InlineData("session.jsonl.tmp-4242", false)]
    [InlineData("session.json.unreadable-1792491600000", false)]
    [InlineData("session.jsonx", false)]
    [InlineData("session.txt", false)]
    [InlineData("", false)]
    public void SessionFileNames(string name, bool expected) => Assert.Equal(expected, GeminiDiscovery.IsSessionFileName(name));

    private string Write(string relative, string content = "{}\n") => _temp.WriteFile("tmp/" + relative, content);

    private List<string> Relative(IEnumerable<DiscoveredFile> files) => Relative(files.Select(f => f.Path));

    private List<string> Relative(IEnumerable<string> paths) =>
        paths.Select(p => Path.GetRelativePath(_root, p).Replace('\\', '/')).Order(StringComparer.Ordinal).ToList();

    private bool TryLinkDirectory(string link, string target)
    {
        try
        {
            Directory.CreateSymbolicLink(_temp.Combine(link.Split('/')), _temp.Combine(target.Split('/')));
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return false;
        }
    }

    private bool TryLinkFile(string link, string target)
    {
        try
        {
            File.CreateSymbolicLink(_temp.Combine(link.Split('/')), target);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return false;
        }
    }
}
