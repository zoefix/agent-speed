using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AgentSpeed.Core.Reporting;

namespace AgentSpeed.Core.Tests.Reporting;

/// <summary>spec/fixtures/report-vectors.json and the other files of the live reporting contract.</summary>
internal static class ReportSpec
{
    private static readonly Lazy<string> RootDirectory = new(LocateRoot);
    private static readonly Lazy<JsonDocument> VectorDocument = new(() =>
        JsonDocument.Parse(File.ReadAllBytes(FixtureLocator.PathOf("report-vectors.json"))));

    /// <summary>The repository's <c>spec</c> folder (tools and schema are not copied to the test output).</summary>
    public static string Directory => RootDirectory.Value;

    public static string Tools => Path.Combine(Directory, "tools");

    public static JsonElement Vectors => VectorDocument.Value.RootElement;

    public static JsonElement Vector(string name) => Vectors.GetProperty(name);

    public static byte[] Example(string name) => File.ReadAllBytes(FixtureLocator.PathOf("report-examples/" + name));

    public static string Text(this JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()! : string.Empty;

    public static string? OptionalText(this JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    public static Dictionary<string, string> Strings(this JsonElement element) =>
        element.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString() ?? string.Empty, StringComparer.Ordinal);

    public static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text);

    /// <summary>A test key of <c>keys</c>.</summary>
    public static SoftwareReportSigner Key(string name) =>
        SoftwareReportSigner.FromPrivateScalar(Convert.FromHexString(Vector("keys").GetProperty(name).Text("d")));

    private static string LocateRoot()
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Path.GetDirectoryName(SourceFile()) })
        {
            for (var dir = string.IsNullOrEmpty(start) ? null : new DirectoryInfo(start); dir is not null; dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, "spec");
                if (File.Exists(Path.Combine(candidate, "tools", "report_reference.py")) && File.Exists(Path.Combine(candidate, "report-schema.json")))
                {
                    return candidate;
                }
            }
        }

        throw new DirectoryNotFoundException("spec/tools/report_reference.py not found above " + AppContext.BaseDirectory);
    }

    private static string SourceFile([CallerFilePath] string path = "") => path;
}

/// <summary>Runs spec/tools/report_reference.py's validators on documents the C# code produced.</summary>
internal static class ReferencePython
{
    private static readonly Lazy<bool> IsAvailable = new(Probe);

    /// <summary>python3 runs; tests that need it pass trivially otherwise.</summary>
    public static bool Available => IsAvailable.Value;

    /// <summary>The strict errors of each body (schema plus the conformance rules of REPORTING 6).</summary>
    public static IReadOnlyList<string> StrictErrors(IReadOnlyList<byte[]> bodies, IReadOnlyList<string>? sections = null)
    {
        var directory = Path.Combine(Path.GetTempPath(), "report-bodies-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(directory);
        try
        {
            for (var i = 0; i < bodies.Count; i++)
            {
                File.WriteAllBytes(Path.Combine(directory, i.ToString("D3", System.Globalization.CultureInfo.InvariantCulture) + ".json"), bodies[i]);
            }

            var sectionArgument = sections is null ? "r.SECTIONS" : "tuple(" + JsonSerializer.Serialize(sections) + ")";
            var script = $$"""
                import json, sys, pathlib
                sys.path.insert(0, sys.argv[1])
                import report_reference as r
                schema = r.load_schema()
                bad = []
                for path in sorted(pathlib.Path(sys.argv[2]).glob('*.json')):
                    body = json.loads(path.read_text(encoding='utf-8'))
                    for error in r.report_errors(body, schema, strict=True, sections={{sectionArgument}}):
                        bad.append(path.name + ': ' + error)
                print(json.dumps(bad))
                """;
            var output = Run(script, ReportSpec.Tools, directory);
            return JsonSerializer.Deserialize<string[]>(output) ?? [];
        }
        finally
        {
            System.IO.Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>Runs a script with <c>python3 -I -c</c>; returns its standard output.</summary>
    public static string Run(string script, params string[] arguments)
    {
        var start = new ProcessStartInfo("python3")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add("-I");
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add(script);
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start) ?? throw new InvalidOperationException("python3 did not start");
        var error = process.StandardError.ReadToEndAsync();
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException("python3 failed: " + error.Result);
        }

        return output;
    }

    private static bool Probe()
    {
        try
        {
            return Run("print('ok')").Trim() == "ok" && File.Exists(Path.Combine(ReportSpec.Tools, "report_server.py"));
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or DirectoryNotFoundException)
        {
            return false;
        }
    }
}

/// <summary><c>python3 spec/tools/report_server.py</c> on a free loopback port; never reaches beyond 127.0.0.1.</summary>
internal sealed class ReferenceServer : IDisposable
{
    private readonly Process _process;
    private readonly StringBuilder _log = new();

    public ReferenceServer(params string[] arguments)
    {
        Port = FreePort();
        Service = ReportServiceUrl.Parse("http://127.0.0.1:" + Port.ToString(System.Globalization.CultureInfo.InvariantCulture))!;
        var start = new ProcessStartInfo("python3")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false,
        };
        start.ArgumentList.Add("-I");
        start.ArgumentList.Add("-u");
        start.ArgumentList.Add(Path.Combine(ReportSpec.Tools, "report_server.py"));
        start.ArgumentList.Add("--port");
        start.ArgumentList.Add(Port.ToString(System.Globalization.CultureInfo.InvariantCulture));
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        _process = Process.Start(start) ?? throw new InvalidOperationException("report_server.py did not start");
        _process.OutputDataReceived += (_, e) => Append(e.Data);
        _process.ErrorDataReceived += (_, e) => Append(e.Data);
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();
        var deadline = Stopwatch.StartNew();
        while (!Log.Contains("listening", StringComparison.Ordinal))
        {
            if (deadline.Elapsed > TimeSpan.FromSeconds(10) || _process.HasExited)
            {
                Dispose();
                throw new InvalidOperationException("report_server.py did not start: " + Log);
            }

            Thread.Sleep(50);
        }
    }

    public int Port { get; }

    public ReportServiceUrl Service { get; }

    public string Log
    {
        get
        {
            lock (_log)
            {
                return _log.ToString();
            }
        }
    }

    public void Dispose()
    {
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                _process.WaitForExit(5000);
            }
        }
        catch (InvalidOperationException)
        {
        }

        _process.Dispose();
    }

    private void Append(string? line)
    {
        if (line is null)
        {
            return;
        }

        lock (_log)
        {
            _log.AppendLine(line);
        }
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
