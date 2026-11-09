using System.Globalization;
using System.Text.Json;
using AgentSpeed.Presentation.Updates;

namespace AgentSpeed.Presentation.Characters;

/// <summary>Pack ids (CHARACTERS 1): lowercase letters, digits and dashes, 1–64 characters, not starting or ending with a dash.</summary>
public static class CharacterId
{
    public static bool IsValid(string? id) =>
        id is { Length: >= 1 and <= 64 } && !id.StartsWith('-') && !id.EndsWith('-') && id.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-');
}

/// <summary>The speed bands a pack can hold a pose for (CHARACTERS 1); a missing band uses the nearest lower one.</summary>
public static class CharacterBands
{
    public static IReadOnlyList<string> All { get; } = ["idle", "cruise", "boost", "overdrive", "redline"];

    public static int Order(string band) => All.ToList().IndexOf(band);
}

/// <summary>Display names of a pack in the four app languages.</summary>
public sealed record CharacterNames(string ZhHant, string ZhHans, string En, string Ja)
{
    internal bool IsComplete => new[] { ZhHant, ZhHans, En, Ja }.All(n => !string.IsNullOrWhiteSpace(n));

    /// <summary>The name in an app language code (<c>zh-Hant</c>, <c>zh-Hans</c>, <c>en</c>, <c>ja</c>); English otherwise.</summary>
    public string For(string languageCode) => languageCode switch
    {
        "zh-Hant" => ZhHant,
        "zh-Hans" => ZhHans,
        "ja" => Ja,
        _ => En,
    };

    internal static CharacterNames? Read(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        string? S(string key) => e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        return S("zh-Hant") is { } a && S("zh-Hans") is { } b && S("en") is { } c && S("ja") is { } d ? new CharacterNames(a, b, c, d) : null;
    }

    internal void Write(Utf8JsonWriter w)
    {
        w.WriteStartObject();
        w.WriteString("zh-Hant", ZhHant);
        w.WriteString("zh-Hans", ZhHans);
        w.WriteString("en", En);
        w.WriteString("ja", Ja);
        w.WriteEndObject();
    }
}

public sealed record CharacterArtist(string Name, string? Url = null)
{
    /// <summary>The credit link, only when it is an https URL.</summary>
    public Uri? Link => Url is not null && Uri.TryCreate(Url, UriKind.Absolute, out var link) && link.Scheme == Uri.UriSchemeHttps && link.Host.Length > 0
        ? link
        : null;

    internal static CharacterArtist? Read(JsonElement e) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String
            ? new CharacterArtist(n.GetString()!, e.TryGetProperty("url", out var u) && u.ValueKind == JsonValueKind.String ? u.GetString() : null)
            : null;

    internal void Write(Utf8JsonWriter w)
    {
        w.WriteStartObject();
        w.WriteString("name", Name);
        if (Url is not null)
        {
            w.WriteString("url", Url);
        }

        w.WriteEndObject();
    }
}

public enum CharacterPackErrorKind
{
    Format,
    InvalidId,
    MissingIdle,
    UnsafePath,
    SymbolicLink,
    MissingFile,
    TooLarge,
    NewerApp,
    Mismatch,
}

/// <summary>Why a pack, a catalog or an archive is refused.</summary>
public sealed class CharacterPackException : Exception
{
    public CharacterPackException()
    {
    }

    public CharacterPackException(string message)
        : base(message)
    {
    }

    public CharacterPackException(string message, Exception inner)
        : base(message, inner)
    {
    }

    public CharacterPackException(CharacterPackErrorKind kind, string detail = "")
        : base(detail.Length > 0 ? $"{kind}: {detail}" : kind.ToString())
    {
        Kind = kind;
        Detail = detail;
    }

    public CharacterPackErrorKind Kind { get; }

    public string Detail { get; } = string.Empty;
}

/// <summary><c>manifest.json</c> of a pack (CHARACTERS 1).</summary>
public sealed record CharacterManifest
{
    public const string FormatName = "agent-speed-character-pack";
    public const int FormatVersion = 1;

    public string Format { get; init; } = FormatName;

    public int Version { get; init; } = FormatVersion;

    public required string Id { get; init; }

    public required int PackVersion { get; init; }

    public required CharacterNames Name { get; init; }

    public required IReadOnlyList<string> Vendors { get; init; }

    public required string Accent { get; init; }

    public CharacterArtist? Artist { get; init; }

    public string? License { get; init; }

    public string? MinApp { get; init; }

    public required IReadOnlyDictionary<string, string> Poses { get; init; }

    public string Preview { get; init; } = "preview.png";

