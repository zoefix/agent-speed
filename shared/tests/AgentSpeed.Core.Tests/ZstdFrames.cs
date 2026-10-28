using System.Buffers.Binary;
using System.Text;
using ZstdSharp;
using ZstdSharp.Unsafe;

namespace AgentSpeed.Core.Tests;

/// <summary>Writes zstd frame streams the way DeepSeek Harness does: one frame per append, checksum, no content size.</summary>
internal static class ZstdFrames
{
    public static byte[] Frame(ReadOnlySpan<byte> content, bool contentSize = false)
    {
        using var compressor = new Compressor(3);
        compressor.SetParameter(ZSTD_cParameter.ZSTD_c_checksumFlag, 1);
        compressor.SetParameter(ZSTD_cParameter.ZSTD_c_contentSizeFlag, contentSize ? 1 : 0);
        return compressor.Wrap(content).ToArray();
    }

    public static byte[] Text(string text) => Frame(Encoding.UTF8.GetBytes(text));

    public static byte[] Lines(params string[] lines) => Text(string.Concat(lines.Select(l => l + "\n")));

    public static byte[] Skippable(int payloadBytes, byte variant = 0)
    {
        var frame = new byte[8 + payloadBytes];
        BinaryPrimitives.WriteUInt32LittleEndian(frame, 0x184D2A50u | (variant & 0xFu));
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(4), (uint)payloadBytes);
        frame.AsSpan(8).Fill(0x5A);
        return frame;
    }

    public static string Write(string path, params byte[][] frames)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, [.. frames.SelectMany(f => f)]);
        return Path.GetFullPath(path);
    }

    public static void Append(string path, params byte[][] frames)
    {
        using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        foreach (var frame in frames)
        {
            stream.Write(frame);
        }
    }
}
