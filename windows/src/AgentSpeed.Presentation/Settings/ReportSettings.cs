using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using AgentSpeed.Core.Reporting;

namespace AgentSpeed.Presentation.Settings;

/// <summary>The service presets of DESIGN 11: Codex Radar, then a custom URL.</summary>
public enum ReportPreset
{
    CodexRadar,
    Custom,
}

/// <summary>How a service's reports travel (DESIGN 11, Transport): WebSocket by default, HTTP by the user's choice.</summary>
public enum ReportTransportKind
{
    Http,
    WebSocket,
}

public static class ReportTransports
{
    /// <summary>
    /// The transport reports actually take (METRICS 11.9, Availability): WebSocket when it is chosen and the confirmed document
    /// offers a valid capability, otherwise HTTP. The choice itself is kept either way.
    /// </summary>
    public static ReportTransportKind Effective(ReportTransportKind chosen, ReportDiscovery? confirmed) =>
        chosen == ReportTransportKind.WebSocket && confirmed?.WebSocket is not null ? ReportTransportKind.WebSocket : ReportTransportKind.Http;
}

/// <summary>
/// One service this device registered with (DESIGN 11, Storage): what the user confirmed, the device id, the registered
/// name and the key's age. Keys themselves live in the key store, never here.
/// </summary>
public sealed record ReportIdentity
{
    public required ReportServiceUrl Service { get; init; }

    public required string DeviceId { get; init; }

    /// <summary>The registered username; null until a registration succeeded.</summary>
    public string? Username { get; init; }

    public string? ProfileUrl { get; init; }

    /// <summary>What the consent sheet (or the invitation) confirmed (REPORTING 3.3).</summary>
    public ReportConsentPin? Pin { get; init; }

    /// <summary>The last valid discovery document and when it was fetched (REPORTING 3.4).</summary>
    public ReportDiscovery? Discovery { get; init; }

    public DateTimeOffset? DiscoveryFetchedAt { get; init; }

    public DateTimeOffset KeyCreatedAt { get; init; }

    /// <summary>A rotated key waits for the service's answer (REPORTING 4.5).</summary>
    public bool PendingKey { get; init; }

    public bool HasApiKey { get; init; }

    public DateTimeOffset LastUsedAt { get; init; }

    public string Id => Service.Origin;

    public bool IsRegistered => Username is not null;
}

/// <summary>Live reporting's settings (DESIGN 11, Storage), persisted as the <c>report</c> object of the settings file.</summary>
public sealed record ReportPreferences
{
    public const int MaxIdentities = 8;

    /// <summary>At most this many services remember the HTTP choice; the oldest choice goes first.</summary>
    public const int MaxHttpServices = 16;
    public const string CodexRadarName = "Codex Radar";
    public const string CodexRadarUrl = "https://deng.codexradar.com";

    /// <summary>The domain the preset covers (REPORTING 3.3): its API may live on any host under it. The invitation names it.</summary>
    public const string CodexRadarDomain = "codexradar.com";

    public static ImmutableArray<ReportSection> DefaultSections { get; } = [.. ReportSections.All];

    public static ReportPreferences Default { get; } = new();

    public bool Enabled { get; init; }

    public ReportPreset Preset { get; init; } = ReportPreset.CodexRadar;

    public string CustomUrl { get; init; } = string.Empty;

    /// <summary>The section switches that are on, in section order.</summary>
    public ImmutableArray<ReportSection> Sections { get; init; } = DefaultSections;

    /// <summary>
    /// The 額度 switch (<c>report.section.quota</c>, REPORTING-EXTENSIONS 11.1): on by default, its own key so that the
    /// sections stored by earlier builds keep it on.
    /// </summary>
    public bool ShareQuota { get; init; } = true;

    /// <summary><see cref="DateTimeOffset.MaxValue"/> pauses until the user resumes.</summary>
    public DateTimeOffset? PausedUntil { get; init; }

    public ImmutableArray<ReportIdentity> Identities { get; init; } = [];

    /// <summary>The dashboard invitation's answer (<c>report.invite</c>); null until the user answers it.</summary>
    public ReportInviteAnswer? Invite { get; init; }

    /// <summary>The name for the next registration when the user typed one; null means the computer name (DESIGN 11, Username).</summary>
    public string? Username { get; init; }

    /// <summary>
    /// The origins of the services whose transport the user set to HTTP, the latest choice last. Every other service uses
    /// WebSocket — the default, also for services set up by earlier builds, whose list of WebSocket origins is ignored.
    /// </summary>
    public ImmutableArray<string> HttpServices { get; init; } = [];

