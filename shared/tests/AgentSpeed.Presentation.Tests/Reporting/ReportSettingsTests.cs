using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using AgentSpeed.Core.Reporting;
using AgentSpeed.Presentation.Settings;

namespace AgentSpeed.Presentation.Tests.Reporting;

public class ReportSettingsTests
{
    /// <summary>Codex Radar's live document (2026-10-23): the API on a sibling host with a path prefix, a retired section name.</summary>
    internal const string CodexRadarDocument = """
        {"protocol":"agent-speed-report","versions":[1],
         "service":{"name":"速蹬雷达","description":"社区贡献的实时速蹬天梯、全站吞吐与模型实测均速。",
          "homepage":"https://deng.codexradar.com/","terms":"https://deng.codexradar.com/agent-speed/terms/",
          "privacy":"https://deng.codexradar.com/agent-speed/privacy/","leaderboard":"https://deng.codexradar.com/",
          "retentionDays":0},
         "api":"https://speed-api.codexradar.com/api/agent-speed","apiKey":{"mode":"none"},
         "username":{"minLength":2,"maxLength":32,"charset":"unicode","spaces":true},
         "limits":{"intervalSeconds":5,"heartbeatSeconds":60,"maxAgeSeconds":30,"maxBodyBytes":16384,"maxModels":16},
         "sections":["sources","models","cost","traffic","machineTraffic"]}
        """;

    /// <summary>A reference-server-style document over loopback http, with API keys and narrower username rules.</summary>
    internal const string LoopbackDocument = """
        {"protocol":"agent-speed-report","versions":[1],
         "service":{"name":"Agent Speed reference server","terms":"https://example.com/terms","privacy":"https://example.com/privacy",
          "contact":"ops@example.com","retentionDays":30},
         "api":"http://127.0.0.1:8787/agent-speed","apiKey":{"mode":"required","url":"https://example.com/keys"},
         "username":{"minLength":3,"maxLength":16,"charset":"ascii","spaces":false},
         "limits":{"intervalSeconds":10,"heartbeatSeconds":120,"maxAgeSeconds":20,"maxBodyBytes":8192,"maxModels":4},
         "sections":["sources","cost"]}
        """;

    private const string DeviceA = "6f1c2a9e-3b4d-4e5f-8a7b-9c0d1e2f3a4b";
    private const string DeviceB = "0b7e4d2c-9a1f-4c3e-b5d6-7e8f9a0b1c2d";

    private static readonly DateTimeOffset T0 = new(2026, 10, 23, 8, 15, 30, 123, TimeSpan.Zero);

    internal static ReportServiceUrl Service(string url) => ReportServiceUrl.Parse(url) ?? throw new InvalidOperationException(url);

    internal static ReportDiscovery Document(string json, string service) =>
        ReportDiscovery.Parse(Encoding.UTF8.GetBytes(json), Service(service), out var problem) ?? throw new InvalidOperationException(problem?.ToString());

    private static ReportIdentity Radar()
    {
        var discovery = Document(CodexRadarDocument, "https://deng.codexradar.com");
        return new ReportIdentity
        {
            Service = Service("https://deng.codexradar.com"),
            DeviceId = DeviceA,
            Username = "Zoes MacBook Pro",
            ProfileUrl = "https://deng.codexradar.com/u/zoe",
            Pin = ReportConsentPin.Of(discovery),
            Discovery = discovery,
            DiscoveryFetchedAt = T0,
            ETag = "\"abc-123\"",
            KeyCreatedAt = T0.AddDays(-10),
            PendingKey = true,
            HasApiKey = false,
            LastUsedAt = T0.AddMinutes(-3),
        };
    }

    private static ReportIdentity Loopback()
    {
        var discovery = Document(LoopbackDocument, "http://127.0.0.1:8787");
        return new ReportIdentity
        {
            Service = Service("http://127.0.0.1:8787"),
            DeviceId = DeviceB,
            Discovery = discovery,
            KeyCreatedAt = T0,
            HasApiKey = true,
            LastUsedAt = T0,
        };
    }

