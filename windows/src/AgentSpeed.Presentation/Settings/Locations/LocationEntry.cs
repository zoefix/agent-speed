using AgentSpeed.Core.Locations;

namespace AgentSpeed.Presentation.Settings.Locations;

/// <summary>One folder a row names: the full path (tooltip, reveal), the path as shown (<c>~</c> for the home folder), its state.</summary>
public sealed record LocationLine(string Path, string Display, bool Readable);

/// <summary>
/// One entry of a source's location list as Settings shows it (DESIGN 5): its folders (the automatic entry can name several),
/// whether anything it reads exists, and what Show in File Explorer opens.
/// </summary>
/// <param name="Exists">The entry reads something that exists; a missing one is shown dimmed with <c>source.folderMissing</c>.</param>
/// <param name="RevealPath">The first readable location, else the first existing one; null when none exists.</param>
/// <param name="IsFile">The location is a database file (a document icon instead of a folder).</param>
public sealed record LocationEntry(DataLocation Location, IReadOnlyList<LocationLine> Lines, bool Exists, string? RevealPath, bool IsFile)
{
    public bool IsAutomatic => Location.IsAutomatic;
}
