using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Text;
using System.Text.Json.Nodes;

namespace AgentSpeed.FakeAgy;

/// <summary>
/// A stand-in for <c>agy</c>: <c>--version</c> and <c>-p /usage …</c> print what <c>fake-agy.json</c> beside the executable
/// says, after recording how they were started (arguments, working folder, standard handles, environment, the browser
/// stubs) as one JSON line in its <c>log</c>; <c>--linger &lt;file&gt;</c> writes its pid there and sleeps.
/// </summary>
internal static partial class Program
{
    private const int StdInput = -10;
    private const int StdError = -12;

    private static int Main(string[] args)
    {
        if (args is ["--linger", var pidFile, ..])
        {
            File.WriteAllText(pidFile, Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Thread.Sleep(120_000);
            return 0;
        }

        var config = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fake-agy.json")))!.AsObject();
        Record(config, args);
        Console.Error.Write("stderr noise that nobody reads\n");
        if (args is ["--version"])
        {
            Thread.Sleep(config["versionSleepMs"]?.GetValue<int>() ?? 0);
            Write(Encoding.UTF8.GetBytes(config["version"]?.GetValue<string>() ?? string.Empty));
            return 0;
        }

        if (config["linger"]?.GetValue<string>() is { } linger)
        {
            Linger(linger);
        }

        if (config["reportBytes"]?.GetValue<int>() is { } bytes and > 0)
        {
            var chunk = Encoding.ASCII.GetBytes(new string('x', 64 * 1024));
            for (var written = 0; written < bytes; written += chunk.Length)
            {
                Write(chunk);
            }
        }
        else
        {
            Write(Encoding.UTF8.GetBytes(config["report"]?.GetValue<string>() ?? string.Empty));
        }

        Thread.Sleep(config["reportSleepMs"]?.GetValue<int>() ?? 0);
        return 0;
    }

    private static void Write(byte[] data)
    {
        using var stdout = Console.OpenStandardOutput();
        stdout.Write(data);
        stdout.Flush();
    }

    /// <summary>A child that inherits this process's handles (its standard output too) and outlives it unless killed.</summary>
    private static void Linger(string pidFile)
    {
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
        start.ArgumentList.Add("--linger");
        start.ArgumentList.Add(pidFile);
        using var child = Process.Start(start)!;
        var deadline = Environment.TickCount64 + 10_000;
        while (!File.Exists(pidFile) && Environment.TickCount64 < deadline)
        {
            Thread.Sleep(20);
        }
    }

    private static void Record(JsonObject config, string[] args)
    {
        if (config["log"]?.GetValue<string>() is not { } log)
        {
            return;
        }

        var cwd = Environment.CurrentDirectory;
        var browser = Environment.GetEnvironmentVariable("BROWSER");
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        var entry = new JsonObject
        {
            ["pid"] = Environment.ProcessId,
            ["exe"] = Environment.ProcessPath,
            ["args"] = new JsonArray([.. args.Select(a => (JsonNode?)a)]),
            ["cwd"] = cwd,
            ["access"] = OperatingSystem.IsWindows()
                ? new DirectoryInfo(cwd).GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.Access)
                : null,
            ["stdin"] = Handle(StdInput, read: true),
            ["stderr"] = Handle(StdError, read: false),
            ["autoUpdate"] = Environment.GetEnvironmentVariable("AGY_CLI_DISABLE_AUTO_UPDATE"),
            ["browser"] = browser,
            ["pathFirst"] = path.Split(';')[0],
            ["openStub"] = Text(Path.Combine(cwd, "open.cmd")),
            ["xdgOpenStub"] = Text(Path.Combine(cwd, "xdg-open.cmd")),
            ["browserExit"] = browser is null ? null : Run("cmd.exe", "/d", "/c", browser, "https://example.invalid/"),
            ["openExit"] = Run("cmd.exe", "/d", "/c", "open", "https://example.invalid/"),
        };
        File.AppendAllText(log, entry.ToJsonString() + "\n");
    }

    /// <summary>The standard handle's file type (2: a character device such as NUL), whether it is a console, and what reading it gives.</summary>
    private static JsonObject Handle(int which, bool read)
    {
        var handle = GetStdHandle(which);
        var type = GetFileType(handle);
        var console = GetConsoleMode(handle, out _);
        var result = new JsonObject { ["type"] = type, ["console"] = console };
        if (read && type == 2 && !console)
        {
            using var stdin = Console.OpenStandardInput();
            result["read"] = stdin.Read(new byte[16]);
        }

        return result;
    }

    private static string? Text(string file) => File.Exists(file) ? File.ReadAllText(file) : null;

    private static int Run(string executable, params string[] arguments)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardOutput = true };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start)!;
        process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return process.ExitCode;
    }

    [LibraryImport("kernel32.dll")]
    private static partial nint GetStdHandle(int which);

    [LibraryImport("kernel32.dll")]
    private static partial uint GetFileType(nint handle);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetConsoleMode(nint handle, out uint mode);
}
