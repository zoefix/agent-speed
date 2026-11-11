using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace AgentSpeed.Core.Quota.Local;

/// <summary>
/// The TCP ports a process listens on at 127.0.0.1 or every address (IPv4, or IPv6 <c>::</c> which also takes IPv4), from
/// <c>GetExtendedTcpTable</c> (QUOTAS 9.4 <i>Ports</i>), in table order, at most 4.
/// </summary>
[SupportedOSPlatform("windows")]
internal static partial class TcpListeners
{
    private const int AfInet = 2;
    private const int AfInet6 = 23;
    private const int OwnerPidListener = 3;
    private const int Ipv4RowSize = 24;
    private const int Ipv6RowSize = 56;

    public static IReadOnlyList<int> Of(int pid)
    {
        var ports = new List<int>();
        foreach (var family in new[] { AfInet, AfInet6 })
        {
            foreach (var (owner, port, loopbackOrAny) in Rows(family))
            {
                if (owner == pid && loopbackOrAny && port > 0 && !ports.Contains(port))
                {
                    ports.Add(port);
                }

                if (ports.Count >= 4)
                {
                    return ports;
                }
            }
        }

        return ports;
    }

    private static unsafe List<(int Owner, int Port, bool LoopbackOrAny)> Rows(int family)
    {
        var rows = new List<(int, int, bool)>();
        var size = 0;
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var buffer = size == 0 ? IntPtr.Zero : Marshal.AllocHGlobal(size);
            try
            {
                var status = GetExtendedTcpTable(buffer, ref size, false, family, OwnerPidListener, 0);
                if (status == 122 || buffer == IntPtr.Zero)
                {
                    if (size is <= 0 or > 16 << 20)
                    {
                        return rows;
                    }

                    continue;
                }

                if (status != 0)
                {
                    return rows;
                }

                var count = *(uint*)buffer;
                var rowSize = family == AfInet ? Ipv4RowSize : Ipv6RowSize;
                var first = (byte*)buffer + 4;
                for (var i = 0; i < count && 4 + (i + 1) * (long)rowSize <= size; i++)
                {
                    var row = first + i * rowSize;
                    if (family == AfInet)
                    {
                        var address = *(uint*)(row + 4);
                        var port = PortOf(*(uint*)(row + 8));
                        var owner = *(int*)(row + 20);
                        rows.Add((owner, port, address is 0 or 0x0100007F));
                    }
                    else
                    {
                        var any = new ReadOnlySpan<byte>(row, 16).IndexOfAnyExcept((byte)0) < 0;
                        var port = PortOf(*(uint*)(row + 20));
                        var owner = *(int*)(row + 52);
                        rows.Add((owner, port, any));
                    }
                }

                return rows;
            }
            finally
            {
                if (buffer != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }
        }

        return rows;
    }

    /// <summary>The port sits in the low 16 bits in network byte order.</summary>
    private static int PortOf(uint value) => (int)(((value & 0xFF) << 8) | ((value >> 8) & 0xFF));

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("iphlpapi.dll")]
    private static partial uint GetExtendedTcpTable(IntPtr table, ref int size, [MarshalAs(UnmanagedType.Bool)] bool order, int family,
        int tableClass, uint reserved);
}
