using System.Buffers.Binary;
using AgentSpeed.Core.Traffic.Helper;
using AgentSpeed.Core.Traffic.Windows;

namespace AgentSpeed.Core.Tests;

/// <summary>WINDOWS-SERVICE 4: the shared wire format and the client half of the exchange.</summary>
public sealed class HelperProtocolTests
{
    private const ulong Epoch = 0x01DC_1234_5678_9ABC;

    [Fact]
    public void RequestRoundTrips()
    {
        var bytes = new byte[HelperProtocol.RequestSize];
        new HelperRequest(1, HelperRequestType.Snapshot, 0xDEADBEEF).Write(bytes);

        Assert.Equal(new byte[] { 0x41, 0x53, 0x54, 0x48 }, bytes[..4]);
        Assert.Equal(HelperRequestCheck.Valid, HelperRequest.TryParse(bytes, out var request));
        Assert.Equal(new HelperRequest(1, HelperRequestType.Snapshot, 0xDEADBEEF), request);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(15)]
    [InlineData(17)]
    [InlineData(32)]
    public void RequestOfWrongSizeIsBad(int size)
    {
        var bytes = new byte[size];
        if (size >= HelperProtocol.RequestSize)
        {
            new HelperRequest(1, HelperRequestType.Hello, 1).Write(bytes);
        }

        Assert.Equal(HelperRequestCheck.BadRequest, HelperRequest.TryParse(bytes, out _));
    }

    [Theory]
    [InlineData(0, 0x00)]
    [InlineData(3, 0x00)]
    [InlineData(12, 0x01)]
    [InlineData(15, 0x80)]
    public void RequestWithWrongMagicOrReservedIsBad(int offset, byte value)
    {
        var bytes = Request(1, 1, 7);
        bytes[offset] = value;

        Assert.Equal(HelperRequestCheck.BadRequest, HelperRequest.TryParse(bytes, out _));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(0x8001)]
    [InlineData(0xFFFF)]
    public void RequestWithUnknownTypeIsBad(int type)
    {
        Assert.Equal(HelperRequestCheck.BadRequest, HelperRequest.TryParse(Request(1, (ushort)type, 7), out var request));
        Assert.Equal(7u, request.Sequence);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(0xFFFF)]
    public void RequestWithOtherVersionIsUnsupported(int version)
    {
        Assert.Equal(HelperRequestCheck.UnsupportedVersion, HelperRequest.TryParse(Request((ushort)version, 2, 9), out var request));
        Assert.Equal(9u, request.Sequence);
        Assert.Equal(HelperRequestType.Snapshot, request.Type);
    }

    [Fact]
    public void HeaderRoundTrips()
    {
        var header = new HelperResponseHeader(1, 2, HelperStatusCode.Ok, 42, Epoch, (uint)HelperSnapshot.PayloadLength(3));
        var bytes = new byte[HelperProtocol.HeaderSize];
        header.Write(bytes);

        Assert.True(HelperResponseHeader.TryParse(bytes, HelperRequestType.Snapshot, 42, out var parsed));
        Assert.Equal(header, parsed);
        Assert.False(parsed.IsError);
        Assert.Equal(HelperRequestType.Snapshot, parsed.RequestType);
    }

    [Fact]
    public void HeaderOfWrongSizeIsRejected()
    {
        var bytes = Header(1, HelperStatusCode.Ok, 5, HelperProtocol.HelloSize);
        Assert.False(HelperResponseHeader.TryParse(bytes.AsSpan(0, 31), HelperRequestType.Hello, 5, out _));
        Assert.False(HelperResponseHeader.TryParse([.. bytes, 0], HelperRequestType.Hello, 5, out _));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(28)]
    [InlineData(31)]
    public void HeaderWithWrongMagicOrReservedIsRejected(int offset)
    {
        var bytes = Header(1, HelperStatusCode.Ok, 5, HelperProtocol.HelloSize);
        bytes[offset] ^= 0x5A;

        Assert.False(HelperResponseHeader.TryParse(bytes, HelperRequestType.Hello, 5, out _));
    }

    [Fact]
    public void HeaderMustEchoSequenceAndType()
    {
        var bytes = Header(1, HelperStatusCode.Ok, 5, HelperProtocol.HelloSize);

        Assert.False(HelperResponseHeader.TryParse(bytes, HelperRequestType.Hello, 6, out _));
        Assert.False(HelperResponseHeader.TryParse(bytes, HelperRequestType.Snapshot, 5, out _));
    }

