using System.Text.Json;

namespace AgentSpeed.Presentation.Characters;

/// <summary><c>catalog.json</c> of the <c>characters</c> release (CHARACTERS 2).</summary>
public sealed record CharacterCatalog(IReadOnlyList<CharacterCatalog.Entry> Characters)
{
    public const string FormatName = "agent-speed-character-catalog";
    public const int FormatVersion = 1;
    public const int MaxCatalogBytes = 1 << 20;
    public const long MaxPackBytes = 200L << 20;

    public static Uri Url { get; } = new("https://github.com/zoefix/agent-speed/releases/download/characters/catalog.json");

    /// <summary>Hosts a catalog or pack download may touch, redirects included (NETWORK row Characters).</summary>
    public static IReadOnlySet<string> AllowedHosts { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "github.com", "objects.githubusercontent.com", "release-assets.githubusercontent.com",
    };

    public sealed record Entry
    {
        public required string Id { get; init; }

        public required int PackVersion { get; init; }

        public required CharacterNames Name { get; init; }

        public required IReadOnlyList<string> Vendors { get; init; }

        public required string Accent { get; init; }

        public CharacterArtist? Artist { get; init; }

        public string? MinApp { get; init; }

        public required long Size { get; init; }

        public required string Sha256 { get; init; }

        public required Uri Url { get; init; }

        public Uri? Preview { get; init; }

        public bool IsValid =>
            CharacterId.IsValid(Id) && PackVersion >= 1 && Name.IsComplete && CharacterManifest.Rgb(Accent) is not null
            && Size is >= 1 and <= MaxPackBytes && Sha256.Length == 64 && Sha256.All(Uri.IsHexDigit) && Permits(Url)
            && (Preview is null || Permits(Preview));

        internal static Entry? Read(JsonElement e)
        {
            if (e.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            string? S(string key) => e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            Uri? U(string key) => S(key) is { } text && Uri.TryCreate(text, UriKind.Absolute, out var uri) ? uri : null;
            if (S("id") is not { } id || !e.TryGetProperty("packVersion", out var pv) || pv.ValueKind != JsonValueKind.Number || !pv.TryGetInt32(out var packVersion)
                || !e.TryGetProperty("name", out var n) || CharacterNames.Read(n) is not { } name || CharacterManifest.StringList(e, "vendors") is not { } vendors
                || S("accent") is not { } accent || !e.TryGetProperty("size", out var sz) || sz.ValueKind != JsonValueKind.Number || !sz.TryGetInt64(out var size)
                || S("sha256") is not { } sha || U("url") is not { } url)
            {
                return null;
            }

            return new Entry
            {
                Id = id,
                PackVersion = packVersion,
                Name = name,
                Vendors = vendors,
                Accent = accent,
                Artist = e.TryGetProperty("artist", out var a) ? CharacterArtist.Read(a) : null,
                MinApp = S("minApp"),
                Size = size,
                Sha256 = sha.ToLowerInvariant(),
                Url = url,
                Preview = U("preview"),
            };
        }

        internal void Write(Utf8JsonWriter w)
        {
            w.WriteStartObject();
            w.WriteString("id", Id);
            w.WriteNumber("packVersion", PackVersion);
            w.WritePropertyName("name");
            Name.Write(w);
            w.WriteStartArray("vendors");
            foreach (var v in Vendors)
            {
                w.WriteStringValue(v);
            }

            w.WriteEndArray();
            w.WriteString("accent", Accent);
            if (Artist is not null)
            {
                w.WritePropertyName("artist");
                Artist.Write(w);
            }

            if (MinApp is not null)
            {
                w.WriteString("minApp", MinApp);
            }

            w.WriteNumber("size", Size);
            w.WriteString("sha256", Sha256);
            w.WriteString("url", Url.AbsoluteUri);
            if (Preview is not null)
            {
                w.WriteString("preview", Preview.AbsoluteUri);
            }

            w.WriteEndObject();
        }
    }

    /// <summary>Decodes and keeps only valid entries (unique ids, first wins).</summary>
    /// <exception cref="CharacterPackException">Too large, not JSON, or another format or version.</exception>
    public static CharacterCatalog Load(ReadOnlySpan<byte> utf8)
    {
        if (utf8.Length > MaxCatalogBytes)
        {
            throw new CharacterPackException(CharacterPackErrorKind.TooLarge);
        }

        try
        {
            var reader = new Utf8JsonReader(utf8, new JsonReaderOptions { MaxDepth = 16 });
            using var doc = JsonDocument.ParseValue(ref reader);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("format", out var f) || f.ValueKind != JsonValueKind.String || f.GetString() != FormatName
                || !root.TryGetProperty("version", out var v) || v.ValueKind != JsonValueKind.Number || !v.TryGetInt32(out var version) || version != FormatVersion
                || !root.TryGetProperty("characters", out var list) || list.ValueKind != JsonValueKind.Array)
            {
                throw new CharacterPackException(CharacterPackErrorKind.Format);
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            var entries = new List<Entry>();
            foreach (var item in list.EnumerateArray())
            {
                if (Entry.Read(item) is { IsValid: true } entry && seen.Add(entry.Id))
                {
                    entries.Add(entry);
                }
            }

            return new CharacterCatalog(entries);
        }
        catch (JsonException ex)
        {
            throw new CharacterPackException("The catalog is not JSON.", ex);
        }
    }

    /// <summary>HTTPS on the release hosts, default port, no user information.</summary>
    public static bool Permits(Uri? url) =>
        url is { IsAbsoluteUri: true } && url.Scheme == Uri.UriSchemeHttps && url.Port == 443 && url.UserInfo.Length == 0
        && url.IdnHost.Length > 0 && AllowedHosts.Contains(url.IdnHost.ToLowerInvariant());

    public Entry? Find(string id) => Characters.FirstOrDefault(c => c.Id == id);

    public byte[] ToJson()
    {
        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteString("format", FormatName);
            w.WriteNumber("version", FormatVersion);
            w.WriteStartArray("characters");
            foreach (var c in Characters)
            {
                c.Write(w);
            }

            w.WriteEndArray();
            w.WriteEndObject();
        }

        return buffer.ToArray();
    }

    public bool Equals(CharacterCatalog? other) => other is not null && Characters.SequenceEqual(other.Characters);

    public override int GetHashCode() => Characters.Count;
}
