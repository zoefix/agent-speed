using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using AgentSpeed.Core.IO.Sqlite;
using AgentSpeed.Core.Quota.Cookies;

namespace AgentSpeed.Core.Tests.Quota.Cookies;

/// <summary>A fact that needs Windows (DPAPI, file sharing modes); skipped elsewhere.</summary>
public sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = "Needs DPAPI and Windows file sharing";
        }
    }
}

/// <summary>One row of a Chromium <c>cookies</c> table; times are Unix seconds.</summary>
internal readonly record struct ChromeRow(string Host, string Name, string Value, byte[] Encrypted, long ExpiresS, long LastAccessS)
{
    public static ChromeRow Plain(string host, string name, string value, long lastAccessS, long expiresS = 0) =>
        new(host, name, value, [], expiresS, lastAccessS);

    public static ChromeRow Sealed(string host, string name, byte[] encrypted, long lastAccessS, long expiresS = 0) =>
        new(host, name, string.Empty, encrypted, expiresS, lastAccessS);
}

/// <summary>One row of Firefox's <c>moz_cookies</c>: <c>expiry</c> as stored, <c>lastAccessed</c> in Unix seconds.</summary>
internal readonly record struct FirefoxRow(string Host, string Name, string Value, long Expiry, long LastAccessS);

/// <summary>Browser user data trees written into a test's own temporary folder (QUOTAS 9.10); never the real ones.</summary>
internal static class BrowserStores
{
    public const long NowS = 1_794_338_773;

    public static long NowMs => NowS * 1000;

    /// <summary>Microseconds since 1601 of a Unix time in seconds.</summary>
    public static long ChromeTime(long seconds) => seconds * 1_000_000 + ChromeCookieStore.WindowsEpochOffsetUs;

    /// <summary>A Windows <c>v10</c> value: AES-256-GCM, with the SHA-256 of <paramref name="host"/> first when given.</summary>
    public static byte[] Gcm(string plain, byte[] key, string? host = null, string prefix = "v10")
    {
        byte[] body = [.. host is null ? [] : SHA256.HashData(Encoding.UTF8.GetBytes(host)), .. Encoding.UTF8.GetBytes(plain)];
        var nonce = RandomNumberGenerator.GetBytes(12);
        var cipher = new byte[body.Length];
        var tag = new byte[16];
        using var gcm = new AesGcm(key, 16);
        gcm.Encrypt(nonce, body, cipher, tag);
        return [.. Encoding.ASCII.GetBytes(prefix), .. nonce, .. cipher, .. tag];
    }

    /// <summary>A <c>cookies</c> database with Chromium's own schema (version 24) and <c>meta</c> table.</summary>
    public static string Chrome(string path, int version, params ChromeRow[] rows)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var db = SqliteConnection.Open(path, SqliteOpenMode.ReadWriteCreate);
        db.Execute($"""
            CREATE TABLE meta(key LONGVARCHAR NOT NULL UNIQUE PRIMARY KEY, value LONGVARCHAR);
            INSERT INTO meta VALUES('version', '{version}'), ('last_compatible_version', '{version}');
            CREATE TABLE cookies(creation_utc INTEGER NOT NULL, host_key TEXT NOT NULL, top_frame_site_key TEXT NOT NULL,
                name TEXT NOT NULL, value TEXT NOT NULL, encrypted_value BLOB NOT NULL, path TEXT NOT NULL,
                expires_utc INTEGER NOT NULL, is_secure INTEGER NOT NULL, is_httponly INTEGER NOT NULL,
                last_access_utc INTEGER NOT NULL, has_expires INTEGER NOT NULL, is_persistent INTEGER NOT NULL,
                priority INTEGER NOT NULL, samesite INTEGER NOT NULL, source_scheme INTEGER NOT NULL,
                source_port INTEGER NOT NULL, last_update_utc INTEGER NOT NULL, source_type INTEGER NOT NULL,
                has_cross_site_ancestor INTEGER NOT NULL);
            CREATE UNIQUE INDEX cookies_unique_index ON cookies(host_key, top_frame_site_key, has_cross_site_ancestor, name,
                path, source_scheme, source_port);
            """);
        using var insert = db.Prepare("""
            INSERT INTO cookies VALUES(?1, ?2, '', ?3, ?4, ?5, '/', ?6, 1, 1, ?7, ?8, ?8, 1, 0, 2, 443, ?1, 0, 0)
            """);
        var creation = 0;
        foreach (var row in rows)
        {
            insert.Bind(1, ChromeTime(NowS - 86_400) + creation++)
                .Bind(2, row.Host)
                .Bind(3, row.Name)
                .Bind(4, row.Value)
                .BindBlob(5, row.Encrypted)
                .Bind(6, row.ExpiresS == 0 ? 0 : ChromeTime(row.ExpiresS))
                .Bind(7, ChromeTime(row.LastAccessS))
                .Bind(8, row.ExpiresS == 0 ? 0 : 1)
                .Run();
        }

