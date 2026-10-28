using System.IO.Compression;
using System.Buffers.Binary;
using AgentSpeed.Core.Reporting;

namespace AgentSpeed.Core.Tests.Reporting;

[Collection("report server")]
public sealed class ProfileEndToEndTests
{
    [Fact]
    public async Task TwoIndependentDeviceKeysPairOnlyAfterApprovalAndShareProfileAndAvatar()
    {
        if (!ReferencePython.Available) return;
        using var server = new ReferenceServer("--extensions");
        using var transport = new HttpReportTransport();
        var client = new ReportClientInfo("1.3.0", "Windows", 11);
        var api = new ReportApi(transport, client.UserAgent);
        var discovery = Assert.IsType<ReportDiscoveryOutcome.Document>(await api.DiscoverAsync(server.Service)).Discovery;
        Assert.Equal(new ReportExtensions(true, true, true, true), discovery.Extensions);
        using var firstKey = new SoftwareReportSigner();
        using var secondKey = new SoftwareReportSigner();
        using var thirdKey = new SoftwareReportSigner();
        var first = ReportCanonical.NewUuid();
        var second = ReportCanonical.NewUuid();
        var third = ReportCanonical.NewUuid();
        Assert.NotEqual(firstKey.PublicKey, secondKey.PublicKey);
        var profileApi = new ReportProfileApi(transport);

        async Task<ReportHttpResponse> Send(string device, IReportSigner key, string method, string path, byte[]? body = null, bool image = false)
        {
            var request = new ReportRequestBuilder(client.UserAgent, device, null).Make(method, discovery.Api.Endpoint(path), body ?? [], key, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            if (image && method == "PUT") request = request with { Headers = request.Headers.Where(h => h.Key != "Content-Type").Append(new KeyValuePair<string, string>("Content-Type", "image/png")).ToArray() };
            return Assert.IsType<ReportHttpResponse>(await profileApi.SendAsync(request, image && method == "GET", CancellationToken.None));
        }
        ReportProfile Profile(ReportHttpResponse response) => Assert.IsType<ReportProfile>(ReportProfileJson.Profile(response.Body, discovery.UsernameRules));
        ReportPairing Pairing(ReportHttpResponse response) => Assert.IsType<ReportPairing>(ReportProfileJson.Pairing(response.Body));
        foreach (var (id, key, name) in new[] { (first, firstKey, "WindowsDeviceA"), (second, secondKey, "WindowsDeviceB"), (third, thirdKey, "WindowsDeviceC") })
        {
            var register = await Send(id, key, "POST", "/v1/devices", ReportDocuments.Registration(id, name, key.PublicKey, client));
            Assert.Equal(201, register.Status);
        }
        var created = await Send(first, firstKey, "POST", "/v1/profile/identity", "{}"u8.ToArray());
        Assert.Contains(created.Status, new[] { 200, 201 });
        var shared = Profile(created).SharedUserId;
        Assert.NotNull(shared);
        var invite = await Send(first, firstKey, "POST", "/v1/profile/pairings", "{}"u8.ToArray());
        Assert.Equal(201, invite.Status);
        var pairing = Pairing(invite);
        Assert.NotNull(pairing.Code);
        var claim = await Send(second, secondKey, "POST", "/v1/profile/pairings/claim", ReportJson.Object(("code", ReportJson.Of(pairing.Code))).ToCompactBytes());
        Assert.Equal(202, claim.Status);
        Assert.Equal("pending", Pairing(claim).State);
        Assert.Null(Profile(await Send(second, secondKey, "GET", "/v1/profile")).SharedUserId);
        Assert.Equal(404, (await Send(third, thirdKey, "GET", "/v1/profile/pairings/" + pairing.Id)).Status);
        Assert.NotEqual(200, (await Send(second, secondKey, "POST", "/v1/profile/pairings/" + pairing.Id + "/approve", "{}"u8.ToArray())).Status);
        var approve = await Send(first, firstKey, "POST", "/v1/profile/pairings/" + pairing.Id + "/approve", "{}"u8.ToArray());
        Assert.Equal(200, approve.Status);
        Assert.Equal("approved", Pairing(approve).State);
        Assert.Equal(shared, Profile(await Send(second, secondKey, "GET", "/v1/profile")).SharedUserId);
        Assert.Equal(2, Profile(await Send(first, firstKey, "GET", "/v1/profile")).Devices.Count);
        var rename = await Send(second, secondKey, "PATCH", "/v1/profile", ReportJson.Object(("displayName", ReportJson.Of("Shared Windows"))).ToCompactBytes());
        Assert.Equal(200, rename.Status);
        Assert.Equal("Shared Windows", Profile(await Send(first, firstKey, "GET", "/v1/profile")).DisplayName);
        foreach (var (device, key, username) in new[] { (first, firstKey, "WindowsDeviceA"), (second, secondKey, "WindowsDeviceB") })
        {
            var counters = new ReportCounters();
            counters.Add(new(Requests: 1, Output: 100, MeasuredOutput: 100, MeasuredMs: 1000), new("claude-opus-5-5", SourceKind.ClaudeCode), ExecutionModeNative.ClaudeSpeed("fast"));
            var envelope = new ReportEnvelope(client, device, username, ReportCanonical.NewUuid(), 1, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), 5000, ReportActivity.Active, 0);
            var body = ReportBody.Data(envelope, new ReportWindowContent(counters, new Dictionary<SourceKind, Core.Traffic.TrafficBytes>(), Rate.Zero, 0, null),
                ReportSample.Empty, new ReportOptions(new HashSet<ReportSection> { ReportSection.Models }) { Extensions = discovery.Extensions });
            var accepted = await Send(device, key, "POST", "/v1/reports", body);
            Assert.Equal("accepted", ReportReply.Parse(accepted.Status, accepted.Headers, accepted.Body, DateTimeOffset.UtcNow).Result);
        }
        var png = Image();
        Assert.True(ReportAvatarImage.IsUpload(png));
        var upload = await Send(first, firstKey, "PUT", "/v1/profile/avatar", png, image: true);
        Assert.Equal(200, upload.Status);
        var avatar = Profile(upload).Avatar;
        Assert.NotNull(avatar);
        var download = await Send(second, secondKey, "GET", "/v1/profile/avatar", image: true);
        Assert.Equal(200, download.Status);
        Assert.Equal(avatar.Sha256, ReportHash.Sha256Hex(download.Body));
        Assert.True(ReportAvatarImage.IsUpload(download.Body));
        Assert.Null(Profile(await Send(second, secondKey, "DELETE", "/v1/profile/avatar")).Avatar);
        Assert.Equal(404, (await Send(first, firstKey, "GET", "/v1/profile/avatar", image: true)).Status);
        Assert.Null(Profile(await Send(second, secondKey, "DELETE", "/v1/profile/identity")).SharedUserId);
        Assert.Equal(shared, Profile(await Send(first, firstKey, "GET", "/v1/profile")).SharedUserId);
        Assert.Single(Profile(await Send(first, firstKey, "GET", "/v1/profile")).Devices);
    }

    private static byte[] Image()
    {
        using var png = new MemoryStream();
        png.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(0, 4), 256);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4, 4), 256);
        header[8] = 8; header[9] = 6;
        Chunk(png, "IHDR", header);
        using var compressed = new MemoryStream();
        using (var deflate = new ZLibStream(compressed, CompressionLevel.SmallestSize, leaveOpen: true)) deflate.Write(new byte[(256 * 4 + 1) * 256]);
        Chunk(png, "IDAT", compressed.ToArray());
        Chunk(png, "IEND", []);
        return png.ToArray();
    }
    private static void Chunk(Stream stream, string name, byte[] data)
    {
        var kind = System.Text.Encoding.ASCII.GetBytes(name);
        Span<byte> integer = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(integer, data.Length);
        stream.Write(integer); stream.Write(kind); stream.Write(data);
        var crc = uint.MaxValue;
        foreach (var value in kind.Concat(data))
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0U : 0xedb88320U);
        }
        BinaryPrimitives.WriteUInt32BigEndian(integer, crc ^ uint.MaxValue);
        stream.Write(integer);
    }
}
