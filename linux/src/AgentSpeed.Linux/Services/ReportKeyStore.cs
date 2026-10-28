using System.Security.Cryptography;
using AgentSpeed.Core.Reporting;
using AgentSpeed.Linux.Platform;

namespace AgentSpeed.Reporting;

/// <summary>Per-user device secrets; directory 0700, files 0600, atomic replacement.</summary>
internal sealed class ReportKeyStore(string directory)
{
    public string Directory { get; } = directory;
    public static ReportKeyStore Standard() => new(Path.Combine(LinuxPaths.Data, "report"));
    private string FilePath(string id, string suffix)
    {
        if (!ReportCanonical.IsDeviceId(id)) throw new ArgumentException("Invalid device id.", nameof(id));
        return Path.Combine(Directory, id + "." + suffix);
    }
    private void Write(string path, byte[] value)
    {
        var temp = Path.Combine(Directory, "." + Guid.NewGuid().ToString("N"));
        try
        {
            LinuxPaths.EnsurePrivate(Directory);
            using (var file = new FileStream(temp, new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write,
                UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite }))
            { file.Write(value); file.Flush(true); }
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new CryptographicException("The key could not be stored.", ex);
        }
        finally { Delete(temp); }
    }
    public SoftwareReportSigner CreateKey(string deviceId, bool pending = false)
    {
        var signer = new SoftwareReportSigner();
        var bytes = signer.ExportPkcs8();
        try { Write(FilePath(deviceId, pending ? "pending.key" : "key"), bytes); return signer; }
        catch { signer.Dispose(); throw; }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
    public SoftwareReportSigner? LoadKey(string deviceId, bool pending = false)
    {
        byte[]? bytes = null;
        try { bytes = File.ReadAllBytes(FilePath(deviceId, pending ? "pending.key" : "key")); return SoftwareReportSigner.FromPkcs8(bytes); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException or ArgumentException) { return null; }
        finally { if (bytes is not null) CryptographicOperations.ZeroMemory(bytes); }
    }
    public bool PromotePending(string deviceId)
    {
        try { File.Move(FilePath(deviceId, "pending.key"), FilePath(deviceId, "key"), true); return true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }
    public void DeletePending(string deviceId) => Delete(FilePath(deviceId, "pending.key"));
    public void DeleteAll(string deviceId)
    {
        foreach (var suffix in new[] { "key", "pending.key", "apikey" }) Delete(FilePath(deviceId, suffix));
    }
    public bool HasFiles(string deviceId) => new[] { "key", "pending.key", "apikey" }.Any(s => File.Exists(FilePath(deviceId, s)));
    public void SaveApiKey(string apiKey, string deviceId)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(apiKey);
        try { Write(FilePath(deviceId, "apikey"), bytes); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
    public string? LoadApiKey(string deviceId)
    {
        try { return File.ReadAllText(FilePath(deviceId, "apikey")); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }
    public void DeleteApiKey(string deviceId) => Delete(FilePath(deviceId, "apikey"));

    private static void Delete(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}

internal static class ReportComputerName
{
    public static string? Current => Environment.MachineName;
}