    private static ReportPreferences Full(DateTimeOffset? paused, ReportInviteAnswer? invite, string? username) => new()
    {
        Enabled = true,
        Preset = ReportPreset.Custom,
        CustomUrl = "http://127.0.0.1:8787",
        Sections = [ReportSection.Sources, ReportSection.Traffic],
        PausedUntil = paused,
        Identities = [Radar(), Loopback()],
        Invite = invite,
        Username = username,
    };

    private static ReportPreferences RoundTrip(ReportPreferences report) =>
        SettingsSerializer.Deserialize(SettingsSerializer.Serialize(AppSettings.Default with { Report = report })).Report;

    private static void AssertSameIdentity(ReportIdentity expected, ReportIdentity actual)
    {
        Assert.Equal(expected.Service, actual.Service);
        Assert.Equal(expected.DeviceId, actual.DeviceId);
        Assert.Equal(expected.Username, actual.Username);
        Assert.Equal(expected.ProfileUrl, actual.ProfileUrl);
        Assert.Equal(expected.Pin, actual.Pin);
        Assert.Equal(expected.Discovery?.ToJson().ToCompactString(), actual.Discovery?.ToJson().ToCompactString());
        Assert.Equal(expected.DiscoveryFetchedAt, actual.DiscoveryFetchedAt);
        Assert.Equal(expected.ETag, actual.ETag);
        Assert.Equal(expected.KeyCreatedAt, actual.KeyCreatedAt);
        Assert.Equal(expected.PendingKey, actual.PendingKey);
        Assert.Equal(expected.HasApiKey, actual.HasApiKey);
        Assert.Equal(expected.LastUsedAt, actual.LastUsedAt);
        Assert.Equal(expected.Id, actual.Id);
    }

    public static TheoryData<int, int, string?> Variants => new()
    {
        { 0, 0, null },
        { 1, 1, "racer-7k3m9q" },
        { 2, 2, "小明的 Mac" },
    };

    [Theory]
    [MemberData(nameof(Variants))]
    public void PreferencesRoundTripThroughTheSettingsFile(int pausedCase, int inviteCase, string? username)
    {
        DateTimeOffset? paused = pausedCase switch
        {
            0 => null,
            1 => T0.AddHours(1),
            _ => DateTimeOffset.MaxValue,
        };
        ReportInviteAnswer? invite = inviteCase switch
        {
            0 => null,
            1 => ReportInviteAnswer.Accepted,
            _ => ReportInviteAnswer.Declined,
        };
        var original = Full(paused, invite, username);
        var loaded = RoundTrip(original);

        Assert.Equal(original.Enabled, loaded.Enabled);
        Assert.Equal(original.Preset, loaded.Preset);
        Assert.Equal(original.CustomUrl, loaded.CustomUrl);
        Assert.Equal(original.Sections.ToArray(), loaded.Sections.ToArray());
        Assert.Equal(original.PausedUntil, loaded.PausedUntil);
        Assert.Equal(original.Invite, loaded.Invite);
        Assert.Equal(original.Username, loaded.Username);
        Assert.Equal(2, loaded.Identities.Length);
        AssertSameIdentity(original.Identities[0], loaded.Identities[0]);
        AssertSameIdentity(original.Identities[1], loaded.Identities[1]);
    }

