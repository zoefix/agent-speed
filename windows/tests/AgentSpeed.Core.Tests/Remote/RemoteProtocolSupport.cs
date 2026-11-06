using System.Collections;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using AgentSpeed.Core.Remote;

namespace AgentSpeed.Core.Tests.Remote;

/// <summary>A fact that needs the POSIX shells the remote scripts run in; skipped on Windows.</summary>
public sealed class UnixFactAttribute : FactAttribute
{
    public UnixFactAttribute()
    {
        if (OperatingSystem.IsWindows())
        {
            Skip = "The remote scripts need /bin/sh";
        }
    }
}

/// <summary>A theory that needs the POSIX shells the remote scripts run in; skipped on Windows.</summary>
public sealed class UnixTheoryAttribute : TheoryAttribute
{
    public UnixTheoryAttribute()
    {
        if (OperatingSystem.IsWindows())
        {
            Skip = "The remote scripts need /bin/sh";
        }
    }
}

/// <summary>JSON text with sorted keys (as <c>JSONSerialization.sortedKeys</c> writes it), from dictionaries, lists and scalars.</summary>
internal static class SortedJson
{
    private static readonly JsonWriterOptions Options = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static string Of(object? value)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, Options))
        {
            Write(writer, value);
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void Write(Utf8JsonWriter writer, object? value)
    {
        switch (value)
        {
            case null:
                writer.WriteNullValue();
                break;
            case string text:
                writer.WriteStringValue(text);
                break;
            case bool flag:
                writer.WriteBooleanValue(flag);
                break;
            case int number:
                writer.WriteNumberValue(number);
                break;
            case long number:
                writer.WriteNumberValue(number);
                break;
            case double number:
                writer.WriteNumberValue(number);
                break;
            case IDictionary<string, object?> map:
                writer.WriteStartObject();
                foreach (var (key, item) in map.OrderBy(p => p.Key, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(key);
                    Write(writer, item);
                }

                writer.WriteEndObject();
                break;
            case IEnumerable list:
                writer.WriteStartArray();
                foreach (var item in list)
                {
                    Write(writer, item);
                }

                writer.WriteEndArray();
                break;
            default:
                throw new ArgumentException("Unsupported JSON value " + value.GetType(), nameof(value));
        }
    }
}

/// <summary>Relay lines for tests (the macOS <c>RelayLines</c> of RemoteTestSupport.swift).</summary>
internal static class RelayLines
{
    public const string HostId = "8C0D6B0A-1111-4222-8333-944455556666";
    public const long BaseMs = 1_780_000_000_000;
    public const string Ready = """{"t":"ready"}""";

    public static string Prefix => $"ssh://{HostId}/";

    public static string Json(Dictionary<string, object?> value) => SortedJson.Of(value);

    public static string Hello(long now = BaseMs, int protocol = 1) => Json(new()
    {
        ["t"] = "hello", ["protocol"] = protocol, ["version"] = "1.7.0", ["arch"] = "x86_64", ["now"] = now,
    });

    public static Dictionary<string, object?> Fact(
        string kind = "serviceTier", string value = "fast", string raw = "priority", string evidence = "recorded",
        string? provider = "openai", string scope = "request") => new()
    {
        ["kind"] = kind, ["value"] = value, ["rawValue"] = raw, ["evidence"] = evidence, ["provider"] = provider, ["scope"] = scope,
    };

    public static Dictionary<string, object?> Action(long callMs = BaseMs, long? resultMs = BaseMs + 400) => new()
    {
        ["tool"] = "shell", ["digest"] = "0123456789abcdef", ["callMs"] = callMs, ["resultMs"] = resultMs, ["failed"] = false,
        ["model"] = "gpt-5.5", ["seeded"] = true,
    };

    public static Dictionary<string, object?> AgentObject(string id = "/home/u/.codex/sessions/a.jsonl") => new()
    {
        ["id"] = id, ["source"] = "codex", ["project"] = "proj", ["cwd"] = "/home/u/proj", ["model"] = "gpt-5.5",
        ["subagent"] = false, ["phase"] = "generating", ["inFlightStartMs"] = BaseMs, ["lastLineMs"] = BaseMs + 500,
        ["modes"] = new List<object?> { Fact() }, ["sessionModes"] = new List<object?>(), ["configuredModes"] = new List<object?>(),
        ["actions"] = new List<object?> { Action() },
    };

    public static string Agent(Dictionary<string, object?>? value = null) =>
        Json(new() { ["t"] = "agent", ["a"] = value ?? AgentObject() });

    public static string Agent(Action<Dictionary<string, object?>> change)
    {
        var value = AgentObject();
        change(value);
        return Agent(value);
    }

    public static Dictionary<string, object?> ResponseObject(string key = "resp_1") => new()
    {
        ["key"] = key, ["source"] = "codex", ["agent"] = "/home/u/.codex/sessions/a.jsonl", ["model"] = "gpt-5.5",
        ["startMs"] = BaseMs, ["endMs"] = BaseMs + 2000, ["firstMs"] = BaseMs + 300, ["input"] = 1200, ["cachedInput"] = 1000,
        ["cacheWriteInput"] = 0, ["cacheWrite1hInput"] = 0, ["output"] = 340, ["reasoning"] = 120, ["requestCount"] = 1,
        ["modes"] = new List<object?>(), ["final"] = true, ["provider"] = "openai", ["fast"] = false,
    };

    public static string Response(Dictionary<string, object?>? value = null, bool completion = true, string project = "proj") =>
        Json(new() { ["t"] = "response", ["r"] = value ?? ResponseObject(), ["completion"] = completion, ["project"] = project });

    public static string Response(Action<Dictionary<string, object?>> change)
    {
        var value = ResponseObject();
        change(value);
        return Response(value);
    }

    public static string Clock(long id, long now) => Json(new() { ["t"] = "clock", ["id"] = id, ["now"] = now });

    /// <summary><paramref name="text"/> with <paramref name="old"/> replaced; fails when it is absent, so no test passes vacuously.</summary>
    public static string Replaced(string text, string old, string replacement)
    {
        Assert.Contains(old, text, StringComparison.Ordinal);
        return text.Replace(old, replacement, StringComparison.Ordinal);
    }

    public static RelayDecodeOutcome Decode(string line, string host = HostId) => new RelayLineDecoder(host).Decode(Encoding.UTF8.GetBytes(line));

    public static RelayMessage Message(string line, string host = HostId)
    {
        var outcome = Decode(line, host);
        Assert.True(outcome.Kind == RelayDecodeKind.Message, $"not a message ({outcome.Kind} {outcome.Error?.Message}): {Shortened(line)}");
        return outcome.Message!;
    }

    public static RemoteLineException? Invalid(string line) =>
        Decode(line) is { Kind: RelayDecodeKind.Invalid, Error: { } error } ? error : null;

    private static string Shortened(string line) => line.Length <= 300 ? line : line[..300] + "…";
}

/// <summary>Deterministic bytes (the xorshift of the macOS <c>pseudoRandomData</c>) and their digests.</summary>
internal static class TestBytes
{
    public static byte[] PseudoRandom(int count, ulong seed = 0x9E37_79B9_7F4A_7C15)
    {
        var state = seed;
        var bytes = new byte[count];
        for (var i = 0; i < count; i++)
        {
            state ^= state >> 12;
            state ^= state << 25;
            state ^= state >> 27;
            bytes[i] = (byte)((state * 0x2545_F491_4F6C_DD1D) >> 56);
        }

        return bytes;
    }

    public static string Sha256(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));

    public static string Sha256(string text) => Sha256(Encoding.UTF8.GetBytes(text));
}

