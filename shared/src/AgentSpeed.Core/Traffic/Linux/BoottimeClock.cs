using System.Runtime.InteropServices;

namespace AgentSpeed.Core.Traffic.Linux;

/// <summary>Linux monotonic time that continues advancing while the machine is suspended.</summary>
internal static partial class BoottimeClock
{
    internal const int ClockId = 7; // CLOCK_BOOTTIME

    [StructLayout(LayoutKind.Sequential)]
    private struct Timespec
    {
        public long Seconds;
        public long Nanoseconds;
    }

    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    internal static long Milliseconds()
    {
        if (ClockGetTime(ClockId, out var time) != 0)
        {
            throw new InvalidOperationException($"clock_gettime(CLOCK_BOOTTIME) failed with errno {Marshal.GetLastPInvokeError()}.");
        }

        return ToMilliseconds(time.Seconds, time.Nanoseconds);
    }

    internal static long ToMilliseconds(long seconds, long nanoseconds) =>
        checked(seconds * 1000 + nanoseconds / 1_000_000);

    [LibraryImport("libc", EntryPoint = "clock_gettime", SetLastError = true)]
    private static partial int ClockGetTime(int clockId, out Timespec time);
}
