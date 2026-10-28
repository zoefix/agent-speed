using System.Buffers.Binary;
using System.Text;
using AgentSpeed.Core.Reporting;

namespace AgentSpeed.Core.Tests.Reporting;

public sealed class ReportProfileTests
{
    private const string DeviceId = "11111111-1111-4111-8111-111111111111";
    private const string Profile = """{"sharedUserId":null,"displayName":"Tester","avatar":null,"devices":[{"deviceId":"11111111-1111-4111-8111-111111111111","platform":"Windows","isCurrent":true,"online":true}]}""";
    private static string Pairing => """{"id":"11111111-1111-4111-8111-111111111111","state":"pending","expiresAt":"EXPIRY","claimant":{"deviceId":"22222222-2222-4222-8222-222222222222","platform":"macOS"}}""".Replace("EXPIRY", ReportInstant.Format(DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeMilliseconds()), StringComparison.Ordinal);

    [Fact]
    public void ProfileAcceptsOnlyBoundedValidatedDataAndUnknownFieldsAreIgnored()
    {
        var profile = ReportProfileJson.Profile(Encoding.UTF8.GetBytes(Profile), ReportUsernameRules.Standard);
        Assert.NotNull(profile);
        Assert.Null(profile.SharedUserId);
        Assert.Equal(DeviceId, Assert.Single(profile.Devices).DeviceId);
        Assert.Null(ReportProfileJson.Profile(Encoding.UTF8.GetBytes(Profile.Replace("Tester", "bad\nname", StringComparison.Ordinal)), ReportUsernameRules.Standard));
        Assert.Null(ReportProfileJson.Profile(Encoding.UTF8.GetBytes(Profile.Replace(DeviceId, "../x", StringComparison.Ordinal)), ReportUsernameRules.Standard));
        Assert.Null(ReportProfileJson.Profile(Encoding.UTF8.GetBytes(Profile.Replace("\"online\":true", "\"online\":1", StringComparison.Ordinal)), ReportUsernameRules.Standard));
        Assert.Null(ReportProfileJson.Profile(new byte[65_537], ReportUsernameRules.Standard));
        Assert.Null(ReportProfileJson.Profile("[]"u8, ReportUsernameRules.Standard));
        Assert.Null(ReportProfileJson.Profile(Encoding.UTF8.GetBytes(Profile.Replace("Windows", "OtherOS", StringComparison.Ordinal)), ReportUsernameRules.Standard));
        var duplicateCurrent = System.Text.Json.Nodes.JsonNode.Parse(Profile)!;
        var devices = duplicateCurrent["devices"]!.AsArray();
        var other = devices[0]!.DeepClone();
        other["deviceId"] = "22222222-2222-4222-8222-222222222222";
        devices.Add(other);
        Assert.Null(ReportProfileJson.Profile(Encoding.UTF8.GetBytes(duplicateCurrent.ToJsonString()), ReportUsernameRules.Standard));
    }

    [Theory]
    [InlineData("abcd-efgh-jklm", "ABCDEFGHJKLM")]
    [InlineData("ABCDEFGHJKLM", "ABCDEFGHJKLM")]
    [InlineData("ABCD-EFGH-JKLI", null)]
    [InlineData("ABCD-EFGH-JKLO", null)]
    [InlineData("ABCD-EFGH-JKL0", null)]
    [InlineData("ABCD-EFGH-JKL1", null)]
    [InlineData("ABCD EFGH JKLM", null)]
    [InlineData("<script>", null)]
    public void PairingCodeUsesTheSharedAlphabet(string input, string? expected) => Assert.Equal(expected, ReportProfileJson.NormalizeCode(input));

    [Fact]
    public void PairingRequiresAClaimantForApprovalAndNeverPrintsCode()
    {
        var pairing = ReportProfileJson.Pairing(Encoding.UTF8.GetBytes(Pairing));
        Assert.NotNull(pairing);
        Assert.Equal("macOS", pairing.Claimant!.Platform);
        Assert.Null(ReportProfileJson.Pairing(Encoding.UTF8.GetBytes(Pairing.Replace("pending", "unknown", StringComparison.Ordinal))));
        Assert.Null(ReportProfileJson.Pairing("{\"id\":\"11111111-1111-4111-8111-111111111111\",\"state\":\"pending\",\"expiresAt\":\"2026-10-24T12:10:00Z\"}"u8));
        var secret = pairing with { Code = "ABCD-EFGH-JKLM" };
        Assert.DoesNotContain(secret.Code, secret.ToString(), StringComparison.Ordinal);
        var badExpiry = System.Text.Json.Nodes.JsonNode.Parse(Pairing)!;
        badExpiry["expiresAt"] = "2026-10-24T12:10:00Z";
        Assert.Null(ReportProfileJson.Pairing(Encoding.UTF8.GetBytes(badExpiry.ToJsonString())));
        badExpiry["expiresAt"] = ReportInstant.Format(DateTimeOffset.UtcNow.AddMinutes(12).ToUnixTimeMilliseconds());
        Assert.Null(ReportProfileJson.Pairing(Encoding.UTF8.GetBytes(badExpiry.ToJsonString())));
    }

