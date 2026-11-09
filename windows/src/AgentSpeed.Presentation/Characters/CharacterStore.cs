using System.Text.Json;

namespace AgentSpeed.Presentation.Characters;

/// <summary>An installed character pack: its manifest and the folder that holds it (CHARACTERS 2).</summary>
/// <param name="IsDevelopment">Listed from the <c>AGENT_SPEED_CHARACTERS</c> folder; it cannot be removed or updated from the app.</param>
public sealed record InstalledCharacter(CharacterManifest Manifest, string Folder, bool IsDevelopment = false)
{
    public string Id => Manifest.Id;

    public string PreviewPath => Path.Combine(Folder, Manifest.Preview.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>The folder holding <c>rig.json</c> and the atlas of a band's pose.</summary>
    public string PoseFolder(string band) => Path.Combine(Folder, Manifest.PoseFor(band).Replace('/', Path.DirectorySeparatorChar));
}

/// <summary>
/// Installed packs on disk: <c>&lt;root&gt;/&lt;id&gt;/</c> (CHARACTERS 2). Installing and removing replace whole folders,
/// so a player never sees a half-written pack.
/// </summary>
public sealed class CharacterStore(string root, string? development = null, string? appVersion = null)
{
    public const string DevelopmentVariable = "AGENT_SPEED_CHARACTERS";
    private const long ManifestLimit = 64 << 10;

    public string Root { get; } = root;

    public string? Development { get; } = development;

    public string? AppVersion { get; } = appVersion;

    /// <summary><c>&lt;app data&gt;\Characters</c>, with the development folder from the environment when set.</summary>
    public static CharacterStore Standard(string appData, string? appVersion, Func<string, string?>? environment = null)
    {
        var dev = (environment ?? Environment.GetEnvironmentVariable)(DevelopmentVariable);
        return new CharacterStore(Path.Combine(appData, "Characters"), string.IsNullOrWhiteSpace(dev) ? null : dev, appVersion);
    }

    /// <summary>Every valid pack, development ones first, then installed ones by id; a development pack hides an installed one with the same id.</summary>
    public IReadOnlyList<InstalledCharacter> Installed()
    {
        var output = new List<InstalledCharacter>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (folder, dev) in new[] { (Development, true), (Root, false) })
        {
            if (folder is null)
            {
                continue;
            }

            foreach (var pack in Scan(folder, dev, AppVersion))
            {
                if (seen.Add(pack.Id))
                {
                    output.Add(pack);
                }
            }
        }

        return output;
    }

    public InstalledCharacter? Installed(string id) => Installed().FirstOrDefault(p => p.Id == id);

    /// <summary>Reads and validates one unpacked pack folder; null when anything required is missing or invalid.</summary>
    public static InstalledCharacter? Read(string folder, bool isDevelopment = false, string? appVersion = null)
    {
        try
        {
            return new InstalledCharacter(Manifest(folder, appVersion), folder, isDevelopment);
        }
        catch (Exception ex) when (ex is CharacterPackException or JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The validated manifest of an unpacked pack whose every pose holds <c>rig.json</c> and the preview exists.</summary>
    /// <exception cref="CharacterPackException">Missing or invalid.</exception>
    public static CharacterManifest Manifest(string folder, string? appVersion)
    {
        var file = new FileInfo(Path.Combine(folder, "manifest.json"));
        if (!file.Exists || file.LinkTarget is not null || file.Attributes.HasFlag(FileAttributes.ReparsePoint) || file.Length > ManifestLimit)
        {
            throw new CharacterPackException(CharacterPackErrorKind.MissingFile, "manifest.json");
        }

        CharacterManifest manifest;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllBytes(file.FullName));
            manifest = CharacterManifest.Decode(doc.RootElement);
        }
        catch (JsonException)
        {
            throw new CharacterPackException(CharacterPackErrorKind.Format);
        }

        manifest.Validate(appVersion);
        foreach (var path in manifest.Poses.Values.Distinct(StringComparer.Ordinal))
        {
            if (!File.Exists(Path.Combine(folder, path.Replace('/', Path.DirectorySeparatorChar), "rig.json")))
            {
                throw new CharacterPackException(CharacterPackErrorKind.MissingFile, path + "/rig.json");
            }
        }

        if (!File.Exists(Path.Combine(folder, manifest.Preview.Replace('/', Path.DirectorySeparatorChar))))
        {
            throw new CharacterPackException(CharacterPackErrorKind.MissingFile, manifest.Preview);
        }

        return manifest;
    }

