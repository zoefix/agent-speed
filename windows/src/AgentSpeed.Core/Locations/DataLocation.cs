namespace AgentSpeed.Core.Locations;

/// <summary>
/// One entry of a location list (METRICS 2.1): the automatic entry (every automatic root of the source, derived from the
/// environment whenever roots are resolved) or a user location (an absolute, normalized path).
/// </summary>
public readonly record struct DataLocation
{
    private DataLocation(string? path)
    {
        Path = path;
    }

    public static DataLocation Automatic => default;

    /// <summary>The user location's path; null for the automatic entry.</summary>
    public string? Path { get; }

    public bool IsAutomatic => Path is null;

    /// <summary>A user location; the path is taken as given (lists normalize their entries).</summary>
    public static DataLocation At(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        return new DataLocation(path);
    }

    public override string ToString() => Path ?? "automatic";
}