/// <summary>A relay bundle with fake executables in a temp folder (the macOS <c>FakeRelayBundle</c>).</summary>
internal sealed class FakeRelayBundle : IDisposable
{
    public FakeRelayBundle(string version = "1.7.0", int bytes = 600_000)
    {
        Folder = new TempDirectory();
        foreach (var arch in Enum.GetValues<RemoteArch>())
        {
            File.WriteAllBytes(Folder.Combine(RemoteRelayName.Bundled(arch)), TestBytes.PseudoRandom(bytes, arch == RemoteArch.X86_64 ? 7UL : 8UL));
        }

        Bundle = new RemoteRelayBundle(Folder.Path, version);
        X86 = Bundle.Executable(RemoteArch.X86_64) ?? throw new InvalidOperationException("no x86_64 relay");
    }

    public TempDirectory Folder { get; }

    public RemoteRelayBundle Bundle { get; }

    public RemoteRelayExecutable X86 { get; }

    public void Dispose() => Folder.Dispose();
}

internal sealed record ShellOutcome(int Status, string Stdout, string Stderr);

/// <summary>Runs a program with an argument vector (never through a shell), feeding stdin from its own task.</summary>
internal static class ShellRunner
{
    public static ShellOutcome Run(string executable, IEnumerable<string> arguments, byte[]? stdin = null,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        if (environment is not null)
        {
            info.Environment.Clear();
            foreach (var (key, value) in environment)
            {
                info.Environment[key] = value;
            }
        }

        using var process = Process.Start(info) ?? throw new InvalidOperationException("cannot start " + executable);
        var output = ReadAll(process.StandardOutput.BaseStream);
        var errors = ReadAll(process.StandardError.BaseStream);
        var input = Task.Run(() =>
        {
            try
            {
                process.StandardInput.BaseStream.Write(stdin ?? []);
                process.StandardInput.BaseStream.Flush();
            }
            catch (IOException)
            {
            }
            finally
            {
                try
                {
                    process.StandardInput.Close();
                }
                catch (IOException)
                {
                }
            }
        });
        if (!process.WaitForExit(TimeSpan.FromSeconds(60)))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException(executable + " did not exit");
        }

        input.GetAwaiter().GetResult();
        return new ShellOutcome(process.ExitCode, Encoding.UTF8.GetString(output.GetAwaiter().GetResult()),
            Encoding.UTF8.GetString(errors.GetAwaiter().GetResult()));
    }

    /// <summary><c>sh -c '&lt;script&gt;' agent-speed &lt;arguments&gt;</c> with the script as one argument (what the remote shell ends with).</summary>
    public static ShellOutcome Script(string script, IEnumerable<string> arguments, string shell = "/bin/sh", byte[]? stdin = null,
        IReadOnlyDictionary<string, string>? environment = null) =>
        Run(shell, ["-c", script, "agent-speed", .. arguments], stdin, environment);

    private static async Task<byte[]> ReadAll(Stream stream)
    {
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer).ConfigureAwait(false);
        return buffer.ToArray();
    }
}
