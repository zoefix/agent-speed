using System.Globalization;
using System.Text.Json;

namespace AgentSpeed.Presentation.Character;

/// <summary><c>manifest.json</c> of a character pack as the player reads it (spec/CHARACTERS.md, Character packs).</summary>
public sealed record PackManifest
{
    public const string Format = "agent-speed-character-pack";
    public const int Version = 1;

    public sealed record ArtistCredit(string Name, string? Url);

    public required string FormatName { get; init; }

    public required int FormatVersion { get; init; }

    public required string Id { get; init; }

    public required int PackVersion { get; init; }

    /// <summary>Display names by app language (<c>zh-Hant</c>, <c>zh-Hans</c>, <c>en</c>, <c>ja</c>).</summary>
    public required IReadOnlyDictionary<string, string> Name { get; init; }

    /// <summary><c>providers</c> keys of prices.json this character stands for.</summary>
    public required IReadOnlyList<string> Vendors { get; init; }

    /// <summary><c>#RRGGBB</c>.</summary>
    public required string Accent { get; init; }

    public ArtistCredit? Artist { get; init; }

    public string? License { get; init; }

    public string? MinApp { get; init; }

    /// <summary>Band key → folder inside the pack.</summary>
    public required IReadOnlyDictionary<string, string> Poses { get; init; }

    public required string Preview { get; init; }

    /// <summary>The character's motion personality; null moves like Claude.</summary>
    public MotionProfile? Motion { get; init; }

    /// <exception cref="JsonException">A required key is missing or of the wrong type.</exception>
    public static PackManifest Decode(JsonElement e)
    {
        RigJson.Object(e);
        ArtistCredit? artist = null;
        if (e.TryGetProperty("artist", out var a) && a.ValueKind == JsonValueKind.Object
            && a.TryGetProperty("name", out var an) && an.ValueKind == JsonValueKind.String)
        {
            var url = a.TryGetProperty("url", out var au) && au.ValueKind == JsonValueKind.String ? au.GetString() : null;
            artist = new ArtistCredit(an.GetString()!, url);
        }

        string? Optional(string key) => e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        return new PackManifest
        {
            FormatName = RigJson.String(e, "format"),
            FormatVersion = RigJson.Int(e, "version"),
            Id = RigJson.String(e, "id"),
            PackVersion = RigJson.Int(e, "packVersion"),
            Name = StringMap(RigJson.Required(e, "name")),
            Vendors = RigJson.Array(RigJson.Required(e, "vendors"),
                v => v.ValueKind == JsonValueKind.String ? v.GetString()! : throw new JsonException("Expected a string.")),
            Accent = RigJson.String(e, "accent"),
            Artist = artist,
            License = Optional("license"),
            MinApp = Optional("minApp"),
            Poses = StringMap(RigJson.Required(e, "poses")),
            Preview = RigJson.String(e, "preview"),
            Motion = e.TryGetProperty("motion", out var m) && m.ValueKind == JsonValueKind.Object ? MotionProfile.Decode(m) : null,
        };
    }

    private static Dictionary<string, string> StringMap(JsonElement e)
    {
        RigJson.Object(e);
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var p in e.EnumerateObject())
        {
            map[p.Name] = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString()! : throw new JsonException("Expected a string.");
        }

        return map;
    }

    /// <summary>Whether an app of <paramref name="version"/> (dotted numbers) can play the pack.</summary>
    public bool PlayableByApp(string version)
    {
        if (MinApp is null)
        {
            return true;
        }

        static int[] Parts(string s) => s.Split('.').Select(p => int.TryParse(p, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0).ToArray();
        var a = Parts(version);
        var b = Parts(MinApp);
        for (var i = 0; i < Math.Max(a.Length, b.Length); i++)
        {
            var x = i < a.Length ? a[i] : 0;
            var y = i < b.Length ? b[i] : 0;
            if (x != y)
            {
                return x > y;
            }
        }

        return true;
    }
}

/// <summary>Why a pack cannot be loaded.</summary>
public sealed class PackLoadException : Exception
{
    public PackLoadException()
    {
    }

    public PackLoadException(string message)
        : base(message)
    {
    }

    public PackLoadException(string message, Exception inner)
        : base(message, inner)
    {
    }

    private PackLoadException(string kind, string detail, Exception? inner)
        : base($"{kind}: {detail}", inner)
    {
        Kind = kind;
        Detail = detail;
    }

    public string Kind { get; } = string.Empty;

    public string Detail { get; } = string.Empty;

    public static PackLoadException Unsupported(string format, int version) => new("unsupported", $"{format} {version}", null);

    public static PackLoadException BadId(string id) => new("badID", id, null);

    public static PackLoadException UnsafePath(string path) => new("unsafePath", path, null);

    public static PackLoadException MissingIdlePose() => new("missingIdlePose", string.Empty, null);

    public static PackLoadException Unreadable(string path, Exception? inner = null) => new("unreadable", path, inner);

    public static PackLoadException Rig(string band, CharacterRigException error) => new("rig", $"{band} {error.Message}", error);
}

/// <summary>One band's illustration: its rig and the atlas image the renderer loads.</summary>
public sealed record CharacterPose(SpeedBand Band, CharacterRig Rig, string AtlasPath);