    /// <summary>The chosen transport (the Transport row shows it); see <see cref="EffectiveTransportOf"/> for the one in use.</summary>
    public ReportTransportKind TransportOf(ReportServiceUrl? service) =>
        service is not null && HttpServices.Contains(service.Origin, StringComparer.Ordinal) ? ReportTransportKind.Http : ReportTransportKind.WebSocket;

    /// <summary>The transport reports take: the choice, or HTTP while the service's stored document lacks the capability.</summary>
    public ReportTransportKind EffectiveTransportOf(ReportServiceUrl? service) =>
        ReportTransports.Effective(TransportOf(service), Identity(service)?.Discovery);

    public ReportPreferences WithTransport(string origin, ReportTransportKind transport)
    {
        var others = HttpServices.Where(o => !string.Equals(o, origin, StringComparison.Ordinal));
        var list = transport == ReportTransportKind.Http ? others.Append(origin) : others;
        return this with { HttpServices = [.. list.TakeLast(MaxHttpServices)] };
    }

    public bool HasRegisteredIdentity => Identities.Any(i => i.IsRegistered);

    public bool IsPaused(DateTimeOffset now) => PausedUntil is { } until && until > now;

    public bool IsSectionOn(ReportSection section) => Sections.Contains(section);

    public ReportIdentity? Identity(ReportServiceUrl? service) =>
        service is null ? null : Identities.FirstOrDefault(i => string.Equals(i.Id, service.Origin, StringComparison.Ordinal));

    public ReportPreferences WithSection(ReportSection section, bool on) => this with
    {
        Sections = [.. ReportSections.All.Where(s => s == section ? on : Sections.Contains(s))],
    };

    /// <summary>
    /// Adds or replaces the identity of its origin; beyond <see cref="MaxIdentities"/> the oldest unused one is forgotten
    /// locally first, and its device id is returned so the caller deletes its keys.
    /// </summary>
    public ReportPreferences Upsert(ReportIdentity identity, out IReadOnlyList<string> dropped)
    {
        var list = Identities.ToList();
        var index = list.FindIndex(i => i.Id == identity.Id);
        var removed = new List<string>();
        if (index >= 0)
        {
            list[index] = identity;
        }
        else
        {
            list.Add(identity);
            while (list.Count > MaxIdentities)
            {
                var oldest = list.Where(i => i.Id != identity.Id).MinBy(i => i.LastUsedAt)!;
                list.Remove(oldest);
                removed.Add(oldest.DeviceId);
            }
        }

        dropped = removed;
        return this with { Identities = [.. list] };
    }

    public ReportPreferences Updating(string origin, Func<ReportIdentity, ReportIdentity> change) => this with
    {
        Identities = [.. Identities.Select(i => i.Id == origin ? change(i) : i)],
    };

    public ReportPreferences Removing(string origin) => this with { Identities = [.. Identities.Where(i => i.Id != origin)] };

    /// <summary>The same instance when nothing needs fixing, so equal settings stay equal.</summary>
    public ReportPreferences Validated()
    {
        var valid = Identities.Where(i => ReportCanonical.IsDeviceId(i.DeviceId))
            .GroupBy(i => i.Id, StringComparer.Ordinal).Select(g => g.First()).Take(MaxIdentities).ToArray();
        var name = Username is { Length: > 0 and <= 128 } ? Username : null;
        var url = CustomUrl.Length <= ReportServiceUrl.MaxLength ? CustomUrl : string.Empty;
        var http = HttpServices.Where(o => o is { Length: > 0 } && o.Length <= ReportServiceUrl.MaxLength).Reverse()
            .Distinct(StringComparer.Ordinal).Take(MaxHttpServices).Reverse().ToArray();
        if (valid.Length == Identities.Length && name == Username && url == CustomUrl && Enum.IsDefined(Preset)
            && http.SequenceEqual(HttpServices, StringComparer.Ordinal))
        {
            return this;
        }

        return this with
        {
            Identities = [.. valid],
            Username = name,
            CustomUrl = url,
            Preset = Enum.IsDefined(Preset) ? Preset : ReportPreset.CodexRadar,
            HttpServices = [.. http],
        };
    }

