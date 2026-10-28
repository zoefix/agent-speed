namespace AgentSpeed.Presentation.Tests.TestSupport;

/// <summary>A unique, self-deleting folder under the system temp path.</summary>
internal sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "AgentSpeed.Presentation.Tests." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string Combine(params string[] parts) => System.IO.Path.Combine([Path, .. parts]);

    /// <summary>Every file in the folder, including hidden ones.</summary>
    public string[] Files() =>
        Directory.GetFiles(Path, "*", new EnumerationOptions { AttributesToSkip = 0, RecurseSubdirectories = true });

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