    /// <summary>Checks everything a player relies on; <paramref name="appVersion"/> (<c>x.y.z</c>) rejects packs that need a newer app.</summary>
    /// <exception cref="CharacterPackException">The manifest is refused.</exception>
    public void Validate(string? appVersion)
    {
        if (Format != FormatName || Version != FormatVersion)
        {
            throw new CharacterPackException(CharacterPackErrorKind.Format);
        }

        if (!CharacterId.IsValid(Id) || PackVersion < 1 || !Name.IsComplete || Rgb(Accent) is null)
        {
            throw new CharacterPackException(CharacterPackErrorKind.InvalidId);
        }

        if (!Poses.ContainsKey("idle"))
        {
            throw new CharacterPackException(CharacterPackErrorKind.MissingIdle);
        }

        foreach (var (band, path) in Poses)
        {
            if (CharacterBands.Order(band) < 0)
            {
                throw new CharacterPackException(CharacterPackErrorKind.Format);
            }

            if (!CharacterArchive.IsSafe(path))
            {
                throw new CharacterPackException(CharacterPackErrorKind.UnsafePath, path);
            }
        }

        if (!CharacterArchive.IsSafe(Preview))
        {
            throw new CharacterPackException(CharacterPackErrorKind.UnsafePath, Preview);
        }

        if (MinApp is not null && SemanticVersion.ParseOrNull(MinApp) is { } needed && SemanticVersion.ParseOrNull(appVersion) is { } current
            && current < needed && !current.IsDevelopment)
        {
            throw new CharacterPackException(CharacterPackErrorKind.NewerApp, MinApp);
        }
    }

    /// <summary>The pose folder of a band: its own, else the nearest lower band's (idle always exists after validation).</summary>
    public string PoseFor(string band)
    {
        var order = CharacterBands.Order(band);
        for (var i = CharacterBands.All.Count - 1; i >= 0; i--)
        {
            if (i <= order && Poses.TryGetValue(CharacterBands.All[i], out var path))
            {
                return path;
            }
        }

        return Poses.TryGetValue("idle", out var idle) ? idle : "poses/idle";
    }

    public (double R, double G, double B)? AccentRgb => Rgb(Accent);

    /// <summary><c>#RRGGBB</c> as 0...1 components; null for anything else.</summary>
    public static (double R, double G, double B)? Rgb(string? text)
    {
        if (text is not { Length: 7 } || text[0] != '#'
            || !uint.TryParse(text.AsSpan(1), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var value))
        {
            return null;
        }

        return (((value >> 16) & 0xFF) / 255.0, ((value >> 8) & 0xFF) / 255.0, (value & 0xFF) / 255.0);
    }

    /// <exception cref="JsonException">Not a manifest: a required key is missing or of the wrong type.</exception>
    public static CharacterManifest Decode(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("Expected an object.");
        }

        string Required(string key) =>
            e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : throw new JsonException($"Missing {key}.");
        int Integer(string key) =>
            e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : throw new JsonException($"Missing {key}.");
        string? Optional(string key) => e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        var poses = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!e.TryGetProperty("poses", out var p) || p.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("Missing poses.");
        }

        foreach (var pose in p.EnumerateObject())
        {
            poses[pose.Name] = pose.Value.ValueKind == JsonValueKind.String ? pose.Value.GetString()! : throw new JsonException("Bad pose.");
        }

        return new CharacterManifest
        {
            Format = Required("format"),
            Version = Integer("version"),
            Id = Required("id"),
            PackVersion = Integer("packVersion"),
            Name = e.TryGetProperty("name", out var n) && CharacterNames.Read(n) is { } names ? names : throw new JsonException("Missing name."),
            Vendors = StringList(e, "vendors") ?? throw new JsonException("Missing vendors."),
            Accent = Required("accent"),
            Artist = e.TryGetProperty("artist", out var a) ? CharacterArtist.Read(a) : null,
            License = Optional("license"),
            MinApp = Optional("minApp"),
            Poses = poses,
            Preview = Required("preview"),
        };
    }

    internal static string[]? StringList(JsonElement e, string key)
    {
        if (!e.TryGetProperty(key, out var v) || v.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var list = new List<string>();
        foreach (var item in v.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            list.Add(item.GetString()!);
        }

        return list.ToArray();
    }

    public void Write(Utf8JsonWriter w)
    {
        ArgumentNullException.ThrowIfNull(w);
        w.WriteStartObject();
        w.WriteString("format", Format);
        w.WriteNumber("version", Version);
        w.WriteString("id", Id);
        w.WriteNumber("packVersion", PackVersion);
        w.WritePropertyName("name");
        Name.Write(w);
        w.WriteStartArray("vendors");
        foreach (var vendor in Vendors)
        {
            w.WriteStringValue(vendor);
        }

        w.WriteEndArray();
        w.WriteString("accent", Accent);
        if (Artist is not null)
        {
            w.WritePropertyName("artist");
            Artist.Write(w);
        }

        if (License is not null)
        {
            w.WriteString("license", License);
        }

        if (MinApp is not null)
        {
            w.WriteString("minApp", MinApp);
        }

        w.WriteStartObject("poses");
        foreach (var (band, path) in Poses)
        {
            w.WriteString(band, path);
        }

        w.WriteEndObject();
        w.WriteString("preview", Preview);
        w.WriteEndObject();
    }

    public byte[] ToJson()
    {
        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer))
        {
            Write(w);
        }

        return buffer.ToArray();
    }
}
