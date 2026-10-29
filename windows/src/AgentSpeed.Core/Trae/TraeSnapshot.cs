using System.Buffers.Binary;
using System.Security.Cryptography;
using AgentSpeed.Core.Antigravity;
using AgentSpeed.Core.IO;

namespace AgentSpeed.Core.Trae;

/// <summary>Read-only SQLCipher 4 snapshot. Authenticates every page and merges only committed WAL frames.</summary>
internal static class TraeSnapshot
{
    internal const int PageSize = 4096;
    internal const int MaxBytes = 256 * 1024 * 1024;
    private const int PayloadEnd = PageSize - 80;
    // Trae's shipped local-store key, not an account credential. See spec/research/trae.md.
    private static readonly byte[] Key = Convert.FromHexString("3605f6691095a993f03d5009c918352ef5be31ae31e8f000212b81ff058da773");

    public static byte[] Read(string path)
    {
        var before = AntigravityStamp.Of(path) ?? throw new FileNotFoundException("Trae database is missing.", path);
        RejectJournal(path);
        var database = ReadFile(path);
        var wal = before.WalLength > 0 ? ReadFile(path + "-wal") : [];
        RejectJournal(path);
        if (before != AntigravityStamp.Of(path)) throw new IOException("Trae database changed while taking a snapshot.");
        return Decode(database, wal);
    }

    internal static byte[] Decode(byte[] database, byte[] wal)
    {
        if (database.Length < PageSize || database.Length > MaxBytes || database.Length % PageSize != 0)
            throw new InvalidDataException("Unsupported Trae database size.");
        var plain = database.AsSpan(0, 16).SequenceEqual("SQLite format 3\0"u8);
        if (plain && BinaryPrimitives.ReadUInt16BigEndian(database.AsSpan(16, 2)) != PageSize)
            throw new InvalidDataException("Unsupported Trae SQLite page size.");
        var frames = CommittedFrames(wal, out var committedSize);
        var pageCount = committedSize > 0 ? committedSize : database.Length / PageSize;
        if (pageCount <= 0 || pageCount > MaxBytes / PageSize) throw new InvalidDataException("Trae snapshot is too large.");
        var result = new byte[pageCount * PageSize];
        var salt = database.AsSpan(0, 16).ToArray();
        var macSalt = salt.Select(b => (byte)(b ^ 0x3a)).ToArray();
        var macKey = plain ? [] : Rfc2898DeriveBytes.Pbkdf2(Key, macSalt, 2, HashAlgorithmName.SHA512, 32);
        using var aes = Aes.Create();
        aes.Key = Key;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.None;
        for (var page = 1; page <= pageCount; page++)
        {
            ReadOnlySpan<byte> source;
            if (frames.TryGetValue(page, out var offset)) source = wal.AsSpan(offset, PageSize);
            else if (page <= database.Length / PageSize) source = database.AsSpan((page - 1) * PageSize, PageSize);
            else throw new InvalidDataException("Missing Trae database page.");
            var target = result.AsSpan((page - 1) * PageSize, PageSize);
            if (plain) source.CopyTo(target);
            else DecryptPage(source, target, page, salt, macKey, aes);
        }

        // sqlite3_deserialize cannot attach a WAL. It receives the already-merged committed snapshot.
        result[18] = result[19] = 1;
        return result;
    }

    private static void DecryptPage(ReadOnlySpan<byte> page, Span<byte> output, int number, byte[] salt, byte[] macKey, Aes aes)
    {
        var start = number == 1 ? 16 : 0;
        if (number == 1 && !page[..16].SequenceEqual(salt)) throw new InvalidDataException("Trae database salt changed.");
        var authenticated = new byte[PayloadEnd + 16 - start + 4];
        page.Slice(start, PayloadEnd + 16 - start).CopyTo(authenticated);
        BinaryPrimitives.WriteUInt32LittleEndian(authenticated.AsSpan(authenticated.Length - 4), (uint)number);
        var digest = HMACSHA512.HashData(macKey, authenticated);
        if (!CryptographicOperations.FixedTimeEquals(digest, page[(PayloadEnd + 16)..]))
            throw new InvalidDataException("Trae database page authentication failed.");
        aes.DecryptCbc(page.Slice(start, PayloadEnd - start), page.Slice(PayloadEnd, 16), output.Slice(start, PayloadEnd - start), PaddingMode.None);
        if (number == 1) "SQLite format 3\0"u8.CopyTo(output);
    }

