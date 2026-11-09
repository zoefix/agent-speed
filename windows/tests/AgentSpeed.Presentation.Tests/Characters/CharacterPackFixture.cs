using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using AgentSpeed.Core.Network;
using AgentSpeed.Presentation.Characters;

namespace AgentSpeed.Presentation.Tests.Characters;

/// <summary>Builds unpacked and zipped character packs in a temporary folder (no art: a few bytes stand in for images).</summary>
internal sealed class CharacterPackFixture : IDisposable
{
    public CharacterPackFixture(string? root = null)
    {
        Root = root ?? Path.Combine(Path.GetTempPath(), "character-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    public static CharacterNames Names { get; } = new("Claude 娘", "Claude 娘", "Claude-chan", "Claudeちゃん");

    public static CharacterManifest Manifest(string id = "claude", int packVersion = 1, IReadOnlyDictionary<string, string>? poses = null,
        string? minApp = null, IReadOnlyList<string>? vendors = null) => new()
    {
        Id = id,
        PackVersion = packVersion,
        Name = Names,
        Vendors = vendors ?? ["anthropic"],
        Accent = "#D97757",
        Artist = new CharacterArtist("ZipZipPipe"),
        MinApp = minApp,
        Poses = poses ?? new Dictionary<string, string> { ["idle"] = "poses/idle" },
    };

    /// <summary><c>&lt;root&gt;/&lt;folder&gt;/</c> with manifest.json, preview.png and a rig.json per pose.</summary>
    public string Pack(CharacterManifest manifest, string? folder = null)
    {
        var dir = Path.Combine(Root, folder ?? manifest.Id);
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "manifest.json"), manifest.ToJson());
        File.WriteAllBytes(Path.Combine(dir, "preview.png"), [0x89, 0x50, 0x4E, 0x47]);
        foreach (var path in manifest.Poses.Values.Distinct())
        {
            var pose = Path.Combine(dir, path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(pose);
            File.WriteAllText(Path.Combine(pose, "rig.json"), "{\"format\":\"agent-speed-character\",\"version\":1}");
        }

        return dir;
    }

    /// <summary>A zip of a pack folder (entries with forward slashes), and its size and SHA-256.</summary>
    public (string Archive, long Size, string Sha256) Zip(string id = "claude", IReadOnlyList<string>? vendors = null)
    {
        var folder = Pack(Manifest(id, vendors: vendors), "src-" + id);
        var archive = Path.Combine(Root, id + "-1.zip");
        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
        {
            foreach (var file in Directory.GetFiles(folder, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
            {
                zip.CreateEntryFromFile(file, Path.GetRelativePath(folder, file).Replace(Path.DirectorySeparatorChar, '/'));
            }
        }

        return (archive, new FileInfo(archive).Length, CharacterInstaller.Sha256Hex(archive));
    }

    /// <summary>A zip with the given entries and contents, exactly as named.</summary>
    public string RawZip(string name, params (string Path, string Content)[] entries)
    {
        var archive = Path.Combine(Root, name);
        using var zip = ZipFile.Open(archive, ZipArchiveMode.Create);
        foreach (var (path, content) in entries)
        {
            using var stream = zip.CreateEntry(path).Open();
            stream.Write(Encoding.UTF8.GetBytes(content));
        }

        return archive;
    }

    public static CharacterCatalog.Entry Entry(string id, long size, string sha, IReadOnlyList<string>? vendors = null) => new()
    {
        Id = id,
        PackVersion = 1,
        Name = Names,
        Vendors = vendors ?? ["anthropic"],
        Accent = "#D97757",
        Size = size,
        Sha256 = sha,
        Url = new Uri($"https://github.com/zoefix/agent-speed/releases/download/characters/{id}-1.zip"),
    };

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

/// <summary>Copies the archive of the pack each URL names, recording the order and overlap of the requests.</summary>
internal sealed class MapFetcher(IReadOnlyDictionary<string, string> archives, int delayMs = 20) : ICharacterPackFetcher
{
    private readonly object _lock = new();
    private readonly List<string> _log = [];
    private int _running;

    public int MostAtOnce { get; private set; }

    public IReadOnlyList<string> Order
    {
        get
        {
            lock (_lock)
            {
                return _log.ToArray();
            }
        }
    }

    public async Task FetchAsync(Uri url, string destination, long expectedSize, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var id = url.Segments[^1].Split('-')[0];
        lock (_lock)
        {
            _log.Add(id);
            _running++;
            MostAtOnce = Math.Max(MostAtOnce, _running);
        }

        try
        {
            await Task.Delay(delayMs, cancellationToken);
            File.Copy(archives[id], destination);
            progress?.Report(1);
        }
        finally
        {
            lock (_lock)
            {
                _running--;
            }
        }
    }
}

/// <summary>Answers every catalog request with one response.</summary>
internal sealed class FixedTransport(HttpFetchResponse response) : IHttpTransport
{
    public int Requests { get; private set; }

    public Task<HttpFetchResponse> GetAsync(HttpFetchRequest request, CancellationToken cancellationToken = default)
    {
        Requests++;
        return Task.FromResult(response);
    }
}

internal static class LibraryWait
{
    public static async Task Until(Func<bool> done, int timeoutMs = 10_000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!done() && sw.ElapsedMilliseconds < timeoutMs)
        {
            await Task.Delay(10);
        }
    }

    public static Task Settle(CharacterLibrary library) => Until(() => !library.IsBusy);

    public static string Sha(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));
}