    [Fact]
    public void PairingBoundsUseTheSameCorrectedClockAsRequestSigning()
    {
        var serverNow = DateTimeOffset.UtcNow;
        var expiry = ReportInstant.Format(serverNow.AddMinutes(10).ToUnixTimeMilliseconds());
        var document = System.Text.Json.Nodes.JsonNode.Parse(Pairing)!;
        document["expiresAt"] = expiry;
        var bytes = Encoding.UTF8.GetBytes(document.ToJsonString());
        Assert.Null(ReportProfileJson.Pairing(bytes, serverNow.AddMinutes(-2)));
        foreach (var skewSeconds in new[] { -120, 120 })
        {
            var localNow = serverNow.AddSeconds(skewSeconds);
            var correctedNow = localNow.AddSeconds(-skewSeconds);
            var pairing = Assert.IsType<ReportPairing>(ReportProfileJson.Pairing(bytes, correctedNow));
            Assert.InRange((pairing.ExpiresAt - correctedNow).TotalSeconds, 599.999, 600.001);
        }
    }

    [Fact]
    public void ImageHeadersRejectDecompressionBombsAnimationUnknownFormatsAndTruncation()
    {
        Assert.Equal(new ReportImageSize(256, 256, true), ReportAvatarImage.ReadHeader(Png(256, 256)));
        Assert.True(ReportAvatarImage.IsUpload(Png(256, 256)));
        Assert.False(ReportAvatarImage.IsUpload(Png(512, 256)));
        Assert.Null(ReportAvatarImage.ReadHeader(Png(8193, 1)));
        Assert.Null(ReportAvatarImage.ReadHeader(Png(8192, 8192)));
        Assert.Null(ReportAvatarImage.ReadHeader(Png(256, 256, animate: true)));
        Assert.Null(ReportAvatarImage.ReadHeader(Png(256, 256)[..^1]));
        Assert.Null(ReportAvatarImage.ReadHeader("<svg/>"u8));
        Assert.Null(ReportAvatarImage.ReadHeader("GIF89a"u8));
        Assert.Null(ReportAvatarImage.ReadHeader(new byte[ReportAvatarImage.MaxInputBytes + 1]));
        byte[] jpeg = [255, 216, 255, 192, 0, 8, 8, 1, 0, 1, 0, 3];
        Assert.Equal(new ReportImageSize(256, 256, false), ReportAvatarImage.ReadHeader(jpeg));
        jpeg[7] = 255;
        Assert.Null(ReportAvatarImage.ReadHeader(jpeg));
    }

    [Fact]
    public async Task ProfileTransportHasSeparateBoundsAndNeverUsesDiscoveryRedirects()
    {
        var transport = new CaptureTransport();
        var api = new ReportProfileApi(transport);
        var request = new ReportRequest("GET", new Uri("https://example.com/v1/profile"), [], []);
        Assert.NotNull(await api.SendAsync(request, avatar: false, CancellationToken.None));
        Assert.Equal(65_536, transport.Maximum);
        Assert.Same(request, transport.Request);
        await api.SendAsync(request with { Url = new Uri("https://example.com/v1/profile/avatar") }, avatar: true, CancellationToken.None);
        Assert.Equal(512 * 1024, transport.Maximum);
        Assert.Equal(0, transport.Discoveries);
    }

    private sealed class CaptureTransport : IReportTransport
    {
        public int Maximum { get; private set; }
        public int Discoveries { get; private set; }
        public ReportRequest? Request { get; private set; }
        public Task<ReportHttpResponse> SendAsync(ReportRequest request, int maxBytes, TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            Maximum = maxBytes;
            Request = request;
            return Task.FromResult(new ReportHttpResponse(200, new Dictionary<string, string>(), Encoding.UTF8.GetBytes(Profile)));
        }
        public Task<ReportHttpResponse> GetAsync(Uri url, string? etag, string userAgent, int maxBytes, TimeSpan timeout, CancellationToken cancellationToken = default)
        { Discoveries++; throw new InvalidOperationException(); }
    }
    private static byte[] Png(int width, int height, bool animate = false)
    {
        using var stream = new MemoryStream();
        stream.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(0, 4), width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4, 4), height);
        header[8] = 8; header[9] = 6;
        Chunk(stream, "IHDR", header);
        if (animate) Chunk(stream, "acTL", new byte[8]);
        Chunk(stream, "IDAT", [0]);
        Chunk(stream, "IEND", []);
        return stream.ToArray();
    }
    private static void Chunk(Stream stream, string name, byte[] data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        stream.Write(length); stream.Write(Encoding.ASCII.GetBytes(name)); stream.Write(data); stream.Write(new byte[4]);
    }
}
