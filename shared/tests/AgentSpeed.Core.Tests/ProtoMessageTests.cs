using System.Buffers.Binary;
using System.Text;
using AgentSpeed.Core.Antigravity;
using AgentSpeed.Core.Parsing;

namespace AgentSpeed.Core.Tests;

public sealed class ProtoMessageTests
{
    private const ulong TokenCap = 10_000_000;

    public static TheoryData<byte[], ulong> Varints() => new()
    {
        { [0x00], 0 },
        { [0x01], 1 },
        { [0x7F], 127 },
        { [0x80, 0x01], 128 },
        { [0xAC, 0x02], 300 },
        { [0xFF, 0xFF, 0x03], 65_535 },
        { [0xFF, 0xFF, 0xFF, 0xFF, 0x0F], uint.MaxValue },
        { [0x81, 0x80, 0x00], 1 },
        { [0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x7F], (ulong)long.MaxValue },
        { [0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x01], 1UL << 63 },
        { [0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x01], ulong.MaxValue },
        { [0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x7F], ulong.MaxValue },
        { [0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x02], 0 },
        { [0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x7E], 0 },
        { [0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x03], 1UL << 63 },
    };

    public static TheoryData<byte[]> TruncatedVarints() =>
    [
        [],
        [0x80],
        [0xFF, 0xFF],
        [0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80],
    ];

    public static TheoryData<byte[]> MalformedMessages() =>
    [
        [0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x01],
        [0x08, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x01],
        [0x88],
        [0x08],
        [0x08, 0x80],
        [0x00, 0x00],
        [0x02, 0x00],
        [0x05, 0x00, 0x00, 0x00, 0x00],
        [0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00],
        [0x80, 0x00, 0x00],
        [0x08, 0x01, 0x00, 0x00],
        [0x0D, 0x01, 0x02, 0x03],
        [0x09, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07],
        [0x0A],
        [0x0A, 0x80],
        [0x0A, 0x03, 0x01, 0x02],
        [0x0A, 0xFF, 0xFF, 0xFF, 0xFF, 0x07],
        [0x0A, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x01],
        [0x08, 0x01, 0x0A, 0x02, 0x41],
    ];

    public static TheoryData<byte[], string> Utf8Texts() => new()
    {
        { [0x66, 0xFF, 0x6F], "f\uFFFDo" },
        { [0xE6, 0x98], "\uFFFD" },
        { [0xC0, 0xAF], "\uFFFD\uFFFD" },
        { [0xED, 0xA0, 0x80], "\uFFFD\uFFFD\uFFFD" },
        { [0xE6, 0x98, 0x9F], "\u661F" },
        { [0x61, 0xF0, 0x9F, 0x98], "a\uFFFD" },
        { [0xF4, 0x90, 0x80, 0x80], "\uFFFD\uFFFD\uFFFD\uFFFD" },
        { [0x80, 0x80], "\uFFFD\uFFFD" },
    };

    [Theory]
    [MemberData(nameof(Varints))]
    public void VarintsDecodeLeastSignificantGroupFirstAndDropBitsAbove64(byte[] encoded, ulong expected)
    {
        var position = 0;
        Assert.Equal(expected, ProtoMessage.ReadVarint(encoded, ref position));
        Assert.Equal(encoded.Length, position);

        var message = ProtoMessage.Parse(Pb.Concat([0x08], encoded, Pb.UInt(2, 7)));
        Assert.Equal(expected, message.UInt(1));
        Assert.Equal(7UL, message.UInt(2));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(10)]
    public void VarintsOfOneToTenBytesStopAtTheFirstByteWithoutContinuation(int length)
    {
        var expected = 1UL << (7 * (length - 1));
        byte[] data = [0x2A, .. Enumerable.Repeat((byte)0x80, length - 1), 0x01, 0x2A];

        var position = 1;
        Assert.Equal(expected, ProtoMessage.ReadVarint(data, ref position));
        Assert.Equal(length + 1, position);
        Assert.Equal(length, Pb.Varint(expected).Length);
    }

    [Fact]
    public void AnElevenByteVarintIsMalformed()
    {
        byte[] data = [0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x01];

        Assert.Throws<ProtoFormatException>(() => ReadVarintAt(data, 0));
        Assert.Throws<ProtoFormatException>(() => ParseOnly(data));
        Assert.Throws<ProtoFormatException>(() => ParseOnly([0x08, .. data]));
    }

