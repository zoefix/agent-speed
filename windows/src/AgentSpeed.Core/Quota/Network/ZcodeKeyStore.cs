using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace AgentSpeed.Core.Quota.Network;

/// <summary>Where the ZCode key lives (QUOTAS 9.8): the platform's secret store only, never the settings file.</summary>
public interface IZcodeKeyStore
{
    /// <summary>Whether a key is saved.</summary>
    bool HasKey();

    /// <summary>The key, read right before its request.</summary>
    string? Read();

    bool Save(string key);

    void Remove();
}

/// <summary>
/// Windows Credential Manager: a generic credential <c>AgentSpeed:zcode-api-key</c> of this user on this computer (not
/// roaming), its blob the key's UTF-8 bytes, cleared after every read.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed partial class CredentialManagerZcodeKeyStore(string target = CredentialManagerZcodeKeyStore.DefaultTarget) : IZcodeKeyStore
{
    public const string DefaultTarget = "AgentSpeed:zcode-api-key";

    private const uint GenericType = 1;
    private const uint PersistLocalMachine = 2;

    public string Target { get; } = target;

    public unsafe bool HasKey()
    {
        if (!CredReadW(Target, GenericType, 0, out var pointer))
        {
            return false;
        }

        var credential = (Credential*)pointer;
        if (credential->CredentialBlob != IntPtr.Zero && credential->CredentialBlobSize <= 4096)
        {
            CryptographicOperations.ZeroMemory(new Span<byte>((void*)credential->CredentialBlob, (int)credential->CredentialBlobSize));
        }

        CredFree(pointer);
        return true;
    }

    public unsafe string? Read()
    {
        if (!CredReadW(Target, GenericType, 0, out var pointer))
        {
            return null;
        }

        try
        {
            var credential = (Credential*)pointer;
            if (credential->CredentialBlobSize is 0 or > 4096 || credential->CredentialBlob == IntPtr.Zero)
            {
                return null;
            }

            var blob = new Span<byte>((void*)credential->CredentialBlob, (int)credential->CredentialBlobSize);
            try
            {
                return Encoding.UTF8.GetString(blob);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(blob);
            }
        }
        finally
        {
            CredFree(pointer);
        }
    }

    public unsafe bool Save(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        var bytes = Encoding.UTF8.GetBytes(key);
        var target = Marshal.StringToHGlobalUni(Target);
        var user = Marshal.StringToHGlobalUni("Agent Speed");
        try
        {
            fixed (byte* blob = bytes)
            {
                var credential = new Credential
                {
                    Type = GenericType,
                    TargetName = target,
                    CredentialBlobSize = (uint)bytes.Length,
                    CredentialBlob = (IntPtr)blob,
                    Persist = PersistLocalMachine,
                    UserName = user,
                };
                return CredWriteW(&credential, 0);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
            Marshal.FreeHGlobal(target);
            Marshal.FreeHGlobal(user);
        }
    }

    public void Remove() => _ = CredDeleteW(Target, GenericType, 0);

    [StructLayout(LayoutKind.Sequential)]
    private struct Credential
    {
        public uint Flags;
        public uint Type;
        public IntPtr TargetName;
        public IntPtr Comment;
        public long LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public IntPtr TargetAlias;
        public IntPtr UserName;
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("advapi32.dll", EntryPoint = "CredReadW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CredReadW(string target, uint type, uint flags, out IntPtr credential);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("advapi32.dll", EntryPoint = "CredWriteW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool CredWriteW(Credential* credential, uint flags);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("advapi32.dll", EntryPoint = "CredDeleteW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CredDeleteW(string target, uint type, uint flags);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("advapi32.dll", EntryPoint = "CredFree")]
    private static partial void CredFree(IntPtr buffer);
}

/// <summary>A key kept in memory (captures and tests).</summary>
public sealed class MemoryZcodeKeyStore(string? key = null) : IZcodeKeyStore
{
    private readonly Lock _lock = new();
    private string? _key = key;

    /// <summary>How often the key was read (tests: only when a request is due).</summary>
    public int Reads { get; private set; }

    public bool HasKey()
    {
        lock (_lock)
        {
            return _key is not null;
        }
    }

    public string? Read()
    {
        lock (_lock)
        {
            Reads++;
            return _key;
        }
    }

    public bool Save(string key)
    {
        lock (_lock)
        {
            _key = key;
            return true;
        }
    }

    public void Remove()
    {
        lock (_lock)
        {
            _key = null;
        }
    }
}

/// <summary>A pasted key as saved: surrounding white space dropped, nothing else changed; null when empty or unfit for a header.</summary>
public static class ZcodeKey
{
    public static string? Cleaned(string? text)
    {
        var key = text?.Trim();
        return key is { Length: > 0 and <= 4096 } && key.All(c => c is > ' ' and <= '~') ? key : null;
    }
}