    public bool Equals(ReportPreferences? other) =>
        other is not null && Enabled == other.Enabled && Preset == other.Preset && CustomUrl == other.CustomUrl
        && Sections.SequenceEqual(other.Sections) && ShareQuota == other.ShareQuota && PausedUntil == other.PausedUntil
        && Identities.SequenceEqual(other.Identities) && Invite == other.Invite && Username == other.Username
        && HttpServices.SequenceEqual(other.HttpServices, StringComparer.Ordinal);

    public override int GetHashCode() => HashCode.Combine(Enabled, Preset, CustomUrl, ShareQuota, Identities.Length, Invite, Username, HttpServices.Length);
}

/// <summary>The <c>report</c> object of the settings file. Tolerant: an invalid member falls back to its default.</summary>
public static class ReportSettingsJson
{
    public static void Write(Utf8JsonWriter w, ReportPreferences p)
    {
        ArgumentNullException.ThrowIfNull(w);
        ArgumentNullException.ThrowIfNull(p);
        w.WriteStartObject("report");
        w.WriteBoolean("enabled", p.Enabled);
        w.WriteString("preset", p.Preset == ReportPreset.Custom ? "custom" : "codexRadar");
        w.WriteString("customUrl", p.CustomUrl);
        w.WriteStartArray("sections");
        foreach (var section in p.Sections)
        {
            w.WriteStringValue(ReportSections.Name(section));
        }

        w.WriteEndArray();
        w.WriteBoolean("shareQuota", p.ShareQuota);
        if (p.PausedUntil is { } until)
        {
            w.WriteString("pausedUntil", until == DateTimeOffset.MaxValue ? "indefinite" : Stamp(until));
        }
        else
        {
            w.WriteNull("pausedUntil");
        }

        if (p.Invite is { } invite)
        {
            w.WriteString("invite", invite == ReportInviteAnswer.Accepted ? "accepted" : "declined");
        }
        else
        {
            w.WriteNull("invite");
        }

        if (p.Username is { } name)
        {
            w.WriteString("username", name);
        }
        else
        {
            w.WriteNull("username");
        }

        w.WriteStartArray("httpServices");
        foreach (var origin in p.HttpServices)
        {
            w.WriteStringValue(origin);
        }

        w.WriteEndArray();
        w.WriteStartArray("identities");
        foreach (var identity in p.Identities)
        {
            w.WriteStartObject();
            w.WriteString("service", identity.Service.Text);
            w.WriteString("deviceId", identity.DeviceId);
            WriteNullable(w, "username", identity.Username);
            WriteNullable(w, "profileUrl", identity.ProfileUrl);
            if (identity.Pin is { } pin)
            {
                w.WriteStartObject("pin");
                w.WriteString("name", pin.Name);
                w.WriteString("terms", pin.Terms);
                w.WriteString("privacy", pin.Privacy);
                w.WriteString("api", pin.Api);
                w.WriteStartArray("sections");
                foreach (var section in pin.Sections)
                {
                    w.WriteStringValue(section);
                }

                w.WriteEndArray();
                if (pin.RetentionDays is { } days)
                {
                    w.WriteNumber("retentionDays", days);
                }

                w.WriteString("apiKeyMode", ReportSections.Name(pin.ApiKeyMode));
                w.WriteEndObject();
            }

            if (identity.Discovery is { } discovery)
            {
                w.WritePropertyName("discovery");
                w.WriteRawValue(discovery.ToJson().ToCompactString(), skipInputValidation: false);
            }

            WriteNullable(w, "discoveryFetchedAt", identity.DiscoveryFetchedAt is { } fetched ? Stamp(fetched) : null);
            w.WriteString("keyCreatedAt", Stamp(identity.KeyCreatedAt));
            w.WriteBoolean("pendingKey", identity.PendingKey);
            w.WriteBoolean("hasApiKey", identity.HasApiKey);
            w.WriteString("lastUsedAt", Stamp(identity.LastUsedAt));
            w.WriteEndObject();
        }

        w.WriteEndArray();
        w.WriteEndObject();
    }

    public static ReportPreferences Read(JsonElement? element)
    {
        if (element is not { ValueKind: JsonValueKind.Object } r)
        {
            return ReportPreferences.Default;
        }

        var sections = r.TryGetProperty("sections", out var list) && list.ValueKind == JsonValueKind.Array
            ? ReportSections.All.Where(s => list.EnumerateArray().Any(e => e.ValueKind == JsonValueKind.String && e.GetString() == ReportSections.Name(s)))
                .ToImmutableArray()
            : ReportPreferences.DefaultSections;
        DateTimeOffset? paused = Text(r, "pausedUntil") switch
        {
            "indefinite" => DateTimeOffset.MaxValue,
            { } stamp => ParseStamp(stamp),
            null => null,
        };
        var identities = new List<ReportIdentity>();
        if (r.TryGetProperty("identities", out var items) && items.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in items.EnumerateArray())
            {
                if (ReadIdentity(item) is { } identity)
                {
                    identities.Add(identity);
                }
            }
        }

