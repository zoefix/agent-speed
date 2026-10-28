using System.Text;
using System.Text.Json;

namespace AgentSpeed.Presentation.Settings;

public enum SettingsLoadStatus
{
    Loaded,
    Missing,
    RecoveredFromCorruption,
    Unreadable,
}

public sealed record SettingsLoadResult(AppSettings Settings, SettingsLoadStatus Status, string? BackupPath = null);

/// <summary>Persists settings as JSON with atomic replace; a corrupt file is set aside and defaults are used.</summary>
public sealed class SettingsStore
{
    private readonly object _gate = new();

    public SettingsStore(string filePath)
    {
        ArgumentException.ThrowIfNullOrEmpty(filePath);
        FilePath = Path.GetFullPath(filePath);
    }

    public string FilePath { get; }

    public string CorruptBackupPath => FilePath + ".corrupt";

    public SettingsLoadResult Load()
    {
        lock (_gate)
        {
            string text;
            try
            {
                var info = new FileInfo(FilePath);
                if (!info.Exists)
                {
                    return new SettingsLoadResult(AppSettings.Default, SettingsLoadStatus.Missing);
                }

                if (info.Length > SettingsSerializer.MaxFileBytes)
                {
                    return SetAside();
                }

                text = File.ReadAllText(FilePath, Encoding.UTF8);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return new SettingsLoadResult(AppSettings.Default, SettingsLoadStatus.Unreadable);
            }

            try
            {
                return new SettingsLoadResult(SettingsSerializer.Deserialize(text), SettingsLoadStatus.Loaded);
            }
            catch (Exception ex) when (ex is JsonException or ArgumentException)
            {
                return SetAside();
            }
        }
    }

    /// <summary>Writes to a temporary file in the same folder, flushes it to disk, then swaps it in.</summary>
    public void Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var json = SettingsSerializer.Serialize(settings);
        var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(json);

        lock (_gate)
        {
            var directory = Path.GetDirectoryName(FilePath)!;
            Directory.CreateDirectory(directory);
            var temp = Path.Combine(directory, $".{Path.GetFileName(FilePath)}.{Guid.NewGuid():N}.tmp");
            try
            {
                using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096,
                           FileOptions.WriteThrough))
                {
                    stream.Write(bytes);
                    stream.Flush(flushToDisk: true);
                }

                if (File.Exists(FilePath))
                {
                    File.Replace(temp, FilePath, destinationBackupFileName: null, ignoreMetadataErrors: true);
                }
                else
                {
                    File.Move(temp, FilePath);
                }
            }
            finally
            {
                TryDelete(temp);
            }
        }
    }

    private SettingsLoadResult SetAside()
    {
        try
        {
            File.Move(FilePath, CorruptBackupPath, overwrite: true);
            return new SettingsLoadResult(AppSettings.Default, SettingsLoadStatus.RecoveredFromCorruption, CorruptBackupPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new SettingsLoadResult(AppSettings.Default, SettingsLoadStatus.RecoveredFromCorruption);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
