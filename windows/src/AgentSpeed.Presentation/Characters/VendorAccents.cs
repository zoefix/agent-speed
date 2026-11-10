using AgentSpeed.Presentation.Colors;

namespace AgentSpeed.Presentation.Characters;

/// <summary>The vendor dot's color (CHARACTERS 1 <c>accent</c>): the accent of the character that stands for the vendor.</summary>
public static class VendorAccents
{
    /// <summary>An installed pack's accent first, then the catalog's; null when no character stands for the vendor.</summary>
    public static Rgba? Of(string? vendor, IReadOnlyList<InstalledCharacter> installed, CharacterCatalog? catalog)
    {
        ArgumentNullException.ThrowIfNull(installed);
        if (string.IsNullOrEmpty(vendor))
        {
            return null;
        }

        foreach (var pack in installed)
        {
            if (pack.Manifest.Vendors.Contains(vendor, StringComparer.Ordinal) && CharacterManifest.Rgb(pack.Manifest.Accent) is { } rgb)
            {
                return new Rgba(rgb.R, rgb.G, rgb.B);
            }
        }

        foreach (var entry in catalog?.Characters ?? [])
        {
            if (entry.Vendors.Contains(vendor, StringComparer.Ordinal) && CharacterManifest.Rgb(entry.Accent) is { } rgb)
            {
                return new Rgba(rgb.R, rgb.G, rgb.B);
            }
        }

        return null;
    }
}