    [Fact]
    public void StoredDiscoveryKeepsWhatTheClientUnderstood()
    {
        var loaded = RoundTrip(Full(null, null, null));
        var radar = loaded.Identities[0].Discovery!;
        Assert.Equal("速蹬雷达", radar.Name);
        Assert.Equal("https://speed-api.codexradar.com/api/agent-speed", radar.Api.Text);
        Assert.Equal(0, radar.RetentionDays);
        Assert.Equal(ReportSections.All.ToHashSet(), radar.Sections.ToHashSet());

        var loopback = loaded.Identities[1].Discovery!;
        Assert.True(loopback.Api.IsLoopbackHttp);
        Assert.Equal(ReportApiKeyMode.Required, loopback.ApiKeyMode);
        Assert.Equal("https://example.com/keys", loopback.ApiKeyUrl);
        Assert.Equal(new ReportUsernameRules(3, 16, ReportUsernameCharset.Ascii, spaces: false), loopback.UsernameRules);
        Assert.Equal(new ReportLimits(10, 120, 20, 8192, 4), loopback.Limits);
        Assert.Equal(new HashSet<ReportSection> { ReportSection.Sources, ReportSection.Cost }, loopback.Sections.ToHashSet());
        Assert.Equal("ops@example.com", loopback.Contact);
    }

    [Fact]
    public void SettingsFileNeverHoldsSecrets()
    {
        var json = SettingsSerializer.Serialize(AppSettings.Default with { Report = Full(null, ReportInviteAnswer.Accepted, null) });
        using var document = JsonDocument.Parse(json);
        var report = document.RootElement.GetProperty("report");
        var names = new List<(string Name, JsonValueKind Kind)>();
        Collect(report, names);

        string[] secretNames = ["d", "privateKey", "pkcs8", "key", "secret", "token", "authorization", "signature"];
        Assert.DoesNotContain(names, n => secretNames.Contains(n.Name, StringComparer.OrdinalIgnoreCase));
        // apiKey appears only as the stored discovery document's {mode, url} object, never as a value.
        Assert.DoesNotContain(names, n => string.Equals(n.Name, "apiKey", StringComparison.OrdinalIgnoreCase) && n.Kind != JsonValueKind.Object);
        Assert.Contains(names, n => n.Name == "hasApiKey" && n.Kind is JsonValueKind.True or JsonValueKind.False);
        Assert.DoesNotContain("Bearer", json, StringComparison.Ordinal);
    }

