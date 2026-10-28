using System.Security.Cryptography;
using System.Text.Json;
using AgentSpeed.Core.Reporting;

namespace AgentSpeed.Core.Tests.Reporting;

/// <summary>REPORTING 5 against spec/fixtures/report-vectors.json: keys, canonical strings, signatures and verification.</summary>
public sealed class ReportSigningTests
{
    private const string DeviceId = "6f1c2a9e-3b4d-4e5f-8a7b-9c0d1e2f3a4b";

    private static JsonElement Request(string name) => ReportSpec.Vector("requests").GetProperty(name);

    [Fact]
    public void KeysEncodeAsJwkThumbprintAndSpki()
    {
        foreach (var name in new[] { "device", "other", "rotated" })
        {
            using var signer = ReportSpec.Key(name);
            var entry = ReportSpec.Vector("keys").GetProperty(name);
            var jwk = entry.GetProperty("jwk");
            var expected = ReportJson.Object(("kty", ReportJson.Of("EC")), ("crv", ReportJson.Of("P-256")),
                ("x", ReportJson.Of(jwk.Text("x"))), ("y", ReportJson.Of(jwk.Text("y"))));
            Assert.Equal(expected.ToCompactString(), signer.PublicKey.Jwk.ToCompactString());
            Assert.Equal(entry.Text("thumbprint"), signer.PublicKey.Thumbprint);
            Assert.Equal(entry.Text("thumbprint")[..8], signer.PublicKey.Fingerprint);
            Assert.Equal(signer.PublicKey, ReportPublicKey.ParseJwk(jwk));

            using var exported = ECDsa.Create();
            exported.ImportParameters(new ECParameters
            {
                Curve = ECCurve.NamedCurves.nistP256,
                Q = new ECPoint { X = signer.PublicKey.X.ToArray(), Y = signer.PublicKey.Y.ToArray() },
            });
            Assert.Equal(entry.Text("spki"), Base64Url.Encode(exported.ExportSubjectPublicKeyInfo()));
        }
    }

    [Fact]
    public void CanonicalStringsAndBodyHashes()
    {
        var requests = ReportSpec.Vector("requests").EnumerateObject().ToList();
        Assert.Equal(6, requests.Count);
        foreach (var property in requests)
        {
            var request = property.Value;
            var headers = request.GetProperty("headers").Strings();
            var body = ReportSpec.Utf8(request.Text("body"));
            var canonical = ReportCanonical.Build(request.Text("method"), request.Text("authority"), request.Text("path"),
                headers["X-AgentSpeed-Timestamp"], headers["X-AgentSpeed-Nonce"], headers["X-AgentSpeed-Device"], body);
            Assert.Equal(request.Text("canonical"), canonical);
            Assert.Equal(request.Text("bodySha256"), ReportHash.Sha256Hex(body));
            var url = new Uri(request.Text("url"));
            Assert.Equal(request.Text("authority"), ReportUrls.Authority(url));
            Assert.Equal(request.Text("path"), ReportUrls.RequestPath(url));
            Assert.Null(ReportCanonical.MalformedHeader(headers));
        }

        Assert.Equal("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855", ReportHash.Sha256Hex([]));
    }

    [Fact]
    public void VectorSignaturesVerify()
    {
        var keys = new[] { "device", "other", "rotated" }.Select(name => ReportSpec.Key(name).PublicKey).ToArray();
        foreach (var property in ReportSpec.Vector("requests").EnumerateObject())
        {
            var request = property.Value;
            var canonical = ReportSpec.Utf8(request.Text("canonical"));
            var signature = Base64Url.Decode(request.GetProperty("headers").Text("X-AgentSpeed-Signature"));
            Assert.NotNull(signature);
            Assert.Contains(keys, key => key.Verify(canonical, signature));
            var der = Base64Url.Decode(request.Text("signatureDer"));
            Assert.NotNull(der);
            Assert.Equal(signature, DerToP1363(der));
        }
    }