    [Theory]
    [InlineData(1, HelperStatusCode.BadRequest)]
    [InlineData(1 | HelperProtocol.ErrorFlag, HelperStatusCode.Ok)]
    public void ErrorFlagMustAgreeWithStatus(int type, HelperStatusCode status)
    {
        Assert.False(HelperResponseHeader.TryParse(Header((ushort)type, status, 5, 0), HelperRequestType.Hello, 5, out _));
    }

    [Theory]
    [InlineData(5u)]
    [InlineData(0xFFFFFFFFu)]
    public void UnknownStatusIsRejected(uint status)
    {
        Assert.False(HelperResponseHeader.TryParse(
            Header(1 | HelperProtocol.ErrorFlag, (HelperStatusCode)status, 5, 0), HelperRequestType.Hello, 5, out _));
    }

    [Theory]
    [InlineData(HelperRequestType.Hello, 0, true)]
    [InlineData(HelperRequestType.Hello, 40, true)]
    [InlineData(HelperRequestType.Hello, 39, false)]
    [InlineData(HelperRequestType.Hello, 41, false)]
    [InlineData(HelperRequestType.Hello, 16, false)]
    [InlineData(HelperRequestType.Snapshot, 0, true)]
    [InlineData(HelperRequestType.Snapshot, 16, true)]
    [InlineData(HelperRequestType.Snapshot, 48, true)]
    [InlineData(HelperRequestType.Snapshot, 15, false)]
    [InlineData(HelperRequestType.Snapshot, 47, false)]
    [InlineData(HelperRequestType.Snapshot, 40, false)]
    [InlineData(HelperRequestType.Snapshot, HelperProtocol.MaxSnapshotPayload, true)]
    [InlineData(HelperRequestType.Snapshot, HelperProtocol.MaxSnapshotPayload + 32, false)]
    [InlineData(HelperRequestType.Snapshot, int.MaxValue, false)]
    public void OkPayloadLengthFollowsTheType(HelperRequestType type, int length, bool valid)
    {
        Assert.Equal(valid, HelperResponseHeader.TryParse(Header((ushort)type, HelperStatusCode.Ok, 5, (uint)length), type, 5, out _));
    }

    [Theory]
    [InlineData(HelperStatusCode.UnsupportedVersion, 40, true)]
    [InlineData(HelperStatusCode.UnsupportedVersion, 0, true)]
    [InlineData(HelperStatusCode.UnsupportedVersion, 48, false)]
    [InlineData(HelperStatusCode.BadRequest, 0, true)]
    [InlineData(HelperStatusCode.BadRequest, 40, false)]
    [InlineData(HelperStatusCode.RateLimited, 0, true)]
    [InlineData(HelperStatusCode.RateLimited, 16, false)]
    [InlineData(HelperStatusCode.Overloaded, 0, true)]
    [InlineData(HelperStatusCode.Overloaded, 48, false)]
    public void ErrorPayloadLengthFollowsTheStatus(HelperStatusCode status, int length, bool valid)
    {
        var type = (ushort)HelperRequestType.Snapshot | HelperProtocol.ErrorFlag;
        Assert.Equal(valid, HelperResponseHeader.TryParse(Header((ushort)type, status, 5, (uint)length), HelperRequestType.Snapshot, 5, out _));
    }

    [Fact]
    public void HelloRoundTrips()
    {
        var hello = new HelperHello(1, 3, new HelperVersion(1, 2, 3).Packed, HelperHello.SessionHealthy, 7, 8, 9);
        var bytes = new byte[HelperProtocol.HelloSize];
        hello.Write(bytes);

        Assert.True(HelperHello.TryParse(bytes, out var parsed));
        Assert.Equal(hello, parsed);
        Assert.True(parsed.IsSessionHealthy);
        Assert.Equal(new HelperVersion(1, 2, 3), parsed.Version);
        Assert.True(parsed.Supports(2));
        Assert.False(parsed.Supports(4));
    }

    [Fact]
    public void MalformedHelloIsRejected()
    {
        var bytes = new byte[HelperProtocol.HelloSize];
        new HelperHello(1, 1, 0x010000, 1, 0, 0, 0).Write(bytes);

        Assert.False(HelperHello.TryParse(bytes.AsSpan(0, 39), out _));
        Assert.False(HelperHello.TryParse([.. bytes, 0], out _));

        var reserved = (byte[])bytes.Clone();
        reserved[12] = 1;
        Assert.False(HelperHello.TryParse(reserved, out _));

        var inverted = new byte[HelperProtocol.HelloSize];
        new HelperHello(3, 2, 0x010000, 1, 0, 0, 0).Write(inverted);
        Assert.False(HelperHello.TryParse(inverted, out _));
    }

