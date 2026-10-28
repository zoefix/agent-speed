using System.Text;
using AgentSpeed.Core.Parsing;

namespace AgentSpeed.Core.Tests;

public sealed class Rfc3339Tests
{
    private const long T0 = 1_792_490_400_000;

    [Theory]
    [InlineData("2026-10-20T10:00:00Z", T0)]
    [InlineData("2026-10-20t10:00:00z", T0)]
    [InlineData("2026-10-20T10:00:00.5Z", T0 + 500)]
    [InlineData("2026-10-20T10:00:00.123Z", T0 + 123)]
    [InlineData("2026-10-20T10:00:00.123456Z", T0 + 123)]
    [InlineData("2026-10-20T10:00:00.999999999Z", T0 + 999)]
    [InlineData("2026-10-20T10:00:00.000Z", T0)]
    [InlineData("2026-10-20T18:00:00+08:00", T0)]
    [InlineData("2026-10-20T18:00:00.250+08:00", T0 + 250)]
    [InlineData("2026-10-20T04:30:00-0530", T0)]
    [InlineData("2026-10-20T04:30:00-05:30", T0)]
    [InlineData("2026-10-20T10:00:00+00:00", T0)]
    [InlineData("1970-01-01T00:00:00Z", 0)]
    [InlineData("1969-12-31T23:59:59.500Z", -500)]
    [InlineData("2024-02-29T00:00:00Z", 1_709_164_800_000)]
    [InlineData("2000-02-29T12:00:00Z", 951_825_600_000)]
    public void ParsesValidTimestamps(string text, long expected)
    {
        Assert.True(Rfc3339.TryParseUnixMs(Encoding.ASCII.GetBytes(text), out var ms));
        Assert.Equal(expected, ms);
    }

    [Theory]
    [InlineData("2026-10-20T10:00:00.123Z")]
    [InlineData("2026-01-01T00:00:00Z")]
    [InlineData("2026-12-31T23:59:59.999Z")]
    [InlineData("2030-06-15T07:08:09.010+02:00")]
    [InlineData("2001-03-04T05:06:07-11:45")]
    public void AgreesWithDateTimeOffset(string text)
    {
        Assert.True(Rfc3339.TryParseUnixMs(Encoding.ASCII.GetBytes(text), out var ms));
        Assert.Equal(Ts.Ms(text), ms);
    }

    [Theory]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("not a timestamp at all!")]
    [InlineData("2026-10-20T10:00:00")]
    [InlineData("2026-10-20T10:00:00.123")]
    [InlineData("2026-10-20")]
    [InlineData("2026-13-20T10:00:00Z")]
    [InlineData("2026-00-20T10:00:00Z")]
    [InlineData("2026-10-32T10:00:00Z")]
    [InlineData("2026-10-00T10:00:00Z")]
    [InlineData("2026-02-29T10:00:00Z")]
    [InlineData("1900-02-29T10:00:00Z")]
    [InlineData("2026-04-31T10:00:00Z")]
    [InlineData("2026-10-20T24:00:00Z")]
    [InlineData("2026-10-20T10:60:00Z")]
    [InlineData("2026-10-20T10:00:00.Z")]
    [InlineData("2026-10-20T10:00:00Q")]
    [InlineData("2026-10-20T10:00:00Zjunk")]
    [InlineData("2026-10-20T10:00:00+8:00")]
    [InlineData("2026-10-20T10:00:00+08:0")]
    [InlineData("2026-10-20T10:00:00+08:00:00")]
    [InlineData("2026/10/20T10:00:00Z")]
    [InlineData("2026-10-20X10:00:00Z")]
    [InlineData("20a6-10-20T10:00:00Z")]
    public void RejectsInvalidTimestamps(string text)
    {
        Assert.False(Rfc3339.TryParseUnixMs(Encoding.ASCII.GetBytes(text), out _));
    }
}