        return path;
    }

    /// <summary>
    /// A <c>cookies.sqlite</c> with Firefox's schema in WAL mode. The returned connection stays open with checkpoints off,
    /// so the rows live only in the <c>-wal</c> until it is disposed.
    /// </summary>
    public static IDisposable Firefox(string path, params FirefoxRow[] rows)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var db = SqliteConnection.Open(path, SqliteOpenMode.ReadWriteCreate);
        db.Execute("""
            PRAGMA journal_mode = WAL;
            PRAGMA wal_autocheckpoint = 0;
            CREATE TABLE moz_cookies (id INTEGER PRIMARY KEY, originAttributes TEXT NOT NULL DEFAULT '', name TEXT, value TEXT,
                host TEXT, path TEXT, expiry INTEGER, lastAccessed INTEGER, creationTime INTEGER, isSecure INTEGER,
                isHttpOnly INTEGER, inBrowserElement INTEGER DEFAULT 0, sameSite INTEGER DEFAULT 0, rawSameSite INTEGER DEFAULT 0,
                schemeMap INTEGER DEFAULT 0, isPartitionedAttributeSet INTEGER DEFAULT 0,
                CONSTRAINT moz_uniqueid UNIQUE (name, host, path, originAttributes));
            """);
        using var insert = db.Prepare("""
            INSERT INTO moz_cookies(name, value, host, path, expiry, lastAccessed, creationTime, isSecure, isHttpOnly)
            VALUES(?1, ?2, ?3, '/', ?4, ?5, ?5, 1, 1)
            """);
        foreach (var row in rows)
        {
            insert.Bind(1, row.Name).Bind(2, row.Value).Bind(3, row.Host).Bind(4, row.Expiry).Bind(5, row.LastAccessS * 1_000_000).Run();
        }

        return db;
    }

    /// <summary>A <c>Local State</c> whose <c>os_crypt.encrypted_key</c> is <c>DPAPI</c> + <paramref name="wrapped"/>, in base64.</summary>
    public static void LocalState(string userData, byte[] wrapped)
    {
        Directory.CreateDirectory(userData);
        var json = new JsonObject
        {
            ["browser"] = new JsonObject { ["enabled_labs_experiments"] = new JsonArray() },
            ["os_crypt"] = new JsonObject
            {
                ["audit_enabled"] = true,
                ["encrypted_key"] = Convert.ToBase64String([.. "DPAPI"u8, .. wrapped]),
            },
        };
        File.WriteAllText(Path.Combine(userData, "Local State"), json.ToJsonString());
    }
}

/// <summary>Hands out a copy of a fixed key and keeps what it handed out, so tests can see the reader zero it.</summary>
internal sealed class FixedUnwrapper(byte[]? key) : IBrowserKeyUnwrapper
{
    public byte[]? Key { get; set; } = key;

    public List<BrowserKind> Asked { get; } = [];

    public List<byte[]> HandedOut { get; } = [];

    public byte[]? Unwrap(BrowserKind browser, ReadOnlySpan<byte> wrapped)
    {
        Asked.Add(browser);
        if (Key is null)
        {
            return null;
        }

        var copy = Key.ToArray();
        HandedOut.Add(copy);
        return copy;
    }
}

/// <summary>Counts the calls to the real DPAPI unwrapper.</summary>
internal sealed class CountingUnwrapper(IBrowserKeyUnwrapper inner) : IBrowserKeyUnwrapper
{
    public List<BrowserKind> Asked { get; } = [];

    public byte[]? Unwrap(BrowserKind browser, ReadOnlySpan<byte> wrapped)
    {
        Asked.Add(browser);
        return inner.Unwrap(browser, wrapped);
    }
}

/// <summary><c>CryptProtectData</c> for test keys only.</summary>
internal static class TestDpapi
{
    private const uint UiForbidden = 0x1;
    private const uint LocalMachine = 0x4;

    /// <summary>
    /// The current user's DPAPI blob of <paramref name="data"/>; the machine's when the user's keys are out of reach (a
    /// key-based SSH logon has no password to open them). <c>CryptUnprotectData</c> reads both the same way.
    /// </summary>
    public static byte[] Protect(byte[] data)
    {
        var handle = GCHandle.Alloc(data, GCHandleType.Pinned);
        try
        {
            var input = new DataBlob { Size = data.Length, Data = handle.AddrOfPinnedObject() };
            if (!CryptProtectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, UiForbidden, out var output)
                && !CryptProtectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, UiForbidden | LocalMachine, out output))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }

            try
            {
                var bytes = new byte[output.Size];
                Marshal.Copy(output.Data, bytes, 0, output.Size);
                return bytes;
            }
            finally
            {
                LocalFree(output.Data);
            }
        }
        finally
        {
            handle.Free();
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Size;
        public IntPtr Data;
    }

    [DllImport("crypt32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(
        ref DataBlob dataIn, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, uint flags, out DataBlob dataOut);

    [DllImport("kernel32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern IntPtr LocalFree(IntPtr memory);
}
