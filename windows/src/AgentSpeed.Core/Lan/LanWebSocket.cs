using System.Buffers.Binary;

namespace AgentSpeed.Core.Lan;

public enum LanClientAction
{
    /// <summary>More bytes are needed.</summary>
    Incomplete,
    Pong,
    EchoClose,
    Close,
    Ignore,
}

/// <summary>What the server does with one client frame (LAN 4.5); <see cref="Consumed"/> bytes of the input belong to it.</summary>
/// <param name="Code">The close code to send (<see cref="LanClientAction.Close"/>) or echo (null when the client sent none).</param>
public readonly record struct LanClientFrame(LanClientAction Action, int Consumed = 0, byte[]? Payload = null, int? Code = null);

/// <summary>
/// The server's side of RFC 6455 on the shared port (LAN 4.5): unfragmented, unmasked text frames out; client frames checked
/// (masked, no reserved bits or opcodes, control frames unfragmented and at most 125 bytes, data at most 4096 bytes).
/// </summary>
public static class LanWebSocket
{
    public const int Text = 0x1;
    public const int Binary = 0x2;
    public const int CloseOpcode = 0x8;
    public const int Ping = 0x9;
    public const int PongOpcode = 0xA;
    public const int ClientLimit = 4096;
    public const int GoingAway = 1001;
    public const int ProtocolError = 1002;
    public const int PolicyViolation = 1008;
    public const int TooBig = 1009;

    /// <summary>A server frame: FIN, unmasked.</summary>
    public static byte[] Frame(int opcode, ReadOnlySpan<byte> payload)
    {
        var size = payload.Length;
        var head = size < 126 ? 2 : size <= 0xFFFF ? 4 : 10;
        var frame = new byte[head + size];
        frame[0] = (byte)(0x80 | opcode);
        if (size < 126)
        {
            frame[1] = (byte)size;
        }
        else if (size <= 0xFFFF)
        {
            frame[1] = 126;
            BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(2), (ushort)size);
        }
        else
        {
            frame[1] = 127;
            BinaryPrimitives.WriteUInt64BigEndian(frame.AsSpan(2), (ulong)size);
        }

        payload.CopyTo(frame.AsSpan(head));
        return frame;
    }

    public static byte[] Close(int? code)
    {
        if (code is not { } value)
        {
            return Frame(CloseOpcode, []);
        }

        Span<byte> payload = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(payload, (ushort)value);
        return Frame(CloseOpcode, payload);
    }

    /// <summary>The handling of the client frame at the start of <paramref name="data"/>.</summary>
    public static LanClientFrame Client(ReadOnlySpan<byte> data)
    {
        if (data.Length < 2)
        {
            return new LanClientFrame(LanClientAction.Incomplete);
        }

        var fin = (data[0] & 0x80) != 0;
        var reserved = data[0] & 0x70;
        var opcode = data[0] & 0x0F;
        var masked = (data[1] & 0x80) != 0;
        ulong size = (ulong)(data[1] & 0x7F);
        var position = 2;
        if (reserved != 0 || opcode is not (0x0 or Text or Binary or CloseOpcode or Ping or PongOpcode) || !masked)
        {
            return new LanClientFrame(LanClientAction.Close, Code: ProtocolError);
        }

        if (size == 126)
        {
            if (data.Length < 4)
            {
                return new LanClientFrame(LanClientAction.Incomplete);
            }

            size = BinaryPrimitives.ReadUInt16BigEndian(data[2..]);
            position = 4;
        }
        else if (size == 127)
        {
            if (data.Length < 10)
            {
                return new LanClientFrame(LanClientAction.Incomplete);
            }

            size = BinaryPrimitives.ReadUInt64BigEndian(data[2..]);
            position = 10;
        }

        var control = opcode >= CloseOpcode;
        if (control && (!fin || size > 125))
        {
            return new LanClientFrame(LanClientAction.Close, Code: ProtocolError);
        }

        if (!control && size > ClientLimit)
        {
            return new LanClientFrame(LanClientAction.Close, Code: TooBig);
        }

        var total = position + 4 + (int)size;
        if (data.Length < total)
        {
            return new LanClientFrame(LanClientAction.Incomplete);
        }

        var mask = data.Slice(position, 4);
        var payload = data.Slice(position + 4, (int)size).ToArray();
        for (var i = 0; i < payload.Length; i++)
        {
            payload[i] ^= mask[i % 4];
        }

        switch (opcode)
        {
            case Ping:
                return new LanClientFrame(LanClientAction.Pong, total, payload);
            case CloseOpcode when payload.Length == 1:
                return new LanClientFrame(LanClientAction.Close, total, Code: ProtocolError);
            case CloseOpcode:
                return new LanClientFrame(LanClientAction.EchoClose, total, Code: payload.Length >= 2 ? BinaryPrimitives.ReadUInt16BigEndian(payload) : null);
            default:
                return new LanClientFrame(LanClientAction.Ignore, total);
        }
    }
}
