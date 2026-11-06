using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows.Interop;
using AgentSpeed.App;
using Vortice.DXGI;

namespace AgentSpeed.Orb.Gpu;

/// <summary>
/// Developer measurement <c>AGENT_SPEED_FRAME_STATS=FILE</c> (a fully qualified path) of how the frame clock shares the UI
/// thread with input. Raw lines, appended to FILE once a second (QPC ticks; durations in ms):
/// <c>T,qpc,wait,tick,present,presents,busy,input</c> per tick message (wait: from post to handling; present: time in Present
/// over all surfaces; busy: presents the compositor was not ready for; input: input was queued when the tick ran) and
/// <c>I,qpc,message,latency,orb</c> per mouse message injected with <c>dwExtraInfo</c> = 0xA5 in the top byte of 32 bits and the
/// injection time below it (10 µs units of QPC, modulo 2^24). Nothing is recorded without the variable.
/// </summary>
internal static class FrameStats
{
    private const long InjectedTag = 0xA5000000;
    private const long StampMask = 0xFFFFFF;
    private static readonly string? s_path = StatsPath();
    private static readonly object s_gate = new();
    private static readonly double s_msPerTick = 1000.0 / Stopwatch.Frequency;
    private static StringBuilder s_lines = new();
    private static Timer? s_flush;
    private static long s_presentTicks;
    private static int s_presents;
    private static int s_busy;

    public static bool Enabled => s_path is not null;

    /// <summary>Starts recording on the UI thread (once): the input hook and the writer.</summary>
    public static void Attach()
    {
        if (s_path is null || s_flush is not null)
        {
            return;
        }

        ComponentDispatcher.ThreadPreprocessMessage += OnThreadMessage;
        Add(Header());
        s_flush = new Timer(_ => Flush(), null, 1000, 1000);
    }

    public static void BeginTick()
    {
        s_presentTicks = 0;
        s_presents = 0;
        s_busy = 0;
    }

    public static void EndTick(long start, long posted, bool input)
    {
        var end = Stopwatch.GetTimestamp();
        Add(string.Create(CultureInfo.InvariantCulture,
            $"T,{start},{(posted > 0 ? (start - posted) * s_msPerTick : -1):0.###},{(end - start) * s_msPerTick:0.###},{s_presentTicks * s_msPerTick:0.###},{s_presents},{s_busy},{(input ? 1 : 0)}"));
    }

    /// <summary>A frame skipped because the compositor or the GPU was not ready (counted with the busy presents).</summary>
    public static void AddSkip() => s_busy++;

    public static void AddPresent(long ticks, bool busy)
    {
        s_presentTicks += ticks;
        s_presents++;
        if (busy)
        {
            s_busy++;
        }
    }

    /// <summary>A QPC time in 10 µs units, modulo 2^24 (Windows keeps only 32 bits of an input's extra information).</summary>
    private static long Stamp(long qpc) => (long)(qpc * (100_000.0 / Stopwatch.Frequency)) & StampMask;

    private static void OnThreadMessage(ref MSG msg, ref bool handled)
    {
        if (msg.message is < 0x0200 or > 0x020E)
        {
            return;
        }

        var extra = (long)OrbNative.GetMessageExtraInfo() & 0xFFFFFFFF;
        if ((extra & ~StampMask) != InjectedTag)
        {
            return;
        }

        var now = Stopwatch.GetTimestamp();
        var latency = ((Stamp(now) - extra) & StampMask) * 0.01;
        var orb = OrbHostWindow.IsOrbWindow(msg.hwnd) ? 1 : 0;
        Add(string.Create(CultureInfo.InvariantCulture, $"I,{now},{msg.message},{latency:0.##},{orb}"));
    }

    private static string Header()
    {
        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture, $"# qpc {Stopwatch.Frequency}; device {GpuDevice.Shared?.AdapterName ?? "-"}");
        try
        {
            using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
            for (uint i = 0; factory.EnumAdapters1(i, out var adapter).Success && adapter is not null; i++)
            {
                using (adapter)
                {
                    var outputs = new List<string>();
                    for (uint j = 0; adapter.EnumOutputs(j, out var output).Success && output is not null; j++)
                    {
                        using (output)
                        {
                            outputs.Add(output.Description.DeviceName);
                        }
                    }

                    text.Append(CultureInfo.InvariantCulture, $"\n# adapter {i}: {adapter.Description1.Description.Trim()} [{string.Join(' ', outputs)}]");
                }
            }
        }
        catch (SharpGen.Runtime.SharpGenException ex)
        {
            text.Append(CultureInfo.InvariantCulture, $"\n# adapters: {ex.Message}");
        }

        return text.ToString();
    }

    private static void Add(string line)
    {
        lock (s_gate)
        {
            s_lines.Append(line).Append('\n');
        }
    }

    private static void Flush()
    {
        StringBuilder lines;
        lock (s_gate)
        {
            if (s_lines.Length == 0)
            {
                return;
            }

            lines = s_lines;
            s_lines = new StringBuilder(lines.Capacity);
        }

        try
        {
            File.AppendAllText(s_path!, lines.ToString());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn("AGENT_SPEED_FRAME_STATS: could not write.", ex);
        }
    }

    private static string? StatsPath() =>
        Environment.GetEnvironmentVariable("AGENT_SPEED_FRAME_STATS") is { Length: > 0 } path && Path.IsPathFullyQualified(path)
            && !path.StartsWith(@"\\", StringComparison.Ordinal)
            ? path
            : null;
}