/// <summary>An unpacked character pack: its manifest and one rig per band present (idle is required).</summary>
public sealed class CharacterPack
{
    private const long MaxJsonBytes = 64L << 20;

    private CharacterPack(string root, PackManifest manifest, CharacterPose[] poses)
    {
        Root = root;
        Manifest = manifest;
        Poses = poses;
    }

    public string Root { get; }

    public PackManifest Manifest { get; }

    /// <summary>Present bands in band order; the first is idle.</summary>
    public IReadOnlyList<CharacterPose> Poses { get; }

    public string Id => Manifest.Id;

    public string PreviewPath => Path.Combine(Root, Manifest.Preview.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>
    /// Loads and validates a pack folder: known format, a safe id, relative paths that stay inside the folder (no
    /// <c>..</c>, no absolute paths, no symbolic links), and rigs the player understands.
    /// </summary>
    /// <exception cref="PackLoadException">The pack cannot be played.</exception>
    public static CharacterPack Load(string folder)
    {
        var root = RealPath(folder);
        PackManifest manifest;
        try
        {
            using var doc = JsonDocument.Parse(ReadLimited(Path.Combine(root, "manifest.json"), 1L << 20));
            manifest = PackManifest.Decode(doc.RootElement);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            throw PackLoadException.Unreadable("manifest.json", ex);
        }

        if (manifest.FormatName != PackManifest.Format || manifest.FormatVersion != PackManifest.Version)
        {
            throw PackLoadException.Unsupported(manifest.FormatName, manifest.FormatVersion);
        }

        if (!IsSafeId(manifest.Id))
        {
            throw PackLoadException.BadId(manifest.Id);
        }

        _ = Resolve(manifest.Preview, root);
        var poses = new List<CharacterPose>();
        foreach (var band in SpeedBands.All)
        {
            if (!manifest.Poses.TryGetValue(band.Key(), out var poseFolder))
            {
                continue;
            }

            var dir = Resolve(poseFolder, root);
            var rigPath = Resolve("rig.json", dir, root);
            RigDocument doc;
            try
            {
                doc = RigDocument.Decode(ReadLimited(rigPath, MaxJsonBytes));
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                throw PackLoadException.Unreadable($"{poseFolder}/rig.json", ex);
            }

            CharacterRig rig;
            try
            {
                rig = new CharacterRig(doc);
            }
            catch (CharacterRigException ex)
            {
                throw PackLoadException.Rig(band.Key(), ex);
            }

            var atlas = Resolve(rig.AtlasFile, dir, root);
            poses.Add(new CharacterPose(band, rig, atlas));
        }

        if (poses.Count == 0 || poses[0].Band != SpeedBand.Idle)
        {
            throw PackLoadException.MissingIdlePose();
        }

        return new CharacterPack(root, manifest, poses.ToArray());
    }

    internal static bool IsSafeId(string id) => id.Length is > 0 and <= 64 && id.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-');

    /// <summary>
    /// <paramref name="relative"/> inside <paramref name="directory"/> (itself inside <paramref name="root"/>), refusing
    /// absolute paths, <c>..</c>, empty components, backslashes and any symbolic link or junction on the way.
    /// </summary>
    internal static string Resolve(string relative, string directory, string? root = null)
    {
        root ??= directory;
        var parts = relative.Split('/');
        if (relative.Length == 0 || relative.StartsWith('/') || relative.Contains('\\', StringComparison.Ordinal) || relative.Contains(':', StringComparison.Ordinal)
            || parts.Any(p => p.Length == 0 || p == "." || p == ".."))
        {
            throw PackLoadException.UnsafePath(relative);
        }

        var path = directory;
        foreach (var part in parts)
        {
            path = Path.Combine(path, part);
            if (IsLink(path))
            {
                throw PackLoadException.UnsafePath(relative);
            }
        }

        var full = Path.GetFullPath(path);
        var rootFull = Path.GetFullPath(root);
        var prefix = rootFull.EndsWith(Path.DirectorySeparatorChar) ? rootFull : rootFull + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            throw PackLoadException.UnsafePath(relative);
        }

        return full;
    }

    private static bool IsLink(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (info.Exists)
            {
                return info.LinkTarget is not null || info.Attributes.HasFlag(FileAttributes.ReparsePoint);
            }

            var dir = new DirectoryInfo(path);
            return dir.Exists && (dir.LinkTarget is not null || dir.Attributes.HasFlag(FileAttributes.ReparsePoint));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    private static string RealPath(string folder)
    {
        var full = Path.GetFullPath(folder);
        var dir = new DirectoryInfo(full);
        if (dir.Exists && dir.LinkTarget is not null && dir.ResolveLinkTarget(returnFinalTarget: true) is { } target)
        {
            return Path.GetFullPath(target.FullName);
        }

        return full;
    }

    private static byte[] ReadLimited(string path, long limit)
    {
        var info = new FileInfo(path);
        if (!info.Exists)
        {
            throw new FileNotFoundException("Missing file.", Path.GetFileName(path));
        }

        if (info.Length > limit)
        {
            throw new IOException("File too large.");
        }

        return File.ReadAllBytes(path);
    }
}
