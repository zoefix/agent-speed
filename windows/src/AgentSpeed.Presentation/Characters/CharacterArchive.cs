using System.IO.Compression;

namespace AgentSpeed.Presentation.Characters;

/// <summary>One entry of a pack archive, as its central directory describes it.</summary>
public readonly record struct CharacterArchiveEntry(string Path, long UncompressedSize = 0, bool IsSymbolicLink = false)
{
    public bool IsDirectory => Path.EndsWith('/');
}

/// <summary>
/// What a pack archive may contain (CHARACTERS 1) and how it is unpacked: safe relative paths (no <c>..</c>, no absolute
/// paths, no backslashes, colons or other names Windows treats specially), no links, bounded entry count and size, a
/// manifest at the top. Unpacking writes only regular files and folders inside the target and stops at the declared
/// sizes, so a lying archive cannot write more.
/// </summary>
public static class CharacterArchive
{
    public const int MaxEntries = 4_096;
    public const long MaxExpandedSize = 400L << 20;
    public const long MaxEntrySize = 64L << 20;

    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>A relative path of safe components separated by <c>/</c> (a trailing <c>/</c> marks a folder).</summary>
    public static bool IsSafe(string? path)
    {
        if (string.IsNullOrEmpty(path) || path.Length > 512 || path[0] is '/' or '~')
        {
            return false;
        }

        if (path.Any(c => c < 0x20 || c == 0x7F || c is '\\' or ':' or '*' or '?' or '"' or '<' or '>' or '|'))
        {
            return false;
        }

        var trimmed = path.EndsWith('/') ? path[..^1] : path;
        var parts = trimmed.Split('/');
        return parts.Length > 0 && parts.All(IsSafeComponent);
    }

    private static bool IsSafeComponent(string part)
    {
        if (part.Length == 0 || part is "." or ".." || part.EndsWith('.') || part.EndsWith(' ') || part.StartsWith(' '))
        {
            return false;
        }

        var stem = part.Split('.')[0];
        return !Reserved.Contains(stem);
    }

    /// <exception cref="CharacterPackException">An entry is unsafe, a link, too large, or the manifest is missing.</exception>
    public static void Validate(IReadOnlyList<CharacterArchiveEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (entries.Count == 0 || entries.Count > MaxEntries)
        {
            throw new CharacterPackException(CharacterPackErrorKind.TooLarge);
        }

        long total = 0;
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            if (!IsSafe(entry.Path))
            {
                throw new CharacterPackException(CharacterPackErrorKind.UnsafePath, entry.Path);
            }

            if (entry.IsSymbolicLink)
            {
                throw new CharacterPackException(CharacterPackErrorKind.SymbolicLink, entry.Path);
            }

            if (entry.UncompressedSize < 0 || entry.UncompressedSize > MaxEntrySize)
            {
                throw new CharacterPackException(CharacterPackErrorKind.TooLarge);
            }

            total += entry.UncompressedSize;
            if (total > MaxExpandedSize)
            {
                throw new CharacterPackException(CharacterPackErrorKind.TooLarge);
            }

            // Two names one case-insensitive file system folds together would overwrite each other.
            if (!entry.IsDirectory && !names.Add(entry.Path))
            {
                throw new CharacterPackException(CharacterPackErrorKind.UnsafePath, entry.Path);
            }
        }

        if (!entries.Any(e => e.Path == "manifest.json"))
        {
            throw new CharacterPackException(CharacterPackErrorKind.MissingFile, "manifest.json");
        }
    }

    /// <summary>The entries of a zip archive; Unix symbolic links are recognized by their mode.</summary>
    public static IReadOnlyList<CharacterArchiveEntry> Entries(ZipArchive archive)
    {
        ArgumentNullException.ThrowIfNull(archive);
        return archive.Entries.Select(e => new CharacterArchiveEntry(e.FullName, e.Length, IsLink(e))).ToArray();
    }

    private static bool IsLink(ZipArchiveEntry entry)
    {
        var mode = (entry.ExternalAttributes >> 16) & 0xF000;
        return mode == 0xA000;
    }

    /// <summary>Validates and unpacks <paramref name="zipPath"/> into the empty folder <paramref name="destination"/>.</summary>
    /// <exception cref="CharacterPackException">The archive is refused.</exception>
    /// <exception cref="InvalidDataException">The archive is damaged.</exception>
    public static void Extract(string zipPath, string destination, CancellationToken cancellationToken = default)
    {
        using var archive = ZipFile.OpenRead(zipPath);
        Validate(Entries(archive));
        var root = Path.GetFullPath(destination);
        var prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        Directory.CreateDirectory(root);
        var buffer = new byte[81920];
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = entry.FullName.TrimEnd('/').Replace('/', Path.DirectorySeparatorChar);
            var target = Path.GetFullPath(Path.Combine(root, relative));
            if (!target.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                throw new CharacterPackException(CharacterPackErrorKind.UnsafePath, entry.FullName);
            }

            if (entry.FullName.EndsWith('/'))
            {
                Directory.CreateDirectory(target);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using var input = entry.Open();
            using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            long written = 0;
            int read;
            while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                written += read;
                if (written > entry.Length)
                {
                    throw new CharacterPackException(CharacterPackErrorKind.TooLarge);
                }

                output.Write(buffer, 0, read);
            }

            if (written != entry.Length)
            {
                throw new InvalidDataException("An entry is shorter than declared.");
            }
        }
    }
}
