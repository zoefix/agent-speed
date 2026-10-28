using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;

namespace AgentSpeed.Presentation.Updates;

/// <summary>The downloads one install needs, already checked against size limits and the source's URL rules.</summary>
public sealed record InstallPlan(ReleaseInfo Release, ReleaseAsset Package, ReleaseAsset Checksums);

public readonly record struct PackageEntry(string FullName, long Length);

/// <summary>Windows release package rules (UPDATES sections 1, 5 and 6).</summary>
public static class UpdatePackage
{
    public const string ExecutableName = "AgentSpeed.exe";
    public const string ChecksumsName = "SHA256SUMS.txt";

    /// <summary>Largest asset download (UPDATES section 1).</summary>
    public const long MaxAssetBytes = 300L * 1024 * 1024;

    public const long MaxChecksumsBytes = ChecksumManifest.MaxBytes;

    /// <summary>Largest uncompressed executable (an uncompressed single-file build is about 150 MB).</summary>
    public const long MaxExecutableBytes = 400L * 1024 * 1024;

    public const long MaxTotalUncompressedBytes = 1024L * 1024 * 1024;
    public const int MaxEntries = 64;
    public const int MaxEntryNameLength = 260;

    public static string? AssetNameFor(Architecture architecture) => architecture switch
    {
        Architecture.X64 => "AgentSpeed-Windows-x64.zip",
        Architecture.Arm64 => "AgentSpeed-Windows-arm64.zip",
        _ => null,
    };

    /// <exception cref="UpdateException">The release lacks a usable package or checksum file.</exception>
    public static InstallPlan Plan(ReleaseInfo release, Architecture architecture, UpdateSource source)
    {
        ArgumentNullException.ThrowIfNull(release);
        ArgumentNullException.ThrowIfNull(source);
        if (!release.IsOfferable)
        {
            throw new UpdateException(UpdateFailure.Invalid, "Drafts and pre-releases are not installed.");
        }

        var name = AssetNameFor(architecture)
            ?? throw new UpdateException(UpdateFailure.Invalid, $"No package for architecture {architecture}.");
        var package = Require(release, name, MaxAssetBytes, source);
        var checksums = Require(release, ChecksumsName, MaxChecksumsBytes, source);
        return new InstallPlan(release, package, checksums);
    }

    /// <summary>
    /// Rejects archives with unsafe entry names (absolute, drive or UNC paths, <c>..</c>, alternate streams, device-like
    /// trailing dots or spaces), too many entries, oversized content, or anything but exactly one root
    /// <see cref="ExecutableName"/>.
    /// </summary>
    /// <exception cref="UpdateException">The package is not valid.</exception>
    public static PackageEntry ValidateEntries(IEnumerable<PackageEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        PackageEntry? executable = null;
        var count = 0;
        long total = 0;
        foreach (var entry in entries)
        {
            if (++count > MaxEntries)
            {
                throw Invalid("The package has too many entries.");
            }

            if (!IsSafeEntryName(entry.FullName))
            {
                throw Invalid("The package contains an unsafe path.");
            }

            if (entry.Length < 0 || entry.Length > MaxExecutableBytes)
            {
                throw Invalid("A package entry is too large.");
            }

            total += entry.Length;
            if (total > MaxTotalUncompressedBytes)
            {
                throw Invalid("The package is too large.");
            }

            if (string.Equals(entry.FullName, ExecutableName, StringComparison.OrdinalIgnoreCase))
            {
                if (executable is not null)
                {
                    throw Invalid("The package contains the executable twice.");
                }

                executable = entry;
            }
        }

        return executable is { Length: > 0 } found ? found : throw Invalid($"The package does not contain {ExecutableName}.");
    }