    /// <summary>Moves a validated, unpacked pack into place, replacing an older copy atomically. <paramref name="expectedId"/> must match.</summary>
    /// <exception cref="CharacterPackException">The pack is invalid, links out, or has another id.</exception>
    public InstalledCharacter Install(string unpacked, string expectedId)
    {
        var manifest = Manifest(unpacked, AppVersion);
        if (manifest.Id != expectedId)
        {
            throw new CharacterPackException(CharacterPackErrorKind.Mismatch);
        }

        VerifyTree(unpacked);
        Directory.CreateDirectory(Root);
        var target = Path.Combine(Root, manifest.Id);
        var staged = Path.Combine(Root, ".incoming-" + Guid.NewGuid().ToString("N"));
        Move(unpacked, staged);
        if (Directory.Exists(target))
        {
            var old = Path.Combine(Root, ".outgoing-" + Guid.NewGuid().ToString("N"));
            Move(target, old);
            try
            {
                Move(staged, target);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Move(old, target);
                TryDelete(staged);
                throw;
            }

            TryDelete(old);
        }
        else
        {
            Move(staged, target);
        }

        return new InstalledCharacter(manifest, target);
    }

    /// <summary>Deletes an installed (non-development) pack.</summary>
    /// <exception cref="CharacterPackException">The id is not a pack id.</exception>
    public void Remove(string id)
    {
        if (!CharacterId.IsValid(id))
        {
            throw new CharacterPackException(CharacterPackErrorKind.InvalidId);
        }

        var target = Path.Combine(Root, id);
        if (!Directory.Exists(target))
        {
            return;
        }

        var old = Path.Combine(Root, ".outgoing-" + Guid.NewGuid().ToString("N"));
        Move(target, old);
        TryDelete(old);
    }

    /// <summary>A private work folder inside the store (same volume, so the final move is a rename).</summary>
    public string CreateWorkspace()
    {
        var path = Path.Combine(Root, ".work-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>Deletes leftovers of an install or removal that was interrupted.</summary>
    public void CleanLeftovers()
    {
        if (!Directory.Exists(Root))
        {
            return;
        }

        foreach (var dir in Directory.GetDirectories(Root))
        {
            var name = Path.GetFileName(dir);
            if (name.StartsWith(".incoming-", StringComparison.Ordinal) || name.StartsWith(".outgoing-", StringComparison.Ordinal)
                || name.StartsWith(".work-", StringComparison.Ordinal))
            {
                TryDelete(dir);
            }
        }
    }

    /// <summary>Only regular files and folders, no links or junctions, within the size limit.</summary>
    /// <exception cref="CharacterPackException">A link, or too large.</exception>
    public static void VerifyTree(string folder)
    {
        long total = 0;
        var pending = new Stack<DirectoryInfo>();
        pending.Push(new DirectoryInfo(folder));
        while (pending.Count > 0)
        {
            var dir = pending.Pop();
            foreach (var item in dir.EnumerateFileSystemInfos())
            {
                if (item.LinkTarget is not null || item.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    throw new CharacterPackException(CharacterPackErrorKind.SymbolicLink, item.Name);
                }

                switch (item)
                {
                    case DirectoryInfo sub:
                        pending.Push(sub);
                        break;
                    case FileInfo file:
                        total += file.Length;
                        if (total > CharacterArchive.MaxExpandedSize)
                        {
                            throw new CharacterPackException(CharacterPackErrorKind.TooLarge);
                        }

                        break;
                }
            }
        }
    }

    /// <summary>A rename that retries briefly: a scanner may hold a fresh file open for a moment.</summary>
    private static void Move(string from, string to)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                Directory.Move(from, to);
                return;
            }
            catch (Exception ex) when (attempt < 4 && ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(100 * (attempt + 1));
            }
        }
    }

    internal static void TryDelete(string folder)
    {
        try
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static IEnumerable<InstalledCharacter> Scan(string folder, bool isDevelopment, string? appVersion)
    {
        string[] names;
        try
        {
            names = Directory.Exists(folder) ? Directory.GetDirectories(folder).Select(Path.GetFileName).OfType<string>().ToArray() : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            yield break;
        }

        foreach (var name in names.Order(StringComparer.Ordinal))
        {
            if (!CharacterId.IsValid(name))
            {
                continue;
            }

            if (Read(Path.Combine(folder, name), isDevelopment, appVersion) is { } pack && pack.Id == name)
            {
                yield return pack;
            }
        }
    }
}