    /// <summary>Every case of <c>verify</c>: valid, invalid (including <c>n − s</c>, <c>r = 0</c>, <c>s = n</c>, double hashing) and malformed.</summary>
    [Fact]
    public void VerificationCases()
    {
        var cases = ReportSpec.Vector("verify").EnumerateArray().ToList();
        Assert.Equal(24, cases.Count);
        var highS = 0;
        foreach (var item in cases)
        {
            var name = item.Text("name");
            var request = Request(item.Text("request"));
            if (request.TryGetProperty("highS", out var high) && high.ValueKind == JsonValueKind.True)
            {
                highS++;
            }

            var method = request.Text("method");
            var authority = request.Text("authority");
            var path = request.Text("path");
            var body = request.Text("body");
            var headers = request.GetProperty("headers").Strings();
            if (item.TryGetProperty("change", out var change))
            {
                foreach (var member in change.EnumerateObject())
                {
                    switch (member.Name)
                    {
                        case "headers":
                            foreach (var (key, value) in member.Value.Strings())
                            {
                                headers[key] = value;
                            }

                            break;
                        case "body":
                            body = member.Value.GetString()!;
                            break;
                        case "path":
                            path = member.Value.GetString()!;
                            break;
                        case "authority":
                            authority = member.Value.GetString()!;
                            break;
                        case "method":
                            method = member.Value.GetString()!;
                            break;
                        default:
                            Assert.Fail($"{name}: unknown change {member.Name}");
                            break;
                    }
                }
            }

            var header = item.OptionalText("header") ?? ReportCanonical.SignatureHeader;
            using var signer = ReportSpec.Key(item.Text("key"));
            string result;
            if (ReportCanonical.MalformedHeader(headers) is not null || !ReportCanonical.IsSignature(headers.GetValueOrDefault(header) ?? string.Empty))
            {
                result = "malformed";
            }
            else
            {
                var canonical = ReportCanonical.Build(method, authority, path, headers["X-AgentSpeed-Timestamp"], headers["X-AgentSpeed-Nonce"],
                    headers["X-AgentSpeed-Device"], ReportSpec.Utf8(body));
                var signature = Base64Url.Decode(headers[header])!;
                result = signer.PublicKey.Verify(ReportSpec.Utf8(canonical), signature) ? "valid" : "invalid";
            }

            Assert.True(item.Text("expect") == result, $"{name}: expected {item.Text("expect")}, got {result}");
        }

        Assert.True(highS > 0, "the vectors include a high-S signature");
    }

    [Fact]
    public void PublicKeyValidation()
    {
        var cases = ReportSpec.Vector("publicKeys").EnumerateArray().ToList();
        Assert.Equal(8, cases.Count);
        foreach (var item in cases)
        {
            var parsed = ReportPublicKey.ParseJwk(item.GetProperty("jwk"));
            Assert.True((parsed is null ? "invalid" : "valid") == item.Text("expect"), item.Text("name"));
        }
    }

    [Fact]
    public void Rfc6979KnownAnswersVerify()
    {
        var rfc = ReportSpec.Vector("rfc6979");
        var key = ReportPublicKey.FromCoordinates(Convert.FromHexString(rfc.Text("x")), Convert.FromHexString(rfc.Text("y")));
        Assert.NotNull(key);
        var messages = rfc.GetProperty("messages").EnumerateArray().ToList();
        Assert.NotEmpty(messages);
        foreach (var message in messages)
        {
            var signature = Convert.FromHexString(message.Text("r") + message.Text("s"));
            Assert.True(key.Verify(ReportSpec.Utf8(message.Text("message")), signature), message.Text("message"));
            Assert.False(key.Verify(ReportSpec.Utf8(message.Text("message") + "!"), signature));
        }

        using var signer = SoftwareReportSigner.FromPrivateScalar(Convert.FromHexString(rfc.Text("d")));
        Assert.Equal(key, signer.PublicKey);
    }