    [Fact]
    public void SnapshotRoundTrips()
    {
        var entries = new[]
        {
            new HelperEntry(1234, 0, 133_000_000_000_000_000, 10, 20),
            new HelperEntry(5678, HelperEntry.ExitedFlag, 133_000_000_000_000_001, long.MaxValue, 0),
        };
        var payload = Snapshot(99, entries);
        var parsed = new List<HelperEntry>();

        Assert.True(HelperSnapshot.TryParse(payload, parsed, out var sampleTime));
        Assert.Equal(99ul, sampleTime);
        Assert.Equal(entries, parsed);
        Assert.True(parsed[1].Exited);
    }

    [Fact]
    public void EmptySnapshotParses()
    {
        var parsed = new List<HelperEntry> { default };
        Assert.True(HelperSnapshot.TryParse(Snapshot(1, []), parsed, out _));
        Assert.Empty(parsed);
    }

    [Fact]
    public void SnapshotWithDisagreeingCountIsRejected()
    {
        var payload = Snapshot(1, [new HelperEntry(100, 0, 1, 1, 1), new HelperEntry(101, 0, 1, 1, 1)]);
        var parsed = new List<HelperEntry>();

        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(8), 3);
        Assert.False(HelperSnapshot.TryParse(payload, parsed, out _));
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(8), 1);
        Assert.False(HelperSnapshot.TryParse(payload, parsed, out _));
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(8), 2);
        Assert.False(HelperSnapshot.TryParse(payload.AsSpan(0, payload.Length - 1), parsed, out _));
        Assert.False(HelperSnapshot.TryParse(payload.AsSpan(0, 15), parsed, out _));
    }

    [Fact]
    public void SnapshotOverTheMaximumIsRejected()
    {
        var parsed = new List<HelperEntry>();
        var header = new byte[HelperProtocol.SnapshotHeaderSize];
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8), HelperProtocol.MaxEntries + 1);
        Assert.False(HelperSnapshot.TryParse(header, parsed, out _));

        var tooLong = new byte[HelperProtocol.MaxSnapshotPayload + HelperProtocol.EntrySize];
        BinaryPrimitives.WriteUInt32LittleEndian(tooLong.AsSpan(8), HelperProtocol.MaxEntries + 1);
        Assert.False(HelperSnapshot.TryParse(tooLong, parsed, out _));
        Assert.Throws<ArgumentOutOfRangeException>(() => HelperSnapshot.WriteHeader(new byte[16], 0, HelperProtocol.MaxEntries + 1));
    }

    [Fact]
    public void SnapshotWithReservedSetIsRejected()
    {
        var payload = Snapshot(1, [new HelperEntry(100, 0, 1, 1, 1)]);
        payload[12] = 1;
        Assert.False(HelperSnapshot.TryParse(payload, new List<HelperEntry>(), out _));
    }

    [Fact]
    public void ImpossibleEntriesAreDropped()
    {
        var payload = Snapshot(1,
        [
            new HelperEntry(4, 0, 1, 1, 1),
            new HelperEntry(0, 0, 1, 1, 1),
            new HelperEntry(200, 0, 0, 1, 1),
            new HelperEntry(201, 0, 1, (ulong)long.MaxValue + 1, 1),
            new HelperEntry(202, 0, 1, 1, ulong.MaxValue),
            new HelperEntry(5, 0, 1, 1, 1),
        ]);
        var parsed = new List<HelperEntry>();

        Assert.True(HelperSnapshot.TryParse(payload, parsed, out _));
        Assert.Equal(new[] { 5u }, parsed.Select(entry => entry.Pid));
    }

    [Fact]
    public void ExchangeSendsHelloAndParsesTheAnswer()
    {
        var transport = new FakeTransport();
        var exchange = new HelperExchange();
        transport.Respond = request =>
        {
            Assert.Equal(HelperRequestCheck.Valid, HelperRequest.TryParse(request, out var parsed));
            Assert.Equal(HelperRequestType.Hello, parsed.Type);
            return Response(parsed, HelperStatusCode.Ok, HelloPayload(1, 1));
        };

        Assert.Equal(HelperCallResult.Ok, exchange.Hello(transport, 0, out var hello, out var epoch));
        Assert.Equal(Epoch, epoch);
        Assert.Equal(HelperProtocol.BundledVersion, hello.Version);
        Assert.Equal(1u, exchange.Sequence);
    }

    [Fact]
    public void ExchangeIncrementsTheSequence()
    {
        var transport = new FakeTransport();
        var exchange = new HelperExchange();
        var seen = new List<uint>();
        transport.Respond = request =>
        {
            HelperRequest.TryParse(request, out var parsed);
            seen.Add(parsed.Sequence);
            return Response(parsed, HelperStatusCode.Ok, Snapshot(1, []));
        };
        var entries = new List<HelperEntry>();

        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(HelperCallResult.Ok, exchange.Snapshot(transport, i * 1000, entries, out _));
        }

        Assert.Equal(new[] { 1u, 2u, 3u }, seen);
    }

    [Fact]
    public void ExchangeReadsTheSnapshotPayload()
    {
        var transport = new FakeTransport();
        var exchange = new HelperExchange();
        transport.Respond = request =>
        {
            HelperRequest.TryParse(request, out var parsed);
            return Response(parsed, HelperStatusCode.Ok, Snapshot(5, [new HelperEntry(321, 0, 77, 1000, 2000)]));
        };
        var entries = new List<HelperEntry>();

        Assert.Equal(HelperCallResult.Ok, exchange.Snapshot(transport, 0, entries, out var epoch));
        Assert.Equal(Epoch, epoch);
        Assert.Equal(new[] { new HelperEntry(321, 0, 77, 1000, 2000) }, entries);
    }

    [Fact]
    public void ExchangeValidatesTheHeaderBeforeReadingThePayload()
    {
        var transport = new FakeTransport();
        var exchange = new HelperExchange();
        transport.Respond = request =>
        {
            HelperRequest.TryParse(request, out var parsed);
            var bytes = Response(parsed, HelperStatusCode.Ok, Snapshot(1, []));
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(24), HelperProtocol.MaxSnapshotPayload + 32);
            return bytes;
        };

        Assert.Equal(HelperCallResult.Failed, exchange.Snapshot(transport, 0, new List<HelperEntry>(), out _));
        Assert.Equal(new[] { HelperProtocol.HeaderSize }, transport.Reads);
    }

    [Fact]
    public void ExchangeRejectsAWrongSequenceEcho()
    {
        var transport = new FakeTransport();
        var exchange = new HelperExchange();
        transport.Respond = request =>
        {
            HelperRequest.TryParse(request, out var parsed);
            return Response(parsed with { Sequence = parsed.Sequence + 1 }, HelperStatusCode.Ok, HelloPayload(1, 1));
        };

        Assert.Equal(HelperCallResult.Failed, exchange.Hello(transport, 0, out _, out _));
    }

    [Fact]
    public void ExchangeReportsAnUnsupportedVersionWithTheHello()
    {
        var transport = new FakeTransport();
        var exchange = new HelperExchange();
        transport.Respond = request =>
        {
            HelperRequest.TryParse(request, out var parsed);
            return Response(parsed, HelperStatusCode.UnsupportedVersion, HelloPayload(2, 3));
        };

        Assert.Equal(HelperCallResult.UnsupportedVersion, exchange.Hello(transport, 0, out var hello, out _));
        Assert.Equal((ushort)2, hello.MinProtocol);
        Assert.Equal(HelperCallResult.UnsupportedVersion, exchange.Snapshot(transport, 10, new List<HelperEntry>(), out _));
    }

    [Theory]
    [InlineData(HelperStatusCode.BadRequest, false)]
    [InlineData(HelperStatusCode.Overloaded, false)]
    [InlineData(HelperStatusCode.RateLimited, true)]
    public void ExchangeMapsErrorStatuses(HelperStatusCode status, bool rateLimited)
    {
        var expected = rateLimited ? HelperCallResult.RateLimited : HelperCallResult.Failed;
        var transport = new FakeTransport();
        var exchange = new HelperExchange();
        transport.Respond = request =>
        {
            HelperRequest.TryParse(request, out var parsed);
            return Response(parsed, status, []);
        };

        Assert.Equal(expected, exchange.Snapshot(transport, 0, new List<HelperEntry>(), out _));
    }

    [Fact]
    public void ExchangeFailsOnTransportErrors()
    {
        var exchange = new HelperExchange();
        var writeFails = new FakeTransport { WriteError = 109 };
        Assert.Equal(HelperCallResult.Failed, exchange.Hello(writeFails, 0, out _, out _));
        Assert.Equal(109, exchange.LastError);

        var silent = new FakeTransport { Respond = _ => [] };
        Assert.Equal(HelperCallResult.Failed, exchange.Hello(silent, 1000, out _, out _));
        Assert.Equal(1460, exchange.LastError);

        var truncated = new FakeTransport
        {
            Respond = request =>
            {
                HelperRequest.TryParse(request, out var parsed);
                return Response(parsed, HelperStatusCode.Ok, HelloPayload(1, 1))[..50];
            },
        };
        Assert.Equal(HelperCallResult.Failed, exchange.Hello(truncated, 2000, out _, out _));
    }

    [Fact]
    public void ExchangeRejectsAMalformedSnapshotPayload()
    {
        var transport = new FakeTransport();
        var exchange = new HelperExchange();
        transport.Respond = request =>
        {
            HelperRequest.TryParse(request, out var parsed);
            var payload = Snapshot(1, [new HelperEntry(100, 0, 1, 1, 1)]);
            payload[12] = 1;
            return Response(parsed, HelperStatusCode.Ok, payload);
        };

        Assert.Equal(HelperCallResult.Failed, exchange.Snapshot(transport, 0, new List<HelperEntry>(), out _));
    }

    [Fact]
    public void ExchangeNeverSendsAFifthRequestInOneSecond()
    {
        var transport = new FakeTransport();
        var exchange = new HelperExchange();
        transport.Respond = request =>
        {
            HelperRequest.TryParse(request, out var parsed);
            return Response(parsed, HelperStatusCode.Ok, HelloPayload(1, 1));
        };

        for (var i = 0; i < 4; i++)
        {
            Assert.Equal(HelperCallResult.Ok, exchange.Hello(transport, 100 + i, out _, out _));
        }

        Assert.Equal(HelperCallResult.RateLimited, exchange.Hello(transport, 999, out _, out _));
        Assert.Equal(4, transport.Writes);
        Assert.Equal(HelperCallResult.Ok, exchange.Hello(transport, 1100, out _, out _));
    }

    private static byte[] Request(ushort version, ushort type, uint sequence)
    {
        var bytes = new byte[HelperProtocol.RequestSize];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, HelperProtocol.Magic);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(4), version);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(6), type);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), sequence);
        return bytes;
    }

    private static byte[] Header(ushort type, HelperStatusCode status, uint sequence, uint length)
    {
        var bytes = new byte[HelperProtocol.HeaderSize];
        new HelperResponseHeader(1, type, status, sequence, Epoch, length).Write(bytes);
        return bytes;
    }

    internal static byte[] HelloPayload(ushort min, ushort max)
    {
        var bytes = new byte[HelperProtocol.HelloSize];
        new HelperHello(min, max, HelperProtocol.BundledVersion.Packed, HelperHello.SessionHealthy, 0, 0, 0).Write(bytes);
        return bytes;
    }

    internal static byte[] Snapshot(ulong sampleTime, IReadOnlyList<HelperEntry> entries)
    {
        var bytes = new byte[HelperSnapshot.PayloadLength(entries.Count)];
        HelperSnapshot.WriteHeader(bytes, sampleTime, entries.Count);
        for (var i = 0; i < entries.Count; i++)
        {
            entries[i].Write(bytes.AsSpan(HelperProtocol.SnapshotHeaderSize + i * HelperProtocol.EntrySize));
        }

        return bytes;
    }

    private static byte[] Response(HelperRequest request, HelperStatusCode status, byte[] payload)
    {
        var type = (ushort)request.Type;
        if (status != HelperStatusCode.Ok)
        {
            type |= HelperProtocol.ErrorFlag;
        }

        var bytes = new byte[HelperProtocol.HeaderSize + payload.Length];
        new HelperResponseHeader(HelperProtocol.Version, type, status, request.Sequence, Epoch, (uint)payload.Length).Write(bytes);
        payload.CopyTo(bytes, HelperProtocol.HeaderSize);
        return bytes;
    }

    /// <summary>A lockstep peer: each written request queues its response; reads drain it.</summary>
    private sealed class FakeTransport : IHelperTransport
    {
        private readonly Queue<byte> _pending = new();

        public Func<byte[], byte[]> Respond { get; set; } = _ => [];

        public int WriteError { get; init; }

        public int Writes { get; private set; }

        public List<int> Reads { get; } = [];

        public int Write(ReadOnlySpan<byte> data)
        {
            if (WriteError != 0)
            {
                return WriteError;
            }

            Writes++;
            Assert.Equal(HelperProtocol.RequestSize, data.Length);
            Assert.Empty(_pending);
            foreach (var value in Respond(data.ToArray()))
            {
                _pending.Enqueue(value);
            }

            return 0;
        }

        public int ReadExactly(Span<byte> data)
        {
            Reads.Add(data.Length);
            if (_pending.Count < data.Length)
            {
                _pending.Clear();
                return 1460;
            }

            for (var i = 0; i < data.Length; i++)
            {
                data[i] = _pending.Dequeue();
            }

            return 0;
        }
    }
}