    [Theory]
    [MemberData(nameof(TruncatedVarints))]
    public void ATruncatedVarintIsMalformed(byte[] data)
    {
        Assert.Throws<ProtoFormatException>(() => ReadVarintAt(data, 0));
        Assert.Throws<ProtoFormatException>(() => ParseOnly([0x08, .. data]));
    }

    [Theory]
    [MemberData(nameof(MalformedMessages))]
    public void BrokenWireFormatMakesTheWholeMessageMalformed(byte[] data)
    {
        Assert.Throws<ProtoFormatException>(() => ParseOnly(data));
    }

    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(6)]
    [InlineData(7)]
    public void GroupsAndReservedWireTypesAreMalformed(int wire)
    {
        byte[] payload = [0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00];

        Assert.Throws<ProtoFormatException>(() => ParseOnly([.. Pb.Key(1, wire), .. payload]));
        Assert.Throws<ProtoFormatException>(() => ParseOnly([.. Pb.UInt(1, 5), .. Pb.Key(1000, wire), .. payload]));
    }

    [Fact]
    public void FieldsThatExactlyFillTheBufferAreAccepted()
    {
        var message = ProtoMessage.Parse(Pb.Concat(
            Pb.Fixed32(1, 0x04030201), Pb.Fixed64(2, 0x0807060504030201), Pb.Bytes(3, 0x41, 0x42), Pb.Bytes(4)));

        Assert.Equal(4, message.Count);
        Assert.Equal("AB", message.String(3));
        Assert.Equal(string.Empty, message.String(4));
        Assert.True(message.Has(1, ProtoMessage.WireFixed32));
        Assert.True(message.Has(2, ProtoMessage.WireFixed64));
        Assert.True(message.Has(4, ProtoMessage.WireLength));
    }

    [Fact]
    public void UnknownFieldsOfEveryValidWireTypeAreSkipped()
    {
        var data = Pb.Concat(
            Pb.UInt(1, 7),
            Pb.Fixed64(2, 0x0102030405060708),
            Pb.Text(3, "skip me"),
            Pb.Fixed32(4, 0xDEADBEEF),
            Pb.UInt(5, 300),
            Pb.Bytes(6, 0x0B),
            Pb.Key(536_870_911, ProtoMessage.WireVarint), Pb.Varint(11),
            Pb.Key((1UL << 32) + 1, ProtoMessage.WireVarint), Pb.Varint(99),
            Pb.Key((1UL << 61) - 1, ProtoMessage.WireLength), Pb.Varint(1), [0x00],
            Pb.UInt(9, 9));

        var message = ProtoMessage.Parse(data);

        Assert.Equal(10, message.Count);
        Assert.Equal(7UL, message.UInt(1));
        Assert.Equal(9UL, message.UInt(9));
        Assert.Equal(11UL, message.UInt(536_870_911));
    }

    [Fact]
    public void AKnownFieldWithAnotherWireTypeIsIgnored()
    {
        var message = ProtoMessage.Parse(Pb.Concat(
            Pb.UInt(1, 5), Pb.Fixed64(1, 77), Pb.Fixed32(1, 88), Pb.Text(1, "x"),
            Pb.UInt(2, 42),
            Pb.Fixed32(3, 1), Pb.Fixed64(3, 2)));

        Assert.Equal(5UL, message.UInt(1));
        Assert.Equal("x", message.String(1));
        Assert.Equal(string.Empty, message.String(2));
        Assert.Empty(message.Strings(2));
        Assert.False(message.TryMessage(2, out _));
        Assert.Null(message.Timestamp(2));
        Assert.False(message.Has(2, ProtoMessage.WireLength));
        Assert.True(message.Has(2, ProtoMessage.WireVarint));
        Assert.Equal(0UL, message.UInt(3));
        Assert.Empty(message.RepeatedUInt(3));
    }

    [Fact]
    public void TheLastOccurrenceWinsAndFirstTakesTheFirst()
    {
        var data = Pb.Concat(
            Pb.UInt(1, 1),
            Pb.Text(2, "first"),
            Pb.Message(3, Pb.UInt(1, 10)),
            Pb.Timestamp(4, 100),
            Pb.UInt(1, 2),
            Pb.Text(2, "last"),
            Pb.Message(3, Pb.UInt(1, 20)),
            Pb.Message(3, Pb.UInt(2, 30)),
            Pb.Timestamp(4, 200),
            Pb.Text(5, "set"),
            Pb.Text(5, string.Empty));

        var message = ProtoMessage.Parse(data);

        Assert.Equal(2UL, message.UInt(1));
        Assert.Equal("last", message.String(2));
        Assert.Equal("first", message.String(2, first: true));
        Assert.Equal(["first", "last"], message.Strings(2));
        Assert.Equal(200_000, message.Timestamp(4));
        Assert.Equal(string.Empty, message.String(5));
        Assert.Equal("set", message.String(5, first: true));

        Assert.True(message.TryMessage(3, out var last));
        Assert.Equal(0UL, last.UInt(1));
        Assert.Equal(30UL, last.UInt(2));
        Assert.True(message.TryMessage(3, out var first, first: true));
        Assert.Equal(10UL, first.UInt(1));
        Assert.Equal(0UL, first.UInt(2));
    }

    [Fact]
    public void MissingValuesAreProto3Defaults()
    {
        var empty = ProtoMessage.Parse([]);

        Assert.Equal(0, empty.Count);
        Assert.Equal(0UL, empty.UInt(1));
        Assert.Equal(0L, empty.Int(1));
        Assert.Equal(string.Empty, empty.String(1));
        Assert.Equal(string.Empty, empty.String(1, first: true));
        Assert.Empty(empty.Strings(1));
        Assert.False(empty.TryMessage(1, out _));
        Assert.False(empty.TryMessage(1, out _, first: true));
        Assert.Null(empty.Timestamp(1));
        Assert.Empty(empty.RepeatedUInt(1));
        Assert.False(empty.Has(1, ProtoMessage.WireVarint));

        var withEmptyNested = ProtoMessage.Parse(Pb.Bytes(1));
        Assert.True(withEmptyNested.TryMessage(1, out var nested));
        Assert.Equal(0, nested.Count);
        Assert.Null(withEmptyNested.Timestamp(1));
    }

    [Theory]
    [InlineData(-1L)]
    [InlineData(-2L)]
    [InlineData(-2_147_483_648L)]
    [InlineData(long.MinValue)]
    [InlineData(long.MaxValue)]
    public void NegativeIntegersAreTenByteVarintsReadAsSignedLongs(long value)
    {
        var encoded = Pb.Int(1, value);
        var message = ProtoMessage.Parse(encoded);

        Assert.Equal(value, message.Int(1));
        Assert.Equal(unchecked((ulong)value), message.UInt(1));
        Assert.Equal(value < 0 ? 11 : 10, encoded.Length);
    }

    [Fact]
    public void AnInt32SignExtendedOnlyToThirtyTwoBitsStaysPositive()
    {
        var message = ProtoMessage.Parse([0x08, 0xFF, 0xFF, 0xFF, 0xFF, 0x0F]);

        Assert.Equal(4_294_967_295L, message.Int(1));
    }

    [Fact]
    public void RepeatedUnsignedScalarsArePackedUnpackedOrMixedInOrder()
    {
        Assert.Equal([1UL, 300UL, ulong.MaxValue], ProtoMessage.Parse(Pb.Packed(2, 1, 300, ulong.MaxValue)).RepeatedUInt(2));
        Assert.Equal([5UL, 7UL], ProtoMessage.Parse(Pb.Concat(Pb.UInt(2, 5), Pb.UInt(2, 7))).RepeatedUInt(2));

        var mixed = ProtoMessage.Parse(Pb.Concat(
            Pb.UInt(2, 1), Pb.UInt(3, 99), Pb.Packed(2, 2, 3), Pb.Fixed32(2, 4), Pb.Fixed64(2, 4), Pb.UInt(2, 5), Pb.Packed(2)));

        Assert.Equal([1UL, 2UL, 3UL, 5UL], mixed.RepeatedUInt(2));
        Assert.Equal(5UL, mixed.UInt(2));
    }

    [Theory]
    [InlineData(new byte[] { 0x80 })]
    [InlineData(new byte[] { 0x01, 0xFF })]
    [InlineData(new byte[] { 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x01 })]
    public void AMalformedPackedPayloadThrowsOnlyWhenRead(byte[] payload)
    {
        var data = Pb.Concat(Pb.Bytes(2, payload), Pb.UInt(3, 4));

        var message = ProtoMessage.Parse(data);
        Assert.Equal(4UL, message.UInt(3));
        Assert.Throws<ProtoFormatException>(() => ReadRepeated(data, 2));
    }

    [Fact]
    public void AMalformedNestedMessageThrowsOnlyWhenItIsRead()
    {
        var data = Pb.Concat(Pb.Bytes(1, 0x0B), Pb.UInt(2, 9), Pb.Bytes(3, 0x08), Pb.Bytes(4, 0x12, 0x03, 0x41), Pb.UInt(5, 1));

        var message = ProtoMessage.Parse(data);

        Assert.Equal(9UL, message.UInt(2));
        Assert.Equal("\v", message.String(1));
        Assert.True(message.Has(1, ProtoMessage.WireLength));
        Assert.Throws<ProtoFormatException>(() => ReadNested(data, 1));
        Assert.Throws<ProtoFormatException>(() => ReadTimestamp(data, 3));
        Assert.Throws<ProtoFormatException>(() => ReadNested(data, 4));
    }

    [Fact]
    public void OnlyTheOccurrenceThatIsReadMustBeWellFormed()
    {
        var data = Pb.Concat(Pb.Bytes(1, 0x0B), Pb.Message(1, Pb.UInt(1, 5)));

        var message = ProtoMessage.Parse(data);

        Assert.True(message.TryMessage(1, out var last));
        Assert.Equal(5UL, last.UInt(1));
        Assert.Throws<ProtoFormatException>(() => ReadNested(data, 1, first: true));
    }

    [Theory]
    [MemberData(nameof(Utf8Texts))]
    public void InvalidUtf8BecomesReplacementCharacters(byte[] payload, string expected)
    {
        var message = ProtoMessage.Parse(Pb.Concat(Pb.Bytes(1, payload), Pb.Bytes(1, payload)));

        Assert.Equal(expected, message.String(1));
        Assert.Equal(expected, message.String(1, first: true));
        Assert.Equal([expected, expected], message.Strings(1));
    }

    [Theory]
    [InlineData(1L, 0L, 1_000L)]
    [InlineData(1_760_000_000L, 123_456_789L, 1_760_000_000_123L)]
    [InlineData(1L, 999_999_999L, 1_999L)]
    [InlineData(1L, 999_999L, 1_000L)]
    [InlineData(1L, 1_000_000L, 1_001L)]
    [InlineData(1L, 1_000_000_000L, 1_000L)]
    [InlineData(1L, -1L, 1_000L)]
    [InlineData(1L, 4_294_967_296L, 1_000L)]
    [InlineData(9_223_372_036_854_774L, 999_999_999L, 9_223_372_036_854_774_999L)]
    [InlineData(9_223_372_036_854_775L, 807_000_000L, long.MaxValue)]
    [InlineData(9_223_372_036_854_775L, 808_000_000L, null)]
    [InlineData(9_223_372_036_854_776L, 0L, null)]
    [InlineData(0L, 500_000_000L, null)]
    [InlineData(-1L, 0L, null)]
    [InlineData(long.MinValue, 0L, null)]
    public void TimestampsAreValidOnlyWithPositiveSeconds(long seconds, long nanos, long? expected)
    {
        var message = ProtoMessage.Parse(Pb.Message(1, Pb.Int(1, seconds), Pb.Int(2, nanos)));

        Assert.Equal(expected, message.Timestamp(1));
    }

    [Fact]
    public void TimestampFieldsWithOtherWireTypesCountAsMissing()
    {
        Assert.Equal(5_000, ProtoMessage.Parse(Pb.Message(1, Pb.Int(1, 5))).Timestamp(1));
        Assert.Null(ProtoMessage.Parse(Pb.Message(1, Pb.Fixed64(1, 5))).Timestamp(1));
        Assert.Equal(5_000, ProtoMessage.Parse(Pb.Message(1, Pb.Int(1, 5), Pb.Fixed32(2, 900_000_000))).Timestamp(1));
        Assert.Null(ProtoMessage.Parse(Pb.Message(1)).Timestamp(1));
        Assert.Null(ProtoMessage.Parse(Pb.UInt(1, 5)).Timestamp(1));
    }

    [Fact]
    public void StepDecodesEveryTimestampAndItsUsage()
    {
        var blob = Pb.Concat(
            Pb.Timestamp(1, 1_760_000_000, 100_000_000),
            Pb.Timestamp(6, 1_760_000_001),
            Pb.Timestamp(7, 1_760_000_005, 250_000_000),
            Pb.Timestamp(8, 1_760_000_006),
            Pb.Timestamp(22, 1_760_000_004),
            Pb.Timestamp(32, 1_760_000_000, 200_000_000),
            Pb.Message(9,
                Pb.UInt(2, 1200), Pb.UInt(3, 300), Pb.UInt(4, 99_999_999), Pb.UInt(5, 800), Pb.Text(7, "msg-1"),
                Pb.UInt(9, 100), Pb.UInt(10, 200), Pb.Text(11, "resp-1")),
            Pb.Text(4, "ignored"),
            Pb.Bytes(20, 0x0B));

        var step = AntigravityDecoder.Step(blob);

        Assert.Equal(1_760_000_000_100, step.CreatedMs);
        Assert.Equal(1_760_000_001_000, step.ViewableMs);
        Assert.Equal(1_760_000_005_250, step.FinishedGeneratingMs);
        Assert.Equal(1_760_000_006_000, step.CompletedMs);
        Assert.Equal(1_760_000_004_000, step.LastChunkMs);
        Assert.Equal(1_760_000_000_200, step.StartedMs);
        Assert.Equal(new AntigravityUsage(1200, 300, 800, 100, 200, "resp-1"), step.Usage);
        Assert.Equal(
            [1_760_000_000_100L, 1_760_000_001_000L, 1_760_000_005_250L, 1_760_000_006_000L, 1_760_000_004_000L, 1_760_000_000_200L],
            step.Times());
        Assert.Equal(1_760_000_006_000, step.LatestTime);
    }

    [Fact]
    public void StepWithoutValidTimestampsOrUsage()
    {
        var step = AntigravityDecoder.Step(Pb.Concat(Pb.Timestamp(1, 0, 5), Pb.Message(7, Pb.Int(1, -3)), Pb.Message(8)));

        Assert.Null(step.CreatedMs);
        Assert.Null(step.FinishedGeneratingMs);
        Assert.Null(step.CompletedMs);
        Assert.Empty(step.Times());
        Assert.Null(step.LatestTime);
        Assert.Null(step.Usage);

        var empty = AntigravityDecoder.Step(Pb.Message(9));
        Assert.Equal(new AntigravityUsage(0, 0, 0, 0, 0, null), empty.Usage);
        Assert.False(empty.Usage!.HasTokens);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(9)]
    [InlineData(10)]
    public void AStepWithMoreThanTenMillionTokensInAnyFieldIsMalformed(int field)
    {
        var over = Pb.Message(9, Pb.UInt(field, TokenCap + 1));
        var huge = Pb.Message(9, Pb.UInt(field, ulong.MaxValue));

        Assert.Throws<ProtoFormatException>(() => AntigravityDecoder.Step(over));
        Assert.Throws<ProtoFormatException>(() => AntigravityDecoder.Step(huge));

        var atCap = AntigravityDecoder.Step(Pb.Message(9, Pb.UInt(field, TokenCap)));
        Assert.True(atCap.Usage!.HasTokens);

        var lastWins = AntigravityDecoder.Step(Pb.Message(9, Pb.UInt(field, TokenCap + 1), Pb.UInt(field, 5)));
        Assert.True(lastWins.Usage!.HasTokens);

        var otherWire = AntigravityDecoder.Step(Pb.Message(9, Pb.Fixed64(field, TokenCap + 1)));
        Assert.False(otherWire.Usage!.HasTokens);
    }

    [Theory]
    [InlineData("resp", "msg", "resp")]
    [InlineData("resp", null, "resp")]
    [InlineData("", "msg", "msg")]
    [InlineData(null, "msg", "msg")]
    [InlineData("", "", null)]
    [InlineData(null, null, null)]
    public void StepKeyIsResponseIdElseMessageIdElseNull(string? responseId, string? messageId, string? expected)
    {
        var fields = new List<byte[]> { Pb.UInt(3, 1) };
        if (messageId is not null)
        {
            fields.Add(Pb.Text(7, messageId));
        }

        if (responseId is not null)
        {
            fields.Add(Pb.Text(11, responseId));
        }

        var step = AntigravityDecoder.Step(Pb.Message(9, [.. fields]));

        Assert.Equal(expected, step.Usage!.Key);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(9)]
    [InlineData(10)]
    public void AnyOfTheFiveTokenFieldsMakesUsageCount(int field)
    {
        Assert.True(AntigravityDecoder.Step(Pb.Message(9, Pb.UInt(field, 1))).Usage!.HasTokens);
        Assert.False(AntigravityDecoder.Step(Pb.Message(9, Pb.UInt(4, 1))).Usage!.HasTokens);
    }

    [Fact]
    public void StepsWithMalformedNestedFieldsThatAreReadAreMalformed()
    {
        Assert.Throws<ProtoFormatException>(() => AntigravityDecoder.Step(Pb.Bytes(1, 0x0B)));
        Assert.Throws<ProtoFormatException>(() => AntigravityDecoder.Step(Pb.Bytes(32, 0x08)));
        Assert.Throws<ProtoFormatException>(() => AntigravityDecoder.Step(Pb.Bytes(9, 0x0F)));
        Assert.Throws<ProtoFormatException>(() => AntigravityDecoder.Step([0x48]));
    }

    [Theory]
    [InlineData(1000L, 300L, 400L, 120L, 180L, 1400L, 400L, 300L, 120L)]
    [InlineData(10L, 0L, 0L, 120L, 180L, 10L, 0L, 300L, 120L)]
    [InlineData(0L, 0L, 0L, 0L, 50L, 0L, 0L, 50L, 0L)]
    [InlineData(0L, 7L, 5L, 0L, 0L, 5L, 5L, 7L, 0L)]
    [InlineData(0L, 0L, 0L, 0L, 0L, 0L, 0L, 0L, 0L)]
    public void UsageMapsToNormalizedTokens(
        long input, long output, long cacheRead, long thinking, long response,
        long expectedInput, long expectedCached, long expectedOutput, long expectedReasoning)
    {
        var usage = new AntigravityUsage(input, output, cacheRead, thinking, response, null);

        Assert.Equal(new TokenCounts(expectedInput, expectedCached, 0, expectedOutput, expectedReasoning), usage.ToTokens());
    }

    [Fact]
    public void GenerationTakesResponseModelElseDisplayNameAndItsStepIndices()
    {
        var generation = AntigravityDecoder.Generation(12, Pb.Concat(
            Pb.Message(1, Pb.Text(19, "gemini-3-pro-preview"), Pb.Text(21, "Gemini 3 Pro"), Pb.UInt(1, 4)),
            Pb.Packed(2, 4, 5),
            Pb.UInt(2, 7),
            Pb.UInt(2, 4)));

        Assert.Equal(12, generation.Idx);
        Assert.Equal("gemini-3-pro-preview", generation.Model);
        Assert.Equal([4UL, 5UL, 7UL], generation.StepIndices.Order());

        Assert.Equal("Gemini 3 Pro", AntigravityDecoder.Generation(1, Pb.Message(1, Pb.Text(19, ""), Pb.Text(21, "Gemini 3 Pro"))).Model);
        Assert.Equal("Gemini 3 Pro", AntigravityDecoder.Generation(1, Pb.Message(1, Pb.Text(21, "Gemini 3 Pro"))).Model);
        Assert.Null(AntigravityDecoder.Generation(1, Pb.Message(1, Pb.Text(19, ""), Pb.Text(21, ""))).Model);
        Assert.Null(AntigravityDecoder.Generation(1, Pb.Message(1)).Model);
        Assert.Null(AntigravityDecoder.Generation(1, Pb.Packed(2, 1)).Model);
        Assert.Equal("b", AntigravityDecoder.Generation(1, Pb.Concat(Pb.Message(1, Pb.Text(19, "a")), Pb.Message(1, Pb.Text(21, "b")))).Model);
        Assert.Empty(AntigravityDecoder.Generation(1, Pb.Message(1, Pb.Text(19, "a"))).StepIndices);
    }

    [Fact]
    public void GenerationWithAMalformedChatModelOrIndicesIsMalformed()
    {
        Assert.Throws<ProtoFormatException>(() => AntigravityDecoder.Generation(1, Pb.Bytes(1, 0x0B)));
        Assert.Throws<ProtoFormatException>(() => AntigravityDecoder.Generation(1, Pb.Bytes(2, 0x80)));
        Assert.Throws<ProtoFormatException>(() => AntigravityDecoder.Generation(1, [0x0A, 0x05, 0x01]));
    }

    [Fact]
    public void ExecutorReadsSignedFieldsAndTheNestedPlannerModel()
    {
        var executor = AntigravityDecoder.Executor(3, Pb.Concat(
            Pb.Int(1, 2),
            Pb.Int(3, 41),
            Pb.Message(10, Pb.UInt(2, 1), Pb.Message(1, Pb.UInt(1, 9), Pb.Text(28, "gemini-3-flash")))));

        Assert.Equal(new AntigravityExecutor(3, 2, 41, "gemini-3-flash"), executor);
        Assert.Equal(new AntigravityExecutor(4, 0, -1, null), AntigravityDecoder.Executor(4, Pb.Int(3, -1)));
        Assert.Equal(-7, AntigravityDecoder.Executor(4, Pb.Int(1, -7)).TerminationReason);
        Assert.Null(AntigravityDecoder.Executor(1, Pb.Message(10)).Model);
        Assert.Null(AntigravityDecoder.Executor(1, Pb.Message(10, Pb.Message(1))).Model);
        Assert.Null(AntigravityDecoder.Executor(1, Pb.Message(10, Pb.Message(1, Pb.Text(28, "")))).Model);
        Assert.Null(AntigravityDecoder.Executor(1, Pb.Message(10, Pb.Text(28, "top-level"))).Model);
        Assert.Null(AntigravityDecoder.Executor(1, Pb.Concat(Pb.Message(10, Pb.Message(1, Pb.Text(28, "a"))), Pb.Message(10))).Model);
    }

    [Fact]
    public void ExecutorWithAMalformedCascadeOrPlannerIsMalformed()
    {
        Assert.Throws<ProtoFormatException>(() => AntigravityDecoder.Executor(1, Pb.Bytes(10, 0x0B)));
        Assert.Throws<ProtoFormatException>(() => AntigravityDecoder.Executor(1, Pb.Message(10, Pb.Bytes(1, 0x08))));
    }

    [Fact]
    public void TrajectoryProjectComesFromTheFirstWorkspaceUri()
    {
        var trajectory = AntigravityDecoder.Trajectory(Pb.Concat(
            Pb.Message(1, Pb.Text(1, "file:///home/me/ws-first")),
            Pb.Message(1, Pb.Text(1, "file:///home/me/ws-second")),
            Pb.Timestamp(2, 1_760_000_000, 5_000_000),
            Pb.Text(7, "file:///c%3A/Users/dev/projects/orbit"),
            Pb.Text(7, "file:///home/me/other")));

        Assert.Equal(new AntigravityTrajectory(1_760_000_000_005, null, "orbit", false), trajectory);
    }

    [Fact]
    public void TrajectoryProjectFallsBackToTheFirstWorkspaceFolder()
    {
        var first = Pb.Message(1, Pb.Text(1, "file:///home/me/ws-first"));
        var second = Pb.Message(1, Pb.Text(1, "file:///home/me/ws-second"));

        Assert.Equal("ws-first", AntigravityDecoder.Trajectory(Pb.Concat(first, second)).Project);
        Assert.Equal("ws-first", AntigravityDecoder.Trajectory(Pb.Concat(Pb.Text(7, ""), first)).Project);
        Assert.Equal("ws-first", AntigravityDecoder.Trajectory(Pb.Concat(Pb.Text(7, "vscode-remote://host"), first)).Project);
        Assert.Null(AntigravityDecoder.Trajectory(Pb.Concat(Pb.Text(7, ""), Pb.Text(7, "file:///home/me/second-uri"))).Project);
        Assert.Null(AntigravityDecoder.Trajectory(Pb.Message(1)).Project);
        Assert.Null(AntigravityDecoder.Trajectory(Pb.Concat(Pb.Message(1), second)).Project);
        Assert.Null(AntigravityDecoder.Trajectory([]).Project);
        Assert.Equal("ws-first", AntigravityDecoder.Trajectory(Pb.Concat(first, Pb.Bytes(1, 0x0B))).Project);
    }

    [Fact]
    public void AMalformedFirstWorkspaceMakesTheTrajectoryMalformed()
    {
        Assert.Throws<ProtoFormatException>(() => AntigravityDecoder.Trajectory(
            Pb.Concat(Pb.Bytes(1, 0x0B), Pb.Message(1, Pb.Text(1, "file:///home/me/ok")), Pb.Text(7, "file:///home/me/proj"))));
        Assert.Throws<ProtoFormatException>(() => AntigravityDecoder.Trajectory(Pb.Bytes(2, 0x08)));
    }

    [Fact]
    public void TrajectorySubagentFromParentNestingDepthOrSpec()
    {
        Assert.Equal(new AntigravityTrajectory(null, "abc", null, true), AntigravityDecoder.Trajectory(Pb.Text(5, "abc")));
        Assert.Equal(new AntigravityTrajectory(null, null, null, false), AntigravityDecoder.Trajectory(Pb.Text(5, "")));
        Assert.True(AntigravityDecoder.Trajectory(Pb.Int(17, 1)).IsSubagent);
        Assert.False(AntigravityDecoder.Trajectory(Pb.Int(17, 0)).IsSubagent);
        Assert.False(AntigravityDecoder.Trajectory(Pb.Int(17, -1)).IsSubagent);
        Assert.True(AntigravityDecoder.Trajectory(Pb.Message(8)).IsSubagent);
        Assert.True(AntigravityDecoder.Trajectory(Pb.Bytes(8, 0x0B)).IsSubagent);
        Assert.False(AntigravityDecoder.Trajectory(Pb.UInt(8, 1)).IsSubagent);
        Assert.False(AntigravityDecoder.Trajectory([]).IsSubagent);
    }

    [Theory]
    [InlineData("file:///c%3A/Users/dev/projects/orbit", "orbit")]
    [InlineData("vscode-remote://ssh-remote+host/home/x/proj", "proj")]
    [InlineData("C:\\Users\\me\\proj", "proj")]
    [InlineData("c:/Users/me/proj", "proj")]
    [InlineData("/home/me/proj/", "proj")]
    [InlineData("file:///home/me/proj/", "proj")]
    [InlineData("proj", "proj")]
    [InlineData("file:///home/me/my%zzproj", "my%zzproj")]
    [InlineData("file:///x/100%", "100%")]
    [InlineData("file:///x/%4", "%4")]
    [InlineData("file:///home/me/%E6%98%9F", "\u661F")]
    [InlineData("file:///home/me/%e6%98%9f", "\u661F")]
    [InlineData("file:///x/%E6%98%9Fend%C3", "\u661Fend\uFFFD")]
    [InlineData("file:///home/me/%FF", "\uFFFD")]
    [InlineData("file:///x/%E6%98", "\uFFFD")]
    [InlineData("file:///x/%C0%AF", "\uFFFD\uFFFD")]
    [InlineData("file:///x/%ED%A0%80", "\uFFFD\uFFFD\uFFFD")]
    [InlineData("file:///x/%E6%98\u661F", "\uFFFD\u661F")]
    [InlineData("file:///home/me/a%2Fb", "b")]
    [InlineData("file:///home/me/a%5Cb", "b")]
    [InlineData("%E6%98%9F", "%E6%98%9F")]
    [InlineData("ab:c", "c")]
    [InlineData("a:b/c", "c")]
    [InlineData("file:x%2Fy", "y")]
    [InlineData("1ab:foo/bar", "bar")]
    [InlineData("a.b+c-d:/x/y", "y")]
    [InlineData("vscode-remote://host", null)]
    [InlineData("file://", null)]
    [InlineData("file:", null)]
    [InlineData("/", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void UriBasenameStripsSchemeAndAuthorityAndPercentDecodes(string? value, string? expected)
    {
        Assert.Equal(expected, AntigravityDecoder.UriBasename(value));
    }

    private static void ParseOnly(byte[] data) => ProtoMessage.Parse(data);

    private static void ReadVarintAt(byte[] data, int position) => ProtoMessage.ReadVarint(data, ref position);

    private static void ReadNested(byte[] data, int number, bool first = false) =>
        ProtoMessage.Parse(data).TryMessage(number, out _, first);

    private static void ReadTimestamp(byte[] data, int number) => ProtoMessage.Parse(data).Timestamp(number);

    private static void ReadRepeated(byte[] data, int number) => ProtoMessage.Parse(data).RepeatedUInt(number);

    /// <summary>A minimal protobuf writer for test blobs.</summary>
    private static class Pb
    {
        public static byte[] Varint(ulong value)
        {
            var bytes = new List<byte>(10);
            while (value >= 0x80)
            {
                bytes.Add((byte)(value | 0x80));
                value >>= 7;
            }

            bytes.Add((byte)value);
            return [.. bytes];
        }

        public static byte[] Key(ulong number, int wire) => Varint((number << 3) | (uint)wire);

        public static byte[] UInt(int number, ulong value) => [.. Key((ulong)number, ProtoMessage.WireVarint), .. Varint(value)];

        public static byte[] Int(int number, long value) => UInt(number, unchecked((ulong)value));

        public static byte[] Fixed32(int number, uint value)
        {
            var payload = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(payload, value);
            return [.. Key((ulong)number, ProtoMessage.WireFixed32), .. payload];
        }

        public static byte[] Fixed64(int number, ulong value)
        {
            var payload = new byte[8];
            BinaryPrimitives.WriteUInt64LittleEndian(payload, value);
            return [.. Key((ulong)number, ProtoMessage.WireFixed64), .. payload];
        }

        public static byte[] Bytes(int number, params byte[] payload) =>
            [.. Key((ulong)number, ProtoMessage.WireLength), .. Varint((ulong)payload.Length), .. payload];

        public static byte[] Text(int number, string text) => Bytes(number, Encoding.UTF8.GetBytes(text));

        public static byte[] Message(int number, params byte[][] fields) => Bytes(number, Concat(fields));

        public static byte[] Packed(int number, params ulong[] values) => Bytes(number, Concat([.. values.Select(Varint)]));

        public static byte[] Timestamp(int number, long seconds, long nanos = 0) =>
            nanos == 0 ? Message(number, Int(1, seconds)) : Message(number, Int(1, seconds), Int(2, nanos));

        public static byte[] Concat(params byte[][] parts) => [.. parts.SelectMany(part => part)];
    }
}
