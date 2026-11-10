using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;

namespace AgentSpeed.Core.Quota.Cookies;

/// <summary><c>crypt32!CryptUnprotectData</c>: the output is copied, then zeroed and freed with <c>LocalFree</c>.</summary>
[SupportedOSPlatform("windows")]
internal static unsafe partial class DpapiNative
{
    private const uint UiForbidden = 0x1;

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public uint Size;
        public byte* Data;
    }

    /// <summary>The unwrapped bytes, or null when DPAPI refuses (another user's or machine's blob, no key, corrupt).</summary>
    public static byte[]? Unprotect(ReadOnlySpan<byte> wrapped)
    {
        if (wrapped.IsEmpty)
        {
            return null;
        }

        fixed (byte* pointer = wrapped)
        {
            var input = new DataBlob { Size = (uint)wrapped.Length, Data = pointer };
            var output = default(DataBlob);
            if (!CryptUnprotectData(&input, 0, null, 0, 0, UiForbidden, &output))
            {
                return null;
            }

            try
            {
                return output.Data is null ? null : new ReadOnlySpan<byte>(output.Data, checked((int)output.Size)).ToArray();
            }
            finally
            {
                if (output.Data is not null)
                {
                    CryptographicOperations.ZeroMemory(new Span<byte>(output.Data, checked((int)output.Size)));
                    LocalFree((nint)output.Data);
                }
            }
        }
    }

    [LibraryImport("crypt32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CryptUnprotectData(
        DataBlob* dataIn, nint description, DataBlob* entropy, nint reserved, nint prompt, uint flags, DataBlob* dataOut);

    [LibraryImport("kernel32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial nint LocalFree(nint memory);
}
