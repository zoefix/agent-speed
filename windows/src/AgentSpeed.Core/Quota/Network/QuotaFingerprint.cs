using System.Buffers;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace AgentSpeed.Core.Quota.Network;

/// <summary>
/// The first 8 bytes of the SHA-256 of a credential (QUOTAS 9.1): a snapshot is keyed by it, so signing in to another
/// account drops the old account's numbers, and a refused credential is not sent again until it changes. The credential
/// itself is never kept.
/// </summary>
public readonly record struct QuotaFingerprint(ulong Value)
{
    public static QuotaFingerprint Of(string credential)
    {
        ArgumentNullException.ThrowIfNull(credential);
        var buffer = ArrayPool<byte>.Shared.Rent(Encoding.UTF8.GetMaxByteCount(credential.Length));
        try
        {
            var length = Encoding.UTF8.GetBytes(credential, buffer);
            return Of(buffer.AsSpan(0, length));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public static QuotaFingerprint Of(ReadOnlySpan<byte> credential)
    {
        Span<byte> digest = stackalloc byte[32];
        SHA256.HashData(credential, digest);
        return new QuotaFingerprint(BinaryPrimitives.ReadUInt64BigEndian(digest));
    }

    public override string ToString() => Value.ToString("x16", System.Globalization.CultureInfo.InvariantCulture);
}
