using AgentSpeed.Core.IO;
using AgentSpeed.Core.Parsing;

namespace AgentSpeed.Core.OpenBitFun;

internal static class OpenBitFunFiles
{
    public static string DefaultRoot
    {
        get
        {
            var home = new[] { Environment.GetEnvironmentVariable("OPENBITFUN_HOME"), Environment.GetEnvironmentVariable("OPENBITFUN_E2E_HOME") }
                .FirstOrDefault(value => !string.IsNullOrEmpty(value))
                ?? System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".openbitfun");
            return System.IO.Path.Combine(home, "projects");
        }
    }

    public static string ProjectsOf(string path) => Path.GetFileName(Path.TrimEndingDirectorySeparator(path)) == "projects"
        ? path : Path.Combine(path, "projects");

    public static IEnumerable<FileInfo> Traces(string metadata)
    {
        var session = new FileInfo(metadata).Directory!;
        var shared = new DirectoryInfo(Path.Combine(session.Parent!.Parent!.FullName, "request-traces"));
        List<DirectoryInfo> folders = [new(Path.Combine(session.FullName, "request-traces"))];
        if (shared.Exists && (shared.Attributes & FileAttributes.ReparsePoint) == 0)
            folders.Add(new(Path.Combine(shared.FullName, session.Name)));
        return folders.Where(folder => folder.Exists && (folder.Attributes & FileAttributes.ReparsePoint) == 0)
            .SelectMany(folder => folder.EnumerateFiles("request-*.json", new EnumerationOptions
            { RecurseSubdirectories = false, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint }))
            .OrderBy(file => file.FullName, StringComparer.Ordinal);
    }

    public static IEnumerable<FileInfo> Documents(string metadata, bool includeTraces = true)
    {
        var turns = new DirectoryInfo(Path.Combine(new FileInfo(metadata).DirectoryName!, "turns"));
        var files = turns.Exists && (turns.Attributes & FileAttributes.ReparsePoint) == 0
            ? turns.EnumerateFiles("turn-*.json", new EnumerationOptions
                { RecurseSubdirectories = false, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint })
            : Enumerable.Empty<FileInfo>();
        return includeTraces ? files.Concat(Traces(metadata)) : files;
    }

    public static OpenBitFunUsageSnapshot Usage(string metadata, OpenBitFunUsageLedger? ledger = null) =>
        (ledger ?? OpenBitFunUsageLedger.Default).ForSession(new FileInfo(metadata).Directory!.Name);

    public static DiscoveredFile Stat(string metadata, OpenBitFunUsageSnapshot? usage = null, IEnumerable<FileInfo>? documents = null)
    {
        usage ??= Usage(metadata);
        var file = new FileInfo(metadata);
        var length = file.Length;
        var modified = file.LastWriteTimeUtc;
        foreach (var document in documents ?? Documents(metadata, includeTraces: usage.Records.Count == 0))
        {
            length += document.Length;
            if (document.LastWriteTimeUtc > modified) modified = document.LastWriteTimeUtc;
        }
        if (usage.LastWriteUtc > modified) modified = usage.LastWriteUtc;
        return new(file.FullName, length, modified) { ContentSignature = usage.Signature };
    }

    public static string MetadataOf(string changed)
    {
        var folder = new FileInfo(changed).Directory;
        if (folder?.Name is "request-traces" or "turns") return Path.Combine(folder.Parent!.FullName, "metadata.json");
        if (folder?.Parent?.Name == "request-traces")
            return Path.Combine(folder.Parent.Parent!.FullName, "sessions", folder.Name, "metadata.json");
        return changed;
    }
}

// A session joins its round timing documents with the default, shared usage ledger.
internal sealed class OpenBitFunTailer(string path, OpenBitFunUsageLedger? ledger = null) : TranscriptTailer(path)
{
    private readonly Dictionary<string, DocumentTailer> _documents = new(StringComparer.Ordinal);
    private OpenBitFunUsageSnapshot _usage = OpenBitFunUsageSnapshot.Empty;
    private IReadOnlyList<(string Turn, string Round)>? _baseline;

    public long ContentSignature => _usage.Signature;

    public override void StartAt(long offset, StartAlignment alignment = StartAlignment.None)
    {
        Offset = offset;
        if (offset <= 0) return;
        var usage = OpenBitFunFiles.Usage(Path, ledger);
        _baseline = usage.Records.Select(record => (record.Turn, record.Round)).Distinct().ToArray();
        foreach (var file in OpenBitFunFiles.Documents(Path, includeTraces: usage.Records.Count == 0))
        {
            var reader = new DocumentTailer(file.FullName);
            reader.StartAtEnd(file.Length, file.LastWriteTimeUtc);
            _documents[file.FullName] = reader;
        }
    }

    public override TailReadResult ReadAppended(ILineSink sink, CancellationToken cancellationToken = default)
    {
        var batch = (OpenBitFunBatch)sink;
        var usage = OpenBitFunFiles.Usage(Path, ledger);
        var documents = OpenBitFunFiles.Documents(Path, includeTraces: usage.Records.Count == 0).ToArray();
        var stat = OpenBitFunFiles.Stat(Path, usage, documents);
        var paths = documents.Select(file => file.FullName).Prepend(Path).ToHashSet(StringComparer.Ordinal);
        long bytes = 0;
        if (!ReferenceEquals(_usage, usage))
        {
            batch.Usage = usage;
            _usage = usage;
            bytes++; // A changed shared snapshot still needs applying when the session documents are unchanged.
        }
        batch.Baseline = _baseline;
        _baseline = null;
        foreach (var document in paths.OrderBy(file => file == Path ? 0 : 1).ThenBy(file => file, StringComparer.Ordinal))
        {
            if (!_documents.TryGetValue(document, out var reader)) _documents[document] = reader = new DocumentTailer(document);
            bytes += reader.ReadAppended(sink, cancellationToken).BytesRead;
        }
        foreach (var removed in _documents.Keys.Where(file => !paths.Contains(file)).ToArray()) _documents.Remove(removed);
        Offset = stat.Length;
        TotalBytesRead += bytes;
        return new(false, stat.Length, stat.LastWriteUtc, bytes);
    }
}