    public static bool IsSafeEntryName(string? name)
    {
        if (string.IsNullOrEmpty(name) || name.Length > MaxEntryNameLength)
        {
            return false;
        }

        if (name[0] is '/' or '\\' || name.Contains(':', StringComparison.Ordinal))
        {
            return false;
        }

        var directory = name[^1] is '/' or '\\';
        var path = directory ? name[..^1] : name;
        if (path.Length == 0)
        {
            return false;
        }

        foreach (var segment in path.Split('/', '\\'))
        {
            if (segment.Length == 0 || segment is "." or ".." || segment[^1] is '.' or ' ')
            {
                return false;
            }

            foreach (var c in segment)
            {
                if (char.IsControl(c) || c is '<' or '>' or '"' or '|' or '?' or '*')
                {
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>
    /// Validates the archive and writes its executable to <paramref name="destinationDirectory"/> under a fixed name,
    /// never more bytes than the entry declares. Returns the extracted file's path.
    /// </summary>
    /// <exception cref="UpdateException">The archive is unreadable or not valid.</exception>
    public static string ExtractExecutable(string zipPath, string destinationDirectory)
    {
        ArgumentException.ThrowIfNullOrEmpty(zipPath);
        ArgumentException.ThrowIfNullOrEmpty(destinationDirectory);
        var root = Path.GetFullPath(destinationDirectory);
        var target = Path.GetFullPath(Path.Combine(root, ExecutableName));
        if (!string.Equals(Path.GetDirectoryName(target), root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                StringComparison.Ordinal))
        {
            throw Invalid("The extraction target is outside the temporary folder.");
        }

        var created = false;
        try
        {
            using var archive = OpenArchive(zipPath);
            ValidateEntries(archive.Entries.Select(e => new PackageEntry(e.FullName, e.Length)));
            var entry = archive.Entries.Single(e => string.Equals(e.FullName, ExecutableName, StringComparison.OrdinalIgnoreCase));
            using var input = entry.Open();
            using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920);
            created = true;
            CopyAtMost(input, output, entry.Length);
            if (output.Length != entry.Length)
            {
                throw Invalid("The executable is shorter than declared.");
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or NotSupportedException or UnauthorizedAccessException)
        {
            if (created)
            {
                TryDelete(target);
            }

            throw new UpdateException(UpdateFailure.Invalid, "The package could not be extracted.", ex);
        }
        catch (UpdateException)
        {
            if (created)
            {
                TryDelete(target);
            }

            throw;
        }

        return target;
    }

    /// <summary>Entry names and sizes as <see cref="ExtractExecutable"/> sees them.</summary>
    /// <exception cref="InvalidDataException">The file is not a zip archive.</exception>
    public static IReadOnlyList<PackageEntry> ReadEntries(string zipPath)
    {
        using var archive = OpenArchive(zipPath);
        return archive.Entries.Select(e => new PackageEntry(e.FullName, e.Length)).ToArray();
    }

    /// <summary>
    /// Names are decoded as UTF-8 even without the zip "language encoding" flag (some tools omit it), never with a
    /// legacy code page.
    /// </summary>
    private static ZipArchive OpenArchive(string zipPath) => ZipFile.Open(zipPath, ZipArchiveMode.Read, Encoding.UTF8);

    private static ReleaseAsset Require(ReleaseInfo release, string name, long maxBytes, UpdateSource source)
    {
        var asset = release.FindAsset(name) ?? throw Invalid($"The release has no {name}.");
        if (asset.Size <= 0 || asset.Size > maxBytes)
        {
            throw Invalid($"{name} has an invalid size.");
        }

        return source.IsAllowed(asset.DownloadUrl) ? asset : throw Invalid($"{name} is not hosted on an allowed host.");
    }

    private static void CopyAtMost(Stream input, Stream output, long limit)
    {
        var buffer = new byte[81920];
        long written = 0;
        int read;
        while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
        {
            written += read;
            if (written > limit)
            {
                throw Invalid("The executable is larger than declared.");
            }

            output.Write(buffer, 0, read);
        }
    }

    private static UpdateException Invalid(string message) => new(UpdateFailure.Invalid, message);

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
