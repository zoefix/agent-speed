using System.Text;
using AgentSpeed.Core.Parsing;

namespace AgentSpeed.Core.Tests;

internal static class FixtureLocator
{
    private static readonly Lazy<string> Root = new(Locate);

    public static string FixturesDirectory => Root.Value;

    public static string PathOf(string relative) =>
        Path.GetFullPath(Path.Combine(FixturesDirectory, relative.Replace('/', Path.DirectorySeparatorChar)));

    private static string Locate()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "spec", "fixtures");
            if (File.Exists(Path.Combine(candidate, "parser-expected.json")))
            {
                return candidate;
            }
        }

        throw new DirectoryNotFoundException("spec/fixtures/parser-expected.json not found above " + AppContext.BaseDirectory);
    }
}

/// <summary>Records every emission and keeps the latest per key in first-emission order.</summary>
internal sealed class RecordingSink : IResponseSink
{
    private readonly Dictionary<string, ParsedResponse> _latest = new(StringComparer.Ordinal);
    private readonly List<string> _order = [];

    public List<(ParsedResponse Response, bool IsCompletion)> Emissions { get; } = [];

    public IReadOnlyList<ParsedResponse> Latest => _order.Select(key => _latest[key]).ToList();

    public void OnResponse(ParsedResponse response, bool isCompletion)
    {
        Emissions.Add((response, isCompletion));
        if (!_latest.ContainsKey(response.Key))
        {
            _order.Add(response.Key);
        }

        _latest[response.Key] = response;
    }

    public void Clear()
    {
        Emissions.Clear();
        _latest.Clear();
        _order.Clear();
    }
}

internal sealed class LineCollector : ILineSink
{
    public List<string> Lines { get; } = [];

    public int Malformed { get; private set; }

    public void OnLine(ReadOnlySpan<byte> line) => Lines.Add(Encoding.UTF8.GetString(line));

    public void OnMalformed() => Malformed++;

    public List<string> Take()
    {
        var copy = new List<string>(Lines);
        Lines.Clear();
        return copy;
    }
}

internal sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "agent-speed-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string Combine(params string[] parts) => System.IO.Path.Combine([Path, .. parts]);

    public string WriteFile(string relative, string content)
    {
        var full = Combine(relative.Split('/'));
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        return full;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }
}

internal static class Feed
{
    public static void Lines<TLine>(TranscriptParser<TLine> parser, IResponseSink sink, params string[] lines)
        where TLine : struct
    {
        foreach (var line in lines)
        {
            parser.Feed(Encoding.UTF8.GetBytes(line), sink);
        }
    }

    /// <summary>Splits on '\n', strips a trailing '\r' and skips the final empty piece.</summary>
    public static IEnumerable<byte[]> SplitJsonl(byte[] content)
    {
        var start = 0;
        while (start < content.Length)
        {
            var newline = Array.IndexOf(content, (byte)'\n', start);
            var end = newline < 0 ? content.Length : newline;
            var length = end - start;
            if (length > 0 && content[end - 1] == '\r')
            {
                length--;
            }

            yield return content.AsSpan(start, length).ToArray();
            if (newline < 0)
            {
                yield break;
            }

            start = newline + 1;
        }
    }
}

internal static class Ts
{
    public static long Ms(string rfc3339) => DateTimeOffset.Parse(rfc3339, System.Globalization.CultureInfo.InvariantCulture).ToUnixTimeMilliseconds();

    public static string Iso(long unixMs) =>
        DateTimeOffset.FromUnixTimeMilliseconds(unixMs).UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>The fixture folder <c>workbuddy</c> plays the home folder of both WorkBuddy editions (METRICS 4.9).</summary>
internal static class WorkBuddyFixture
{
    public static string DataFolder(SourceKind source) =>
        FixtureLocator.PathOf(source == SourceKind.WorkBuddyAi ? "workbuddy/.workbuddy-ai" : "workbuddy/.workbuddy");

    public static string RootOf(SourceKind source) => Path.Combine(DataFolder(source), "projects");

    public static SourceKind SourceOf(string relative) =>
        relative.StartsWith("workbuddy/.workbuddy-ai/", StringComparison.Ordinal) ? SourceKind.WorkBuddyAi
        : relative.StartsWith("workbuddy/.workbuddy/", StringComparison.Ordinal) ? SourceKind.WorkBuddy
        : throw new ArgumentOutOfRangeException(nameof(relative), relative, null);
}

/// <summary>The fixture folder <c>omp</c> plays <c>~/.omp</c>: its automatic roots are the default root and every profile.</summary>
internal static class OmpFixture
{
    private static readonly Lazy<IReadOnlyList<string>> LazyRoots = new(() => Omp.OmpPaths.Roots(
        name => name == "PI_CONFIG_DIR" ? "omp" : null, macOS: false, FixtureLocator.FixturesDirectory));

    public static IReadOnlyList<string> Roots => LazyRoots.Value;

    public static string RootOf(string path) =>
        Roots.First(root => path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal));
}