    /// <summary>The builder signs what a verifier rebuilds; a signature over the digest (hashed twice) does not verify.</summary>
    [Fact]
    public void BuilderSignsTheCanonicalString()
    {
        using var signer = ReportSpec.Key("device");
        using var rotated = ReportSpec.Key("rotated");
        var vector = Request("rotate");
        var headers = vector.GetProperty("headers").Strings();
        var builder = new ReportRequestBuilder("AgentSpeed/1.3.0 (macOS)", DeviceId, null);
        var url = new Uri(vector.Text("url"));
        var request = builder.Make("PUT", url, ReportSpec.Utf8(vector.Text("body")), signer, 1_792_656_931, headers["X-AgentSpeed-Nonce"], rotated);
        var canonical = ReportSpec.Utf8(vector.Text("canonical"));
        Assert.True(signer.PublicKey.Verify(canonical, Base64Url.Decode(request.Header(ReportCanonical.SignatureHeader))!));
        Assert.True(rotated.PublicKey.Verify(canonical, Base64Url.Decode(request.Header(ReportCanonical.NewKeySignatureHeader))!));
        Assert.Equal("application/json", request.Header("Content-Type"));
        Assert.Equal("application/json", request.Header("Accept"));
        Assert.Equal("AgentSpeed/1.3.0 (macOS)", request.Header("User-Agent"));
        Assert.Null(request.Header("Authorization"));
        foreach (var name in new[] { "X-AgentSpeed-Device", "X-AgentSpeed-Timestamp", "X-AgentSpeed-Nonce" })
        {
            Assert.Equal(headers[name], request.Header(name));
        }

        Assert.Null(ReportCanonical.MalformedHeader(request.Headers.ToDictionary(h => h.Key, h => h.Value)));
        var doubled = signer.Sign(SHA256.HashData(canonical));
        Assert.False(signer.PublicKey.Verify(canonical, doubled));

        var keyed = new ReportRequestBuilder("AgentSpeed/1.3.0 (Windows)", DeviceId, "secret-12345");
        var delete = keyed.Make("DELETE", url, [], signer, 1_792_656_931);
        Assert.Null(delete.Header("Content-Type"));
        Assert.Equal("Bearer secret-12345", delete.Header("Authorization"));
        Assert.Equal(22, delete.Header(ReportCanonical.NonceHeader)!.Length);
        Assert.DoesNotContain("secret", delete.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(delete.Header(ReportCanonical.SignatureHeader)!, delete.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("secret", keyed.ToString(), StringComparison.Ordinal);
    }

    /// <summary>Signatures of this platform's signer verify with either form of s, and fresh nonces and ids are well-formed.</summary>
    [Fact]
    public void FreshKeysNoncesAndIds()
    {
        using var signer = new SoftwareReportSigner();
        var message = ReportSpec.Utf8("AGENTSPEED-V1\nPOST");
        for (var i = 0; i < 20; i++)
        {
            var signature = signer.Sign(message);
            Assert.Equal(64, signature.Length);
            Assert.True(signer.PublicKey.Verify(message, signature));
            Assert.True(signer.PublicKey.Verify(message, FlipS(signature)), "the other form of s verifies too");
        }

        var nonces = Enumerable.Range(0, 50).Select(_ => ReportCanonical.NewNonce()).ToHashSet();
        Assert.Equal(50, nonces.Count);
        Assert.All(nonces, n => Assert.Equal(16, Base64Url.Decode(n)!.Length));
        var ids = Enumerable.Range(0, 50).Select(_ => ReportCanonical.NewUuid()).ToHashSet();
        Assert.Equal(50, ids.Count);
        Assert.All(ids, id => Assert.True(ReportCanonical.IsDeviceId(id), id));
        Assert.False(ReportCanonical.IsDeviceId("6F1C2A9E-3B4D-4E5F-8A7B-9C0D1E2F3A4B"));
        Assert.False(ReportCanonical.IsDeviceId("6f1c2a9e-3b4d-1e5f-8a7b-9c0d1e2f3a4b"));

        using var restored = SoftwareReportSigner.FromPkcs8(signer.ExportPkcs8());
        Assert.Equal(signer.PublicKey, restored.PublicKey);
        Assert.True(signer.PublicKey.Verify(message, restored.Sign(message)));
    }

    /// <summary>Every request of the <c>server</c> sequences is well-formed and signed by a test key, and every expected answer maps to REPORTING 8.3.</summary>
    [Fact]
    public void ServerSequences()
    {
        var cases = ReportSpec.Vector("server").EnumerateArray().ToList();
        Assert.Equal(4, cases.Count);
        var keys = new[] { "device", "other", "rotated" }.Select(name => ReportSpec.Key(name).PublicKey).ToArray();
        var steps = 0;
        foreach (var item in cases)
        {
            var authority = item.GetProperty("config").Text("authority");
            foreach (var step in item.GetProperty("steps").EnumerateArray())
            {
                steps++;
                var request = step.GetProperty("request");
                var headers = request.GetProperty("headers").Strings();
                Assert.Null(ReportCanonical.MalformedHeader(headers));
                var canonical = ReportCanonical.Build(request.Text("method"), authority, request.Text("path"), headers["X-AgentSpeed-Timestamp"],
                    headers["X-AgentSpeed-Nonce"], headers["X-AgentSpeed-Device"], ReportSpec.Utf8(request.Text("body")));
                var signature = Base64Url.Decode(headers["X-AgentSpeed-Signature"])!;
                Assert.True(keys.Any(k => k.Verify(ReportSpec.Utf8(canonical), signature)), request.Text("path"));

                var expect = step.GetProperty("expect");
                var reply = new ReportReply(expect.GetProperty("status").GetInt32())
                {
                    Result = expect.OptionalText("result"),
                    Code = expect.OptionalText("code"),
                    ErrorUsername = expect.OptionalText("username"),
                    Reason = expect.OptionalText("reason"),
                };
                var (outcome, reaction) = ReportOutcome.Classify(reply, hasPendingKey: false);
                switch (reply.Code)
                {
                    case null:
                        Assert.Equal(new ReportAttemptOutcome.Delivered(null), outcome);
                        break;
                    case "rate_limited":
                        Assert.Equal(new ReportAttemptOutcome.RateLimited(60), outcome);
                        break;
                    case "nonce_replayed":
                        Assert.Equal(new ReportAttemptOutcome.Again(), outcome);
                        break;
                    case "username_mismatch":
                        Assert.Equal(new ReportReaction.Rename("zoe-mbp"), reaction);
                        break;
                    case "unknown_device":
                        Assert.Equal(new ReportReaction.ReRegister(), reaction);
                        break;
                    case "signature_invalid":
                        Assert.Equal(new ReportReaction.RecheckRegistration(), reaction);
                        break;
                    case "timestamp_out_of_window":
                        Assert.Equal(new ReportReaction.CorrectClock(null), reaction);
                        break;
                    case "seq_conflict":
                        Assert.Equal(new ReportReaction.NewRun(), reaction);
                        break;
                    case "version_unsupported":
                        Assert.Equal(new ReportReaction.RefreshDiscovery(true), reaction);
                        break;
                    case "payload_too_large":
                        Assert.Equal(new ReportReaction.RefreshDiscovery(false), reaction);
                        break;
                    case "api_key_required" or "api_key_invalid":
                        Assert.Equal(new ReportReaction.NeedsUser(ReportProblem.ApiKey), reaction);
                        break;
                    case "device_conflict":
                        Assert.Equal(new ReportReaction.NeedsUser(ReportProblem.KeyMismatch), reaction);
                        break;
                    case "username_taken":
                        Assert.Equal(new ReportReaction.NeedsUser(ReportProblem.UsernameTaken), reaction);
                        break;
                    case "report_stale":
                        Assert.Equal(ReportReaction.None, reaction);
                        Assert.Equal(new ReportAttemptOutcome.Rejected(), outcome);
                        break;
                    case "bad_request" or "payload_invalid":
                        Assert.Equal(new ReportReaction.Strike(false), reaction);
                        break;
                    default:
                        Assert.True(outcome is ReportAttemptOutcome.Rejected, reply.Code);
                        break;
                }
            }
        }

        Assert.True(steps > 40);
    }

    [Fact]
    public void Base64UrlIsStrict()
    {
        Assert.Equal("-_8", Base64Url.Encode([0xFB, 0xFF]));
        Assert.Equal(new byte[] { 0xFB, 0xFF }, Base64Url.Decode("-_8"));
        Assert.Null(Base64Url.Decode("-_8="));
        Assert.Null(Base64Url.Decode("-_9"));
        Assert.Null(Base64Url.Decode("+/8"));
        Assert.Null(Base64Url.Decode("A"));
        Assert.Equal(Array.Empty<byte>(), Base64Url.Decode(""));
    }

    [Fact]
    public void InstantsHaveThreeFractionDigits()
    {
        Assert.Equal("2026-10-22T08:15:30.500Z", ReportInstant.Format(1_792_656_930_500));
        Assert.Equal("2026-10-22T08:15:31.000Z", ReportInstant.Format(1_792_656_931_000));
        Assert.Equal("1970-01-01T00:00:00.000Z", ReportInstant.Format(0));
    }

    private static byte[] FlipS(byte[] signature)
    {
        var order = new System.Numerics.BigInteger(Convert.FromHexString("FFFFFFFF00000000FFFFFFFFFFFFFFFFBCE6FAADA7179E84F3B9CAC2FC632551"),
            isUnsigned: true, isBigEndian: true);
        var s = new System.Numerics.BigInteger(signature.AsSpan(32), isUnsigned: true, isBigEndian: true);
        var flipped = (order - s).ToByteArray(isUnsigned: true, isBigEndian: true);
        var result = new byte[64];
        signature.AsSpan(0, 32).CopyTo(result);
        flipped.CopyTo(result.AsSpan(64 - flipped.Length));
        return result;
    }

    private static byte[] DerToP1363(byte[] der)
    {
        var reader = new System.Formats.Asn1.AsnReader(der, System.Formats.Asn1.AsnEncodingRules.DER);
        var sequence = reader.ReadSequence();
        var r = sequence.ReadIntegerBytes().ToArray().SkipWhile(b => b == 0).ToArray();
        var s = sequence.ReadIntegerBytes().ToArray().SkipWhile(b => b == 0).ToArray();
        var output = new byte[64];
        r.CopyTo(output, 32 - r.Length);
        s.CopyTo(output, 64 - s.Length);
        return output;
    }
}