    private static void Collect(JsonElement element, List<(string, JsonValueKind)> names)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    names.Add((property.Name, property.Value.ValueKind));
                    Collect(property.Value, names);
                }

                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    Collect(item, names);
                }

                break;
        }
    }

    [Fact]
    public void MissingReportObjectGivesTheDefaults()
    {
        var settings = SettingsSerializer.Deserialize("""{"version":2}""");
        Assert.Equal(ReportPreferences.Default, settings.Report);
        Assert.False(settings.Report.Enabled);
        Assert.Equal(ReportPreset.CodexRadar, settings.Report.Preset);
        Assert.Equal(ReportPreferences.DefaultSections.ToArray(), settings.Report.Sections.ToArray());
        Assert.Null(settings.Report.Invite);
        Assert.Empty(settings.Report.Identities);
        Assert.Equal(ReportPreferences.Default, ReportSettingsJson.Read(null));
    }

    [Theory]
    [InlineData("""{"report":"nope"}""")]
    [InlineData("""{"report":[1,2]}""")]
    [InlineData("""{"report":{"enabled":"yes","preset":7,"sections":"all","identities":{},"invite":"maybe","username":5}}""")]
    public void InvalidReportMembersFallBackToTheirDefaults(string json)
    {
        var report = SettingsSerializer.Deserialize(json).Report;
        Assert.False(report.Enabled);
        Assert.Equal(ReportPreset.CodexRadar, report.Preset);
        Assert.Equal(ReportPreferences.DefaultSections.ToArray(), report.Sections.ToArray());
        Assert.Empty(report.Identities);
        Assert.Null(report.Invite);
        Assert.Null(report.Username);
    }

    [Fact]
    public void UnknownSectionNamesAreIgnored()
    {
        var report = SettingsSerializer.Deserialize("""{"report":{"sections":["machineTraffic","cost","models","bogus"]}}""").Report;
        Assert.Equal(new[] { ReportSection.Models, ReportSection.Cost }, report.Sections.ToArray());
        var none = SettingsSerializer.Deserialize("""{"report":{"sections":[]}}""").Report;
        Assert.Empty(none.Sections);
    }

    [Fact]
    public void InvalidIdentitiesAreDropped()
    {
        const string json = """
            {"report":{"identities":[
              {"service":"https://a.example.com","deviceId":"6f1c2a9e-3b4d-4e5f-8a7b-9c0d1e2f3a4b","keyCreatedAt":"2026-10-23T08:00:00Z","lastUsedAt":"2026-10-23T08:00:00Z"},
              {"service":"https://b.example.com","deviceId":"6F1C2A9E-3B4D-4E5F-8A7B-9C0D1E2F3A4B"},
              {"service":"https://c.example.com","deviceId":"../../etc/passwd"},
              {"service":"http://evil.example.com","deviceId":"0b7e4d2c-9a1f-4c3e-b5d6-7e8f9a0b1c2d"},
              {"service":"https://d.example.com"},
              {"deviceId":"0b7e4d2c-9a1f-4c3e-b5d6-7e8f9a0b1c2d"},
              "text",
              {"service":"https://e.example.com","deviceId":"5a6b7c8d-9e0f-4a1b-8c2d-3e4f5a6b7c8d","username":"","profileUrl":"http://x.example.com","etag":5}
            ]}}
            """;
        var report = SettingsSerializer.Deserialize(json).Report;
        Assert.Equal(new[] { "https://a.example.com", "https://e.example.com" }, report.Identities.Select(i => i.Service.Text));
        var e = report.Identities[1];
        Assert.Null(e.Username);
        Assert.Null(e.ProfileUrl);
        Assert.Null(e.ETag);
        Assert.Null(e.Pin);
        Assert.Null(e.Discovery);
    }

    [Fact]
    public void ACorruptDiscoveryKeepsTheIdentityWithoutIt()
    {
        const string json = """
            {"report":{"identities":[
              {"service":"https://deng.codexradar.com","deviceId":"6f1c2a9e-3b4d-4e5f-8a7b-9c0d1e2f3a4b","username":"zoe",
               "discovery":{"protocol":"agent-speed-report","versions":[1],"service":{"name":"X","terms":"http://insecure","privacy":"https://x.example.com/p"},"api":"https://x.example.com"}},
              {"service":"https://f.example.com","deviceId":"0b7e4d2c-9a1f-4c3e-b5d6-7e8f9a0b1c2d","discovery":"garbage"}
            ]}}
            """;
        var report = SettingsSerializer.Deserialize(json).Report;
        Assert.Equal(2, report.Identities.Length);
        Assert.All(report.Identities, i => Assert.Null(i.Discovery));
        Assert.Equal("zoe", report.Identities[0].Username);
        Assert.True(report.Identities[0].IsRegistered);
    }

    [Fact]
    public void ALoopbackApiStoredForAnHttpsServiceIsRejectedOnLoad()
    {
        const string json = """
            {"report":{"identities":[
              {"service":"https://deng.codexradar.com","deviceId":"6f1c2a9e-3b4d-4e5f-8a7b-9c0d1e2f3a4b",
               "discovery":{"protocol":"agent-speed-report","versions":[1],"service":{"name":"X","terms":"https://x.example.com/t","privacy":"https://x.example.com/p"},"api":"http://127.0.0.1:8787"}}
            ]}}
            """;
        Assert.Null(SettingsSerializer.Deserialize(json).Report.Identities[0].Discovery);
    }

    [Fact]
    public void AtMostEightIdentitiesAreLoaded()
    {
        var items = Enumerable.Range(1, 11).Select(i =>
            $$"""{"service":"https://s{{i}}.example.com","deviceId":"{{Device(i)}}"}""");
        var json = "{\"report\":{\"identities\":[" + string.Join(',', items) + "]}}";
        var report = SettingsSerializer.Deserialize(json).Report;
        Assert.Equal(ReportPreferences.MaxIdentities, report.Identities.Length);
        Assert.Equal(Enumerable.Range(1, 8).Select(i => $"https://s{i}.example.com"), report.Identities.Select(i => i.Service.Text));
    }

    [Fact]
    public void DuplicateOriginsKeepTheFirst()
    {
        var json = "{\"report\":{\"identities\":[{\"service\":\"https://a.example.com\",\"deviceId\":\"" + Device(1)
            + "\"},{\"service\":\"https://a.example.com/\",\"deviceId\":\"" + Device(2) + "\"}]}}";
        var report = SettingsSerializer.Deserialize(json).Report;
        Assert.Equal(Device(1), Assert.Single(report.Identities).DeviceId);
    }

    [Fact]
    public void PausedUntilIndefiniteAndTimesParse()
    {
        Assert.Equal(DateTimeOffset.MaxValue,
            SettingsSerializer.Deserialize("""{"report":{"pausedUntil":"indefinite"}}""").Report.PausedUntil);
        Assert.Equal(T0, SettingsSerializer.Deserialize("""{"report":{"pausedUntil":"2026-10-23T08:15:30.1230000+00:00"}}""").Report.PausedUntil);
        Assert.Null(SettingsSerializer.Deserialize("""{"report":{"pausedUntil":"soon"}}""").Report.PausedUntil);
        Assert.Null(SettingsSerializer.Deserialize("""{"report":{"pausedUntil":"1900-01-01T00:00:00Z"}}""").Report.PausedUntil);
    }

    [Fact]
    public void ValidatedKeepsTheSameInstanceWhenNothingChanges()
    {
        var report = Full(T0, ReportInviteAnswer.Declined, "zoe");
        Assert.Same(report, report.Validated());
        Assert.Same(ReportPreferences.Default, ReportPreferences.Default.Validated());

        var settings = AppSettings.Default with { Report = report };
        var once = settings.Validated();
        var twice = once.Validated();
        Assert.Same(report, once.Report);
        Assert.Same(report, twice.Report);
        // OrbAnchors is rebuilt by every Validated() (an existing quirk outside live reporting); apart from it the records are equal.
        Assert.Equal(once with { OrbAnchors = twice.OrbAnchors }, twice);
    }

    [Fact]
    public void ValidatedFixesWhatIsWrong()
    {
        var bad = new ReportIdentity { Service = Service("https://a.example.com"), DeviceId = "not-a-uuid" };
        var report = ReportPreferences.Default with
        {
            Identities = [bad, Radar(), Radar() with { DeviceId = DeviceB }],
            Username = new string('x', 200),
            CustomUrl = "https://" + new string('a', 3000),
            Preset = (ReportPreset)42,
        };
        var fixedReport = report.Validated();
        Assert.NotSame(report, fixedReport);
        Assert.Equal(DeviceA, Assert.Single(fixedReport.Identities).DeviceId);
        Assert.Null(fixedReport.Username);
        Assert.Equal(string.Empty, fixedReport.CustomUrl);
        Assert.Equal(ReportPreset.CodexRadar, fixedReport.Preset);
        Assert.Same(fixedReport, fixedReport.Validated());
    }

    [Fact]
    public void EqualityComparesSectionsAndIdentitiesByValue()
    {
        var radar = Radar();
        var a = ReportPreferences.Default with { Sections = [ReportSection.Cost], Identities = [radar] };
        var b = ReportPreferences.Default with { Sections = ImmutableArray.Create(ReportSection.Cost), Identities = ImmutableArray.Create(radar) };
        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.True(a == b);
        Assert.NotEqual(a, b with { Sections = [ReportSection.Models] });
        Assert.NotEqual(a, b with { Identities = [radar with { Username = "other" }] });
        Assert.NotEqual(a, b with { PausedUntil = T0 });
        Assert.NotEqual(a, b with { Invite = ReportInviteAnswer.Accepted });
        Assert.Equal(AppSettings.Default with { Report = a }, AppSettings.Default with { Report = b });
    }

    [Fact]
    public void WithSectionKeepsSectionOrder()
    {
        var none = ReportPreferences.Default with { Sections = [] };
        var report = none.WithSection(ReportSection.Traffic, true).WithSection(ReportSection.Sources, true).WithSection(ReportSection.Cost, true);
        Assert.Equal(new[] { ReportSection.Sources, ReportSection.Cost, ReportSection.Traffic }, report.Sections.ToArray());
        Assert.Equal(new[] { ReportSection.Sources, ReportSection.Traffic }, report.WithSection(ReportSection.Cost, false).Sections.ToArray());
        Assert.Equal(report.Sections.ToArray(), report.WithSection(ReportSection.Cost, true).Sections.ToArray());
        Assert.True(report.IsSectionOn(ReportSection.Traffic));
        Assert.False(report.IsSectionOn(ReportSection.Models));
    }

    [Fact]
    public void IsPausedFollowsTheEnd()
    {
        Assert.False(ReportPreferences.Default.IsPaused(T0));
        var hour = ReportPreferences.Default with { PausedUntil = T0.AddHours(1) };
        Assert.True(hour.IsPaused(T0));
        Assert.False(hour.IsPaused(T0.AddHours(1)));
        Assert.True((ReportPreferences.Default with { PausedUntil = DateTimeOffset.MaxValue }).IsPaused(T0.AddYears(50)));
    }

    [Fact]
    public void UpsertReplacesItsOriginAndForgetsTheOldestUnusedBeyondEight()
    {
        var report = ReportPreferences.Default;
        for (var i = 1; i <= 8; i++)
        {
            report = report.Upsert(Identity(i, T0.AddMinutes(i)), out var none);
            Assert.Empty(none);
        }

        Assert.Equal(8, report.Identities.Length);

        // The same origin replaces in place.
        var renamed = report.Upsert(Identity(3, T0.AddMinutes(3)) with { Username = "renamed" }, out var replaced);
        Assert.Empty(replaced);
        Assert.Equal(8, renamed.Identities.Length);
        Assert.Equal("renamed", renamed.Identities[2].Username);

        // A ninth forgets the least recently used one (s1), never the one just added.
        var ninth = report.Upsert(Identity(9, T0.AddYears(-1)), out var dropped);
        Assert.Equal(Device(1), Assert.Single(dropped));
        Assert.Equal(8, ninth.Identities.Length);
        Assert.DoesNotContain(ninth.Identities, i => i.DeviceId == Device(1));
        Assert.Contains(ninth.Identities, i => i.DeviceId == Device(9));
    }

    [Fact]
    public void UpdatingAndRemovingAffectOnlyTheirOrigin()
    {
        var report = ReportPreferences.Default with { Identities = [Identity(1, T0), Identity(2, T0)] };
        var updated = report.Updating(Identity(2, T0).Id, i => i with { Username = "two" });
        Assert.Null(updated.Identities[0].Username);
        Assert.Equal("two", updated.Identities[1].Username);
        var removed = updated.Removing(Identity(1, T0).Id);
        Assert.Equal(Device(2), Assert.Single(removed.Identities).DeviceId);
        Assert.True(removed.HasRegisteredIdentity);
        Assert.False(report.HasRegisteredIdentity);
        Assert.Equal(Device(2), removed.Identity(Service("https://s2.example.com/"))?.DeviceId);
        Assert.Null(removed.Identity(null));
    }

    private static ReportIdentity Identity(int i, DateTimeOffset lastUsed) => new()
    {
        Service = Service($"https://s{i}.example.com"),
        DeviceId = Device(i),
        KeyCreatedAt = T0,
        LastUsedAt = lastUsed,
    };

    private static string Device(int i) => $"00000000-0000-4000-8000-{i:D12}";
}
