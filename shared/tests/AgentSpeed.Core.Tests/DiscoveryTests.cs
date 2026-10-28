using AgentSpeed.Core.IO;

namespace AgentSpeed.Core.Tests;

public sealed class DiscoveryTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly string _root;
    private readonly string _outside;

    public DiscoveryTests()
    {
        _root = _temp.Combine("root");
        _outside = _temp.Combine("outside");
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_outside);
    }

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void EnumeratesJsonlFilesRecursively()
    {
        var top = Write("root/top.jsonl");
        var nested = Write("root/x/y/nested.jsonl");
        Write("root/x/notes.txt");
        Write("root/x/data.json");
        Write("root/x/archive.jsonl.bak");

        var found = Relative(TranscriptDiscovery.Enumerate(_root));

        Assert.Equal(Relative([top, nested]), found);
    }

    [Fact]
    public void ReportsLengthAndModificationTime()
    {
        var path = Write("root/a.jsonl", "12345\n");
        var stamp = new DateTime(2026, 10, 20, 10, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(path, stamp);

        var file = Assert.Single(TranscriptDiscovery.Enumerate(_root));

        Assert.Equal(Path.GetFullPath(path), Path.GetFullPath(file.Path));
        Assert.Equal(6, file.Length);
        Assert.Equal(stamp, file.LastWriteUtc);
    }

    [Fact]
    public void RespectsMaximumDepth()
    {
        var seven = Write("root/d1/d2/d3/d4/d5/d6/seven.jsonl");
        var eight = Write("root/d1/d2/d3/d4/d5/d6/d7/eight.jsonl");

        var found = Relative(TranscriptDiscovery.Enumerate(_root));

        Assert.Equal(6, TranscriptDiscovery.MaxDepth);
        Assert.Equal(Relative([seven]), found);
        Assert.True(TranscriptDiscovery.IsAcceptablePath(_root, seven));
        Assert.False(TranscriptDiscovery.IsAcceptablePath(_root, eight));
    }

    [Fact]
    public void MissingRootYieldsNothing()
    {
        Assert.Empty(TranscriptDiscovery.Enumerate(_temp.Combine("does-not-exist")));
    }

    [Fact]
    public void SkipsSymlinkedDirectories()
    {
        var real = Write("root/real/a.jsonl");
        Write("outside/linked/b.jsonl");
        if (!TryCreateDirectoryLink(Path.Combine(_root, "link-out"), Path.Combine(_outside, "linked"))
            || !TryCreateDirectoryLink(Path.Combine(_root, "link-in"), Path.Combine(_root, "real")))
        {
            return;
        }

        var found = Relative(TranscriptDiscovery.Enumerate(_root));

        Assert.Equal(Relative([real]), found);
        Assert.True(TranscriptDiscovery.IsAcceptablePath(_root, real));
        Assert.False(TranscriptDiscovery.IsAcceptablePath(_root, Path.Combine(_root, "link-out", "b.jsonl")));
        Assert.False(TranscriptDiscovery.IsAcceptablePath(_root, Path.Combine(_root, "link-in", "a.jsonl")));
    }

    [Fact]
    public void SkipsSymlinkedFiles()
    {
        var real = Write("root/real.jsonl");
        var target = Write("outside/target.jsonl");
        var link = Path.Combine(_root, "link.jsonl");
        try
        {
            File.CreateSymbolicLink(link, target);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return;
        }

        var found = Relative(TranscriptDiscovery.Enumerate(_root));

        Assert.Equal(Relative([real]), found);
        Assert.False(TranscriptDiscovery.IsAcceptablePath(_root, link));
    }

    [Fact]
    public void AcceptablePathRejectsOutsideAndNonTranscriptPaths()
    {
        var inside = Write("root/p/s.jsonl");
        var outside = Write("outside/s.jsonl");
        var text = Write("root/p/s.txt");

        Assert.True(TranscriptDiscovery.IsAcceptablePath(_root, inside));
        Assert.False(TranscriptDiscovery.IsAcceptablePath(_root, outside));
        Assert.False(TranscriptDiscovery.IsAcceptablePath(_root, Path.Combine(_root, "..", "outside", "s.jsonl")));
        Assert.False(TranscriptDiscovery.IsAcceptablePath(_root, text));
        Assert.False(TranscriptDiscovery.IsAcceptablePath(_root, Path.Combine(_root, "p", "s.jsonl.tmp")));
        Assert.False(TranscriptDiscovery.IsAcceptablePath(_root, _root));
        Assert.False(TranscriptDiscovery.IsAcceptablePath(_root, _temp.Combine("root-sibling", "s.jsonl")));
    }

    [Fact]
    public void DshRootsListOnlySessionFiles()
    {
        var zstd = Write("root/--Users-me-quasar--/session-a/session.v4.jsonl.zstd");
        var plain = Write("root/--Users-me-quasar--/session-b/session.v12.jsonl");
        Write("root/--Users-me-quasar--/session-a/session.lock");
        Write("root/--Users-me-quasar--/session-a/other.jsonl");
        Write("root/--Users-me-quasar--/session-a/session.v.jsonl");
        Write("root/--Users-me-quasar--/session-a/session.v4.jsonl.zst");
        Write("root/--Users-me-quasar--/session-a/session.v4x.jsonl");

        var found = Relative(TranscriptDiscovery.Enumerate(_root, TranscriptNaming.DshSession));

        Assert.Equal(Relative([zstd, plain]), found);
        Assert.True(TranscriptDiscovery.IsAcceptablePath(_root, zstd, TranscriptNaming.DshSession));
        Assert.False(TranscriptDiscovery.IsAcceptablePath(_root, zstd));
        Assert.False(TranscriptDiscovery.IsAcceptablePath(
            _root, Path.Combine(_root, "--Users-me-quasar--", "session-a", "session.lock"), TranscriptNaming.DshSession));
    }

    [Theory]
    [InlineData("session.v4.jsonl.zstd", true)]
    [InlineData("SESSION.V4.JSONL.ZSTD", true)]
    [InlineData("session.v10.jsonl", true)]
    [InlineData("session.v4.jsonl.zstd.tmp", false)]
    [InlineData("xsession.v4.jsonl", false)]
    [InlineData("session.v-1.jsonl", false)]
    [InlineData("session.jsonl", false)]
    public void DshSessionNames(string name, bool expected) =>
        Assert.Equal(expected, TranscriptDiscovery.IsDshSessionName(name));

    private string Write(string relative, string content = "{}\n") => _temp.WriteFile(relative, content);

    private List<string> Relative(IEnumerable<DiscoveredFile> files) => Relative(files.Select(f => f.Path));

    private List<string> Relative(IEnumerable<string> paths) =>
        paths.Select(p => Path.GetRelativePath(_root, p).Replace('\\', '/')).Order(StringComparer.Ordinal).ToList();

    private static bool TryCreateDirectoryLink(string link, string target)
    {
        try
        {
            Directory.CreateSymbolicLink(link, target);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return false;
        }
    }
}