    private static Dictionary<int, int> CommittedFrames(byte[] wal, out int databaseSize)
    {
        databaseSize = 0;
        var result = new Dictionary<int, int>();
        if (wal.Length == 0) return result;
        if (wal.Length < 32) throw new IOException("Incomplete Trae WAL header.");
        var header = wal.AsSpan(0, 32);
        var magic = BinaryPrimitives.ReadUInt32BigEndian(header);
        if (magic is not 0x377f0682 and not 0x377f0683 || BinaryPrimitives.ReadUInt32BigEndian(header[4..]) != 3007000
            || BinaryPrimitives.ReadUInt32BigEndian(header[8..]) != PageSize)
            throw new InvalidDataException("Unsupported Trae WAL header.");
        var little = magic == 0x377f0682;
        uint one = 0, two = 0;
        Checksum(header[..24], little, ref one, ref two);
        if (one != BinaryPrimitives.ReadUInt32BigEndian(header[24..]) || two != BinaryPrimitives.ReadUInt32BigEndian(header[28..]))
            throw new InvalidDataException("Invalid Trae WAL header checksum.");
        var pending = new Dictionary<int, int>();
        for (var offset = 32; offset + 24 + PageSize <= wal.Length; offset += 24 + PageSize)
        {
            var frame = wal.AsSpan(offset, 24 + PageSize);
            if (!frame.Slice(8, 8).SequenceEqual(header.Slice(16, 8))) break;
            var page = BinaryPrimitives.ReadUInt32BigEndian(frame);
            var size = BinaryPrimitives.ReadUInt32BigEndian(frame[4..]);
            if (page == 0 || page > MaxBytes / PageSize || size > MaxBytes / PageSize) break;
            Checksum(frame[..8], little, ref one, ref two);
            Checksum(frame[24..], little, ref one, ref two);
            if (one != BinaryPrimitives.ReadUInt32BigEndian(frame[16..]) || two != BinaryPrimitives.ReadUInt32BigEndian(frame[20..])) break;
            pending[(int)page] = offset + 24;
            if (size > 0)
            {
                foreach (var pair in pending) result[pair.Key] = pair.Value;
                pending.Clear();
                databaseSize = (int)size;
            }
        }

        return result;
    }

    private static void Checksum(ReadOnlySpan<byte> bytes, bool little, ref uint one, ref uint two)
    {
        for (var i = 0; i < bytes.Length; i += 8)
        {
            var a = little ? BinaryPrimitives.ReadUInt32LittleEndian(bytes[i..]) : BinaryPrimitives.ReadUInt32BigEndian(bytes[i..]);
            var b = little ? BinaryPrimitives.ReadUInt32LittleEndian(bytes[(i + 4)..]) : BinaryPrimitives.ReadUInt32BigEndian(bytes[(i + 4)..]);
            unchecked { one += a + two; two += b + one; }
        }
    }

    private static byte[] ReadFile(string path)
    {
        using var handle = TranscriptTailer.Open(path);
        var size = RandomAccess.GetLength(handle);
        if (size < 0 || size > MaxBytes) throw new InvalidDataException("Trae store exceeds the snapshot size limit.");
        var bytes = new byte[(int)size];
        var offset = 0;
        while (offset < bytes.Length)
        {
            var count = RandomAccess.Read(handle, bytes.AsSpan(offset), offset);
            if (count == 0) throw new IOException("Trae store changed while reading.");
            offset += count;
        }
        return bytes;
    }

    private static void RejectJournal(string path)
    {
        var journal = path + "-journal";
        if (!File.Exists(journal)) return;
        using var handle = TranscriptTailer.Open(journal);
        Span<byte> header = stackalloc byte[8];
        var length = RandomAccess.Read(handle, header, 0);
        for (var index = 0; index < length; index++)
            if (header[index] != 0) throw new IOException("Trae has an active rollback journal.");
    }
}