        return new ReportPreferences
        {
            Enabled = r.TryGetProperty("enabled", out var enabled) && enabled.ValueKind == JsonValueKind.True,
            Preset = Text(r, "preset") == "custom" ? ReportPreset.Custom : ReportPreset.CodexRadar,
            CustomUrl = Text(r, "customUrl") ?? string.Empty,
            Sections = sections,
            // Only an explicit false turns it off: settings of earlier builds have no such member.
            ShareQuota = !(r.TryGetProperty("shareQuota", out var quota) && quota.ValueKind == JsonValueKind.False),
            PausedUntil = paused,
            Identities = [.. identities],
            Invite = Text(r, "invite") switch
            {
                "accepted" => ReportInviteAnswer.Accepted,
                "declined" => ReportInviteAnswer.Declined,
                _ => null,
            },
            Username = Text(r, "username"),
            // `webSocketServices` of earlier builds is not read: those services use the WebSocket default now.
            HttpServices = r.TryGetProperty("httpServices", out var origins) && origins.ValueKind == JsonValueKind.Array
                ? [.. origins.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!)]
                : [],
        }.Validated();
    }

    private static ReportIdentity? ReadIdentity(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object || ReportServiceUrl.Parse(Text(item, "service")) is not { } service
            || Text(item, "deviceId") is not { } deviceId || !ReportCanonical.IsDeviceId(deviceId))
        {
            return null;
        }

        ReportConsentPin? pin = null;
        if (item.TryGetProperty("pin", out var p) && p.ValueKind == JsonValueKind.Object && Text(p, "name") is { } name
            && Text(p, "terms") is { } terms && Text(p, "privacy") is { } privacy && Text(p, "api") is { } api)
        {
            var pinSections = p.TryGetProperty("sections", out var s) && s.ValueKind == JsonValueKind.Array
                ? s.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!).Order(StringComparer.Ordinal).ToArray()
                : [];
            int? retention = p.TryGetProperty("retentionDays", out var days) && days.TryGetInt32(out var value) ? value : null;
            pin = new ReportConsentPin(name, terms, privacy, api, pinSections, retention,
                ReportSections.ParseMode(Text(p, "apiKeyMode")) ?? ReportApiKeyMode.None);
        }

        ReportDiscovery? discovery = null;
        if (item.TryGetProperty("discovery", out var document) && document.ValueKind == JsonValueKind.Object)
        {
            discovery = ReportDiscovery.Parse(System.Text.Encoding.UTF8.GetBytes(document.GetRawText()), service, out _);
        }

        var username = Text(item, "username");
        return new ReportIdentity
        {
            Service = service,
            DeviceId = deviceId,
            Username = username is { Length: > 0 and <= 128 } ? username : null,
            ProfileUrl = ReportUrls.HttpsLink(Text(item, "profileUrl")),
            Pin = pin,
            Discovery = discovery,
            DiscoveryFetchedAt = Text(item, "discoveryFetchedAt") is { } fetched ? ParseStamp(fetched) : null,
            KeyCreatedAt = ParseStamp(Text(item, "keyCreatedAt")) ?? DateTimeOffset.UtcNow,
            PendingKey = item.TryGetProperty("pendingKey", out var pending) && pending.ValueKind == JsonValueKind.True,
            HasApiKey = item.TryGetProperty("hasApiKey", out var hasKey) && hasKey.ValueKind == JsonValueKind.True,
            LastUsedAt = ParseStamp(Text(item, "lastUsedAt")) ?? DateTimeOffset.UtcNow,
        };
    }

    private static void WriteNullable(Utf8JsonWriter w, string name, string? value)
    {
        if (value is null)
        {
            w.WriteNull(name);
        }
        else
        {
            w.WriteString(name, value);
        }
    }

    private static string? Text(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        try
        {
            return value.GetString();
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static string Stamp(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset? ParseStamp(string? text) =>
        DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var value)
            && value.Year is >= 2000 and <= 9000
            ? value
            : null;
}
