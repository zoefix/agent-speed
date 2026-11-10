namespace AgentSpeed.Core.Quota.Cookies;

/// <summary>Unwraps a Chromium browser's cookie key (QUOTAS 9.10): DPAPI on Windows, a stand-in in tests.</summary>
public interface IBrowserKeyUnwrapper
{
    /// <summary>
    /// The plain key of <paramref name="browser"/>'s <c>os_crypt.encrypted_key</c>, its <c>DPAPI</c> prefix already
    /// dropped; null when it cannot be unwrapped. The caller zeroes the result.
    /// </summary>
    byte[]? Unwrap(BrowserKind browser, ReadOnlySpan<byte> wrapped);
}

/// <summary>The current user's DPAPI (<c>CryptUnprotectData</c>, never showing a prompt); nothing off Windows.</summary>
public sealed class DpapiKeyUnwrapper : IBrowserKeyUnwrapper
{
    public static DpapiKeyUnwrapper Instance { get; } = new();

    public byte[]? Unwrap(BrowserKind browser, ReadOnlySpan<byte> wrapped) =>
        OperatingSystem.IsWindows() ? DpapiNative.Unprotect(wrapped) : null;
}
